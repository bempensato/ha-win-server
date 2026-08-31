using System.Formats.Tar;
using System.Text.Json;

namespace HaWinServer.Core;

/// <summary>What a Home Assistant backup says about itself, read from the backup.json at the root of the archive.</summary>
public sealed record BackupMetadata(
    string? Name,
    DateTimeOffset? Date,
    string? HomeAssistantVersion,
    bool IsProtected,
    bool ExcludesDatabase);

/// <summary>
/// Reads the metadata Home Assistant puts at the root of every backup, so the
/// app can answer three questions BEFORE staging a restore that would
/// otherwise fail minutes later, inside the container, in a log nobody is
/// watching:
///
/// - Is it encrypted? If not, don't ask for a password at all.
/// - Which Home Assistant version made it? HA refuses to restore a backup from
///   a NEWER version than the one running (backup_restore.py raises "You need
///   at least Home Assistant version X"), and instances here are pinned to a
///   version, so that is a check this app can make up front.
/// - Does it contain the database? A "slim" backup doesn't, and that is worth
///   saying out loud before someone expects their history back.
///
/// Schema confirmed against a real backup:
///   {"compressed": true, "date": "...", "homeassistant": {"exclude_database": true,
///    "version": "2026.2.3"}, "name": "...", "protected": false, "type": "partial", ...}
/// The entry is named "./backup.json" in practice, so the leading "./" is
/// tolerated rather than assumed away.
///
/// Uses System.Formats.Tar from the BCL - still no NuGet dependency.
/// </summary>
public static class BackupFile
{
    public static BackupMetadata? TryReadMetadata(string tarPath)
    {
        try
        {
            using var stream = File.OpenRead(tarPath);
            using var reader = new TarReader(stream);

            while (reader.GetNextEntry() is { } entry)
            {
                var name = entry.Name.StartsWith("./", StringComparison.Ordinal)
                    ? entry.Name[2..]
                    : entry.Name;

                if (!name.Equals("backup.json", StringComparison.OrdinalIgnoreCase)) continue;
                if (entry.DataStream is not { } data) return null;

                using var doc = JsonDocument.Parse(data);
                var root = doc.RootElement;

                string? version = null;
                var excludesDatabase = false;
                if (root.TryGetProperty("homeassistant", out var ha) && ha.ValueKind == JsonValueKind.Object)
                {
                    if (ha.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String)
                    {
                        version = v.GetString();
                    }
                    if (ha.TryGetProperty("exclude_database", out var ed) && ed.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    {
                        excludesDatabase = ed.GetBoolean();
                    }
                }

                return new BackupMetadata(
                    Name: root.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null,
                    Date: root.TryGetProperty("date", out var d)
                          && d.ValueKind == JsonValueKind.String
                          && DateTimeOffset.TryParse(d.GetString(), out var parsedDate)
                        ? parsedDate
                        : null,
                    HomeAssistantVersion: version,
                    IsProtected: root.TryGetProperty("protected", out var p) && p.ValueKind == JsonValueKind.True,
                    ExcludesDatabase: excludesDatabase);
            }
        }
        catch (Exception)
        {
            // Not a tar, not a Home Assistant backup, unreadable, truncated -
            // all the same answer to the caller: "can't vouch for this file".
        }

        return null;
    }

    /// <summary>
    /// True when the backup needs a newer Home Assistant than the instance is
    /// pinned to. Null when it can't be decided (an unparseable version, or an
    /// instance still on a moving tag) - the caller then lets it through
    /// rather than blocking on a guess.
    /// </summary>
    public static bool? NeedsNewerHomeAssistant(string? backupVersion, string instanceTag)
    {
        var backup = TryParseVersion(backupVersion);
        var instance = TryParseVersion(instanceTag);
        if (backup is null || instance is null) return null;
        return backup > instance;
    }

    /// <summary>
    /// Home Assistant uses calendar versioning ("2026.8.3"). Anything with a
    /// pre-release suffix is trimmed to its first three numeric parts, and
    /// anything else (e.g. the "stable" tag) is simply not comparable.
    /// </summary>
    private static Version? TryParseVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return null;

        var parts = version.Split('.');
        var trimmed = parts.Length >= 3 ? string.Join('.', parts[0], parts[1], parts[2]) : version;
        return Version.TryParse(trimmed, out var parsed) ? parsed : null;
    }
}
