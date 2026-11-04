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
/// %LOCALAPPDATA%\WowCompanion\maps and reused from disk after that.
/// </summary>
public static class MapImages
{
    /// <summary>wago.tools serves game files by file id (FileDataID).</summary>
    private const string FileUrl = "https://wago.tools/api/casc/{0}?download";

    private static string? _folder;
    private static HttpClient? _http;
    private static readonly ConcurrentDictionary<string, Lazy<Task<ImageSource?>>> Loaded = new();
    private static readonly SemaphoreSlim Downloads = new(4);

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
        await Downloads.WaitAsync();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)); // a stuck tile mustn't hold a slot
            var bytes = await _http!.GetByteArrayAsync(string.Format(FileUrl, fileId), timeout.Token);
            return Blp.Decode(bytes);
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
