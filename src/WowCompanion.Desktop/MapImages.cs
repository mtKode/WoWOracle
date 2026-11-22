using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WowCompanion.Core;

namespace WowCompanion.Desktop;

/// <summary>
/// Zone map pictures, put together from the game's own map tiles: each tile is a BLP file downloaded from wago.tools
/// by file id, decoded, and placed in the grid; the finished map is saved as a PNG under
/// %LOCALAPPDATA%\WowCompanion\maps and reused from disk after that. wago.tools limits how fast files may be fetched
/// (HTTP 429), so each tile is kept on disk as soon as it arrives (maps	iles) and a refused one is asked for again
/// after the wait the server names: a map that didn't finish needs only its missing tiles next time.
/// </summary>
public static class MapImages
{
    /// <summary>wago.tools serves game files by file id (FileDataID).</summary>
    private const string FileUrl = "https://wago.tools/api/casc/{0}?download";

    private static string? _folder;
    private static HttpClient? _http;
    private static readonly ConcurrentDictionary<string, Lazy<Task<ImageSource?>>> Loaded = new();
    private static readonly SemaphoreSlim Downloads = new(2);
    private const int Attempts = 5;

    public static void Initialize(string appRoot, HttpClient http)
    {
        _folder = Path.Combine(appRoot, "maps");
        _http = http;
    }

    /// <summary>The whole map picture, or null when its tiles can't be had.</summary>
    public static Task<ImageSource?> GetAsync(ZoneMap map)
    {
        // The tile ids are part of the name, so a new picture in a later build is fetched again.
        var files = map.Tiles.Sum(t => (long)t.FileId) + map.Overlays.Sum(o => o.Tiles.Sum(t => (long)t.FileId));
        var key = $"{map.UiMapId}-{files % 1_000_000_007}-explored";
        var task = Loaded.GetOrAdd(key, _ => new Lazy<Task<ImageSource?>>(() => Task.Run(() => LoadAsync(map, key)))).Value;
        // A failed load (offline) may work later.
        _ = task.ContinueWith(t => { if (t.IsFaulted || t.Result is null) Loaded.TryRemove(key, out _); }, TaskScheduler.Default);
        return task;
    }

