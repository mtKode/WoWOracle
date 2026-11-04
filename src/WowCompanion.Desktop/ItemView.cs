using System.Globalization;
using System.Windows.Media;
using WowCompanion.Core;

namespace WowCompanion.Desktop;

public sealed record Fact(string Label, string Value);
/// <summary>One entry in a details card: a bright first line and dimmer lines under it (may contain line breaks).</summary>
public sealed record SourceLine(string Title, string Detail = "")
{
    public string Place { get; init; } = "";
    /// <summary>The place has map coordinates ("View on map").</summary>
    public bool HasMap => MapPlace.Find(Place) is not null;
    public Brush TitleBrush { get; init; } = SourceColors.Normal;
    /// <summary>Reagents, each in its item quality colour.</summary>
    public IReadOnlyList<ColoredLine> Chips { get; init; } = [];
    public bool HasChips => Chips.Count > 0;
    /// <summary>Skill-up ranges in their colours (Crafted by).</summary>
    public IReadOnlyList<ColoredLine> Bands { get; init; } = [];
    public bool HasBands => Bands.Count > 0;
    public string BandTip { get; init; } = "";
    /// <summary>Drop chance, e.g. "18%"; empty for vendors and quests.</summary>
    public string ChanceText { get; init; } = "";
}

/// <summary>Colours for the source lists, game-like: bosses orange, elites gold, vendors green, quests yellow, places blue.</summary>
public static class SourceColors
{
    public static readonly Brush Normal = Make("#EADBB8");
    public static readonly Brush Boss = Make("#FF8000");
    public static readonly Brush Elite = Make("#FFD100");
    public static readonly Brush Rare = Make("#C8C8FF");
    public static readonly Brush Vendor = Make("#8FE07A");
    public static readonly Brush Quest = Make("#FFD100");
    public static readonly Brush Profession = Make("#E8B860");
    public static readonly Brush Place = Make("#8CC4F0");

    public static Brush ForRank(int rank) => rank switch { 3 => Boss, 1 or 2 => Elite, 4 => Rare, _ => Normal };

    private static Brush Make(string hex)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        brush.Freeze();
        return brush;
    }
}

public sealed record SourceSection(string Heading, IReadOnlyList<SourceLine> Lines);

/// <summary>A tooltip line with its own colour (set pieces, set bonuses).</summary>
public sealed record ColoredLine(string Text, Brush Brush);

