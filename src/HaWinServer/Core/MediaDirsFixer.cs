using System.Text.RegularExpressions;

namespace HaWinServer.Core;

/// <summary>
/// Strips <c>homeassistant.media_dirs</c> entries whose path doesn't exist
/// inside the container before starting an instance - confirmed on a real
/// restored-backup instance: a path from the machine the backup came from
/// (e.g. a macOS "/Users/.../Music" folder) obviously doesn't exist inside
/// this Linux container, and Home Assistant refuses to start at all over it
/// ("Not a directory for dictionary value 'media_dirs->media', got
/// '/Users/name/Music'"), with no way to click past it - the app has to fix
/// the file to get the instance running again.
///
/// Same "no YAML parser" constraint and same narrow, line-based approach as
/// HaConfigPatcher: this only ever touches the flat "key: /path" entries
/// directly under homeassistant.media_dirs, and only DELETES entries whose
/// path is confirmed missing (see WslManager.CheckDirectoriesExistAsync) -
/// it never invents or rewrites a path, and always backs up first.
/// </summary>
public static class MediaDirsFixer
{
    /// <summary>Anchors a line as a plausible POSIX absolute path with no shell metacharacters - the same guard class as UsbDevices' device paths, applied here before ever interpolating a path into a script.</summary>
    private static bool IsPlausibleContainerPath(string path) =>
        path.StartsWith('/') && path.IndexOfAny("\"'`$\\;&|<>(){}\n\r".ToCharArray()) < 0;

    private static readonly Regex HomeAssistantKeyPattern = new(@"^homeassistant:\s*$", RegexOptions.Compiled);
    private static readonly Regex MediaDirsKeyPattern = new(@"^(\s+)media_dirs:\s*$", RegexOptions.Compiled);
    private static readonly Regex EntryPattern = new(@"^\s+([A-Za-z0-9_-]+):\s*(.+?)\s*$", RegexOptions.Compiled);

    public sealed record MediaDirEntry(string Key, string Path);

    /// <summary>Finds the homeassistant: -> media_dirs: sub-block and returns each "key: path" pair in it, in file order.</summary>
    public static IReadOnlyList<MediaDirEntry> ExtractEntries(string yamlContent)
    {
        var (lines, homeIndex, homeEnd, mediaDirsIndex, mediaDirsIndent) = LocateMediaDirsBlock(yamlContent);
        if (mediaDirsIndex < 0) return Array.Empty<MediaDirEntry>();

        var entries = new List<MediaDirEntry>();
        for (var i = mediaDirsIndex + 1; i < homeEnd; i++)
        {
            var line = lines[i];
            if (line.Trim().Length == 0) continue;

            var indentLength = line.Length - line.TrimStart(' ').Length;
            if (indentLength <= mediaDirsIndent) break; // end of the media_dirs sub-block

            var m = EntryPattern.Match(line);
            if (m.Success)
            {
                entries.Add(new MediaDirEntry(m.Groups[1].Value, m.Groups[2].Value.Trim('"', '\'')));
            }
        }

        return entries;
    }

    /// <summary>Removes the named entries from media_dirs (and the media_dirs: key itself if that empties it), preserving every other line untouched.</summary>
    public static string RemoveEntries(string yamlContent, ISet<string> keysToRemove)
    {
        if (keysToRemove.Count == 0) return yamlContent;

        var hadCrLf = yamlContent.Contains("\r\n", StringComparison.Ordinal);
        var (lines, _, homeEnd, mediaDirsIndex, mediaDirsIndent) = LocateMediaDirsBlock(yamlContent);
        if (mediaDirsIndex < 0) return yamlContent;

        var blockEnd = homeEnd;
        for (var i = mediaDirsIndex + 1; i < homeEnd; i++)
        {
            if (lines[i].Trim().Length == 0) continue;
            var indentLength = lines[i].Length - lines[i].TrimStart(' ').Length;
            if (indentLength <= mediaDirsIndent) { blockEnd = i; break; }
        }

        var linesToDelete = new List<int>();
        var remainingEntries = 0;
        for (var i = mediaDirsIndex + 1; i < blockEnd; i++)
        {
            var m = EntryPattern.Match(lines[i]);
            if (!m.Success) continue;

            if (keysToRemove.Contains(m.Groups[1].Value))
            {
                linesToDelete.Add(i);
            }
            else
            {
                remainingEntries++;
            }
        }

        if (remainingEntries == 0)
        {
            linesToDelete.Add(mediaDirsIndex); // the whole media_dirs: key is now empty
        }

        var result = lines.ToList();
        foreach (var index in linesToDelete.OrderByDescending(i => i))
        {
            result.RemoveAt(index);
        }

        var joined = string.Join("\n", result);
        return hadCrLf ? joined.Replace("\n", "\r\n") : joined;
    }

