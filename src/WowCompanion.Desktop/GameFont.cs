using System.Globalization;
using System.IO;
using System.Windows.Media;
using WowCompanion.Core;

namespace WowCompanion.Desktop;

/// <summary>
/// Loads Friz Quadrata from the player's own WoW install (Fonts\FRIZQT__.TTF). The font is licensed to Blizzard,
/// so the app never ships a copy: no file found means the app keeps its fallback font.
/// A .ttf in a "font" folder next to the exe also works.
/// To point at a specific file or folder, put the path in %LOCALAPPDATA%\WowCompanion\font-path.txt.
/// Numbers: Friz's digits (especially the old-style ones) are hard to read at small sizes, so the app uses a composite
/// font: digits from Palatino Linotype (clear lining figures with a classic feel), everything else from Friz.
/// </summary>
public static class GameFont
{
    private const string FileName = "FRIZQT__.TTF";

    private static readonly string[] CommonRoots =
    [
        @"C:\Program Files (x86)\World of Warcraft",
        @"C:\Program Files\World of Warcraft",
        @"C:\Games\World of Warcraft",
        @"D:\World of Warcraft",
        @"D:\Games\World of Warcraft",
    ];

    public static FontFamily? TryLoad()
    {
        var path = Find();
        if (path is null) return null;

        try
        {
            var glyphs = new GlyphTypeface(new Uri(path));
            var names = glyphs.Win32FamilyNames;
            var family = names.TryGetValue(CultureInfo.GetCultureInfo("en-US"), out var english)
                ? english
                : names.Values.FirstOrDefault();
            if (family is null) return null;

            return WithClearDigits(path, family, glyphs) ?? new FontFamily(new Uri(Path.GetDirectoryName(path)! + Path.DirectorySeparatorChar), "./#" + family);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private const string DigitFont = "Palatino Linotype";

    /// <summary>
    /// An in-memory composite font: digits 0-9 from Palatino Linotype, everything else from Friz (with Palatino as the
    /// fallback for anything Friz lacks).
    /// </summary>
    private static FontFamily? WithClearDigits(string frizPath, string family, GlyphTypeface glyphs)
    {
        try
        {
            if (!Fonts.SystemFontFamilies.Any(f => f.Source.Equals(DigitFont, StringComparison.OrdinalIgnoreCase))) return null;
            var folder = new Uri(Path.GetDirectoryName(frizPath)! + Path.DirectorySeparatorChar).AbsoluteUri; // file:///C:/…/font/
            var composite = new FontFamily
            {
                Baseline = glyphs.Baseline,
                LineSpacing = glyphs.Height,
            };
            composite.FamilyNames[System.Windows.Markup.XmlLanguage.GetLanguage("en-us")] = "WoW Oracle Text";
            composite.FamilyMaps.Add(new FontFamilyMap { Unicode = "0030-0039", Target = DigitFont, Scale = 1.04 });
            composite.FamilyMaps.Add(new FontFamilyMap { Unicode = "0000-FFFF", Target = folder + "#" + family, Scale = 1 });
            composite.FamilyMaps.Add(new FontFamilyMap { Unicode = "0000-FFFF", Target = DigitFont, Scale = 1 });
            return composite;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? Find()
    {
        var overrideFile = Path.Combine(AppPaths.Default().Root, "font-path.txt");
        if (File.Exists(overrideFile))
        {
            var configured = File.ReadAllText(overrideFile).Trim().Trim('"');
            if (File.Exists(configured)) return configured;
            var inFolder = Path.Combine(configured, FileName);
            if (File.Exists(inFolder)) return inFolder;
        }

        // A "font" folder next to the exe (handy for the shared zip), or in a parent folder while developing.
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var fontDir = Path.Combine(dir.FullName, "font");
            if (!Directory.Exists(fontDir)) continue;
            var ttf = Directory.EnumerateFiles(fontDir, "*.ttf")
                .OrderByDescending(f => Path.GetFileName(f).Contains("friz", StringComparison.OrdinalIgnoreCase)
                                        || Path.GetFileName(f).Equals(FileName, StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault();
            if (ttf is not null) return ttf;
        }

        foreach (var root in CommonRoots.Where(Directory.Exists))
        {
            var direct = Path.Combine(root, "Fonts", FileName);
            if (File.Exists(direct)) return direct;

            foreach (var client in Directory.EnumerateDirectories(root, "_*_"))
            {
                var candidate = Path.Combine(client, "Fonts", FileName);
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }
}
