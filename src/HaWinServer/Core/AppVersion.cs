using System.Reflection;

namespace HaWinServer.Core;

/// <summary>
/// This app's own version, and the reduced semver comparison the update
/// checker needs. Distinct from <see cref="UpdateChecker"/>, which is about
/// Home Assistant's calendar versioning (always three numeric segments) -
/// this app's own tags carry an optional "-beta.N" suffix for the beta
/// channel, which System.Version cannot parse at all.
/// </summary>
public static class AppVersion
{
    /// <summary>
    /// A local build (no -p:InformationalVersion passed) reports this exact
    /// string - see HaWinServer.csproj. Never offered a self-update: someone
    /// running a debug/dev build should not have it silently overwritten by
    /// whatever the latest published release happens to be.
    /// </summary>
    public const string DevelopmentVersion = "0.0.0-dev";

    public static string Current { get; } = ReadCurrent();

    public static bool IsDevelopmentBuild => Current == DevelopmentVersion;

    private static string ReadCurrent()
    {
        var raw = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (string.IsNullOrWhiteSpace(raw)) return DevelopmentVersion;

        // dotnet publish appends "+<git-sha>" to InformationalVersion when
        // SourceRevisionId is available - not part of the version users
        // compare against.
        var plusIndex = raw.IndexOf('+');
        return plusIndex >= 0 ? raw[..plusIndex] : raw;
    }

    /// <summary>
    /// Compares two versions of the form "MAJOR.MINOR.PATCH[-pre]". A stable
    /// version always outranks a prerelease with the same numeric core
    /// (matching semver precedence rules); between two prereleases, dot-
    /// separated identifiers compare numerically when both sides are
    /// numeric, ordinally otherwise.
    /// </summary>
    public static int Compare(string a, string b)
    {
        var (coreA, preA) = Split(a);
        var (coreB, preB) = Split(b);

        var coreCompare = CompareCore(coreA, coreB);
        if (coreCompare != 0) return coreCompare;

        if (preA is null && preB is null) return 0;
        if (preA is null) return 1; // stable > prerelease
        if (preB is null) return -1;

        return ComparePrerelease(preA, preB);
    }

    private static (int[] Core, string? Prerelease) Split(string version)
    {
        var dashIndex = version.IndexOf('-');
        var corePart = dashIndex >= 0 ? version[..dashIndex] : version;
        var prerelease = dashIndex >= 0 ? version[(dashIndex + 1)..] : null;

        var segments = corePart.Split('.')
            .Select(s => int.TryParse(s, out var n) ? n : 0)
            .ToArray();

        var core = new int[3];
        for (var i = 0; i < 3 && i < segments.Length; i++)
        {
            core[i] = segments[i];
        }

        return (core, prerelease);
    }

    private static int CompareCore(int[] a, int[] b)
    {
        for (var i = 0; i < 3; i++)
        {
            var cmp = a[i].CompareTo(b[i]);
            if (cmp != 0) return cmp;
        }
        return 0;
    }

    private static int ComparePrerelease(string a, string b)
    {
        var partsA = a.Split('.');
        var partsB = b.Split('.');

        var length = Math.Max(partsA.Length, partsB.Length);
        for (var i = 0; i < length; i++)
        {
            if (i >= partsA.Length) return -1;
            if (i >= partsB.Length) return 1;

            var cmp = CompareIdentifier(partsA[i], partsB[i]);
            if (cmp != 0) return cmp;
        }

        return 0;
    }

    private static int CompareIdentifier(string a, string b)
    {
        if (int.TryParse(a, out var na) && int.TryParse(b, out var nb))
        {
            return na.CompareTo(nb);
        }

        return string.CompareOrdinal(a, b);
    }
}