    private static (string[] Lines, int HomeIndex, int HomeEnd, int MediaDirsIndex, int MediaDirsIndent) LocateMediaDirsBlock(
        string yamlContent)
    {
        var lines = yamlContent.Replace("\r\n", "\n").Split('\n');

        var homeIndex = Array.FindIndex(lines, l => HomeAssistantKeyPattern.IsMatch(l));
        if (homeIndex < 0) return (lines, -1, -1, -1, 0);

        var homeEnd = lines.Length;
        for (var i = homeIndex + 1; i < lines.Length; i++)
        {
            if (lines[i].Length > 0 && !char.IsWhiteSpace(lines[i][0])) { homeEnd = i; break; }
        }

        for (var i = homeIndex + 1; i < homeEnd; i++)
        {
            var m = MediaDirsKeyPattern.Match(lines[i]);
            if (m.Success) return (lines, homeIndex, homeEnd, i, m.Groups[1].Value.Length);
        }

        return (lines, homeIndex, homeEnd, -1, 0);
    }

    /// <summary>
    /// Checks every media_dirs entry against the container's actual
    /// filesystem and strips the ones that don't exist, backing up
    /// configuration.yaml first. No-op (and no podman call at all) when the
    /// file has no media_dirs block, which is the overwhelming majority of
    /// instances.
    /// </summary>
    public static async Task<IReadOnlyList<MediaDirEntry>> RemoveUnreachableEntriesAsync(
        InstanceSettings instance, Action<string>? log = null, CancellationToken ct = default)
    {
        var path = Path.Combine(instance.WindowsConfigDir, "configuration.yaml");
        if (!File.Exists(path)) return Array.Empty<MediaDirEntry>();

        var content = await File.ReadAllTextAsync(path, ct);
        if (!content.Contains("media_dirs:", StringComparison.Ordinal)) return Array.Empty<MediaDirEntry>();

        var entries = ExtractEntries(content);
        if (entries.Count == 0) return Array.Empty<MediaDirEntry>();

        var checkable = entries.Where(e => IsPlausibleContainerPath(e.Path)).Select(e => e.Path)
            .Distinct(StringComparer.Ordinal).ToList();
        if (checkable.Count == 0) return Array.Empty<MediaDirEntry>();

        var existence = await WslManager.CheckDirectoriesExistAsync(instance, checkable, ct);
        var missing = entries.Where(e => existence.TryGetValue(e.Path, out var exists) && !exists).ToList();
        if (missing.Count == 0) return Array.Empty<MediaDirEntry>();

        var backupPath = path + $".hawinserver-{DateTime.Now:yyyyMMdd-HHmmss}.bak";
        File.Copy(path, backupPath, overwrite: false);
        log?.Invoke($"Backed up configuration.yaml to {Path.GetFileName(backupPath)}");

        var newContent = RemoveEntries(content, missing.Select(e => e.Key).ToHashSet(StringComparer.Ordinal));
        await File.WriteAllTextAsync(path, newContent, ct);

        foreach (var entry in missing)
        {
            log?.Invoke($"Removed unreachable media_dirs entry \"{entry.Key}\": {entry.Path}");
        }

        return missing;
    }
}
