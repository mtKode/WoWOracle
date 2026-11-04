namespace WowCompanion.Core.Calc;

/// <summary>Every number the character sheet tracks. Ratings are kept raw and turned into % at the end.</summary>
public enum Stat
{
    Strength, Agility, Stamina, Intellect, Spirit,
    AllStatsPct, StrengthPct, AgilityPct, StaminaPct, IntellectPct, SpiritPct,
    Health, Mana, Armor,
    AttackPower, RangedAttackPower, MeleeCritPct, MeleeHitPct,
    SpellPower, HolyPower, FirePower, NaturePower, FrostPower, ShadowPower, ArcanePower,
    Healing, SpellCritPct, SpellHitPct, Mp5, Hp5, SpellPenetration,
    CritRating, HitRating, HasteRating, DefenseRating, DodgeRating, ParryRating, BlockRating, BlockValue,
    FireResist, FrostResist, NatureResist, ShadowResist, ArcaneResist, HolyResist,
    // Per-school extras from talents/buffs: % damage done and +% crit for one school.
    FireDamagePct, FrostDamagePct, ArcaneDamagePct, ShadowDamagePct, NatureDamagePct, HolyDamagePct,
    FireCritPct, FrostCritPct, ArcaneCritPct, ShadowCritPct, NatureCritPct, HolyCritPct,
}

/// <summary>A bag of stat amounts that add together (base + gear + talents + buffs).</summary>
public sealed class StatBag
{
    private readonly Dictionary<Stat, double> _values = new();

    public double this[Stat stat]
    {
        get => _values.GetValueOrDefault(stat);
        set => _values[stat] = value;
    }

    public void Add(Stat stat, double amount)
    {
        if (amount != 0) _values[stat] = this[stat] + amount;
    }

    public void Add(StatBag other)
    {
        foreach (var (stat, value) in other._values) Add(stat, value);
    }

    public IEnumerable<KeyValuePair<Stat, double>> NonZero => _values.Where(v => v.Value != 0);

    /// <summary>School index 0..6 (physical, holy, fire, nature, frost, shadow, arcane) to its stats.</summary>
    public static Stat? SchoolPower(int school) => school switch
    {
        1 => Stat.HolyPower, 2 => Stat.FirePower, 3 => Stat.NaturePower, 4 => Stat.FrostPower,
        5 => Stat.ShadowPower, 6 => Stat.ArcanePower, _ => null,
    };

    public static Stat? SchoolDamagePct(int school) => school switch
    {
        1 => Stat.HolyDamagePct, 2 => Stat.FireDamagePct, 3 => Stat.NatureDamagePct, 4 => Stat.FrostDamagePct,
        5 => Stat.ShadowDamagePct, 6 => Stat.ArcaneDamagePct, _ => null,
    };

    public static Stat? SchoolCritPct(int school) => school switch
    {
        1 => Stat.HolyCritPct, 2 => Stat.FireCritPct, 3 => Stat.NatureCritPct, 4 => Stat.FrostCritPct,
        5 => Stat.ShadowCritPct, 6 => Stat.ArcaneCritPct, _ => null,
    };

    public static Stat? SchoolResist(int school) => school switch
    {
        1 => Stat.HolyResist, 2 => Stat.FireResist, 3 => Stat.NatureResist, 4 => Stat.FrostResist,
        5 => Stat.ShadowResist, 6 => Stat.ArcaneResist, _ => null,
    };

    /// <summary>Schools in a school mask (bit 0 physical .. bit 6 arcane).</summary>
    public static IEnumerable<int> Schools(int mask)
    {
        for (var i = 0; i < 7; i++)
            if ((mask & (1 << i)) != 0) yield return i;
    }

