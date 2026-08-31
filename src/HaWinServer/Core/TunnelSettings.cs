namespace HaWinServer.Core;

/// <summary>
/// One machine's Cloudflare Tunnel: a single cloudflared connector process
/// carries every instance's Public Hostname as a separate ingress rule (see
/// CloudflareApi.SyncIngressAsync) - one tunnel, not one per instance, since
/// WSL/podman/the image store are already machine-level shared resources
/// (see WslManager) and the tunnel is exactly as machine-level.
///
/// Deliberately holds no secret: the tunnel run token lives in SecretStore
/// (DPAPI-protected, separate file), keeping settings.json free of secrets -
/// see Settings.cs's own "settings.json holds no secrets" rule.
/// </summary>
public sealed class TunnelSettings
{
    public bool Enabled { get; set; }

    public string? AccountId { get; set; }

    /// <summary>Cloudflare's tunnel id (cfd_tunnel.id) - cached so menu/status don't need an API round trip to display it.</summary>
    public string? TunnelId { get; set; }

    public string? TunnelName { get; set; }

    /// <summary>The Cloudflare zone (e.g. "example.com") new hostnames are created under.</summary>
    public string? Zone { get; set; }

    /// <summary>"auto" lets cloudflared pick (QUIC, falling back on its own); "http2" forces the TCP fallback for networks that block outbound UDP.</summary>
    public string Protocol { get; set; } = "auto";

    /// <summary>Local-only metrics/health port cloudflared is told to listen on - see TunnelSupervisor's readiness probe.</summary>
    public int MetricsPort { get; set; } = 20241;
}
