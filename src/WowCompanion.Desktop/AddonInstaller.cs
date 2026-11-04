using System.IO;
using System.Text.RegularExpressions;

namespace WowCompanion.Desktop;

/// <summary>
/// The WoWOracle addon that ships with the app (addon\WoWOracle next to the exe; the repo's addon folder while
/// developing) and its copies in the WoW clients' Interface\AddOns.
/// </summary>
public static partial class AddonInstaller
{
    public const string Name = "WoWOracle";

    public sealed record ClientAddon(string Client, string ClientName, string? InstalledVersion, bool Linked)
    {
        public string Target => Path.Combine(Client, "Interface", "AddOns", Name);
    }

    /// <summary>The addon folder shipped with the app, or null.</summary>
    public static string? BundledFolder()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "addon", Name);
            if (File.Exists(Path.Combine(candidate, Name + ".toc"))) return candidate;
        }
        return null;
    }

    [GeneratedRegex(@"^##\s*Version:\s*(\S+)", RegexOptions.Multiline)]
    private static partial Regex VersionLine();

    public static string? VersionOf(string addonFolder)
    {
        try
        {
            var toc = Path.Combine(addonFolder, Name + ".toc");
            if (!File.Exists(toc)) return null;
            var m = VersionLine().Match(File.ReadAllText(toc));
            return m.Success ? m.Groups[1].Value : "?";
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// The clients the addon belongs in, WoW Forever only (by the client's flavor), and what each one has now.
    /// Other games' clients (Classic, Classic Era, retail) are never touched.
    /// </summary>
    public static List<ClientAddon> Clients(string wowRoot) =>
        Core.WowInstall.ForeverClients(wowRoot)
            .Select(c =>
            {
                var target = Path.Combine(c, "Interface", "AddOns", Name);
                var linked = Directory.Exists(target) && new DirectoryInfo(target).LinkTarget is not null;
                return new ClientAddon(c, $"{Path.GetFileName(c)} ({Core.WowInstall.GameName(c)})", VersionOf(target), linked);
            })
            .ToList();

    /// <summary>True when "a" is an older version than "b" (0.6.0 vs 0.7.0).</summary>
    public static bool IsOlder(string? a, string? b) =>
        Version.TryParse(a, out var va) && Version.TryParse(b, out var vb) && va < vb;

    /// <summary>Copies the shipped addon into a client (a linked development folder is left alone).</summary>
    public static void Install(string bundled, ClientAddon client)
    {
        if (client.Linked) return;
        if (!Core.WowInstall.IsForever(client.Client)) throw new InvalidOperationException("Not a WoW Forever client.");
        Directory.CreateDirectory(client.Target);
        foreach (var file in Directory.EnumerateFiles(bundled, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(bundled, file);
            var destination = Path.Combine(client.Target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }
    }
}
