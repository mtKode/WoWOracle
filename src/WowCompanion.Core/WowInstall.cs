namespace WowCompanion.Core;

/// <summary>
/// Where World of Warcraft is installed: the folder picked in the app (saved in wow-path.txt), the install path
/// Blizzard writes to the registry, then the usual folders. A WoW folder holds client folders like _retail_,
/// _classic_era_ or the Forever client; addons and saved files live per client.
/// </summary>
public static class WowInstall
{
    public const string PathFile = "wow-path.txt";

    private static readonly string[] CommonRoots =
    [
        @"C:\Program Files (x86)\World of Warcraft",
        @"C:\Program Files\World of Warcraft",
        @"C:\Games\World of Warcraft",
        @"C:\World of Warcraft",
        @"D:\World of Warcraft",
        @"D:\Games\World of Warcraft",
        @"D:\Program Files (x86)\World of Warcraft",
        @"D:\Program Files\World of Warcraft",
        @"E:\World of Warcraft",
        @"E:\Games\World of Warcraft",
    ];

    /// <summary>The folder chosen in the app, if any.</summary>
    public static string? ChosenPath(string appRoot)
    {
        var file = Path.Combine(appRoot, PathFile);
        try { return File.Exists(file) ? File.ReadAllText(file).Trim().Trim('"') : null; }
        catch (IOException) { return null; }
    }

    public static void SaveChosenPath(string appRoot, string path)
    {
        Directory.CreateDirectory(appRoot);
        File.WriteAllText(Path.Combine(appRoot, PathFile), path);
    }

    /// <summary>WoW folders that exist and hold at least one client folder, best guess first.</summary>
    public static List<string> Roots(string appRoot)
    {
        var candidates = new List<string?> { ChosenPath(appRoot) };
        candidates.AddRange(RegistryPaths());
        candidates.AddRange(CommonRoots);
        return candidates
            .Select(p => p is null ? null : Normalize(p))
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// The WoW folder for a picked folder: the folder itself, or its parent when a client folder (_classic_) or
    /// something inside one (Interface, AddOns, WTF) was picked. Null when it isn't a WoW install.
    /// </summary>
    public static string? Normalize(string path)
    {
        try
        {
            var dir = new DirectoryInfo(path.Trim().Trim('"').TrimEnd('\\', '/'));
            for (var i = 0; i < 4 && dir is not null; i++, dir = dir.Parent)
            {
                if (!dir.Exists) continue;
                if (Clients(dir.FullName).Count > 0) return dir.FullName;
            }
        }
        catch (Exception) { }
        return null;
    }

    /// <summary>Client folders in a WoW folder (_retail_, _classic_era_, _classic_beta_, the Forever client…).</summary>
    public static List<string> Clients(string root)
    {
        try
        {
            return Directory.EnumerateDirectories(root, "_*_")
                .Where(d => Path.GetFileName(d).Length > 2)
                .OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>
    /// Product flavors (the client folder's .flavor.info) the app and addon are made for: WoW Forever. The release
    /// client (_forever_) says "wowf", like wago.tools' product code. (The closed beta ran as "wow_classic_beta", a
    /// code other Classic betas use too, so it's no longer counted.)
    /// </summary>
    public static readonly IReadOnlyList<string> ForeverFlavors =
        ["wowf", "wowf_beta", "wowf_ptr", "wow_forever", "wow_forever_beta", "wow_forever_ptr"];

    /// <summary>The client's product flavor from its .flavor.info ("wow_classic_beta"), or "" when there is none.</summary>
    public static string Flavor(string clientDir)
    {
        try
        {
            var file = Path.Combine(clientDir, ".flavor.info");
            if (!File.Exists(file)) return "";
            // "Product Flavor!STRING:0" header, then the flavor.
            return File.ReadAllLines(file).Select(l => l.Trim()).LastOrDefault(l => l.Length > 0 && !l.Contains('!')) ?? "";
        }
        catch (Exception)
        {
            return "";
        }
    }

    /// <summary>A WoW Forever client (by its flavor, or "forever" in the folder name).</summary>
    public static bool IsForever(string clientDir)
    {
        var flavor = Flavor(clientDir);
        return ForeverFlavors.Contains(flavor, StringComparer.OrdinalIgnoreCase)
               || flavor.Contains("forever", StringComparison.OrdinalIgnoreCase)
               || Path.GetFileName(clientDir).Contains("forever", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The WoW Forever clients in a WoW folder: the only ones the addon goes into and is read from.</summary>
    public static List<string> ForeverClients(string root) => Clients(root).Where(IsForever).ToList();

    /// <summary>A readable name for a client's game ("Forever beta", "Classic Era"…).</summary>
    public static string GameName(string clientDir) => Flavor(clientDir).ToLowerInvariant() switch
    {
        "wow_classic_beta" => "Classic beta",
        var f when f.StartsWith("wowf") || f.Contains("forever") => f.Contains("beta") ? "Forever beta" : f.Contains("ptr") ? "Forever PTR" : "Forever",
        "wow" => "Retail",
        "wow_classic_era" => "Classic Era",
        "wow_classic" => "Classic",
        "" => "unknown game",
        var f => f,
    };

    /// <summary>Install paths from Blizzard's registry keys (they point at a client folder or the WoW folder).</summary>
    private static IEnumerable<string> RegistryPaths()
    {
        if (!OperatingSystem.IsWindows()) yield break;
        string[] keys =
        [
            @"SOFTWARE\WOW6432Node\Blizzard Entertainment\World of Warcraft",
            @"SOFTWARE\Blizzard Entertainment\World of Warcraft",
        ];
        foreach (var name in keys)
        {
            string? install = null, game = null;
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(name);
                install = key?.GetValue("InstallPath") as string;
                game = key?.GetValue("GamePath") as string;
            }
            catch (Exception) { }
            if (!string.IsNullOrWhiteSpace(install)) yield return install;
            if (!string.IsNullOrWhiteSpace(game)) yield return Path.GetDirectoryName(game) ?? game;
        }
    }
}