    /// <summary>Item stat type (ItemSparse StatModifier_bonusStat) into the bag.</summary>
    public void AddItemStat(int type, double value)
    {
        switch (type)
        {
            case 0: Add(Stat.Mana, value); break;
            case 1: Add(Stat.Health, value); break;
            case 3: Add(Stat.Agility, value); break;
            case 4: Add(Stat.Strength, value); break;
            case 5: Add(Stat.Intellect, value); break;
            case 6: Add(Stat.Spirit, value); break;
            case 7: Add(Stat.Stamina, value); break;
            case 12: Add(Stat.DefenseRating, value); break;
            case 13: Add(Stat.DodgeRating, value); break;
            case 14: Add(Stat.ParryRating, value); break;
            case 15: Add(Stat.BlockRating, value); break;
            case 31: Add(Stat.HitRating, value); break;
            case 32: Add(Stat.CritRating, value); break;
            case 36: Add(Stat.HasteRating, value); break;
            case 38: Add(Stat.AttackPower, value); break;
            case 39: Add(Stat.RangedAttackPower, value); break;
            case 41: Add(Stat.Healing, value); break;
            case 42: Add(Stat.SpellPower, value); break;
            case 43: Add(Stat.Mp5, value); break;
            case 45: Add(Stat.SpellPower, value); Add(Stat.Healing, value); break; // spell power: damage and healing
            case 46: Add(Stat.Hp5, value); break;
            case 47: Add(Stat.SpellPenetration, value); break;
            case 48: Add(Stat.BlockValue, value); break;
            case 50: Add(Stat.Armor, value); break;
            case 51: Add(Stat.FireResist, value); break;
            case 52: Add(Stat.FrostResist, value); break;
            case 53: Add(Stat.HolyResist, value); break;
            case 54: Add(Stat.ShadowResist, value); break;
            case 55: Add(Stat.NatureResist, value); break;
            case 56: Add(Stat.ArcaneResist, value); break;
            case 84: Add(Stat.HolyPower, value); break;
            case 85: Add(Stat.FirePower, value); break;
            case 86: Add(Stat.NaturePower, value); break;
            case 87: Add(Stat.FrostPower, value); break;
            case 88: Add(Stat.ShadowPower, value); break;
            case 89: Add(Stat.ArcanePower, value); break;
        }
    }

    /// <summary>
    /// A passive aura from an equip effect, talent or buff. Spell modifiers (107/108) are not stats and are
    /// handled by the damage engine. Returns false when the aura isn't understood.
    /// </summary>
    public bool AddAura(int aura, int misc, double amount)
    {
        switch (aura)
        {
            case 13: // +spell damage, misc = school mask
                if ((misc & 0x7E) == 0x7E) Add(Stat.SpellPower, amount);
                else foreach (var school in Schools(misc)) if (SchoolPower(school) is { } s) Add(s, amount);
                return true;
            case 135: Add(Stat.Healing, amount); return true;
            case 99: Add(Stat.AttackPower, amount); return true;
            case 124: Add(Stat.RangedAttackPower, amount); return true;
            case 85: Add(Stat.Mp5, amount); return true;
            case 22: // resistances, misc = school mask
                foreach (var school in Schools(misc))
                    if (school == 0) Add(Stat.Armor, amount);
                    else if (SchoolResist(school) is { } r) Add(r, amount);
                return true;
            case 29: // flat stat, misc = 0 str .. 4 spi, -1 all
                foreach (var s in StatIndex(misc)) Add(s, amount);
                return true;
            case 137: // % stat
                if (misc == -1) Add(Stat.AllStatsPct, amount);
                else foreach (var s in StatIndex(misc)) Add(PctOf(s), amount);
                return true;
            case 52: Add(Stat.MeleeCritPct, amount); return true;
            case 54: Add(Stat.MeleeHitPct, amount); return true;
            case 55: Add(Stat.SpellHitPct, amount); return true;
            case 57: Add(Stat.SpellCritPct, amount); return true;
            case 71: // spell crit % for a school
                foreach (var school in Schools(misc)) if (SchoolCritPct(school) is { } c) Add(c, amount);
                return true;
            case 79: // % damage done, misc = school mask
                foreach (var school in Schools(misc)) if (SchoolDamagePct(school) is { } d) Add(d, amount);
                return true;
            case 34: Add(Stat.Health, amount); return true;
            case 35: Add(Stat.Mana, amount); return true;
            default:
                return false;
        }
    }

    private static IEnumerable<Stat> StatIndex(int misc) => misc switch
    {
        0 => new[] { Stat.Strength },
        1 => new[] { Stat.Agility },
        2 => new[] { Stat.Stamina },
        3 => new[] { Stat.Intellect },
        4 => new[] { Stat.Spirit },
        -1 => new[] { Stat.Strength, Stat.Agility, Stat.Stamina, Stat.Intellect, Stat.Spirit },
        _ => Array.Empty<Stat>(),
    };

    private static Stat PctOf(Stat stat) => stat switch
    {
        Stat.Strength => Stat.StrengthPct,
        Stat.Agility => Stat.AgilityPct,
        Stat.Stamina => Stat.StaminaPct,
        Stat.Intellect => Stat.IntellectPct,
        _ => Stat.SpiritPct,
    };
}
