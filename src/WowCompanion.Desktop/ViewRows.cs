using System.Windows.Media;
using WowCompanion.Core;

namespace WowCompanion.Desktop;

public enum AppView { Items, Recipes, Craft, Spells, Talents, Gear, Calc, Loot, Notes, Journal, Map }

/// <summary>Skill-up bands in the game's colours: orange always gives a point, yellow usually, green sometimes, grey never.</summary>
public static class SkillBands
{
    public static readonly System.Windows.Media.Brush Orange = Make("#FF8040");
    public static readonly System.Windows.Media.Brush Yellow = Make("#FFFF00");
    public static readonly System.Windows.Media.Brush Green = Make("#40BF40");
    public static readonly System.Windows.Media.Brush Grey = Make("#9D9D9D");

    /// <summary>"110-129" style ranges per colour; a band that is empty (e.g. yellow and grey at the same skill) is left out.</summary>
    public static (string Orange, string Yellow, string Green, string Grey) Ranges(int learn, int yellow, int grey, bool estimated)
    {
        if (yellow <= 0 || grey <= 0) return ("", "", "", "");
        // Same yellow and grey: the data only holds the learn skill, not the skill-up range.
        if (yellow == grey) return ($"learn at {(estimated ? "~" : "")}{Math.Max(learn, yellow)}", "", "", "range not in the game data");
        var green = (yellow + grey) / 2;
        string Range(string from, int fromValue, int to) => to >= fromValue ? $"{from}-{to}" : "";
        return (
            // A guessed learn skill (the yellow point): when the orange part starts isn't known.
            estimated ? $"learn ~{learn}" : Range(learn.ToString(), learn, Math.Max(learn, yellow - 1)),
            Range(yellow.ToString(), yellow, green - 1),
            Range(green.ToString(), green, grey - 1),
            $"{grey}+");
    }

    public static IReadOnlyList<ColoredLine> Lines(int learn, int yellow, int grey, bool estimated)
    {
        var (o, y, g, gr) = Ranges(learn, yellow, grey, estimated);
        var list = new List<ColoredLine>();
        if (o.Length > 0) list.Add(new ColoredLine(o, Orange));
        if (y.Length > 0) list.Add(new ColoredLine(y, Yellow));
        if (g.Length > 0) list.Add(new ColoredLine(g, Green));
        if (gr.Length > 0) list.Add(new ColoredLine(gr, Grey));
        return list;
    }

    private static System.Windows.Media.Brush Make(string hex)
    {
        var brush = (System.Windows.Media.SolidColorBrush)new System.Windows.Media.BrushConverter().ConvertFromString(hex)!;
        brush.Freeze();
        return brush;
    }
}

/// <summary>One recipe in the Recipes view.</summary>
public sealed record RecipeRow(int SpellId, string Name, string Meta, string ReagentText, Brush NameBrush, int IconId = 0)
{
    // Skill-up colour bands: orange always gives a point, yellow usually, green sometimes, grey never.
    public string Orange { get; init; } = "";
    public string Yellow { get; init; } = "";
    public string Green { get; init; } = "";
    public string Grey { get; init; } = "";
    public string SkillTip { get; init; } = "";
    /// <summary>Reagents, each in its item quality colour.</summary>
    public IReadOnlyList<ColoredLine> ReagentChips { get; init; } = [];
    /// <summary>The item the recipe makes (0 for enchants and the like). Alt-compare uses it.</summary>
    public int CreatedItemId { get; init; }

    // The crafted item: "Chest · Cloth · item level 55 · requires level 50", then its stats, then green effects.
    public string ItemLine { get; init; } = "";
    public string StatLine { get; init; } = "";
    public string EffectLine { get; init; } = "";

