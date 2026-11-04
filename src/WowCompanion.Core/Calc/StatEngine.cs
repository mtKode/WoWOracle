using System.Globalization;

namespace WowCompanion.Core.Calc;

/// <summary>A talent rank's spell and how strongly it applies (trait talents use one spell scaled by rank).</summary>
public sealed record TalentSpell(int SpellId, double Multiplier, string Source)
{
    /// <summary>Effect index → value at this rank, for trait talents whose values come from a curve.</summary>
    public IReadOnlyDictionary<int, double>? Points { get; init; }

    /// <summary>An effect's amount at this talent's rank.</summary>
    public double Amount(EffectRow e) =>
        Points is not null && Points.TryGetValue(e.Index, out var value) ? value : e.BasePoints * Multiplier;
}

public sealed class CharacterInput
{
    public int ClassId { get; init; }
    public int RaceId { get; init; }
    public int Level { get; init; } = 60;
    /// <summary>Naked Str, Agi, Sta, Int, Spi from the character sheet; null entries use the estimate.</summary>
    public double?[] NakedStats { get; init; } = new double?[5];
    public IReadOnlyList<GearItem> Gear { get; init; } = [];
    public IReadOnlyList<TalentSpell> Talents { get; init; } = [];
    public IReadOnlyList<Buff> Buffs { get; init; } = [];
    /// <summary>Effects for every equip and talent spell (from CalcDataService.GetEffects).</summary>
    public IReadOnlyDictionary<int, List<EffectRow>> Effects { get; init; } = new Dictionary<int, List<EffectRow>>();
}

public sealed record StatLine(string Group, string Label, string Value);

/// <summary>The finished character sheet.</summary>
public sealed class CharacterSheet
{
    public int ClassId { get; init; }
    public int Level { get; init; }
    public StatBag Totals { get; init; } = new();
    public StatBag GearOnly { get; init; } = new();
    public double Strength { get; init; }
    public double Agility { get; init; }
    public double Stamina { get; init; }
    public double Intellect { get; init; }
    public double Spirit { get; init; }
    public double Health { get; init; }
    public double Mana { get; init; }
    public double BaseMana { get; init; }
    public double Armor { get; init; }
    public double AttackPower { get; init; }
    public double RangedAttackPower { get; init; }
    public double MeleeCrit { get; init; }
    public double MeleeHit { get; init; }
    public double SpellCrit { get; init; }
    public double SpellHit { get; init; }
    public bool UsesEstimate { get; init; }
    public List<string> Notes { get; } = [];

    /// <summary>Bonus healing: spell power items (damage and healing), healing items and auras.</summary>
    public double HealingPower => Totals[Stat.Healing];

    public double SpellPowerFor(int school) =>
        Totals[Stat.SpellPower] + (StatBag.SchoolPower(school) is { } s ? Totals[s] : 0);

    public double SpellCritFor(int school) =>
        SpellCrit + (StatBag.SchoolCritPct(school) is { } s ? Totals[s] : 0);

    public double DamagePctFor(int school) =>
        StatBag.SchoolDamagePct(school) is { } s ? Totals[s] : 0;

