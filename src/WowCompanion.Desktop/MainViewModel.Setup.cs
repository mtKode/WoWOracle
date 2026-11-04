using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WowCompanion.Core;

namespace WowCompanion.Desktop;

/// <summary>
/// Finding WoW and installing the addon. A banner above the search bar shows up only when something needs a click:
/// WoW not found (Browse…), the addon not installed (Install addon) or older than the one that came with the app
/// (Update addon). "Addon" in the status bar opens it any time.
/// </summary>
public partial class MainViewModel
{
    private enum SetupAction { None, Browse, Install }

    private SetupAction _setupAction;
    private bool _setupDismissed;

    [ObservableProperty] private bool _setupBannerVisible;
    [ObservableProperty] private string _setupBannerText = "";
    [ObservableProperty] private string _setupButtonText = "";
    [ObservableProperty] private bool _setupShowBrowse;
    [ObservableProperty] private string _wowFolderText = "";
    [ObservableProperty] private bool _hasSetupClients;

    /// <summary>One chip per game client in the banner: "_classic_beta_  ✓ 0.7.0" in green, "not installed" dimmed.</summary>
    public ObservableCollection<SetupClientRow> SetupClients { get; } = [];

    private void FillSetupClients(IEnumerable<AddonInstaller.ClientAddon> clients, string? bundledVersion)
    {
        SetupClients.Clear();
        foreach (var c in clients)
        {
            var (text, brush) = c switch
            {
                { Linked: true } => ($"linked {c.InstalledVersion ?? ""}".Trim(), SetupColors.Linked),
                { InstalledVersion: null } => ("not installed", SetupColors.Missing),
                _ when AddonInstaller.IsOlder(c.InstalledVersion, bundledVersion) => ($"{c.InstalledVersion} → {bundledVersion}", SetupColors.Outdated),
                _ => ($"✓ {c.InstalledVersion}", SetupColors.Installed),
            };
            SetupClients.Add(new SetupClientRow(c.ClientName, text, brush));
        }
        HasSetupClients = SetupClients.Count > 0;
    }

    /// <summary>Checks the WoW folder and the addon. Quiet: only logs and shows the banner when there's something to do.</summary>
    private void CheckSetup(bool announce)
    {
        var roots = WowInstall.Roots(paths.Root);
        var bundled = AddonInstaller.BundledFolder();
        var bundledVersion = bundled is null ? null : AddonInstaller.VersionOf(bundled);

        if (roots.Count == 0)
        {
            WowFolderText = "WoW not found";
            FillSetupClients([], null);
            ShowSetup("World of Warcraft wasn't found in the usual places. Show the Oracle where it's installed to use the addon.",
                "Browse…", SetupAction.Browse, showBrowse: false);
            if (announce)
            {
                Log("World of Warcraft wasn't found. Use Browse… above the search bar to pick its folder.", "warn");
                Toast("Couldn't find World of Warcraft. Browse… lets you pick its folder.");
            }
            return;
        }

        var root = roots[0];
        var clients = AddonInstaller.Clients(root);
        FillSetupClients(clients, bundledVersion);
        var names = string.Join(", ", clients.Select(c => c.ClientName));
        WowFolderText = $"WoW: {root}";
        if (announce) Log($"World of Warcraft found: {root} ({(names.Length > 0 ? names : "no Forever client")}).");

        if (bundled is null || bundledVersion is null)
        {
            HideSetup();
            return;
        }
        if (clients.Count == 0)
        {
            var others = string.Join(", ", WowInstall.Clients(root).Select(c => $"{Path.GetFileName(c)} ({WowInstall.GameName(c)})"));
            ShowSetup($"Found World of Warcraft in {root}, but no WoW Forever client in it"
                      + (others.Length > 0 ? $" (only {others})" : "") + ". The addon is made for Forever only. Install Forever, or pick another folder.",
                "Browse…", SetupAction.Browse, showBrowse: false);
            if (announce) Log("No WoW Forever client found; the addon is only installed into Forever.", "warn");
            return;
        }

        var missing = clients.Where(c => c.InstalledVersion is null).ToList();
        var outdated = clients.Where(c => !c.Linked && AddonInstaller.IsOlder(c.InstalledVersion, bundledVersion)).ToList();
        if (missing.Count == clients.Count)
        {
            ShowSetup($"The WoW Oracle addon isn't installed yet ({names}). It sends your gear, talents, professions and bags to the app.",
                "Install addon", SetupAction.Install, showBrowse: true);
            if (announce) Log("The WoW Oracle addon isn't installed: use \"Install addon\" above the search bar.", "warn");
        }
        else if (outdated.Count > 0)
        {
            ShowSetup($"Addon update: {outdated[0].InstalledVersion} → {bundledVersion} ({string.Join(", ", outdated.Select(c => c.ClientName))}). Reload the game after updating.",
                "Update addon", SetupAction.Install, showBrowse: false);
            if (announce) Log($"A newer WoW Oracle addon came with the app ({bundledVersion}).", "warn");
        }
        else
        {
            HideSetup();
        }
    }

