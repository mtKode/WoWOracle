using System.Globalization;
using System.Windows.Media;
using WowCompanion.Core;

namespace WowCompanion.Desktop;

/// <summary>One spell in the Spells list.</summary>
public sealed record SpellRow(int Id, string Name, string Meta, int IconId = 0)
{
    public static SpellRow From(SpellSummary s)
    {
        var parts = new List<string>();
        if (s.Rank.Length > 0) parts.Add(s.Rank);
        var school = ItemText.SchoolMask(s.SchoolMask);
        if (school.Length > 0 && school != "Physical") parts.Add(school);
        if (s.SpellLevel > 0) parts.Add($"level {s.SpellLevel}");
        return new SpellRow(s.Id, s.Name, string.Join(" · ", parts), s.IconId);
    }
}

/// <summary>The spell tooltip plus the numbers behind it.</summary>
public sealed record SpellView(
    int Id,
    string Name,
    string Rank,
    string CostLine,
    string RangeLine,
    string CastLine,
    string CooldownLine,
    string Description,
    IReadOnlyList<Fact> Facts,
    IReadOnlyList<string> EffectLines)
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public int IconId { get; init; }

    public string ToPlainText()
    {
        var lines = new List<string> { $"{Name} {Rank}".Trim(), CostLine, RangeLine, CastLine, CooldownLine, Description, "" };
        lines.AddRange(Facts.Select(f => $"{f.Label}: {f.Value}"));
        lines.AddRange(EffectLines);
        return string.Join(Environment.NewLine, lines.Where(l => l.Length > 0));
    }

    public static SpellView From(SpellDetail d)
    {
        var cost = "";
        var power = ItemText.PowerName(d.PowerType);
        if (d.PowerCost > 0) cost = $"{d.PowerCost} {power}";
        else if (d.PowerCostPct > 0) cost = $"{d.PowerCostPct}% of base {power.ToLowerInvariant()}";
        if (d.PowerPerSecond > 0) cost += $", plus {d.PowerPerSecond} per sec";

        var range = d.RangeMax > 0 ? $"{d.RangeMax.ToString("0", Inv)} yd range" : d.RangeName;
        var cast = d.CastMs > 0 ? $"{ItemText.Time(d.CastMs)} cast" : "Instant";
        var cooldown = d.CooldownMs > 0 ? $"{ItemText.Time(d.CooldownMs)} cooldown"
            : d.CategoryCooldownMs > 0 ? $"{ItemText.Time(d.CategoryCooldownMs)} cooldown" : "";

        var facts = new List<Fact> { new("Spell ID", d.Id.ToString(Inv)) };
        if (d.ClassName.Length > 0) facts.Add(new Fact("Class", d.ClassName));
        if (d.SkillLine.Length > 0) facts.Add(new Fact("Skill line", d.SkillLine));
        var school = ItemText.SchoolMask(d.SchoolMask);
        if (school.Length > 0) facts.Add(new Fact("School", school));
        if (d.SpellLevel > 0) facts.Add(new Fact("Learned at", $"level {d.SpellLevel}"));
        if (d.MaxLevel > 0) facts.Add(new Fact("Scales until", $"level {d.MaxLevel}"));
        if (d.DurationMs > 0) facts.Add(new Fact("Duration", ItemText.Time(d.DurationMs)));
        if (d.GcdMs > 0) facts.Add(new Fact("Global cooldown", ItemText.Time(d.GcdMs)));
        if (d.ProcChance is > 0 and < 101) facts.Add(new Fact("Proc chance", $"{d.ProcChance}%"));

        var effects = new List<string>();
        foreach (var e in d.Effects)
        {
            var what = EffectName(e);
            if (what.Length == 0) continue;
            var parts = new List<string>();
            if (e.BasePoints != 0)
            {
                var value = Math.Abs(e.BasePoints);
                if (e.Variance > 0)
                {
                    var min = Math.Round(value * (1 - e.Variance / 2), MidpointRounding.AwayFromZero);
                    var max = Math.Round(value * (1 + e.Variance / 2), MidpointRounding.AwayFromZero);
                    parts.Add($"{min.ToString("0", Inv)}-{max.ToString("0", Inv)}");
                }
                else parts.Add(value.ToString("0.##", Inv));
            }
            if (e.PerLevel != 0) parts.Add($"+{e.PerLevel.ToString("0.##", Inv)} per level");
            if (e.PeriodMs > 0)
            {
                var ticks = d.DurationMs > 0 ? d.DurationMs / e.PeriodMs : 0;
                parts.Add(ticks > 0 ? $"every {ItemText.Time(e.PeriodMs)}, {ticks} ticks" : $"every {ItemText.Time(e.PeriodMs)}");
            }
            if (e.SpellCoefficient > 0)
            {
                var ticks = e.PeriodMs > 0 && d.DurationMs > 0 ? d.DurationMs / e.PeriodMs : 1;
                var total = e.SpellCoefficient * Math.Max(1, ticks);
                parts.Add(ticks > 1
                    ? $"spell power x{e.SpellCoefficient.ToString("0.###", Inv)} per tick ({(total * 100).ToString("0.#", Inv)}% total)"
                    : $"spell power x{e.SpellCoefficient.ToString("0.###", Inv)} ({(e.SpellCoefficient * 100).ToString("0.#", Inv)}%)");
            }
            if (e.ApCoefficient > 0) parts.Add($"attack power x{e.ApCoefficient.ToString("0.###", Inv)}");
            if (e.TriggerSpell > 0) parts.Add($"triggers spell {e.TriggerSpell}");
            effects.Add($"#{e.Index + 1} {what}" + (parts.Count > 0 ? ": " + string.Join(", ", parts) : ""));
        }

        return new SpellView(d.Id, d.Name, d.Rank, cost, range, cast, cooldown, d.Description, facts, effects)
        {
            IconId = d.IconId,
        };
    }


    private static string EffectName(SpellEffectInfo e) => e.Effect switch
    {
        2 => "School damage",
        3 => "Dummy",
        6 => AuraName(e.Aura),
        10 => "Heal",
        24 => "Create item",
        27 => "Persistent area aura",
        30 => "Energize",
        35 => "Area aura",
        36 => "Learn spell",
        58 => "Weapon damage",
        64 => "Trigger spell",
        77 => "Script effect",
        121 => "Normalized weapon damage",
        17 => "Weapon damage (flat)",
        31 => "Weapon damage (%)",
        _ => $"Effect {e.Effect}",
    };

    private static string AuraName(int aura) => aura switch
    {
        3 => "Damage over time",
        4 => "Dummy aura",
        8 => "Heal over time",
        13 => "Modify damage done",
        15 => "Damage shield",
        22 => "Modify resistance",
        23 => "Periodic trigger spell",
        29 => "Modify stat",
        31 => "Modify speed",
        42 => "Proc trigger spell",
        52 => "Modify crit %",
        53 => "Modify hit %",
        57 => "Modify spell crit %",
        65 => "Modify casting speed",
        69 => "Absorb damage",
        79 => "Modify damage done %",
        85 => "Modify mana regen",
        99 => "Modify attack power",
        107 => "Add flat modifier",
        108 => "Add % modifier",
        135 => "Modify healing done",
        137 => "Modify total stat %",
        _ => $"Aura {aura}",
    };
}