    public IReadOnlyList<StatLine> Lines()
    {
        var inv = CultureInfo.InvariantCulture;
        string N(double v) => Math.Round(v).ToString("0", inv);
        string P(double v) => v.ToString("0.00", inv) + "%";

        var lines = new List<StatLine>
        {
            new("Attributes", "Strength", N(Strength)),
            new("Attributes", "Agility", N(Agility)),
            new("Attributes", "Stamina", N(Stamina)),
            new("Attributes", "Intellect", N(Intellect)),
            new("Attributes", "Spirit", N(Spirit)),
            new("Vitals", "Health", N(Health)),
        };
        if (Mana > 0) lines.Add(new("Vitals", "Mana", N(Mana)));
        lines.Add(new("Vitals", "Armor", N(Armor)));

        lines.Add(new("Melee", "Attack power", N(AttackPower)));
        lines.Add(new("Melee", "Crit chance", P(MeleeCrit)));
        lines.Add(new("Melee", "Hit chance", P(MeleeHit)));
        lines.Add(new("Melee", "Ranged attack power", N(RangedAttackPower)));

        lines.Add(new("Spell", "Spell power", N(Totals[Stat.SpellPower])));
        foreach (var (stat, label) in new[]
                 {
                     (Stat.FirePower, "Fire power"), (Stat.FrostPower, "Frost power"), (Stat.ArcanePower, "Arcane power"),
                     (Stat.ShadowPower, "Shadow power"), (Stat.NaturePower, "Nature power"), (Stat.HolyPower, "Holy power"),
                 })
        {
            if (Totals[stat] != 0) lines.Add(new("Spell", label, "+" + N(Totals[stat])));
        }
        if (Totals[Stat.Healing] != 0) lines.Add(new("Spell", "Healing power", N(Totals[Stat.Healing])));
        lines.Add(new("Spell", "Crit chance", P(SpellCrit)));
        lines.Add(new("Spell", "Hit chance", P(SpellHit)));
        if (Totals[Stat.Mp5] != 0) lines.Add(new("Spell", "Mana per 5 sec", N(Totals[Stat.Mp5])));
        if (Totals[Stat.SpellPenetration] != 0) lines.Add(new("Spell", "Spell penetration", N(Totals[Stat.SpellPenetration])));
        if (Totals[Stat.HasteRating] != 0)
            lines.Add(new("Spell", "Haste", P(Totals[Stat.HasteRating] / CharacterBase.HasteRatingPerPct)));

        foreach (var (stat, label) in new[]
                 {
                     (Stat.FireResist, "Fire"), (Stat.FrostResist, "Frost"), (Stat.NatureResist, "Nature"),
                     (Stat.ShadowResist, "Shadow"), (Stat.ArcaneResist, "Arcane"),
                 })
        {
            if (Totals[stat] != 0) lines.Add(new("Resistances", label, N(Totals[stat])));
        }
        if (Totals[Stat.DefenseRating] != 0) lines.Add(new("Defense", "Defense rating", N(Totals[Stat.DefenseRating])));
        if (Totals[Stat.DodgeRating] != 0) lines.Add(new("Defense", "Dodge rating", N(Totals[Stat.DodgeRating])));
        if (Totals[Stat.BlockValue] != 0) lines.Add(new("Defense", "Block value", N(Totals[Stat.BlockValue])));
        return lines;
    }
}