/// <summary>Everything the detail pane shows for the selected item, already turned into text.</summary>
public sealed record ItemView(
    int Id,
    string Name,
    Brush NameBrush,
    string LevelLine,
    string BindingLine,
    string KindLine,
    IReadOnlyList<string> StatLines,
    string RequirementLine,
    string Description,
    IReadOnlyList<Fact> Facts,
    IReadOnlyList<SourceSection> Sections,
    int? CraftSpellId)
{
    public string UniqueLine { get; init; } = "";
    public string SlotLine { get; init; } = "";
    public string TypeLine { get; init; } = "";
    public string DamageLine { get; init; } = "";
    public string SpeedLine { get; init; } = "";
    public string DpsLine { get; init; } = "";
    public string ArmorLine { get; init; } = "";
    public IReadOnlyList<string> EffectLines { get; init; } = [];
    public string ClassLine { get; init; } = "";
    public string SetTitle { get; init; } = "";
    public IReadOnlyList<ColoredLine> SetLines { get; init; } = [];

    public int IconId { get; init; }
    public bool CanCraft => CraftSpellId is not null;
    public bool HasSet => SetTitle.Length > 0;

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly Brush Grey = Freeze("#9D9D9D");
    private static readonly Brush Pale = Freeze("#FFD100");

    /// <summary>The whole card as plain text, for pasting into chat or notes.</summary>
    public string ToPlainText()
    {
        var lines = new List<string> { Name, LevelLine, BindingLine, UniqueLine, $"{SlotLine}    {TypeLine}".Trim(), KindLine };
        if (DamageLine.Length > 0) lines.Add($"{DamageLine}    {SpeedLine}");
        lines.Add(DpsLine);
        lines.Add(ArmorLine);
        lines.AddRange(StatLines);
        lines.Add(ClassLine);
        lines.Add(RequirementLine);
        lines.AddRange(EffectLines);
        if (HasSet)
        {
            lines.Add("");
            lines.Add(SetTitle);
            lines.AddRange(SetLines.Select(l => l.Text));
        }
        lines.Add(Description);
        lines = lines.Where(l => l.Length > 0 || l == "").ToList();

        foreach (var section in Sections)
        {
            lines.Add("");
            lines.Add(section.Heading);
            foreach (var line in section.Lines)
            {
                lines.Add(line.Title);
                if (line.Detail.Length > 0) lines.AddRange(line.Detail.Split('\n').Select(x => "    " + x));
                if (line.Place.Length > 0) lines.Add("    " + line.Place);
                if (line.Chips.Count > 0) lines.Add("    Needs: " + string.Join(", ", line.Chips.Select(c => c.Text)));
            }
        }
        return string.Join(Environment.NewLine, lines.Where((l, i) => l.Length > 0 || (i > 0 && lines[i - 1].Length > 0)));
    }

    public static ItemView From(ItemDetail d)
    {
        var facts = new List<Fact>
        {
            new("Item ID", d.Id.ToString(Inv)),
            new("Quality", ItemText.QualityName(d.Quality)),
            new("Item level", d.ItemLevel.ToString(Inv)),
        };
        if (d.RequiredLevel > 0) facts.Add(new Fact("Requires level", d.RequiredLevel.ToString(Inv)));
        if (d.StackSize > 1) facts.Add(new Fact("Stack size", d.StackSize.ToString(Inv)));

        var sells = ItemText.Money(d.SellPrice);
        if (sells.Length > 0) facts.Add(new Fact("Sells for", sells));
        var costs = ItemText.Money(d.BuyPrice);
        if (costs.Length > 0) facts.Add(new Fact("Vendor price", costs));

        // Weapon numbers.
        string damage = "", speed = "", dps = "";
        if (d.DelayMs > 0 && d.DamageMax > 0)
        {
            var school = d.DamageSchool > 0 ? ItemText.SchoolIndex(d.DamageSchool) + " " : "";
            damage = $"{d.DamageMin.ToString("0", Inv)} - {d.DamageMax.ToString("0", Inv)} {school}Damage";
            speed = $"Speed {(d.DelayMs / 1000.0).ToString("0.00", Inv)}";
            var perSecond = (d.DamageMin + d.DamageMax) / 2 / (d.DelayMs / 1000.0);
            dps = $"({perSecond.ToString("0.0", Inv)} damage per second)";
            facts.Add(new Fact("DPS", perSecond.ToString("0.0", Inv)));
        }
        else if (d.DelayMs == 0 && d.DamageMax > 0 && d.InventoryType == 24)
        {
            dps = $"Adds {d.DamageMax.ToString("0.0", Inv)} damage per second";
        }

        var stats = d.Stats.Where(s => !ItemText.IsGreenStat(s.Type)).Select(s => ItemText.Stat(s.Type, s.Value)).ToList();
        var effects = d.Stats.Where(s => ItemText.IsGreenStat(s.Type))
            .Select(s => "Equip: " + ItemText.Stat(s.Type, s.Value)).ToList();
        foreach (var effect in d.Effects)
        {
            var label = ItemText.Trigger(effect.Trigger);
            var body = effect.Trigger == 6
                ? $"Teaches you {effect.SpellName}."
                : effect.Text.Length > 0 ? effect.Text : effect.SpellName;
            if (body.Length == 0) continue;
            var cooldown = effect.CooldownMs > 0 && effect.Trigger is 0 or 5 ? $" ({ItemText.Time(effect.CooldownMs)} cooldown)" : "";
            effects.Add(label + body + cooldown);
        }

        // Set block like the game: name (0/5), pieces in grey, bonuses in grey with their piece count.
        var setTitle = "";
        var setLines = new List<ColoredLine>();
        if (d.Set is { } set)
        {
            setTitle = $"{set.Name} (0/{set.Pieces.Count})";
            setLines.AddRange(set.Pieces.Select(p => new ColoredLine("  " + p.Name, p.ItemId == d.Id ? Pale : Grey)));
            setLines.AddRange(set.Bonuses.Select(b => new ColoredLine($"({b.Threshold}) Set: {b.Text}", Grey)));
        }

        var kind = ItemText.Kind(d.ClassId, d.SubclassId, d.InventoryType);
        var slot = ItemText.Slot(d.InventoryType);
        var equipment = d.ClassId is 2 or 4 && slot.Length > 0;
        var type = d.ClassId == 2 ? kind : ItemText.ArmorType(d.SubclassId, d.InventoryType);

        return new ItemView(
            d.Id,
            d.Name,
            ItemQuality.BrushFor(d.Quality),
            $"Item level {d.ItemLevel}",
            ItemText.Binding(d.Bonding),
            equipment ? "" : kind,
            stats,
            d.RequiredLevel > 0 ? $"Requires level {d.RequiredLevel}" : "",
            d.Description.Length > 0 ? $"“{d.Description}”" : "",
            facts,
            BuildSections(d),
            d.CraftedBy.Count > 0 ? d.CraftedBy[0].SpellId : (int?)null)
        {
            UniqueLine = d.MaxCount == 1 ? "Unique" : d.MaxCount > 1 ? $"Unique ({d.MaxCount})" : "",
            SlotLine = equipment ? slot : "",
            TypeLine = equipment && type != slot ? type : "",
            DamageLine = damage,
            SpeedLine = speed,
            DpsLine = dps,
            ArmorLine = d.Armor > 0 ? $"{d.Armor} Armor" : "",
            EffectLines = effects,
            ClassLine = d.Classes.Count > 0 ? "Classes: " + string.Join(", ", d.Classes) : "",
            SetTitle = setTitle,
            IconId = d.IconId,
            SetLines = setLines,
        };
    }

    private static List<SourceSection> BuildSections(ItemDetail d)
    {
        var sections = new List<SourceSection>();

        if (d.CraftedBy.Count > 0)
        {
            var lines = d.CraftedBy.Select(r =>
            {
                var learn = r.SkillEstimated ? $"~{r.MinSkill} (trainer, estimated)" : r.MinSkill.ToString();
                var detail = new List<string>();
                if (r.TaughtBy.Length > 0) detail.Add($"Learned from {r.TaughtBy}");
                return new SourceLine($"{r.Profession}, requires {learn}", string.Join("\n", detail))
                {
                    TitleBrush = SourceColors.Profession,
                    Bands = SkillBands.Lines(r.MinSkill, r.YellowAt, r.GreyAt, r.SkillEstimated),
                    BandTip = ItemText.SkillUps(r.MinSkill, r.YellowAt, r.GreyAt, r.SkillEstimated),
                    Chips = r.Reagents.Select(x => new ColoredLine($"{x.Quantity}x {x.Name}", ItemQuality.BrushFor(x.Quality))).ToList(),
                };
            }).ToList();
            sections.Add(new SourceSection("Crafted by", lines));
        }

        // Where it comes from (Classic world data). Same name and details twice (several NPC entries) show once.
        (string Kind, string Heading)[] groups =
        [
            ("drop", "Dropped by"), ("vendor", "Sold by"), ("quest", "Quest reward"), ("quest_choice", "Quest reward (choice)"),
            ("object", "Found in"), ("container", "Contained in"), ("pickpocket", "Pickpocketed from"),
            ("skinning", "Skinned from"), ("fishing", "Fished in"),
        ];
        var any = false;
        foreach (var (kind, heading) in groups)
        {
            var lines = d.Sources.Where(s => s.Kind == kind)
                .Select(s => new SourceLine(s.Name, s.Detail)
                {
                    Place = s.Place,
                    ChanceText = s.Chance > 0 && kind is not ("vendor" or "quest" or "quest_choice") ? MainViewModel.ChanceText(s.Chance) : "",
                    TitleBrush = kind switch
                    {
                        "vendor" => SourceColors.Vendor,
                        "quest" or "quest_choice" => SourceColors.Quest,
                        "container" => ItemQuality.BrushFor(s.Quality),
                        _ => SourceColors.ForRank(s.Rank),
                    },
                })
                .Distinct()
                .ToList();
            if (lines.Count == 0) continue;
            any = true;
            sections.Add(new SourceSection(lines.Count > 1 ? $"{heading} ({lines.Count})" : heading, lines));
        }
        if (any)
            sections.Add(new SourceSection("About sources",
                [new SourceLine("From Classic 1.12 world data (CMaNGOS).", "WoW Forever may differ for new or changed items.")]));
        else if (d.CraftedBy.Count == 0)
            sections.Add(new SourceSection("Source", [d.HasSourceData
                ? new SourceLine("No known source in the Classic data.", "It may be new in WoW Forever.")
                : new SourceLine("Source data not loaded yet.", "It downloads with the next data rebuild.")]));

        if (d.UsedIn.Count > 0)
        {
            var lines = d.UsedIn.Select(u => new SourceLine(u.RecipeName, $"{u.Profession}, skill {u.MinSkill}")
            {
                TitleBrush = ItemQuality.BrushFor(u.Quality),
            }).ToList();
            sections.Add(new SourceSection(lines.Count > 1 ? $"Used in recipes ({lines.Count})" : "Used in recipes", lines));
        }

        return sections;
    }

    private static Brush Freeze(string hex)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        brush.Freeze();
        return brush;
    }
}