    private void ShowSetup(string text, string button, SetupAction action, bool showBrowse)
    {
        _setupAction = action;
        SetupBannerText = text;
        SetupButtonText = button;
        SetupShowBrowse = showBrowse;
        SetupBannerVisible = !_setupDismissed;
    }

    private void HideSetup()
    {
        _setupAction = SetupAction.None;
        SetupBannerVisible = false;
    }

    [RelayCommand]
    private void SetupMain()
    {
        if (_setupAction == SetupAction.Browse) BrowseWow();
        else if (_setupAction == SetupAction.Install) InstallAddon();
    }

    [RelayCommand]
    private void DismissSetup()
    {
        _setupDismissed = true;
        SetupBannerVisible = false;
    }

    /// <summary>Status bar "Addon": shows the banner again, with the current state.</summary>
    [RelayCommand]
    private void ShowSetupPanel()
    {
        _setupDismissed = false;
        CheckSetup(announce: false);
        if (!SetupBannerVisible)
        {
            var root = WowInstall.Roots(paths.Root).FirstOrDefault();
            ShowSetup(root is null ? "World of Warcraft isn't set." : $"World of Warcraft: {root}",
                "Install addon", SetupAction.Install, showBrowse: true);
        }
    }

    [RelayCommand]
    private void BrowseWow()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Pick your World of Warcraft folder (the one with _classic_ or similar inside)",
        };
        if (dialog.ShowDialog() != true) return;
        var root = WowInstall.Normalize(dialog.FolderName);
        if (root is null)
        {
            Toast("That folder doesn't look like a WoW install: no game client folder (like _classic_) inside.");
            return;
        }
        WowInstall.SaveChosenPath(paths.Root, root);
        Toast($"World of Warcraft: {root}");
        _setupDismissed = false;
        CheckSetup(announce: true);
        RestartAddonWatch();
        AddonFileChanged(); // read what's there now
    }

    [RelayCommand]
    private void InstallAddon()
    {
        var root = WowInstall.Roots(paths.Root).FirstOrDefault();
        var bundled = AddonInstaller.BundledFolder();
        if (root is null) { BrowseWow(); return; }
        if (AddonInstaller.Clients(root).Count == 0)
        {
            Toast("No WoW Forever client in this WoW folder; the addon is only for Forever.");
            CheckSetup(announce: false);
            return;
        }
        if (bundled is null) { Toast("The addon files didn't come with this copy of the app (addon\\WoWOracle)."); return; }

        var done = new List<string>();
        foreach (var client in AddonInstaller.Clients(root))
        {
            try
            {
                if (client.Linked) continue;
                AddonInstaller.Install(bundled, client);
                done.Add(client.ClientName);
            }
            catch (Exception ex)
            {
                Log($"Couldn't install the addon in {client.ClientName}: {ex.Message}", "warn");
            }
        }
        if (done.Count > 0)
        {
            var version = AddonInstaller.VersionOf(bundled);
            Log($"WoW Oracle addon {version} installed in {string.Join(", ", done)}. In game: /reload (or restart), and enable it in the AddOns list if needed.", "addon");
            Toast($"Addon installed in {string.Join(", ", done)}. /reload in game.");
        }
        ReportKnownIds(WriteKnownIds(_gameDbPath, force: true)); // the fresh copy has the empty placeholder
        CheckSetup(announce: false);
        RestartAddonWatch();
    }

    private void RestartAddonWatch()
    {
        foreach (var w in _addonWatchers) w.Dispose();
        _addonWatchers.Clear();
        WatchAddonFiles();
    }
}

/// <summary>A game client and the addon's state in it, for the setup banner.</summary>
public sealed record SetupClientRow(string Client, string State, Brush StateBrush);

public static class SetupColors
{
    public static readonly Brush Installed = Make("#1EFF00");
    public static readonly Brush Outdated = Make("#FF8040");
    public static readonly Brush Missing = Make("#A8977A");
    public static readonly Brush Linked = Make("#8CC4F0");

    private static Brush Make(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
