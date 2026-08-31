using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HaWinServer.Core;

public sealed record CloudflareZone(string Id, string Name, string Status, string AccountId, string AccountName);

public sealed record CloudflareTunnel(string Id, string Name);

public sealed record CloudflareDnsRecord(string Id, string Name, string Content);

public sealed record IngressRule(string? Hostname, string Service);

public sealed record CloudflareAccessApp(string Id, string Domain);

/// <summary>
/// Thrown for any Cloudflare API failure. Cloudflare's own error messages
/// (errors[].message in the response envelope) are written for a human, so
/// they are surfaced to the wizard verbatim rather than replaced with a
/// generic "request failed".
/// </summary>
public sealed class CloudflareApiException : Exception
{
    public CloudflareApiException(string message) : base(message) { }
}

/// <summary>
/// Thin wrapper over Cloudflare's REST API (api.cloudflare.com/client/v4) -
/// one method per endpoint this app needs, all sharing the same envelope
/// handling. No SDK/NuGet dependency: HttpClient and System.Text.Json are
/// both already in the framework.
///
/// Every call needs an API token scoped to at least "Cloudflare Tunnel: Edit"
/// plus "Zone / DNS: Edit" and "Zone / Zone: Read" (Access calls additionally
/// need "Account / Access: Apps and Policies: Edit") - see the tunnel setup
/// wizard, which links directly to the token creation page and states this.
/// The token itself is used only for the duration of setup/reconfiguration
/// calls; the long-lived secret this app actually persists is the tunnel run
/// token (see SecretStore), not this API token.
///
/// Deliberately never calls GET /accounts: that endpoint needs the separate
/// "Account Settings: Read" permission, which none of the permissions above
/// imply - confirmed against a real token scoped exactly as documented here,
/// which listed zones fine but got an empty account list back. Every zone
/// object already carries its own account id/name nested inside it, so the
/// account is read off the zone the user picks instead (see
/// ListActiveZonesAsync/CloudflareZone) - one less permission to ask for.
/// </summary>
public sealed class CloudflareApi
{
    private const string BaseUrl = "https://api.cloudflare.com/client/v4";

    private readonly HttpClient _client;