/// <summary>Builds the character sheet: naked base + gear + talents + buffs, then Classic conversions.</summary>
public static class StatEngine
{
    public static CharacterSheet Compute(CharacterInput input)
    {
        var gear = new StatBag();
        var notes = new List<string>();
        var unknownAuras = 0;

        foreach (var item in input.Gear)
        {
            foreach (var stat in item.Stats) gear.AddItemStat(stat.Type, stat.Value);
            gear.Add(Stat.Armor, item.Armor);
            foreach (var spell in item.EquipSpells)
            {
                foreach (var e in input.Effects.GetValueOrDefault(spell) ?? new List<EffectRow>())
                {
                    if (e.Effect != 6 || e.Aura is 107 or 108 or 42 or 4 or 23) continue; // modifiers/procs handled elsewhere
                    if (!gear.AddAura(e.Aura, e.Misc0, e.BasePoints)) unknownAuras++;
                }
            }
        }

        var talents = new StatBag();
        foreach (var talent in input.Talents)
        {
            foreach (var e in input.Effects.GetValueOrDefault(talent.SpellId) ?? new List<EffectRow>())
            {
                if (e.Effect != 6 || e.Aura is 107 or 108) continue;
                // Talents that only touch certain spells (a class mask) are spell modifiers, not stats.
                if (e.Mask.Any(m => m != 0) && e.Aura is not (29 or 137)) continue;
                talents.AddAura(e.Aura, e.Misc0, talent.Amount(e));
            }
        }

        var buffs = new StatBag();
        foreach (var buff in input.Buffs)
            foreach (var (stat, amount) in buff.Effects) buffs.Add(stat, amount);

        var totals = new StatBag();
        totals.Add(gear);
        totals.Add(talents);
        totals.Add(buffs);
        if (CharacterBase.RacialPct(input.RaceId) is { } racial) totals.Add(racial.Stat, racial.Pct);

        var estimate = CharacterBase.Primary(input.ClassId, input.RaceId, input.Level);
        var usesEstimate = input.NakedStats.Any(v => v is null);
        Stat[] primary = [Stat.Strength, Stat.Agility, Stat.Stamina, Stat.Intellect, Stat.Spirit];
        Stat[] percent = [Stat.StrengthPct, Stat.AgilityPct, Stat.StaminaPct, Stat.IntellectPct, Stat.SpiritPct];
        var values = new double[5];
        for (var i = 0; i < 5; i++)
        {
            var naked = input.NakedStats.Length > i && input.NakedStats[i] is { } v ? v : estimate[i];
            var multiplier = 1 + (totals[Stat.AllStatsPct] + totals[percent[i]]) / 100;
            values[i] = Math.Floor((naked + totals[primary[i]]) * multiplier);
        }
        var (str, agi, sta, intel, spi) = (values[0], values[1], values[2], values[3], values[4]);

        var (baseHealth, baseMana) = CharacterBase.BaseVitals(input.ClassId, input.Level);
        var health = baseHealth + Math.Max(0, sta - 20) * 10 + Math.Min(sta, 20) + totals[Stat.Health];
        var mana = CharacterBase.UsesMana(input.ClassId)
            ? baseMana + Math.Max(0, intel - 20) * 15 + Math.Min(intel, 20) + totals[Stat.Mana]
            : 0;

        var (agiPerCrit, meleeBase) = CharacterBase.MeleeCrit(input.ClassId);
        var (intPerCrit, spellBase) = CharacterBase.SpellCrit(input.ClassId);
        var critFromRating = totals[Stat.CritRating] / CharacterBase.CritRatingPerPct;
        var hitFromRating = totals[Stat.HitRating] / CharacterBase.HitRatingPerPct;

        if (unknownAuras > 0) notes.Add($"{unknownAuras} gear effects aren't counted yet (procs and special effects).");
        if (usesEstimate) notes.Add("Naked stats are estimates. Enter yours for exact numbers.");

        var sheet = new CharacterSheet
        {
            ClassId = input.ClassId,
            Level = input.Level,
            Totals = totals,
            GearOnly = gear,
            Strength = str,
            Agility = agi,
            Stamina = sta,
            Intellect = intel,
            Spirit = spi,
            Health = health,
            Mana = mana,
            BaseMana = baseMana,
            Armor = totals[Stat.Armor] + agi * 2,
            AttackPower = CharacterBase.AttackPower(input.ClassId, input.Level, str, agi) + totals[Stat.AttackPower],
            RangedAttackPower = CharacterBase.RangedAttackPower(input.ClassId, input.Level, agi)
                                + totals[Stat.RangedAttackPower],
            MeleeCrit = meleeBase + (agiPerCrit > 0 ? agi / agiPerCrit : 0) + critFromRating + totals[Stat.MeleeCritPct],
            MeleeHit = hitFromRating + totals[Stat.MeleeHitPct],
            SpellCrit = intPerCrit > 0 ? spellBase + intel / intPerCrit + critFromRating + totals[Stat.SpellCritPct] : 0,
            SpellHit = hitFromRating + totals[Stat.SpellHitPct],
            UsesEstimate = usesEstimate,
        };
        sheet.Notes.AddRange(notes);
        return sheet;
    }
}
