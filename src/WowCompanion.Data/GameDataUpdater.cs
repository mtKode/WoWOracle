using WowCompanion.Core;

namespace WowCompanion.Data;

/// <summary>
/// Checks for a newer build and, only if there is one, downloads the tables and builds a new database file.
/// Downloads are cached (cache\{version}\*.csv, cache\sources\*.sql.gz), so a failed optional download falls back to
/// what's already on disk instead of building a poorer database, and a schema change can rebuild while offline.
/// Old cache folders are pruned after a successful build; old database files are removed by the app once it has
/// switched to the new one (GameDb.DeleteOlder), never while they may still be open.
/// </summary>
public sealed class GameDataUpdater(WagoClient wago, DataSourceOptions options, AppPaths paths)
{
    private const string FallbackNote = "fallback-version.txt";

    /// <returns>The new build version if a database was built; null if the local data was already current.</returns>
    public async Task<string?> EnsureCurrentAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var local = GameDb.FindCurrent(paths.DataDir);

        progress?.Report("Checking for new game data…");
        BuildInfo? latest;
        try
        {
            latest = await wago.GetLatestBuildAsync(ct);
        }
        catch (Exception ex) when (local is null && !ct.IsCancellationRequested
                                   && ex is HttpRequestException or TaskCanceledException or IOException)
        {
            // Offline and no database for this app version (first start after a schema change): rebuild from the
            // tables already downloaded, if there are any.
            if (NewestCompleteCache() is not { } cached) throw;
            progress?.Report($"Offline: rebuilding build {cached} from saved tables…");
            await BuildAsync(cached, CachedFallback(), NewestDump(), ct);
            return cached;
        }
        if (latest is null)
            throw new InvalidOperationException($"No build found for product '{options.Product}'. Check DataSourceOptions.Product.");

        if (!GameDb.IsNewer(latest.Version, local?.Version)) return null;

        progress?.Report($"Downloading build {latest.Version}…");
        foreach (var table in GameDatabaseBuilder.RequiredTables)
            await wago.DownloadTableAsync(table, latest.Version, paths.CacheDir, ct);

        // Nice-to-have tables: the build works without them, so a missing one is skipped, not fatal.
        foreach (var table in GameDatabaseBuilder.OptionalTables)
        {
            try { await wago.DownloadTableAsync(table, latest.Version, paths.CacheDir, ct); }
            catch (HttpRequestException) { }
        }

        // Classic Era fills items Forever ships without data. Optional: on failure the last one downloaded is used.
        string? fallbackVersion = null;
        if (options.FallbackProduct is { } fallbackProduct)
        {
            try
            {
                progress?.Report("Downloading Classic Era item data…");
                var era = await wago.GetLatestBuildAsync(fallbackProduct, null, ct);
                if (era is not null)
                {
                    foreach (var table in options.FallbackTables)
                    {
                        try { await wago.DownloadTableAsync(table, era.Version, paths.CacheDir, ct); }
                        catch (HttpRequestException) { }
                    }
                    if (File.Exists(Path.Combine(paths.CacheDir, era.Version, "ItemSparse.csv")))
                    {
                        fallbackVersion = era.Version;
                        TryWrite(Path.Combine(paths.CacheDir, FallbackNote), era.Version);
                    }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException && !ct.IsCancellationRequested) { }
            fallbackVersion ??= CachedFallback();
        }

        // Item sources (drops, vendors, quests) from the CMaNGOS Classic dump. The GitHub listing is rate limited,
        // so when it fails the dump already on disk is used (without it the database would have no sources at all).
        string? sourcesDump = null;
        if (options.SourcesListingUrl is { } listing)
        {
            try
            {
                progress?.Report("Downloading item sources (drops, vendors, quests)…");
                sourcesDump = await wago.DownloadSourcesDumpAsync(listing, SourcesDir, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException
                                           or System.Text.Json.JsonException && !ct.IsCancellationRequested) { }
            sourcesDump ??= NewestDump();
        }

        await BuildAsync(latest.Version, fallbackVersion, sourcesDump, ct);
        PruneCache(keep: [latest.Version, fallbackVersion]);
        return latest.Version;
    }

    private string SourcesDir => Path.Combine(paths.CacheDir, "sources");

    private async Task BuildAsync(string version, string? fallbackVersion, string? sourcesDump, CancellationToken ct)
    {
        var output = GameDb.PathFor(paths.DataDir, version);
        await Task.Run(
            () => GameDatabaseBuilder.Build(paths.CacheDir, version, options.Product, output, fallbackVersion, sourcesDump), ct);
    }

    /// <summary>The newest cached build that has every required table.</summary>
    private string? NewestCompleteCache()
    {
        if (!Directory.Exists(paths.CacheDir)) return null;
        return Directory.EnumerateDirectories(paths.CacheDir)
            .Where(dir => GameDatabaseBuilder.RequiredTables.All(t => File.Exists(Path.Combine(dir, t + ".csv"))))
            .Select(dir => Path.GetFileName(dir))
            .OfType<string>()
            .Where(name => Version.TryParse(name, out _))
            .OrderByDescending(name => WagoClient.ParseVersion(name))
            .FirstOrDefault();
    }

    /// <summary>The Classic Era build downloaded last time, if its item table is still on disk.</summary>
    private string? CachedFallback()
    {
        try
        {
            var note = Path.Combine(paths.CacheDir, FallbackNote);
            if (!File.Exists(note)) return null;
            var version = File.ReadAllText(note).Trim();
            return version.Length > 0 && File.Exists(Path.Combine(paths.CacheDir, version, "ItemSparse.csv")) ? version : null;
        }
        catch (IOException) { return null; }
    }

    private string? NewestDump() =>
        Directory.Exists(SourcesDir)
            ? Directory.EnumerateFiles(SourcesDir, "*.sql.gz").OrderByDescending(file => File.GetLastWriteTimeUtc(file)).FirstOrDefault()
            : null;

    /// <summary>Removes cached tables of builds that are no longer used (each build is ~20 MB of CSVs).</summary>
    private void PruneCache(IEnumerable<string?> keep)
    {
        var keepSet = keep.OfType<string>().Append("sources").ToHashSet(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(paths.CacheDir))
            {
                if (keepSet.Contains(Path.GetFileName(dir))) continue;
                try { Directory.Delete(dir, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
    }

    private static void TryWrite(string path, string text)
    {
        try { File.WriteAllText(path, text); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
