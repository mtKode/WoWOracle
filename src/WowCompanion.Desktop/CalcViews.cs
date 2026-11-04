using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using WowCompanion.Core;
using WowCompanion.Core.Calc;

namespace WowCompanion.Desktop;

/// <summary>One slot on the Gear page.</summary>
public sealed record GearSlotRow(int SlotId, string SlotName, int ItemId, string ItemName, Brush NameBrush, int IconId)
{
    /// <summary>Enchant on this slot, e.g. "Enchant Boots - Minor Agility (+5 Agility)"; empty when none.</summary>
    public string EnchantText { get; init; } = "";
    public bool HasEnchant => EnchantText.Length > 0;
    /// <summary>The slot takes enchants (some data exists for it).</summary>
    public bool CanEnchant { get; init; }
    public bool IsEmpty => ItemId == 0;
    public bool HasItem => ItemId != 0;
}

/// <summary>A line in a stats or results box; headers start a group.</summary>
public sealed record SheetRow(string Label, string Value, bool IsHeader)
{
    public bool IsValue => !IsHeader;

    public static IEnumerable<SheetRow> FromLines(IEnumerable<StatLine> lines)
    {
        string? group = null;
        foreach (var line in lines)
        {
            if (line.Group != group)
            {
                group = line.Group;
                yield return new SheetRow(group, "", true);
            }
            yield return new SheetRow(line.Label, line.Value, false);
        }
    }
}

/// <summary>A buff checkbox in the calculator.</summary>
public partial class BuffToggle(Buff buff, Action changed) : ObservableObject
{
    public Buff Buff { get; } = buff;
    public string Name => Buff.Name;
    public string Category => Buff.Category;

    [ObservableProperty] private bool _isOn;

    partial void OnIsOnChanged(bool value) => changed();
}
