using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WowCompanion.Desktop;

/// <summary>
/// Game icons and talent tree art, taken from the game's own files (the same route as the zone maps), cached on disk
/// under %LOCALAPPDATA%\WowCompanion\icons. Every file is fetched once per PC and reused from disk after that.
///   1. The game data stores icons as file numbers (IconFileDataID). The community listfile (wowdev/wow-listfile)
///      maps numbers to paths. It is big, so it is streamed once and only the lines we need are kept
///      (icons\game-files.csv: interface/icons and interface/talentframe).
///   2. The BLP texture is downloaded from wago.tools by file number (a few at a time), decoded and saved as PNG.
/// Icons downloaded by older versions (icons\{size}\{name}.jpg) are still used, so nothing is fetched twice.
/// Nothing here blocks the UI: a missing icon just shows the empty frame.
/// </summary>
public static class IconCache
{
    // Kept for callers; every size uses the same 64 px picture (WPF scales it).
    public const string Small = "small";   // 18 px
    public const string Medium = "medium"; // 36 px
    public const string Large = "large";   // 56 px

    private const string ListfileUrl = "https://github.com/wowdev/wow-listfile/releases/latest/download/community-listfile.csv";
    private const string FileUrl = "https://wago.tools/api/casc/{0}?download";

    private sealed record GameFiles(Dictionary<int, string> Icons, Dictionary<string, int> IconIds, Dictionary<string, int> TalentFrames);

    private static HttpClient? _http;
    private static string _root = "";
    private static Lazy<Task<GameFiles>>? _files;
    private static readonly ConcurrentDictionary<string, Lazy<Task<ImageSource?>>> Loaded = new();
    private static readonly SemaphoreSlim Downloads = new(4); // gentle on wago.tools

    public static void Initialize(string appRoot, HttpClient http)
    {
        _root = Path.Combine(appRoot, "icons");
        Directory.CreateDirectory(_root);
        _http = http;
        _files = new Lazy<Task<GameFiles>>(() => Task.Run(LoadFilesAsync));
    }

    /// <summary>Makes sure the file list exists (downloads it on first run). Returns a status line.</summary>
    public static async Task<string> PrepareAsync()
    {
        if (_files is null) return "";
        var files = await _files.Value;
        return files.Icons.Count > 0 ? "" : "Icons unavailable: couldn't get the icon list. Retrying on next start.";
    }

    public static Task<ImageSource?> GetAsync(int fileId, string size = Medium)
    {
        if (fileId <= 0 || _files is null) return Task.FromResult<ImageSource?>(null);
        return Cached("icon/" + fileId, () => LoadIconAsync(fileId));
    }

    public static async Task<ImageSource?> GetByNameAsync(string name, string size = Medium)
    {
        if (_files is null || name.Length == 0) return null;
        var files = await _files.Value;
        return files.IconIds.TryGetValue(name.ToLowerInvariant(), out var id) ? await GetAsync(id, size) : null;
    }

    /// <summary>Talent tree art for a classic TalentTab (its four interface/talentframe pieces), or null.</summary>
    public static Task<ImageSource?> GetTalentArtAsync(int tabId, string background) =>
        Cached("talent/" + tabId, () => LoadTalentArtAsync(tabId, background));

    private static Task<ImageSource?> Cached(string key, Func<Task<ImageSource?>> load)
    {
        // Never faults: a locked or broken cache file just means no icon (GameIcon awaits this from an async void).
        var task = Loaded.GetOrAdd(key, _ => new Lazy<Task<ImageSource?>>(() => Task.Run(async () =>
        {
            try { return await load(); }
            catch (Exception) { return null; }
        }))).Value;
        // A failed load (offline) may work later.
        _ = task.ContinueWith(t => { if (t.Result is null) Loaded.TryRemove(key, out _); }, TaskScheduler.Default);
        return task;
    }

    // ---------------- loading ----------------

    private static async Task<ImageSource?> LoadIconAsync(int fileId)
    {
        var path = Path.Combine(_root, "files", fileId + ".png");
        if (File.Exists(path)) return Open(await File.ReadAllBytesAsync(path));

        // Downloaded by an older version: reuse it. Only then is the name list needed (on a fresh PC icons don't wait
        // for the list to download).
        var oldSizes = new[] { Large, Medium, Small }.Where(size => Directory.Exists(Path.Combine(_root, size))).ToList();
        if (oldSizes.Count > 0 && (await _files!.Value).Icons.TryGetValue(fileId, out var name))
        {
            foreach (var size in oldSizes)
            {
                var old = Path.Combine(_root, size, name + ".jpg");
                if (File.Exists(old)) return Open(await File.ReadAllBytesAsync(old));
            }
        }

        var image = await TextureAsync(fileId);
        if (image is null) return null;
        return Save(BitmapSource.Create(image.Width, image.Height, 96, 96, PixelFormats.Bgra32, null, image.Bgra, image.Width * 4), path);
    }

