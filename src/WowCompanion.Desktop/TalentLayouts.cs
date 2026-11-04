using System.IO;
using System.Net.Http;
using WowCompanion.Core;

namespace WowCompanion.Desktop;

/// <summary>
/// The Icy Veins talent layout per class, needed only to read and write Icy Veins / Wowhead talent links. Fetched
/// only when you use those links, cached in %LOCALAPPDATA%\WowCompanion\talent-layouts and refreshed at most once a
/// week; when offline the cached copy is used, however old. Nothing else in the app talks to Icy Veins.
/// </summary>
public static class TalentLayouts
{
    private const string BaseUrl = "https://static.icy-veins.com/json/forever-talent-calculator/";
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

    private static HttpClient? _http;
    private static string _folder = "";
    private static readonly Dictionary<string, ExternalLayout> Memory = new();

    public static void Initialize(string appRoot, HttpClient http)
    {
        _http = http;
        _folder = Path.Combine(appRoot, "talent-layouts");
    }

    /// <summary>The layout, or null with a reason when neither the site nor the cache has it.</summary>
    public static async Task<(ExternalLayout? Layout, string Error)> GetAsync(string slug)
    {
        if (Memory.TryGetValue(slug, out var known)) return (known, "");
        if (_http is null) return (null, "Talent layouts aren't set up.");

        var file = Path.Combine(_folder, slug + ".json");
        var fresh = File.Exists(file) && DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < MaxAge;
        if (!fresh)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var json = await _http.GetStringAsync(BaseUrl + slug + ".json", cts.Token);
                var layout = ExternalLayout.Parse(slug, json); // only cache what parses
                Directory.CreateDirectory(_folder);
                await File.WriteAllTextAsync(file, json);
                Memory[slug] = layout;
                return (layout, "");
            }
            catch (Exception)
            {
                // fall back to whatever is cached
            }
        }

        if (File.Exists(file))
        {
            try
            {
                var layout = ExternalLayout.Parse(slug, await File.ReadAllTextAsync(file));
                Memory[slug] = layout;
                return (layout, "");
            }
            catch (Exception) { }
        }
        return (null, "Couldn't reach Icy Veins for the talent layout. Check the connection and try again.");
    }
}
