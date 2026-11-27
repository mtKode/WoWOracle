using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using WowCompanion.Core;

namespace WowCompanion.Desktop;

/// <summary>One mob in the Map page's Farming list.</summary>
public sealed record FarmRow(FarmMob Mob)
{
    public string Name => Mob.Name + (Mob.Elite ? " (elite)" : "");
    /// <summary>"6s 50c" a kill on average.</summary>
    public string PerKillText => ItemText.Money(Mob.PerKill);
    public string Meta => $"{Mob.Zone} · level {Mob.LevelText} · {Mob.Spawns} spawns";
    /// <summary>"coin 2s 10c · greys 4s 40c · items 2c".</summary>
    public string Breakdown => string.Join(" · ", new[]
    {
        Mob.Coin > 0 ? $"coin {ItemText.Money(Mob.Coin)}" : "",
        Mob.Greys > 0 ? $"greys {ItemText.Money(Mob.Greys)}" : "",
        Mob.Other > 0 ? $"items {ItemText.Money(Mob.Other)}" : "",
    }.Where(s => s.Length > 0));
    public string Tip
    {
        get
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var lines = new List<string> { $"{Mob.Name}: about {PerKillText} a kill", Breakdown, "" };
            lines.AddRange(Mob.TopDrops.Select(d =>
                $"{d.Name}: {d.Chance.ToString(d.Chance < 1 ? "0.##" : "0.#", inv)}% × {ItemText.Money(d.Price)} ≈ {ItemText.Money((long)Math.Round(d.PerKill))}"));
            lines.Add("");
            lines.Add("Click to see its spawns on the map.");
            return string.Join("\n", lines.Where((l, i) => l.Length > 0 || i > 0));
        }
    }
}

/// <summary>
/// Map page, "Farming": mobs around your level ranked by the vendor gold a kill gives on average (coin, greys, and
/// other drops at the vendor or, with an Auctionator scan, the auction house), with their spawns on the map.
/// </summary>
public partial class MainViewModel
{
    private FarmService? _farm;
    private int _farmRun;

    public ObservableCollection<FarmRow> FarmRows { get; } = [];

    /// <summary>The left panel shows the farming list.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowZonePanel), nameof(ShowZoneList), nameof(ShowFarmList))]
    private bool _showFarmPanel;
    public bool ShowFarmList => !IsMapSearching && ShowFarmPanel;

    /// <summary>Your level: mobs from 10 below to 1 above it.</summary>
    [ObservableProperty] private int _farmLevel = 60;
    [ObservableProperty] private bool _farmElites;
    /// <summary>Count non-grey drops at their auction price when it's above the vendor's.</summary>
    [ObservableProperty] private bool _farmAuction = true;
    [ObservableProperty] private string _farmNote = "";
    [ObservableProperty] private FarmRow? _selectedFarmRow;

    private void UseFarm(string gameDbPath)
    {
        _farm = new FarmService(gameDbPath);
        if (ShowFarmPanel) _ = LoadFarmAsync();
    }

    partial void OnShowFarmPanelChanged(bool value)
    {
        if (!value) return;
        ShowGatherPanel = false;
        if (SelectedGearSet is { Level: > 0 } set) FarmLevel = set.Level;
        _ = LoadFarmAsync();
    }

    partial void OnFarmLevelChanged(int value) => _ = LoadFarmAsync();
    partial void OnFarmElitesChanged(bool value) => _ = LoadFarmAsync();
    partial void OnFarmAuctionChanged(bool value) => _ = LoadFarmAsync();

    partial void OnSelectedFarmRowChanged(FarmRow? value)
    {
        if (value is null) return;
        var mob = value.Mob;
        ShowOnMap(new MapSpot(mob.Zone, mob.X, mob.Y, mob.Name), mob.Name);
    }

    private async Task LoadFarmAsync()
    {
        if (!ShowFarmPanel || _farm is not { } farm) return;
        var run = ++_farmRun;
        var level = Math.Clamp(FarmLevel, 1, 70);
        var (lo, hi) = (Math.Max(1, level - 10), level + 1);
        var elites = FarmElites;
        Func<int, long, long>? price = FarmAuction && AuctionPrices.Any ? (id, _) => AuctionPrices.Unit(id) : null;
        FarmNote = "Searching…";
        var mobs = await Task.Run(() => farm.Find(lo, hi, elites, price));
        if (run != _farmRun) return;
        FarmRows.Clear();
        foreach (var mob in mobs) FarmRows.Add(new FarmRow(mob));
        FarmNote = mobs.Count == 0
            ? "No mobs found: the coin per kill comes with the next game data rebuild (restart the app)."
            : $"Level {lo}-{hi} mobs with 4+ spawns, by what a kill gives on average (Classic drop rates; Forever's own mobs aren't in it). Hover for the drops.";
    }
}