    private static async Task<ImageSource?> LoadAsync(ZoneMap map, string key)
    {
        if (_folder is null || _http is null || map.Tiles.Count == 0) return null;
        var path = Path.Combine(_folder, key + ".png");
        try
        {
            if (File.Exists(path)) return Open(path);

            var cols = map.Tiles.Max(t => t.Col) + 1;
            var rows = map.Tiles.Max(t => t.Row) + 1;
            var width = map.Width > 0 ? Math.Min(map.Width, cols * map.TileWidth) : cols * map.TileWidth;
            var height = map.Height > 0 ? Math.Min(map.Height, rows * map.TileHeight) : rows * map.TileHeight;
            var canvas = new byte[width * height * 4];

            var decoded = await Task.WhenAll(map.Tiles.Select(async tile => (Tile: tile, Image: await TileAsync(tile.FileId))));
            if (decoded.Any(d => d.Image is null)) return null;

            foreach (var (tile, image) in decoded)
            {
                var left = tile.Col * map.TileWidth;
                var top = tile.Row * map.TileHeight;
                for (var y = 0; y < image!.Height && top + y < height; y++)
                {
                    var copy = Math.Min(image.Width, width - left);
                    if (copy <= 0) break;
                    Buffer.BlockCopy(image.Bgra, y * image.Width * 4, canvas, ((top + y) * width + left) * 4, copy * 4);
                }
            }

            // Explored areas over the base (fully discovered map). A missing piece just stays unexplored.
            var pieces = map.Overlays.SelectMany(o => o.Tiles.Select(t => (Overlay: o, Tile: t))).ToList();
            var overlayImages = await Task.WhenAll(pieces.Select(async p => (p.Overlay, p.Tile, Image: await TileAsync(p.Tile.FileId))));
            var complete = true;
            foreach (var (overlay, tile, image) in overlayImages)
            {
                if (image is null)
                {
                    complete = false;
                    continue;
                }
                Blend(canvas, width, height, image, overlay.OffsetX + tile.Col * 256, overlay.OffsetY + tile.Row * 256,
                    Math.Min(image.Width, overlay.Width - tile.Col * 256), Math.Min(image.Height, overlay.Height - tile.Row * 256));
            }

            var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, canvas, width * 4);
            bitmap.Freeze();
            if (!complete) return bitmap; // saved next time, when every piece came through
            Directory.CreateDirectory(_folder);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            var temp = path + ".tmp";
            await using (var file = File.Create(temp)) encoder.Save(file);
            File.Move(temp, path, overwrite: true);
            // The picture is kept now: its tiles were only for getting there.
            foreach (var id in map.Tiles.Select(t => t.FileId).Concat(map.Overlays.SelectMany(o => o.Tiles.Select(t => t.FileId))))
                try { File.Delete(Path.Combine(_folder, "tiles", id + ".blp")); } catch (Exception) { }
            return bitmap;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Draws a tile with its alpha over the canvas at (left, top), at most w × h of it.</summary>
    private static void Blend(byte[] canvas, int width, int height, Blp.Image image, int left, int top, int w, int h)
    {
        for (var y = 0; y < h; y++)
        {
            var cy = top + y;
            if (cy < 0 || cy >= height) continue;
            for (var x = 0; x < w; x++)
            {
                var cx = left + x;
                if (cx < 0 || cx >= width) continue;
                var s = (y * image.Width + x) * 4;
                var d = (cy * width + cx) * 4;
                var a = image.Bgra[s + 3];
                if (a == 0) continue;
                for (var c = 0; c < 3; c++)
                    canvas[d + c] = (byte)((image.Bgra[s + c] * a + canvas[d + c] * (255 - a)) / 255);
                canvas[d + 3] = 255;
            }
        }
    }

    private static async Task<Blp.Image?> TileAsync(int fileId)
    {
        var cached = Path.Combine(_folder!, "tiles", fileId + ".blp");
        try
        {
            if (File.Exists(cached) && Blp.Decode(await File.ReadAllBytesAsync(cached)) is { } kept) return kept;
        }
        catch (Exception) { } // unreadable: fetch it again

        for (var attempt = 1; attempt <= Attempts; attempt++)
        {
            TimeSpan? wait = null;
            await Downloads.WaitAsync();
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)); // a stuck tile mustn't hold a slot
                using var response = await _http!.GetAsync(string.Format(FileUrl, fileId), timeout.Token);
                if ((int)response.StatusCode == 429 || (int)response.StatusCode >= 500)
                {
                    // Too many requests (or a hiccup): wait as long as the server asks, else 1, 2, 4, 8 s.
                    wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Pow(2, attempt - 1));
                }
                else
                {
                    if (!response.IsSuccessStatusCode) return null; // not there: no use asking again
                    var bytes = await response.Content.ReadAsByteArrayAsync(timeout.Token);
                    var image = Blp.Decode(bytes);
                    if (image is not null) Keep(cached, bytes);
                    return image;
                }
            }
            catch (Exception)
            {
                wait = TimeSpan.FromSeconds(Math.Pow(2, attempt - 1)); // offline or timed out
            }
            finally
            {
                Downloads.Release();
            }
            if (attempt < Attempts) await Task.Delay(wait.Value < TimeSpan.FromSeconds(20) ? wait.Value : TimeSpan.FromSeconds(20));
        }
        return null;
    }

    private static void Keep(string path, byte[] bytes)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception) { } // only a cache
    }

    private static ImageSource Open(string path)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(path);
        image.EndInit();
        image.Freeze();
        return image;
    }
}
