using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using WowCompanion.Core;
using WowCompanion.Core.Calc;

namespace WowCompanion.Desktop;

/// <summary>
/// Hold Alt over any item (search list, item card, recipes, gear page) to see how it compares with what the active
/// gear set wears in that slot. Two-handers compare against main + off hand together; rings and trinkets against both.
/// </summary>
public partial class MainViewModel
{
    [ObservableProperty] private CompareCard? _compareCard;

    public void ShowCompare(int itemId)
    {
        CompareCard = BuildCompare(itemId);
    }

    public void HideCompare() => CompareCard = null;

    /// <summary>The item the keyboard is on when the mouse isn't over one: dropdown highlight, then the open item card.</summary>
    public int FallbackCompareItem()
    {
        if (IsDropdownOpen && HighlightIndex >= 0 && HighlightIndex < Results.Count) return Results[HighlightIndex].Id;
        if (CurrentView == AppView.Items && Selected is not null) return Selected.Id;
        return 0;
    }

    private CompareCard? BuildCompare(int itemId)
    {
        if (_calcData is null || itemId <= 0) return null;
        var candidate = _calcData.GetGearItems(new[] { itemId }).FirstOrDefault();
        if (candidate is null) return null;

        var title = candidate.Name;
        var brush = ItemQuality.BrushFor(candidate.Quality);
        CompareCard Note(string message) => new(title, brush, "", [], message);

        var slots = GearSlots.SlotsFor(candidate.InventoryType);
        if (slots.Length == 0) return Note("Not something you can equip.");
        if (SelectedGearSet is not { } set) return Note("Make a gear set on the Gear page to compare against.");

        var worn = SetGearBySlot(set.Id);
        GearItem? At(int slot) => worn.GetValueOrDefault(slot);

        // What the candidate would replace, as groups (each group = one comparison).
        var groups = new List<(string Heading, List<GearItem> Items)>();
        var main = At(GearSlots.MainHand);
        if (GearSlots.IsTwoHand(candidate.InventoryType))
        {
            var both = new[] { main, At(GearSlots.OffHand) }.OfType<GearItem>().ToList();
            groups.Add((both.Count == 0 ? "Hands are empty" : "vs " + string.Join(" + ", both.Select(i => i.Name)), both));
        }
        else if (slots.Length == 1 && slots[0] == GearSlots.OffHand && main is not null && GearSlots.IsTwoHand(main.InventoryType))
        {
            groups.Add(($"vs {main.Name} (two-hander)", new List<GearItem> { main }));
        }
        else
        {
            foreach (var slot in slots)
            {
                var item = At(slot);
                if (item?.Id == candidate.Id) continue; // already wearing it there
                groups.Add(item is null
                    ? ($"{GearSlots.NameOf(slot)} is empty", new List<GearItem>())
                    : ("vs " + item.Name, new List<GearItem> { item }));
            }
            if (groups.Count == 0) return Note($"Already in {set.Name}.");
        }

        var newBag = GearBag(set, new List<GearItem> { candidate });
        var sections = groups.Select(g =>
        {
            var oldBag = GearBag(set, g.Items);
            var rows = Diff(newBag, oldBag);
            var oldWeapon = g.Items.FirstOrDefault(i => i.IsWeapon);
            if (candidate.IsWeapon) rows.Insert(0, DpsRow(Dps(candidate) - (oldWeapon is null ? 0 : Dps(oldWeapon))));
            var headingBrush = g.Items.Count == 1 ? ItemQuality.BrushFor(g.Items[0].Quality) : CompareColors.Neutral;
            return new CompareSection(g.Heading, headingBrush, rows);
        }).ToList();

        return new CompareCard(title, brush, $"Hold Alt · compared with {set.Name}", sections, "");
    }

    private StatBag GearBag(GearSetInfo set, IReadOnlyList<GearItem> items)
    {
        if (items.Count == 0) return new StatBag();
        var effects = _calcData!.GetEffects(items.SelectMany(i => i.EquipSpells).Distinct().ToList());
        return StatEngine.Compute(new CharacterInput { ClassId = set.ClassId, RaceId = set.RaceId, Gear = items, Effects = effects }).GearOnly;
    }

    private static List<CompareRow> Diff(StatBag next, StatBag now)
    {
        var stats = next.NonZero.Select(v => v.Key).Concat(now.NonZero.Select(v => v.Key)).Distinct().OrderBy(s => s);
        var rows = new List<CompareRow>();
        foreach (var stat in stats)
        {
            var delta = next[stat] - now[stat];
            if (Math.Abs(delta) < 0.05) continue;
            var whole = Math.Abs(delta - Math.Round(delta)) < 0.05;
            var text = (delta > 0 ? "+" : "") + delta.ToString(whole ? "0" : "0.0", CultureInfo.InvariantCulture);
            rows.Add(new CompareRow(text, Humanize(stat), delta > 0 ? CompareColors.Up : CompareColors.Down));
        }
        return rows;
    }

    private static double Dps(GearItem w) => w.DelayMs > 0 ? (w.DamageMin + w.DamageMax) / 2 / (w.DelayMs / 1000.0) : 0;

    private static CompareRow DpsRow(double delta) => new(
        (delta >= 0 ? "+" : "") + delta.ToString("0.0", CultureInfo.InvariantCulture),
        "Damage per second",
        delta > 0.05 ? CompareColors.Up : delta < -0.05 ? CompareColors.Down : CompareColors.Neutral);
}
