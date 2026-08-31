namespace HaWinServer.Core;

/// <summary>
/// Builds a Public Hostname from an instance name and a Cloudflare zone:
/// always a single-level subdomain of the zone ("{instance}.{zone}").
///
/// Deliberately the only scheme offered - a two-level subdomain
/// ("{instance}.{machine}.{zone}") or a wildcard covering one
/// ("*.{machine}.{zone}") both sit outside what Cloudflare's free Universal
/// SSL certificate covers (the zone apex plus exactly one wildcard level,
/// "*.{zone}"), which produced a real, confirmed SSL error for every visitor
/// - not a configuration mistake, a hard platform limit on plans without
/// Advanced Certificate Manager. A single-level subdomain is the one shape
/// guaranteed to work on every Cloudflare plan.
///
/// Slugging logic intentionally mirrors Settings.MakeUniqueId's (lowercase,
/// [a-z0-9-] only, collapsed dashes) but is kept separate: a Public Hostname
/// only needs to be DNS-safe, not unique against other instance ids the way
/// MakeUniqueId's result must be.
/// </summary>
public static class HostnameSlug
{
    public static string Slugify(string name)
    {
        var slug = new string(name.ToLowerInvariant()
            .Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-')
            .ToArray())
            .Trim('-');

        while (slug.Contains("--", StringComparison.Ordinal))
        {
            slug = slug.Replace("--", "-", StringComparison.Ordinal);
        }

        return slug.Length == 0 ? "instance" : slug;
    }

    public static string BuildHostname(string instanceName, string zone) =>
        $"{Slugify(instanceName)}.{zone}";
}