    private static (string Item, string Stats, string Effects) DescribeItem(RecipeSummary r)
    {
        if (r.CreatedItemId <= 0) return ("", "", "");
        var inv = System.Globalization.CultureInfo.InvariantCulture;

        var head = new List<string>();
        var slot = ItemText.Slot(r.InventoryType);
        var kind = r.ClassId == 2 ? ItemText.Kind(r.ClassId, r.SubclassId, r.InventoryType) : ItemText.ArmorType(r.SubclassId, r.InventoryType);
        if (r.ClassId is 2 or 4 && slot.Length > 0)
        {
            head.Add(slot);
            if (kind.Length > 0 && kind != slot) head.Add(kind);
        }
        if (r.ItemLevel > 0) head.Add($"item level {r.ItemLevel}");
        if (r.RequiredLevel > 0) head.Add($"requires level {r.RequiredLevel}");

        var stats = new List<string>();
        if (r.DelayMs > 0 && r.DamageMax > 0)
        {
            var speed = r.DelayMs / 1000.0;
            var dps = (r.DamageMin + r.DamageMax) / 2 / speed;
            stats.Add($"{r.DamageMin.ToString("0", inv)}-{r.DamageMax.ToString("0", inv)} dmg, speed {speed.ToString("0.00", inv)} ({dps.ToString("0.0", inv)} dps)");
        }
        if (r.Armor > 0) stats.Add($"{r.Armor} Armor");
        foreach (var pair in r.Stats.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split(':');
            if (parts.Length == 2 && int.TryParse(parts[0], out var type) && int.TryParse(parts[1], out var value))
                stats.Add(ItemText.Stat(type, value));
        }

        var effects = r.Effects.Split('|', StringSplitOptions.RemoveEmptyEntries)
            .Select(e => e.Split('~', 2))
            .Where(p => p.Length == 2 && int.TryParse(p[0], out _))
            .Select(p => ItemText.Trigger(int.Parse(p[0])) + p[1]);

        return (string.Join(" · ", head), string.Join(", ", stats), string.Join("  ", effects));
    }

    /// <summary>"3x Linen Cloth · 66c": the auction price for the quantity when Auctionator has one.</summary>
    public static string PricedReagent(int itemId, int quantity, string name)
    {
        var price = AuctionPrices.Text(itemId, quantity);
        return price.Length > 0 ? $"{quantity}x {name} · {price}" : $"{quantity}x {name}";
    }

    public static RecipeRow From(RecipeSummary r)
    {
        var learn = r.SkillEstimated ? $"~{r.MinSkill}" : r.MinSkill.ToString();
        var source = r.TaughtBy.Length > 0 ? $" · from {r.TaughtBy}" : r.SkillEstimated ? " · trainer (skill estimated)" : "";
        var bands = SkillBands.Ranges(r.MinSkill, r.YellowAt, r.GreyAt, r.SkillEstimated);
        var item = DescribeItem(r);
        return new RecipeRow(
            r.SpellId,
            r.Name,
            $"{r.Profession} · requires {learn}{source}",
            r.ReagentText.Length > 0 ? r.ReagentText : "No reagents listed",
            r.CreatedItemId > 0 ? ItemQuality.BrushFor(r.Quality) : ItemQuality.BrushFor(1),
            RecipeIcons.Or(r.IconId, r.MinSkill)) // enchants: the enchanting material of their skill bracket
        {
            Orange = bands.Orange,
            Yellow = bands.Yellow,
            Green = bands.Green,
            Grey = bands.Grey,
            SkillTip = ItemText.SkillUps(r.MinSkill, r.YellowAt, r.GreyAt, r.SkillEstimated),
            ReagentChips = r.Reagents.Select(x => new ColoredLine(PricedReagent(x.ItemId, x.Quantity, x.Name), ItemQuality.BrushFor(x.Quality))).ToList(),
            ItemLine = item.Item,
            StatLine = item.Stats,
            EffectLine = item.Effects,
            CreatedItemId = r.CreatedItemId,
        };
    }
}

/// <summary>One queued recipe in the To craft view.</summary>
public sealed record CraftRow(int SpellId, string Name, string Meta, int Quantity, Brush NameBrush, int IconId = 0)
{
    public bool Done { get; init; }
    public double RowOpacity => Done ? 0.45 : 1;
    public string DoneText => Done ? "Undo" : "✓ Done";
    public string DoneTip => Done ? "Put it back on the list" : "Crafted: tick it off (its reagents leave the shopping list)";
}

/// <summary>Total of one reagent across everything queued.</summary>
public sealed record ShoppingRow(string Name, int Quantity, Brush NameBrush, int ItemId = 0)
{
    public string Amount => $"{Quantity}x";
    public string Text => $"{Quantity}x {Name}"; // copied as is (Auctionator's shopping list import takes names)
    /// <summary>Auction price for the whole quantity (Auctionator), "" when unknown.</summary>
    public string PriceText { get; } = AuctionPrices.Label(ItemId, Quantity);
}