    private static async Task<ImageSource?> LoadTalentArtAsync(int tabId, string background)
    {
        var folder = Path.Combine(_root, "talents");
        var path = Path.Combine(folder, tabId + ".png");
        if (File.Exists(path)) return Open(await File.ReadAllBytesAsync(path));
        var old = Path.Combine(folder, tabId + ".jpg"); // from an older version
        if (File.Exists(old)) return Open(await File.ReadAllBytesAsync(old));
        if (background.Length == 0) return null;

        // Classic talent art is four pieces: a big top-left, narrow right strips and a short bottom row.
        var files = await _files!.Value;
        var pieces = new Blp.Image?[4];
        string[] parts = ["topleft", "topright", "bottomleft", "bottomright"];
        for (var i = 0; i < 4; i++)
        {
            if (!files.TalentFrames.TryGetValue($"{background.ToLowerInvariant()}-{parts[i]}", out var id)) return null;
            pieces[i] = await TextureAsync(id);
            if (pieces[i] is null) return null;
        }
        var (tl, tr, bl, br) = (pieces[0]!, pieces[1]!, pieces[2]!, pieces[3]!);
        int width = tl.Width + tr.Width, height = tl.Height + bl.Height;
        var canvas = new byte[width * height * 4];
        void Put(Blp.Image p, int left, int top)
        {
            for (var y = 0; y < p.Height && top + y < height; y++)
                Array.Copy(p.Bgra, y * p.Width * 4, canvas, ((top + y) * width + left) * 4, Math.Min(p.Width, width - left) * 4);
        }
        Put(tl, 0, 0);
        Put(tr, tl.Width, 0);
        Put(bl, 0, tl.Height);
        Put(br, tl.Width, tl.Height);
        // The game shows only the top-left part of the pieces (the rest is texture padding): 300 x 331 of 320 x 384.
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, canvas, width * 4);
        var cropped = new CroppedBitmap(bitmap, new System.Windows.Int32Rect(0, 0, Math.Min(width, 300 * width / 320), Math.Min(height, 331 * height / 384)));
        return Save(cropped, path);
    }

    private static async Task<Blp.Image?> TextureAsync(int fileId)
    {
        if (_http is null) return null;
        await Downloads.WaitAsync();
        try
        {
            // The shared client allows minutes (for the big table downloads); a stuck icon mustn't hold a slot that long.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var response = await _http.GetAsync(string.Format(FileUrl, fileId), timeout.Token);
            if (!response.IsSuccessStatusCode) return null;
            return Blp.Decode(await response.Content.ReadAsByteArrayAsync());
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            Downloads.Release();
        }
    }

    private static ImageSource? Save(BitmapSource bitmap, string path)
    {
        try
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var memory = new MemoryStream();
            encoder.Save(memory);
            var bytes = memory.ToArray();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, path, overwrite: true);
            return Open(bytes);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static ImageSource? Open(byte[] bytes)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = new MemoryStream(bytes);
            image.EndInit();
            image.Freeze(); // frozen images can cross from the worker thread to the UI
            return image;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static async Task<GameFiles> LoadFilesAsync()
    {
        var path = Path.Combine(_root, "game-files.csv");
        var files = new GameFiles(new(), new(StringComparer.OrdinalIgnoreCase), new(StringComparer.OrdinalIgnoreCase));

        if (!File.Exists(path) && _http is not null)
        {
            try
            {
                var temp = path + ".tmp";
                using (var response = await _http.GetAsync(ListfileUrl, HttpCompletionOption.ResponseHeadersRead))
                {
                    response.EnsureSuccessStatusCode();
                    await using var stream = await response.Content.ReadAsStreamAsync();
                    using var reader = new StreamReader(stream);
                    await using var writer = new StreamWriter(temp);
                    while (await reader.ReadLineAsync() is { } line)
                    {
                        // "136235;interface/icons/spell_fire_fireball02.blp"
                        var split = line.IndexOf(';');
                        if (split <= 0) continue;
                        var file = line.AsSpan(split + 1);
                        if (!file.EndsWith(".blp", StringComparison.OrdinalIgnoreCase)) continue;
                        var kind = file.StartsWith("interface/icons/", StringComparison.OrdinalIgnoreCase) ? "i"
                            : file.StartsWith("interface/talentframe/", StringComparison.OrdinalIgnoreCase) ? "t" : null;
                        if (kind is null) continue;
                        var name = Path.GetFileNameWithoutExtension(file.ToString()).ToLowerInvariant();
                        await writer.WriteLineAsync($"{line[..split]};{kind};{name}");
                    }
                }
                File.Move(temp, path, overwrite: true);
            }
            catch (Exception)
            {
                // Fall back to the older icon-only list below; try again next start.
            }
        }

        var source = File.Exists(path) ? path : Path.Combine(_root, "icon-names.csv"); // older "id;name" list
        if (!File.Exists(source)) return files;
        foreach (var line in await File.ReadAllLinesAsync(source))
        {
            var parts = line.Split(';');
            if (parts.Length < 2 || !int.TryParse(parts[0], out var id)) continue;
            var (kind, name) = parts.Length >= 3 ? (parts[1], parts[2]) : ("i", parts[1]);
            if (kind == "t") files.TalentFrames.TryAdd(name, id);
            else
            {
                files.Icons[id] = name;
                files.IconIds.TryAdd(name, id);
            }
        }
        return files;
    }
}
