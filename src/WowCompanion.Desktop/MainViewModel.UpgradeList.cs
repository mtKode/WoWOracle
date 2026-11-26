using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WowCompanion.Core;

namespace WowCompanion.Desktop;

/// <summary>An item on the gear set's upgrade list (Gear page, under the totals).</summary>
public sealed partial class UpgradeListRow(
    int itemId, string name, Brush nameBrush, int iconId, int slot, string slotName, string source, Brush sourceBrush, string gain, bool done)
    : ObservableObject
{
    public int ItemId { get; } = itemId;
    public string Name { get; } = name;
    public Brush NameBrush { get; } = nameBrush;
    public int IconId { get; } = iconId;
    public int Slot { get; } = slot;
    /// <summary>"Head · +24% when added".</summary>
    public string SlotText { get; } = gain.Length > 0 ? $"{slotName} · {gain} when added" : slotName;
    public string SourceText { get; } = source;
    public Brush SourceBrush { get; } = sourceBrush;
    /// <summary>The source has map coordinates ("View on map").</summary>
    public bool HasMap => MapPlace.Find(SourceText) is not null;
    [ObservableProperty] private bool _done = done;
    public double RowOpacity => Done ? 0.5 : 1;
    partial void OnDoneChanged(bool value) => OnPropertyChanged(nameof(RowOpacity));
}

/// <summary>
/// Upgrade list: "+ List" in the upgrade finder puts an item on the gear set's list, to go and get later; tick it off
/// when you have it (equipping it from the list ticks it too). Per gear set, kept in user.sqlite.
/// </summary>
public partial class MainViewModel
{
    private readonly UpgradeListService _upgradeList = new(paths.UserDbPath);

    public ObservableCollection<UpgradeListRow> UpgradeList { get; } = [];
    [ObservableProperty] private string _upgradeListSummary = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(UpgradeListEmpty))] private bool _hasUpgradeList;
    public bool UpgradeListEmpty => !HasUpgradeList;
    [ObservableProperty] private bool _hasDoneUpgrades;

    private void RefreshUpgradeList()
    {
        foreach (var row in UpgradeList) row.PropertyChanged -= OnUpgradeListRowChanged;
        UpgradeList.Clear();
        if (_calcData is null || SelectedGearSet is not { } set)
        {
            UpdateUpgradeListSummary();
            return;
        }
        var entries = _upgradeList.Load(set.Id);
        var items = _calcData.GetGearItems(entries.Select(e => e.ItemId).ToList()).GroupBy(i => i.Id).ToDictionary(g => g.Key, g => g.First());
        foreach (var e in entries)
        {
            var item = items.GetValueOrDefault(e.ItemId);
            var row = new UpgradeListRow(e.ItemId, item?.Name ?? $"Item {e.ItemId}", ItemQuality.BrushFor(item?.Quality ?? 1), item?.IconId ?? 0,
                e.Slot, GearSlots.NameOf(e.Slot), e.Source, SourceBrushFor(e.Source), e.Gain, e.Done);
            row.PropertyChanged += OnUpgradeListRowChanged;
            UpgradeList.Add(row);
        }
        UpdateUpgradeListSummary();
    }

    private void OnUpgradeListRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(UpgradeListRow.Done) || sender is not UpgradeListRow row || SelectedGearSet is not { } set) return;
        _upgradeList.SetDone(set.Id, row.ItemId, row.Done);
        UpdateUpgradeListSummary();
    }

    private void UpdateUpgradeListSummary()
    {
        var done = UpgradeList.Count(r => r.Done);
        HasUpgradeList = UpgradeList.Count > 0;
        HasDoneUpgrades = done > 0;
        UpgradeListSummary = UpgradeList.Count == 0 ? ""
            : done == 0 ? $"{UpgradeList.Count} to get"
            : $"{done} of {UpgradeList.Count} got";
    }

    /// <summary>The colour of a source line, from its "Quest:" / "Drop: …" text.</summary>
    private static Brush SourceBrushFor(string source) =>
        source.StartsWith("Quest", StringComparison.Ordinal) ? SourceColors.Quest
        : source.StartsWith("Vendor", StringComparison.Ordinal) ? SourceColors.Vendor
        : source.StartsWith("Crafted", StringComparison.Ordinal) ? SourceColors.Profession
        : source.StartsWith("World drop", StringComparison.Ordinal) ? SourceColors.Rare
        : SourceColors.Normal;

    /// <summary>Upgrade finder → the gear set's upgrade list.</summary>
    [RelayCommand]
    private void AddUpgradeToList(UpgradeRow? row)
    {
        if (row is null || UpgradeSlot is not { } slot || SelectedGearSet is not { } set) return;
        _upgradeList.Add(set.Id, row.ItemId, slot.SlotId, row.SourceText, row.GainText == "new" ? "" : row.GainText);
        RefreshUpgradeList();
        Toast($"{row.Name} is on {set.Name}'s upgrade list.");
    }

    [RelayCommand]
    private void RemoveFromUpgradeList(UpgradeListRow? row)
    {
        if (row is null || SelectedGearSet is not { } set) return;
        _upgradeList.Remove(set.Id, row.ItemId);
        RefreshUpgradeList();
    }

    [RelayCommand]
    private void ClearDoneUpgrades()
    {
        if (SelectedGearSet is not { } set) return;
        _upgradeList.ClearDone(set.Id);
        RefreshUpgradeList();
    }

    /// <summary>Puts the listed item in its slot and ticks it off.</summary>
    [RelayCommand]
    private void EquipFromUpgradeList(UpgradeListRow? row)
    {
        if (row is null || SelectedGearSet is not { } set || _calcData is null) return;
        var type = _calcData.GetInventoryType(row.ItemId);
        if (GearSlots.IsTwoHand(type)) _gearSets.RemoveItem(set.Id, GearSlots.OffHand);
        if (row.Slot == GearSlots.OffHand && _gearSets.GetItems(set.Id).TryGetValue(GearSlots.MainHand, out var main)
            && GearSlots.IsTwoHand(_calcData.GetInventoryType(main)))
        {
            _gearSets.RemoveItem(set.Id, GearSlots.MainHand); // an off hand pushes out a two-hander
        }
        _gearSets.SetItem(set.Id, row.Slot, row.ItemId);
        _upgradeList.SetDone(set.Id, row.ItemId, true);
        RefreshGearRows(); // refreshes the list too
        Recalculate();
        Toast($"{row.Name} is now in {set.Name} ({GearSlots.NameOf(row.Slot)}) and ticked off.");
    }
}
