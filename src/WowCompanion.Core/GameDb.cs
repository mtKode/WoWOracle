namespace WowCompanion.Core;

/// <summary>
/// Game databases are versioned files (game-s3-1.60.1.70245.sqlite). A new build is written next to the old
/// one, so the app never swaps a file that is open. The highest version wins. The schema number is part of
/// the name: bump SchemaVersion when the tables change and the next start rebuilds from the cached CSVs.
/// </summary>
public static class GameDb
{
    public const int SchemaVersion = 28;

    private static readonly string Prefix = $"game-s{SchemaVersion}-";
    private const string AnyPrefix = "game-";
    private const string Extension = ".sqlite";

    public static string PathFor(string dataDir, string version) =>
        Path.Combine(dataDir, Prefix + version + Extension);

    public static (string Path, string Version)? FindCurrent(string dataDir)
    {
        if (!Directory.Exists(dataDir)) return null;

        (string Path, string Version, Version Parsed)? best = null;
        foreach (var path in Directory.EnumerateFiles(dataDir, Prefix + "*" + Extension))
        {
            var text = VersionText(path);
            if (!Version.TryParse(text, out var parsed)) continue;
            if (best is null || parsed > best.Value.Parsed) best = (path, text, parsed);
        }
        return best is null ? null : (best.Value.Path, best.Value.Version);
    }

    public static bool IsNewer(string candidate, string? current) =>
        current is null
        || (Version.TryParse(candidate, out var c) && Version.TryParse(current, out var k) && c > k);

    /// <summary>Deletes every game database except the one to keep, including ones from older schemas.</summary>
    public static void DeleteOlder(string dataDir, string keepVersion)
    {
        var keep = PathFor(dataDir, keepVersion);
        foreach (var path in Directory.EnumerateFiles(dataDir, AnyPrefix + "*" + Extension))
        {
            if (string.Equals(path, keep, StringComparison.OrdinalIgnoreCase)) continue;
            try { File.Delete(path); }
            catch (IOException) { /* still open; the next start cleans it up */ }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static string VersionText(string path) =>
        Path.GetFileNameWithoutExtension(path)[Prefix.Length..];
}
