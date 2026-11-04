namespace WowCompanion.Core;

public sealed record GearSlot(int Id, string Name);

/// <summary>The character sheet slots, in paper-doll order, and which slots an item can go in.</summary>
public static class GearSlots
{
    public const int MainHand = 16, OffHand = 17;

    public static readonly IReadOnlyList<GearSlot> All =
    [
        new(1, "Head"), new(2, "Neck"), new(3, "Shoulder"), new(15, "Back"), new(5, "Chest"), new(4, "Shirt"),
        new(19, "Tabard"), new(9, "Wrist"), new(10, "Hands"), new(6, "Waist"), new(7, "Legs"), new(8, "Feet"),
        new(11, "Finger 1"), new(12, "Finger 2"), new(13, "Trinket 1"), new(14, "Trinket 2"),
        new(16, "Main Hand"), new(17, "Off Hand"), new(18, "Ranged"),
    ];

    public static string NameOf(int slot) => All.FirstOrDefault(s => s.Id == slot)?.Name ?? $"Slot {slot}";

    /// <summary>Slots an item with this InventoryType fits, best first. Empty when it can't be equipped.</summary>
    public static int[] SlotsFor(int inventoryType) => inventoryType switch
    {
        1 => new[] { 1 },
        2 => new[] { 2 },
        3 => new[] { 3 },
        4 => new[] { 4 },
        5 or 20 => new[] { 5 },
        6 => new[] { 6 },
        7 => new[] { 7 },
        8 => new[] { 8 },
        9 => new[] { 9 },
        10 => new[] { 10 },
        11 => new[] { 11, 12 },
        12 => new[] { 13, 14 },
        13 => new[] { MainHand, OffHand },   // one-hand
        14 or 22 or 23 => new[] { OffHand }, // shield, off-hand weapon, held in off-hand
        15 or 25 or 26 or 28 => new[] { 18 },
        16 => new[] { 15 },
        17 or 21 => new[] { MainHand },      // two-hand, main hand
        19 => new[] { 19 },
        _ => Array.Empty<int>(),
    };

    public static bool IsTwoHand(int inventoryType) => inventoryType == 17;
}
