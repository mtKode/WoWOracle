using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WowCompanion.Core;

namespace WowCompanion.Desktop;

/// <summary>
/// Share panel on the Talents page: one box for Icy Veins and one for Wowhead, each with an export link of the build
/// on screen and an import field (see <see cref="TalentCode"/>). Talents are matched by name through the Icy Veins
/// layout, so links still land right when our game data and the sites are on different patches; whatever can't be
/// placed is listed. Imports always become a new set, so a bad link never overwrites a build.
/// </summary>
public partial class MainViewModel
{
    [ObservableProperty] private bool _isSharePanelOpen;

    [ObservableProperty] private string _icyVeinsExport = "";
    [ObservableProperty] private string _wowheadExport = "";
    [ObservableProperty] private string _icyVeinsImport = "";
    [ObservableProperty] private string _wowheadImport = "";

    /// <summary>Which box the notes below belong to, e.g. "Wowhead import".</summary>
    [ObservableProperty] private string _shareNotesTitle = "";
    [ObservableProperty] private string _shareError = "";
    public ObservableCollection<string> ShareWarnings { get; } = [];

    private Dictionary<int, int> CurrentRanks() =>
        Trees.SelectMany(t => t.Cells).Where(c => c.Rank > 0).ToDictionary(c => c.Id, c => c.Rank);

    private void SetShareNotes(string title, string error, IEnumerable<string> warnings)
    {
        ShareWarnings.Clear();
        foreach (var w in warnings) ShareWarnings.Add(w);
        ShareError = error;
        ShareNotesTitle = error.Length > 0 || ShareWarnings.Count > 0 ? title : "";
    }

    [RelayCommand]
    private async Task ToggleSharePanel()
    {
        IsSharePanelOpen = !IsSharePanelOpen;
        if (IsSharePanelOpen) await RefreshShareLinksAsync();
    }

    /// <summary>Rebuilds both export links; runs whenever the build changes while the panel is open.</summary>
    private async Task RefreshShareLinksAsync()
    {
        if (SelectedTalentClass is not { } cls || _talentTrees.Count == 0)
        {
            IcyVeinsExport = WowheadExport = "";
            return;
        }
        var (layout, error) = await TalentLayouts.GetAsync(TalentCode.Slug(cls));
        if (layout is null)
        {
            IcyVeinsExport = WowheadExport = "";
            SetShareNotes("Export", error, []);
            return;
        }
        var (external, warnings) = TalentCode.ToExternal(_talentTrees, CurrentRanks(), layout, "Icy Veins/Wowhead");
        IcyVeinsExport = TalentCode.IcyVeinsLink(layout, external);
        WowheadExport = TalentCode.WowheadLink(layout, external);
        SetShareNotes("Export", "", warnings);
    }

    [RelayCommand]
    private void CopyIcyVeinsLink() => CopyLink(IcyVeinsExport, "Icy Veins");

    [RelayCommand]
    private void CopyWowheadLink() => CopyLink(WowheadExport, "Wowhead");

    private void CopyLink(string link, string site)
    {
        if (link.Length == 0) return;
        try
        {
            Clipboard.SetText(link);
            Toast($"{site} link copied.");
        }
        catch (Exception)
        {
            Toast("Couldn't reach the clipboard.");
        }
    }

    [RelayCommand]
    private Task ImportIcyVeins() => ImportLinkAsync(IcyVeinsImport, TalentLinkSite.IcyVeins);

    [RelayCommand]
    private Task ImportWowhead() => ImportLinkAsync(WowheadImport, TalentLinkSite.Wowhead);

    private async Task ImportLinkAsync(string text, TalentLinkSite site)
    {
        var siteName = site == TalentLinkSite.IcyVeins ? "Icy Veins" : "Wowhead";
        var title = siteName + " import";
        SetShareNotes(title, "", []);
        if (_talentService is null) { SetShareNotes(title, "Game data isn't loaded yet.", []); return; }

        text = text.Trim();
        if (text.Length == 0) { SetShareNotes(title, $"Paste a {siteName} talent calculator link first.", []); return; }
        if (!TalentCode.TryParse(text, out var link) || link is null)
        {
            var example = site == TalentLinkSite.IcyVeins ? "…/wow-forever/mage-talent-calculator#tc-…" : "…/forever/talent-calc/mage/v2…";
            SetShareNotes(title, $"That isn't a {siteName} talent link. It should look like {example}", []);
            return;
        }
        if (link.Site != site)
        {
            var other = link.Site == TalentLinkSite.IcyVeins ? "Icy Veins" : "Wowhead";
            SetShareNotes(title, $"That's a {other} link. Paste it in the {other} box.", []);
            return;
        }

        var cls = TalentClasses.FirstOrDefault(c => TalentCode.Slug(c) == link.ClassSlug);
        if (cls is null) { SetShareNotes(title, $"Unknown class \"{link.ClassSlug}\" in the link.", []); return; }

        var (layout, layoutError) = await TalentLayouts.GetAsync(link.ClassSlug);
        if (layout is null) { SetShareNotes(title, layoutError, []); return; }

        TalentCodeResult result;
        IReadOnlyList<TalentTree> trees;
        try
        {
            trees = _talentService.GetTrees(cls.Id);
            result = TalentCode.Decode(link, trees, layout);
        }
        catch (Exception ex)
        {
            SetShareNotes(title, "Couldn't read that link: " + ex.Message, []);
            return;
        }

        if (!result.Ok) { SetShareNotes(title, result.Error, result.Warnings); return; }
        if (result.Points == 0)
        {
            SetShareNotes(title, result.Warnings.Count > 0 ? "Nothing in that link fits this game build." : "That link has no talent points in it.", result.Warnings);
            return;
        }

        FlushTalentSave();
        var split = string.Join("/", trees.Select(t => t.Talents.Sum(x => result.Ranks.GetValueOrDefault(x.Id))));
        var level = Math.Clamp(result.Points + 9, 10, 60);
        var id = _talentBuilds.Create(cls.Id, $"{siteName} {split}", level, result.Ranks);
        _talentBuilds.SetActive(cls.Id, id);

        var target = TalentClasses.First(c => c.Id == cls.Id);
        if (SelectedTalentClass?.Id != target.Id) SelectedTalentClass = target; // loads the active (new) set
        else LoadTalentSets(id);

        if (site == TalentLinkSite.IcyVeins) IcyVeinsImport = ""; else WowheadImport = "";
        SetShareNotes(title, "", result.Warnings);
        Toast(result.Warnings.Count == 0
            ? $"Imported {cls.Name} {split} from {siteName} as a new set."
            : $"Imported {cls.Name} {split}, see the notes in the share panel.");
    }
}
