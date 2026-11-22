using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WowCompanion.Core;

namespace WowCompanion.Desktop;

/// <summary>One row of the gathering guide; IsYours marks the one your skill is at.</summary>
public sealed record GatherStepRow(GatherStep Step, bool IsYours)
{
    public string Name => Step.Node.Name;
    public string SkillText => Step.SkillText;
    public string Meta => Step.Meta;
}

/// <summary>
/// Map page, "Ore &amp; herbs": a Mining or Herbalism guide (what to gather from which skill, and where), the zones with
/// spots of the picked node, and a dot on the map for every spot where it can spawn.
/// </summary>
public partial class MainViewModel
{
    private GatherService? _gather;

    public ObservableCollection<GatherStepRow> GatherSteps { get; } = [];
    public ObservableCollection<GatherZone> GatherZones { get; } = [];

    /// <summary>The left panel shows the gathering guide instead of the zone list.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowZonePanel), nameof(ShowZoneList), nameof(ShowGatherList))]
    private bool _showGatherPanel;
    /// <summary>Herbalism instead of Mining.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(GatherMining))] private bool _gatherHerbs;

    /// <summary>The other radio button of each pair.</summary>
    public bool ShowZonePanel { get => !ShowGatherPanel; set => ShowGatherPanel = !value; }
    public bool GatherMining { get => !GatherHerbs; set => GatherHerbs = !value; }

    /// <summary>Search answers replace whichever list the panel shows.</summary>
    public bool ShowZoneList => !IsMapSearching && !ShowGatherPanel;
    public bool ShowGatherList => !IsMapSearching && ShowGatherPanel;
    [ObservableProperty] private GatherStepRow? _selectedGatherStep;
    [ObservableProperty] private GatherZone? _selectedGatherZone;
    /// <summary>The node whose spots are drawn on the map (stays while you change zones).</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasGatherNode))] private GatherNode? _gatherNode;
    [ObservableProperty] private string _gatherNote = "";
    [ObservableProperty] private string _gatherYou = "";

    public bool HasGatherNode => GatherNode is not null;

    private void UseGather(string gameDbPath)
    {
        _gather = new GatherService(gameDbPath);
        GatherNode = GatherNode is { } shown ? _gather.Find(shown.Name) : null; // new data, same node on the map
        LoadGatherGuide();
    }

    partial void OnShowGatherPanelChanged(bool value)
    {
        if (value) LoadGatherGuide();
    }

    partial void OnGatherHerbsChanged(bool value) => LoadGatherGuide();

    /// <summary>Your Mining or Herbalism from the addon (the planner's character, else the newest one).</summary>
    private int? YourGatherSkill(string profession) =>
        (Character ?? _characters.FirstOrDefault()) is { } c && c.Skills.TryGetValue(profession, out var s) ? s.Rank : null;

    private void LoadGatherGuide()
    {
        GatherSteps.Clear();
        if (_gather is null) return;
        var kind = GatherHerbs ? "herbalism" : "mining";
        var profession = GatherHerbs ? "Herbalism" : "Mining";
        var steps = _gather.Guide(kind);
        if (steps.Count == 0)
        {
            GatherYou = "";
            GatherNote = "No ore or herb spots in this game data yet: they come with the next data rebuild (restart the app).";
            return;
        }

        // Yours: the best common node you can gather (rare ones spawn on the common ones' spots anyway).
        var skill = YourGatherSkill(profession);
        var yours = skill is { } k ? steps.LastOrDefault(s => !s.Node.IsRare && s.Node.Skill > 0 && s.Node.Skill <= k) : null;
        foreach (var step in steps) GatherSteps.Add(new GatherStepRow(step, step == yours));
        GatherYou = skill is { } level
            ? $"{(Character ?? _characters.FirstOrDefault())!.Name}: {profession} {level}" + (yours is null ? "" : $" → {yours.Node.Name}")
            : $"Your {profession} skill shows here once the addon has seen it.";
        GatherNote = "A node gives skill until it turns grey, about 100 points above what it needs. Pick one to see its spots.";
    }

    partial void OnSelectedGatherStepChanged(GatherStepRow? value)
    {
        if (value is not null) ShowGatherNode(value.Step.Node, openBestZone: true);
    }

    partial void OnSelectedGatherZoneChanged(GatherZone? value)
    {
        if (value is not null) OpenZoneMapKeepNodes(value.Zone);
    }

    /// <summary>Draws a node's spots; opens the zone with the most of them unless the shown zone has some.</summary>
    private void ShowGatherNode(GatherNode node, bool openBestZone)
    {
        if (_gather is null) return;
        GatherNode = node;
        GatherZones.Clear();
        var zones = _gather.Zones(node.Name).Where(z => _maps?.Find(z.Zone) is not null).ToList();
        foreach (var z in zones) GatherZones.Add(z);
        var here = SelectedMapZone is { } shown && zones.Any(z => string.Equals(z.Zone, shown.Name, StringComparison.OrdinalIgnoreCase));
        if (openBestZone && !here && zones.FirstOrDefault() is { } best) OpenZoneMapKeepNodes(best.Zone);
        else _ = LoadMapAsync();
    }

    private void OpenZoneMapKeepNodes(string zone)
    {
        if (_maps?.Find(zone) is not { } map) return;
        _mapFocus = null;
        CurrentView = AppView.Map;
        if (SelectedMapZone?.UiMapId == map.UiMapId) _ = LoadMapAsync();
        else SelectedMapZone = MapZones.FirstOrDefault(m => m.UiMapId == map.UiMapId) ?? map;
    }

    /// <summary>Takes the dots off the map.</summary>
    [RelayCommand]
    private void ClearGatherNode()
    {
        GatherNode = null;
        SelectedGatherStep = null;
        GatherZones.Clear();
        _ = LoadMapAsync();
    }

    /// <summary>Map search picked an ore or herb: the guide opens on it, the map on its best zone.</summary>
    private void ShowGatherNodeByName(string name)
    {
        if (_gather?.Find(name) is not { } node) return;
        MapSearchText = ""; // back from the answers to the guide, which opens on the node
        ShowGatherPanel = true;
        GatherHerbs = node.Kind == "herbalism";
        SelectedGatherStep = GatherSteps.FirstOrDefault(s => s.Name == node.Name);
        if (SelectedGatherStep is null) ShowGatherNode(node, openBestZone: true); // not in the guide (too few spots)
    }

    /// <summary>The node's dots for the zone on screen (MapPin kind "mining" / "herbalism").</summary>
    private async Task<List<MapPin>> GatherPinsAsync(ZoneMap map)
    {
        if (_gather is not { } gather || GatherNode is not { } node) return [];
        var spots = await Task.Run(() => gather.Spots(node.Name, map.Name));
        var tip = $"{node.Name}\n{node.SkillText}";
        GatherNote = spots.Count == 0
            ? $"{node.Name}: no spots known in {map.Name}. The zones list has where it grows."
            : $"{node.Name}: {spots.Count} spot{(spots.Count == 1 ? "" : "s")} in {map.Name}" + (node.IsRare ? " (rare: it spawns on some of these now and then)." : ".");
        return spots.Select(s => new MapPin(s.X / 100 * map.Width, s.Y / 100 * map.Height, node.Name, node.Kind,
            $"{tip}\n{ItemSearchService.Location(map.Name, s.X, s.Y, map.Name)}")).ToList();
    }
}
