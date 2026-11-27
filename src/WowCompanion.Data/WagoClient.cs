using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WowCompanion.Data;

public sealed record BuildInfo(string Product, string Version);

/// <summary>Talks to wago.tools. Downloads are cached by version, so each table is fetched once per build.</summary>
public sealed class WagoClient(HttpClient http, DataSourceOptions options)
{
    // The builds list is one request for every product: kept for a few minutes so the Forever and Classic Era
    // look-ups of one update share it.
    private (DateTime At, IReadOnlyList<BuildInfo> Builds)? _builds;

    public async Task<IReadOnlyList<BuildInfo>> GetBuildsAsync(CancellationToken ct = default)
    {
        if (_builds is { } cached && DateTime.UtcNow - cached.At < TimeSpan.FromMinutes(10)) return cached.Builds;
        // A short limit of its own: the shared client allows minutes for table downloads, and start-up waits on this.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        await using var stream = await http.GetStreamAsync(options.BuildsUrl, timeout.Token);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token);
        var builds = new List<BuildInfo>();
        Collect(document.RootElement, null, builds);
        _builds = (DateTime.UtcNow, builds);
        return builds;
    }

    public async Task<BuildInfo?> GetLatestBuildAsync(CancellationToken ct = default) =>
        await GetLatestBuildAsync(options.Product, options.VersionPrefix, ct);

    public async Task<BuildInfo?> GetLatestBuildAsync(string product, string? versionPrefix, CancellationToken ct = default)
    {
        var all = await GetBuildsAsync(ct);
        return all
            .Where(b => b.Product.Equals(product, StringComparison.OrdinalIgnoreCase))
            .Where(b => versionPrefix is null || b.Version.StartsWith(versionPrefix, StringComparison.Ordinal))
            .OrderByDescending(b => ParseVersion(b.Version))
            .FirstOrDefault();
    }

    public async Task<string> DownloadTableAsync(string table, string version, string cacheDir, CancellationToken ct = default)
    {
        var directory = Path.Combine(cacheDir, version);
        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, table + ".csv");
        if (File.Exists(path)) return path;

