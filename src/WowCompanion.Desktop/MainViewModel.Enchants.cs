using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WowCompanion.Core;

namespace WowCompanion.Desktop;

/// <summary>An enchant in the Gear page's picker.</summary>
public sealed record EnchantChoiceRow(int EnchantId, string Label, string Summary, bool Temporary, bool IsCurrent)
{
    public string Tag => Temporary ? "temporary" : "";
}

/// <summary>
/// Enchants on the Gear page: "Enchant" on a slot opens a picker with every enchant that fits that slot (recipes,
/// armor kits, oils, stones, poisons), typed to filter. The enchant's stats, weapon damage, procs and auras count in
/// gear totals, compare, upgrades and the calculator. Imported characters bring their enchants along.
/// </summary>
public partial class MainViewModel
{
    private EnchantService? _enchants;
    private readonly Dictionary<int, IReadOnlyList<EnchantOption>> _enchantOptions = [];
    private List<EnchantChoiceRow> _allEnchantChoices = [];

    public ObservableCollection<EnchantChoiceRow> EnchantChoices { get; } = [];

    [ObservableProperty] private bool _isEnchantPickerOpen;
    [ObservableProperty] private GearSlotRow? _enchantSlot;
    [ObservableProperty] private string _enchantFilter = "";
    [ObservableProperty] private string _enchantPickerTitle = "";

    private IReadOnlyList<EnchantOption> EnchantOptionsFor(int slot)
    {
        if (_enchants is null) return [];
        if (!_enchantOptions.TryGetValue(slot, out var list)) _enchantOptions[slot] = list = _enchants.OptionsFor(slot);
        return list;
    }

    private HashSet<int> EnchantableSlots() =>
        GearSlots.All.Select(s => s.Id).Where(id => EnchantOptionsFor(id).Count > 0).ToHashSet();

    /// <summary>Short text for the slot row: "+5 Agility", "Fiery Weapon".</summary>
    private string EnchantLabel(int enchantId) =>
        _enchants?.Get(enchantId)?.Name is { Length: > 0 } name ? name : $"Enchant {enchantId}";

    /// <summary>"Enchant Boots - Minor Agility" -> "Minor Agility".</summary>
    private static string ShortLabel(string label)
    {
        var dash = label.IndexOf(" - ", StringComparison.Ordinal);
        return label.StartsWith("Enchant ", StringComparison.OrdinalIgnoreCase) && dash > 0 ? label[(dash + 3)..] : label;
    }

    /// <summary>Called from the window when a slot's Enchant button is clicked.</summary>
    public bool OpenEnchantPicker(GearSlotRow row)
    {
        if (SelectedGearSet is not { } set || row.ItemId == 0) return false;
        var current = _gearSets.GetSlotEnchants(set.Id).GetValueOrDefault(row.SlotId);
        _allEnchantChoices = EnchantOptionsFor(row.SlotId)
            .Select(o => new EnchantChoiceRow(o.EnchantId, ShortLabel(o.Label), o.Summary, o.Temporary, o.EnchantId == current))
            .ToList();
        if (_allEnchantChoices.Count == 0)
        {
            Toast($"No enchants known for {row.SlotName}.");
            return false;
        }
        EnchantSlot = row;
        EnchantPickerTitle = $"Enchant {row.ItemName}";
        EnchantFilter = "";
        FilterEnchants();
        IsEnchantPickerOpen = true;
        return true;
    }

    partial void OnEnchantFilterChanged(string value) => FilterEnchants();

    private void FilterEnchants()
    {
        var words = EnchantFilter.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        EnchantChoices.Clear();
        foreach (var c in _allEnchantChoices.Where(c => words.All(w =>
                     c.Label.Contains(w, StringComparison.OrdinalIgnoreCase) || c.Summary.Contains(w, StringComparison.OrdinalIgnoreCase))))
            EnchantChoices.Add(c);
    }

    /// <summary>Enter in the picker's filter: take the highlighted match (arrow keys), else the first one.</summary>
    [RelayCommand]
    private void ChooseFirstEnchant(EnchantChoiceRow? highlighted)
    {
        if ((highlighted ?? EnchantChoices.FirstOrDefault()) is { } choice) ChooseEnchant(choice);
    }

    [RelayCommand]
    private void ChooseEnchant(EnchantChoiceRow? choice)
    {
        if (choice is null || EnchantSlot is not { } slot || SelectedGearSet is not { } set) return;
        _gearSets.SetSlotEnchant(set.Id, slot.SlotId, choice.EnchantId);
        IsEnchantPickerOpen = false;
        AfterEnchantChange();
        Toast($"{slot.ItemName}: {choice.Label} ({choice.Summary}).");
    }

    [RelayCommand]
    private void RemoveEnchant()
    {
        if (EnchantSlot is not { } slot || SelectedGearSet is not { } set) return;
        _gearSets.ClearSlotEnchant(set.Id, slot.SlotId);
        IsEnchantPickerOpen = false;
        AfterEnchantChange();
        Toast($"Removed the enchant from {slot.ItemName}.");
    }

    private void AfterEnchantChange()
    {
        RefreshGearRows();
        Recalculate();
        if (IsUpgradePanelOpen) _ = RunUpgradesAsync();
    }
}
