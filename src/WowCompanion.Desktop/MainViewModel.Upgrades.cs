using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WowCompanion.Core;
using WowCompanion.Core.Calc;

namespace WowCompanion.Desktop;

/// <summary>
/// Upgrade finder on the Gear page: "Upgrades" on a slot opens a side panel with alternatives for that slot,
/// ranked by the stats the set already leans on, each with where it comes from. Several options on purpose: the
/// best one is often a raid drop. Toggles hide raid, dungeon and world drops.
/// </summary>
public partial class MainViewModel
{
    private UpgradeFinder? _upgradeFinder;
    private int _upgradeRun;

    public ObservableCollection<UpgradeRow> Upgrades { get; } = [];

    [ObservableProperty] private bool _isUpgradePanelOpen;
    [ObservableProperty] private bool _upgradeBusy;
    [ObservableProperty] private GearSlotRow? _upgradeSlot;
    [ObservableProperty] private string _upgradeTitle = "";
    [ObservableProperty] private string _upgradeWeights = "";
    [ObservableProperty] private string _upgradeNote = "";
    [ObservableProperty] private bool _upgradeRaid = true;
    [ObservableProperty] private bool _upgradeDungeon = true;
    [ObservableProperty] private bool _upgradeWorld = true;

    public bool ShowGearTotals => !IsUpgradePanelOpen;
    partial void OnIsUpgradePanelOpenChanged(bool value) => OnPropertyChanged(nameof(ShowGearTotals));

    partial void OnUpgradeRaidChanged(bool value) => _ = RunUpgradesAsync();
    partial void OnUpgradeDungeonChanged(bool value) => _ = RunUpgradesAsync();
    partial void OnUpgradeWorldChanged(bool value) => _ = RunUpgradesAsync();

    [RelayCommand]
    private async Task FindUpgrades(GearSlotRow? row)
    {
        if (row is null) return;
        UpgradeSlot = row;
        IsUpgradePanelOpen = true;
        await RunUpgradesAsync();
    }

    [RelayCommand]
    private void CloseUpgrades()
    {
        IsUpgradePanelOpen = false;
        Upgrades.Clear();
    }

    private async Task RunUpgradesAsync()
    {
        if (!IsUpgradePanelOpen || UpgradeSlot is not { } slot || SelectedGearSet is not { } set
            || _upgradeFinder is not { } finder || _calcData is not { } data) return;

        var run = ++_upgradeRun;
        UpgradeBusy = true;
        UpgradeTitle = $"{slot.SlotName}: alternatives";
        var className = TalentClasses.FirstOrDefault(c => c.Id == set.ClassId)?.Name ?? "your class";
        try
        {
            var raid = UpgradeRaid;
            var dungeon = UpgradeDungeon;
            var world = UpgradeWorld;
            var result = await Task.Run(() =>
            {
                var worn = SetGearBySlot(set.Id);
                var items = worn.Values.ToList();
                var current = worn.GetValueOrDefault(slot.SlotId);
                return finder.Find(new UpgradeRequest(slot.SlotId, set.ClassId, set.Level, items, current, raid, dungeon, world));
            });
            if (run != _upgradeRun) return;

            Upgrades.Clear();
            var inv = CultureInfo.InvariantCulture;
            foreach (var o in result.Options)
            {
                string gain;
                Brush gainBrush;
                if (o.Gain is { } g)
                {
                    gain = (g >= 0 ? "+" : "") + g.ToString("0", inv) + "%";
                    gainBrush = g > 0.5 ? CompareColors.Up : g < -0.5 ? CompareColors.Down : CompareColors.Neutral;
                }
                else
                {
                    gain = "new";
                    gainBrush = CompareColors.Up;
                }
                Upgrades.Add(new UpgradeRow(o.ItemId, o.Name, ItemQuality.BrushFor(o.Quality), o.IconId, gain, gainBrush,
                    o.StatLine, o.Source.Text, SourceBrush(o.Source.Kind),
                    $"ilvl {o.ItemLevel} · req {o.RequiredLevel}"));
            }
            UpgradeWeights = result.WeightedBy.Count > 0
                ? $"{className} level {set.Level} · weighted by {string.Join(", ", result.WeightedBy)}"
                : $"{className} level {set.Level}";
            UpgradeNote = result.Note.Length > 0 ? result.Note
                : Upgrades.Count == 0 ? "Nothing found with these filters." : "";
        }
        catch (Exception ex)
        {
            if (run == _upgradeRun) UpgradeNote = "Couldn't search: " + ex.Message;
        }
        finally
        {
            if (run == _upgradeRun) UpgradeBusy = false;
        }
    }

    private static Brush SourceBrush(SourceKind kind) => kind switch
    {
        SourceKind.Raid => SourceColors.Boss,
        SourceKind.Dungeon => SourceColors.Elite,
        SourceKind.Quest => SourceColors.Quest,
        SourceKind.Vendor => SourceColors.Vendor,
        SourceKind.Crafted => SourceColors.Profession,
        SourceKind.WorldDrop => SourceColors.Rare,
        _ => SourceColors.Normal,
    };

    /// <summary>Puts the alternative into the slot the panel is for (two-handers clear the off hand).</summary>
    [RelayCommand]
    private async Task EquipUpgrade(UpgradeRow? row)
    {
        if (row is null || UpgradeSlot is not { } slot || SelectedGearSet is not { } set || _calcData is null) return;
        var type = _calcData.GetInventoryType(row.ItemId);
        if (GearSlots.IsTwoHand(type)) _gearSets.RemoveItem(set.Id, GearSlots.OffHand);
        if (slot.SlotId == GearSlots.OffHand && _gearSets.GetItems(set.Id).TryGetValue(GearSlots.MainHand, out var main)
            && GearSlots.IsTwoHand(_calcData.GetInventoryType(main)))
        {
            _gearSets.RemoveItem(set.Id, GearSlots.MainHand); // an off hand pushes out a two-hander
        }
        _gearSets.SetItem(set.Id, slot.SlotId, row.ItemId);
        RefreshGearRows();
        Recalculate();
        Toast($"{row.Name} is now in {set.Name} ({slot.SlotName}).");
        UpgradeSlot = GearRows.FirstOrDefault(r => r.SlotId == slot.SlotId) ?? slot;
        await RunUpgradesAsync();
    }

    [RelayCommand]
    private void OpenUpgrade(UpgradeRow? row)
    {
        if (row is not null) OpenItem(row.ItemId);
    }
}