        var url = options.CsvUrlTemplate.Replace("{table}", table).Replace("{version}", version);
        var temp = path + ".tmp";
        // wago.tools sometimes refuses a request in a burst of downloads (rate limit, timeout): try again before giving up.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using (var source = await http.GetStreamAsync(url, ct))
                await using (var target = File.Create(temp))
                {
                    await source.CopyToAsync(target, ct);
                }
                File.Move(temp, path, overwrite: true);
                return path;
            }
            catch (Exception ex) when (attempt < 4 && !ct.IsCancellationRequested
                                       && ex is HttpRequestException or IOException or TaskCanceledException)
            {
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt * attempt), ct);
            }
        }
    }

    /// <summary>
    /// Finds the *.sql.gz in a GitHub folder listing (contents API) and downloads it once into the folder.
    /// Returns the local path. A newer dump (new file name) is downloaded next time a build runs.
    /// </summary>
    public async Task<string?> DownloadSourcesDumpAsync(string listingUrl, string folder, CancellationToken ct = default)
    {
        Directory.CreateDirectory(folder);
        string? name = null, url = null;
        await using (var stream = await http.GetStreamAsync(listingUrl, ct))
        using (var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct))
        {
            foreach (var entry in document.RootElement.EnumerateArray())
            {
                var entryName = entry.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (entryName is null || !entryName.EndsWith(".sql.gz", StringComparison.OrdinalIgnoreCase)) continue;
                name = entryName;
                url = entry.TryGetProperty("download_url", out var u) ? u.GetString() : null;
                break;
            }
        }
        if (name is null || url is null) return null;

        var path = Path.Combine(folder, name);
        if (File.Exists(path)) return path;
        var temp = path + ".tmp";
        await using (var source = await http.GetStreamAsync(url, ct))
        await using (var target = File.Create(temp))
        {
            await source.CopyToAsync(target, ct);
        }
        File.Move(temp, path, overwrite: true);
        // A newer dump replaces the old one (they're ~10 MB each).
        foreach (var old in Directory.EnumerateFiles(folder, "*.sql.gz"))
        {
            if (string.Equals(old, path, StringComparison.OrdinalIgnoreCase)) continue;
            try { File.Delete(old); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return path;
    }

    /// <summary>
    /// The VMaNGOS world database snapshot (db-&lt;commit&gt;.zip, ~30 MB) boiled down to its object spawns: one
    /// "id map x y" line each (SourceImporter.NodeSpawnsFile), ~1.5 MB, kept in <paramref name="folder"/>. The zip is
    /// not kept. Returns the cached file when the snapshot hasn't changed; null when the release has no database.
    /// </summary>
    public async Task<string?> DownloadNodeSpawnsAsync(string releaseUrl, string folder, CancellationToken ct = default)
    {
        Directory.CreateDirectory(folder);
        string? name = null, url = null;
        await using (var stream = await http.GetStreamAsync(releaseUrl, ct))
        using (var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct))
        {
            if (document.RootElement.TryGetProperty("assets", out var assets))
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var assetName = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
                    if (assetName is null || !assetName.StartsWith("db-", StringComparison.OrdinalIgnoreCase)
                        || !assetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || assetName.Contains("sqlite", StringComparison.OrdinalIgnoreCase)) continue;
                    name = assetName;
                    url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    break;
                }
            }
        }
        if (name is null || url is null) return null;

        var path = Path.Combine(folder, SourceImporter.NodeSpawnsPrefix + Path.GetFileNameWithoutExtension(name) + ".tsv");
        if (File.Exists(path)) return path;
        var zip = Path.Combine(folder, name + ".tmp");
        try
        {
            await using (var source = await http.GetStreamAsync(url, ct))
            await using (var target = File.Create(zip))
            {
                await source.CopyToAsync(target, ct);
            }
            var temp = path + ".tmp";
            using (var archive = ZipFile.OpenRead(zip))
            {
                var entry = archive.Entries.FirstOrDefault(e => e.Name.Equals("mangos.sql", StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidDataException("The VMaNGOS snapshot has no mangos.sql.");
                using var reader = new StreamReader(entry.Open());
                using var writer = new StreamWriter(temp);
                await WriteObjectSpawnsAsync(reader, writer, ct);
            }
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(zip); } catch (IOException) { }
        }
        foreach (var old in Directory.EnumerateFiles(folder, SourceImporter.NodeSpawnsPrefix + "*.tsv"))
        {
            if (string.Equals(old, path, StringComparison.OrdinalIgnoreCase)) continue;
            try { File.Delete(old); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return path;
    }

    // `gameobject` rows: (guid, id, map, x, y, z, orientation, rotation0-3, spawn times, anim, state, flags, visibility,
    // patch_min, patch_max). Rows that end before the last patch (patch_max < 10) are left out.
    private static readonly Regex ObjectRow = new(@"\((\d+),(\d+),(\d+),([-\d.eE+]+),([-\d.eE+]+),([^()]*)\)", RegexOptions.Compiled);

    internal static async Task WriteObjectSpawnsAsync(TextReader reader, TextWriter writer, CancellationToken ct = default)
    {
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (!line.StartsWith("INSERT INTO `gameobject` ", StringComparison.Ordinal)) continue;
            foreach (Match m in ObjectRow.Matches(line))
            {
                var rest = m.Groups[6].Value;
                var last = rest.LastIndexOf(',');
                if (last >= 0 && int.TryParse(rest[(last + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var patchMax) && patchMax < 10) continue;
                await writer.WriteLineAsync($"{m.Groups[2].Value}\t{m.Groups[3].Value}\t{m.Groups[4].Value}\t{m.Groups[5].Value}");
            }
        }
    }

    public static Version ParseVersion(string text) =>
        Version.TryParse(text, out var parsed) ? parsed : new Version(0, 0);

    // Accepts either { "<product>": [ { "version": ... } ] } or a flat array of { "product", "version" }.
    private static void Collect(JsonElement element, string? product, List<BuildInfo> into)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (element.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.String)
                {
                    var name = element.TryGetProperty("product", out var p) && p.ValueKind == JsonValueKind.String
                        ? p.GetString() : product;
                    if (name is not null) into.Add(new BuildInfo(name, version.GetString()!));
                }
                else
                {
                    foreach (var property in element.EnumerateObject()) Collect(property.Value, property.Name, into);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray()) Collect(item, product, into);
                break;
        }
    }
}