    public CloudflareApi(string apiToken)
    {
        _client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiToken);
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("HaWinServer/1.0 (+tray app)");
    }

    // ---- zones (the account is read off the zone itself - see class doc comment) ----

    /// <summary>Active zones only - a pending/moved zone can't host a working Public Hostname.</summary>
    public async Task<IReadOnlyList<CloudflareZone>> ListActiveZonesAsync(CancellationToken ct = default)
    {
        var zones = await GetListAsync<ZoneDto>("/zones?status=active&per_page=50", ct);
        return zones.Select(z => new CloudflareZone(z.Id, z.Name, z.Status, z.Account.Id, z.Account.Name)).ToList();
    }

    // ---- tunnel ---------------------------------------------------------------

    public async Task<CloudflareTunnel> CreateTunnelAsync(string accountId, string name, CancellationToken ct = default)
    {
        var body = new { name, config_src = "cloudflare" };
        var dto = await PostAsync<TunnelDto>($"/accounts/{accountId}/cfd_tunnel", body, ct);
        return new CloudflareTunnel(dto.Id, dto.Name);
    }

    /// <summary>
    /// Tunnel names are unique per account, and this app derives one
    /// deterministically from the machine name (see TrayContext), so a setup
    /// attempt that creates the tunnel but fails on a later step (ingress
    /// sync, DNS, Access) before settings.json is saved would otherwise
    /// orphan it on Cloudflare - every retry would then hit "You already
    /// have a tunnel with this name" (error 1013) forever. Checking here
    /// first lets a retry adopt that same tunnel instead of failing.
    /// </summary>
    public async Task<CloudflareTunnel?> FindTunnelByNameAsync(string accountId, string name, CancellationToken ct = default)
    {
        var tunnels = await GetListAsync<TunnelDto>(
            $"/accounts/{accountId}/cfd_tunnel?name={Uri.EscapeDataString(name)}&is_deleted=false", ct);
        var match = tunnels.FirstOrDefault();
        return match is null ? null : new CloudflareTunnel(match.Id, match.Name);
    }

    public async Task<string> GetTunnelTokenAsync(string accountId, string tunnelId, CancellationToken ct = default)
    {
        var response = await SendAsync<string>(
            HttpMethod.Get, $"/accounts/{accountId}/cfd_tunnel/{tunnelId}/token", body: null, ct);
        return response;
    }

    /// <summary>
    /// Overwrites the tunnel's ENTIRE ingress rule set - Cloudflare's API has
    /// no per-hostname add/remove, only "replace the whole config", so every
    /// caller must always send the complete, current list built from
    /// settings.json (see TrayContext's sync points), never a diff.
    /// </summary>
    public Task SyncIngressAsync(
        string accountId, string tunnelId, IReadOnlyList<IngressRule> rules, CancellationToken ct = default)
    {
        var ingress = rules
            .Select(r => (object)new { hostname = r.Hostname, service = r.Service, originRequest = new { } })
            .Append(new { hostname = (string?)null, service = "http_status:404", originRequest = new { } })
            .ToList();

        var body = new { config = new { ingress } };
        return SendAsync<JsonElement>(HttpMethod.Put, $"/accounts/{accountId}/cfd_tunnel/{tunnelId}/configurations", body, ct);
    }

    public Task DeleteTunnelAsync(string accountId, string tunnelId, CancellationToken ct = default) =>
        SendAsync<JsonElement>(HttpMethod.Delete, $"/accounts/{accountId}/cfd_tunnel/{tunnelId}", body: null, ct);

    // ---- DNS --------------------------------------------------------------------

    public async Task<CloudflareDnsRecord?> FindDnsRecordAsync(
        string zoneId, string fqdn, CancellationToken ct = default)
    {
        var records = await GetListAsync<DnsRecordDto>(
            $"/zones/{zoneId}/dns_records?type=CNAME&name={Uri.EscapeDataString(fqdn)}", ct);
        var record = records.FirstOrDefault();
        return record is null ? null : new CloudflareDnsRecord(record.Id, record.Name, record.Content);
    }

    /// <summary>
    /// Creates (or, if one already exists for this hostname, updates) the
    /// proxied CNAME pointing at the tunnel. Order matters relative to
    /// SyncIngressAsync: the ingress rule must exist BEFORE this DNS record
    /// is created, or a visitor hits Cloudflare error 1033 (DNS resolves, but
    /// no tunnel route claims the hostname yet).
    /// </summary>
    public async Task<CloudflareDnsRecord> UpsertCnameAsync(
        string zoneId, string fqdn, string tunnelId, CancellationToken ct = default)
    {
        var content = $"{tunnelId}.cfargotunnel.com";
        var existing = await FindDnsRecordAsync(zoneId, fqdn, ct);

        if (existing is null)
        {
            var body = new { type = "CNAME", name = fqdn, content, proxied = true };
            var dto = await PostAsync<DnsRecordDto>($"/zones/{zoneId}/dns_records", body, ct);
            return new CloudflareDnsRecord(dto.Id, dto.Name, dto.Content);
        }
        else
        {
            var body = new { type = "CNAME", name = fqdn, content, proxied = true };
            var dto = await PatchAsync<DnsRecordDto>($"/zones/{zoneId}/dns_records/{existing.Id}", body, ct);
            return new CloudflareDnsRecord(dto.Id, dto.Name, dto.Content);
        }
    }

    public Task DeleteDnsRecordAsync(string zoneId, string recordId, CancellationToken ct = default) =>
        SendAsync<JsonElement>(HttpMethod.Delete, $"/zones/{zoneId}/dns_records/{recordId}", body: null, ct);

    // ---- Access (optional per-instance protection) -------------------------------

    public async Task<CloudflareAccessApp> CreateAccessAppAsync(
        string accountId, string fqdn, CancellationToken ct = default)
    {
        var body = new
        {
            name = fqdn,
            domain = fqdn,
            type = "self_hosted",
            session_duration = "24h",
        };
        var dto = await PostAsync<AccessAppDto>($"/accounts/{accountId}/access/apps", body, ct);
        return new CloudflareAccessApp(dto.Id, dto.Domain);
    }

    /// <summary>Allow policy: the named emails, via a one-time-PIN email challenge (needs no IdP configured).</summary>
    public Task CreateAccessAllowPolicyAsync(
        string accountId, string appId, IReadOnlyList<string> allowedEmails, CancellationToken ct = default)
    {
        var body = new
        {
            name = "HaWinServer - allowed users",
            @decision = "allow",
            include = allowedEmails.Select(email => new { email = new { email } }).ToList(),
        };
        return SendAsync<JsonElement>(HttpMethod.Post, $"/accounts/{accountId}/access/apps/{appId}/policies", body, ct);
    }

    /// <summary>
    /// Bypass policy for /api/webhook/* - without this, Access would
    /// challenge every automation webhook call with a login page and break
    /// them outright, since a webhook caller can't complete an interactive
    /// login.
    /// </summary>
    public Task CreateAccessWebhookBypassAsync(
        string accountId, string appId, CancellationToken ct = default)
    {
        var body = new
        {
            name = "HaWinServer - webhook bypass",
            @decision = "bypass",
            include = new object[] { new { everyone = new { } } },
        };
        return SendAsync<JsonElement>(HttpMethod.Post, $"/accounts/{accountId}/access/apps/{appId}/policies", body, ct);
    }

    public Task DeleteAccessAppAsync(string accountId, string appId, CancellationToken ct = default) =>
        SendAsync<JsonElement>(HttpMethod.Delete, $"/accounts/{accountId}/access/apps/{appId}", body: null, ct);

    // ---- envelope plumbing --------------------------------------------------------

    private async Task<List<T>> GetListAsync<T>(string path, CancellationToken ct)
    {
        var element = await SendAsync<JsonElement>(HttpMethod.Get, path, body: null, ct);
        return JsonSerializer.Deserialize<List<T>>(element.GetRawText(), JsonOptions) ?? new List<T>();
    }

    private async Task<T> PostAsync<T>(string path, object body, CancellationToken ct)
    {
        var element = await SendAsync<JsonElement>(HttpMethod.Post, path, body, ct);
        return JsonSerializer.Deserialize<T>(element.GetRawText(), JsonOptions)!;
    }

    private async Task<T> PatchAsync<T>(string path, object body, CancellationToken ct)
    {
        var element = await SendAsync<JsonElement>(HttpMethod.Patch, path, body, ct);
        return JsonSerializer.Deserialize<T>(element.GetRawText(), JsonOptions)!;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Sends one request and unwraps Cloudflare's {success, errors[], result}
    /// envelope. TResult is the shape of "result" - a JsonElement when the
    /// caller will deserialize it further (list endpoints), a plain string
    /// for the token endpoint (Cloudflare returns "result": "<token>").
    /// </summary>
    private async Task<TResult> SendAsync<TResult>(
        HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, BaseUrl + path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        using var response = await _client.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);

        Envelope<TResult> envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<Envelope<TResult>>(text, JsonOptions)
                ?? throw new CloudflareApiException("Cloudflare returned an empty response.");
        }
        catch (JsonException)
        {
            throw new CloudflareApiException(
                $"Cloudflare returned an unexpected response (HTTP {(int)response.StatusCode}): " +
                Truncate(text, 300));
        }

        if (!envelope.Success)
        {
            var messages = envelope.Errors is { Count: > 0 }
                ? string.Join("; ", envelope.Errors.Select(e => $"{e.Message} (code {e.Code})"))
                : $"HTTP {(int)response.StatusCode}";
            throw new CloudflareApiException(messages);
        }

        return envelope.Result;
    }

    private static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..maxLength] + "...";

    // ---- DTOs: only the fields this app reads -------------------------------------

    private sealed record Envelope<T>(
        [property: JsonPropertyName("success")] bool Success,
        [property: JsonPropertyName("result")] T Result,
        [property: JsonPropertyName("errors")] List<ApiError>? Errors);

    private sealed record ApiError(
        [property: JsonPropertyName("code")] int Code,
        [property: JsonPropertyName("message")] string Message);

    private sealed record ZoneAccountDto(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("name")] string Name);

    private sealed record ZoneDto(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("account")] ZoneAccountDto Account);

    private sealed record TunnelDto(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("name")] string Name);

    private sealed record DnsRecordDto(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("content")] string Content);

    private sealed record AccessAppDto(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("domain")] string Domain);
}
