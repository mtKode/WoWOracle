using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WowCompanion.Core;
using WowCompanion.Core.Calc;

namespace WowCompanion.Desktop;

/// <summary>
/// Gear page: saved gear sets for a class/race/level. Items are added from the item details
/// ("Add to gear set") or with Ctrl+Enter in the search dropdown, into the first free slot that fits.
/// </summary>
public partial class MainViewModel
{
    private const string ActiveSetKey = "active_gear_set";

    private CalcDataService? _calcData;
    private bool _loadingGear;
    private bool _replacingSet;

    public ObservableCollection<GearSetInfo> GearSets { get; } = [];
    public ObservableCollection<RaceInfo> Races { get; } = [];
    public ObservableCollection<GearSlotRow> GearRows { get; } = [];
    public ObservableCollection<SheetRow> GearTotals { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGearSet), nameof(NoGearSet), nameof(GearSetLabel))]
    private GearSetInfo? _selectedGearSet;

    [ObservableProperty] private string _gearSetName = "";

    public bool HasGearSet => SelectedGearSet is not null;
    public bool NoGearSet => SelectedGearSet is null;
    public string GearSetLabel => SelectedGearSet is null ? "No gear set yet" : $"Add to: {SelectedGearSet.Name}";

    private void LoadGear()
    {
        if (_calcData is null) return;
        Races.Clear();
        foreach (var race in _calcData.GetRaces()) Races.Add(race);
        ReloadGearSets(int.TryParse(_gearSets.GetSetting(ActiveSetKey), out var active) ? active : null);
        LoadSnapshots();
    }

    private void ReloadGearSets(int? select)
    {
        GearSets.Clear();
        foreach (var set in _gearSets.List()) GearSets.Add(set);
        if (_characterLoaded) RefreshCalcGearChoices();
        SelectedGearSet = GearSets.FirstOrDefault(s => s.Id == select) ?? GearSets.FirstOrDefault();
        if (SelectedGearSet is null) RefreshGearRows();
    }

    partial void OnSelectedGearSetChanged(GearSetInfo? value)
    {
        if (_replacingSet) return;
        GearSetName = value?.Name ?? "";

        if (value is not null) _gearSets.SetSetting(ActiveSetKey, value.Id.ToString(CultureInfo.InvariantCulture));
        RefreshGearRows();
        Recalculate();
    }

    /// <summary>Swaps the record in the list without re-running the selection logic.</summary>
    private void ReplaceSet(GearSetInfo updated)
    {
        var index = GearSets.IndexOf(GearSets.First(s => s.Id == updated.Id));
        _replacingSet = true;
        _loadingGear = true;
        GearSets[index] = updated;
        SelectedGearSet = updated; // same set, new details; the guard skips the reload
        _loadingGear = false;
        _replacingSet = false;
        RefreshCalcGearChoices();
    }

    [RelayCommand]
    private void NewGearSet()
    {
        var classId = SelectedTalentClass?.Id ?? _classes.FirstOrDefault()?.Id ?? 1;
        var raceId = Races.FirstOrDefault()?.Id ?? 1;
        var name = $"Set {GearSets.Count + 1}";
        var id = _gearSets.Create(name, classId, raceId, 60);
        ReloadGearSets(id);
        Toast($"Created {name}. Rename it on the Gear page.");
    }

    [RelayCommand]
    private void RenameGearSet()
    {
        if (SelectedGearSet is not { } set || GearSetName.Trim().Length == 0) return;
        var updated = set with { Name = GearSetName.Trim() };
        _gearSets.Update(updated);
        ReplaceSet(updated);
        Toast($"Renamed to {updated.Name}.");
    }

    [RelayCommand]
    private void DeleteGearSet()
    {
        if (SelectedGearSet is not { } set) return;
        _gearSets.Delete(set.Id);
        ReloadGearSets(null);
        Toast($"Deleted {set.Name}.");
    }

    [RelayCommand]
    private void AddSelectedToGear()
    {
        if (Selected is not null) AddItemToGear(Selected.Id, Selected.Name);
    }

    /// <summary>Shift+Enter in the search dropdown.</summary>
    public void AddHighlightedToGear()
    {
        if (HighlightIndex >= 0 && HighlightIndex < Results.Count) AddRowToGear(Results[HighlightIndex]);
    }

    /// <summary>Shift+click in the search dropdown.</summary>
    public void AddRowToGear(ItemRow row)
    {
        if (!row.IsQuest) AddItemToGear(row.Id, row.Name);
    }

    /// <summary>Ctrl+Enter in the search dropdown.</summary>
    public void AddHighlightedToCraft()
    {
        if (HighlightIndex >= 0 && HighlightIndex < Results.Count) AddRowToCraft(Results[HighlightIndex]);
    }

    /// <summary>Ctrl+click in the search dropdown: queue the recipe that makes this item.</summary>
    public void AddRowToCraft(ItemRow row)
    {
        if (row.IsQuest) return;
        if (_recipes?.FindRecipeFor(row.Id) is not { } spellId)
        {
            Toast($"Nothing crafts {row.Name}.");
            return;
        }
        _craft.Add(spellId);
        RefreshCraftList();
        Toast($"Added {row.Name} to the craft list.");
    }

    private void AddItemToGear(int itemId, string name)
    {
        if (_calcData is null) return;
        var slots = GearSlots.SlotsFor(_calcData.GetInventoryType(itemId));
        if (slots.Length == 0)
        {
            Toast($"{name} can't be equipped.");
            return;
        }
        if (SelectedGearSet is null) NewGearSet();
        if (SelectedGearSet is not { } set) return;

        var current = _gearSets.GetItems(set.Id);
        var slot = slots.FirstOrDefault(s => !current.ContainsKey(s), slots[0]);
        var inventoryType = _calcData.GetInventoryType(itemId);

        // Two-handers take both hands; an off-hand item pushes out a two-hander.
        if (GearSlots.IsTwoHand(inventoryType)) _gearSets.RemoveItem(set.Id, GearSlots.OffHand);
        if (slot == GearSlots.OffHand && current.TryGetValue(GearSlots.MainHand, out var main)
            && GearSlots.IsTwoHand(_calcData.GetInventoryType(main)))
        {
            _gearSets.RemoveItem(set.Id, GearSlots.MainHand);
        }

        _gearSets.SetItem(set.Id, slot, itemId);
        RefreshGearRows();
        Recalculate();
        Toast($"Added {name} to {set.Name} ({GearSlots.NameOf(slot)}).");
    }

    [RelayCommand]
    private void RemoveGearItem(GearSlotRow? row)
    {
        if (row is null || SelectedGearSet is not { } set) return;
        _gearSets.RemoveItem(set.Id, row.SlotId);
        RefreshGearRows();
        Recalculate();
    }

    [RelayCommand]
    private void OpenGearItem(GearSlotRow? row)
    {
        if (row is null || row.ItemId == 0 || _search?.GetDetail(row.ItemId) is not { } detail) return;
        Selected = ItemView.From(detail);
        CurrentView = AppView.Items;
    }

    private void RefreshGearRows()
    {
        GearRows.Clear();
        GearTotals.Clear();
        if (_calcData is null || SelectedGearSet is not { } set) return;

        var slots = _gearSets.GetItems(set.Id);
        var enchants = _gearSets.GetSlotEnchants(set.Id);
        var items = _calcData.GetGearItems(slots.Values.ToList()).ToDictionary(i => i.Id);
        var enchantable = EnchantableSlots();
        foreach (var slot in GearSlots.All)
        {
            if (slots.TryGetValue(slot.Id, out var itemId) && items.TryGetValue(itemId, out var item))
            {
                var enchantText = enchants.TryGetValue(slot.Id, out var enchantId) ? EnchantLabel(enchantId) : "";
                GearRows.Add(new GearSlotRow(slot.Id, slot.Name, item.Id, item.Name, ItemQuality.BrushFor(item.Quality), item.IconId)
                {
                    EnchantText = enchantText,
                    CanEnchant = enchantable.Contains(slot.Id),
                });
            }
            else
                GearRows.Add(new GearSlotRow(slot.Id, slot.Name, 0, "Empty", ItemQuality.BrushFor(0), 0));
        }
    }

    /// <summary>
    /// The set's items per slot, with any extra stats read in game (random suffix) added to that slot's copy.
    /// Two of the same ring count twice.
    /// </summary>
    private Dictionary<int, GearItem> SetGearBySlot(int setId)
    {
        var result = new Dictionary<int, GearItem>();
        if (_calcData is null) return result;
        var slots = _gearSets.GetItems(setId);
        var bonuses = _gearSets.GetSlotBonuses(setId);
        var enchants = _gearSets.GetSlotEnchants(setId);
        var items = _calcData.GetGearItems(slots.Values.Distinct().ToList()).ToDictionary(i => i.Id);
        foreach (var (slot, id) in slots)
        {
            if (!items.TryGetValue(id, out var item)) continue;
            if (bonuses.TryGetValue(slot, out var extra) && extra.Count > 0)
                item = item with { Stats = item.Stats.Concat(extra).ToList() };
            if (enchants.TryGetValue(slot, out var enchantId) && _enchants?.Get(enchantId) is { } enchant)
                item = WithEnchant(item, enchant);
            result[slot] = item;
        }
        return result;
    }

    /// <summary>The item with an enchant's stats, weapon damage, procs and auras added.</summary>
    private static GearItem WithEnchant(GearItem item, EnchantInfo enchant)
    {
        var result = item with
        {
            Stats = item.Stats.Concat(enchant.Stats).ToList(),
            EquipSpells = item.EquipSpells.Concat(enchant.EquipSpells).ToList(),
            OnHitSpells = item.OnHitSpells.Concat(enchant.ProcSpells).ToList(),
        };
        if (enchant.Damage != 0 && item.IsWeapon)
            result = result with { DamageMin = item.DamageMin + enchant.Damage, DamageMax = item.DamageMax + enchant.Damage };
        return result;
    }

    private List<GearItem> SetGear(int setId) => SetGearBySlot(setId).OrderBy(x => x.Key).Select(x => x.Value).ToList();
}
