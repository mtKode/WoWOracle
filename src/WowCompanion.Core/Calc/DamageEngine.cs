using System.Globalization;

namespace WowCompanion.Core.Calc;

public sealed record DamageResult(IReadOnlyList<StatLine> Lines, IReadOnlyList<string> Modifiers, IReadOnlyList<string> Notes)
{
    public double ExpectedPerCast { get; init; }
    public double Dps { get; init; }
    /// <summary>Expected healing (or absorb) per cast, and per second.</summary>
    public double HealPerCast { get; init; }
    public double Hps { get; init; }
    public bool IsHeal => HealPerCast > 0 && ExpectedPerCast <= 0;
    /// <summary>Mana (or rage, energy) per cast after talents.</summary>
    public double Cost { get; init; }
}

/// <summary>
/// Expected damage and healing of one cast, Classic rules. Damage, against a target:
///   hit  = (base + power x coefficient + flat talent bonus) x (1 + damage %)
///   crit = hit x (1 + 50% x (1 + crit bonus %))   spells; physical crits do double
///   per cast = hit avg x (1 + crit chance x crit bonus) x chance to land, plus damage over time (doesn't crit)
/// Talent spell modifiers (aura 107 flat / 108 %) apply when their class mask overlaps the spell's.
/// Base values are at the spell's own level (no per-level scaling yet).
/// Healing: (base + healing power x coefficient) x (1 + healing %), direct heals crit for +50%, heals always land;
/// heals over time and shields (absorbs) don't crit; Chain Heal halves per jump; group heals are per target.
/// Weapon strikes (Sinister Strike, Holy Strike, Mortal Strike, Aimed Shot…) use the main hand (or ranged for shots):
///   hit = (weapon damage + AP / 14 x speed) x weapon % + flat bonus (+ spell power for holy/nature strikes)
/// with normalized speed for "normalized" strikes (1.7 dagger, 2.4 one-hand, 3.3 two-hand, 2.8 ranged), melee crit
/// (double damage) and the melee hit table.
/// </summary>
public static class DamageEngine
{
    // SpellModOp values used by talents.
    private const int OpDamage = 0, OpCritChance = 7, OpCastTime = 10, OpCost = 14, OpCritBonus = 15, OpHit = 16, OpDot = 22;

    // Spell effects that hit with the weapon.
    private const int WeaponFlat = 17, WeaponPercent = 31, WeaponSchool = 58, WeaponNormalized = 121;

    /// <summary>Speed used for attack power on normalized strikes.</summary>
    private static double NormalizedSpeed(GearItem? weapon, bool ranged)
    {
        if (ranged) return 2.8;
        if (weapon is null) return 2.4;
        if (weapon.InventoryType == 17) return 3.3; // two-hand
        return weapon.Name.Contains("Dagger", StringComparison.OrdinalIgnoreCase) || weapon.DelayMs <= 1800 ? 1.7 : 2.4;
    }

    public static DamageResult Compute(
        CalcSpell spell,
        IReadOnlyDictionary<int, List<EffectRow>> effects,
        CharacterSheet sheet,
        IReadOnlyList<TalentSpell> talents,
        int targetLevelDiff,
        GearItem? mainHand = null,
        GearItem? ranged = null)
    {
        var inv = CultureInfo.InvariantCulture;
        string N(double v) => Math.Round(v).ToString("0", inv);
        string D(double v) => v.ToString("0.#", inv);
        string P(double v) => v.ToString("0.0", inv) + "%";

        // ----- talent modifiers that touch this spell -----
        var flat = new Dictionary<int, double>();
        var pct = new Dictionary<int, double>();
        var applied = new List<string>();
        foreach (var talent in talents)
        {
            foreach (var e in effects.GetValueOrDefault(talent.SpellId) ?? new List<EffectRow>())
            {
                if (e.Effect != 6 || e.Aura is not (107 or 108) || !Overlaps(e.Mask, spell.Mask)) continue;
                var amount = talent.Amount(e);
                var target = e.Aura == 107 ? flat : pct;
                target[e.Misc0] = target.GetValueOrDefault(e.Misc0) + amount;
                applied.Add($"{talent.Source}: {Describe(e.Misc0, e.Aura == 108, amount)}");
            }
        }
        double Flat(int op) => flat.GetValueOrDefault(op);
        double Pct(int op) => pct.GetValueOrDefault(op);

        // ----- school, power, chances -----
        var own = effects.GetValueOrDefault(spell.Id) ?? new List<EffectRow>();
        var school = StatBag.Schools(spell.SchoolMask).Where(s => s > 0).DefaultIfEmpty(0).First();
        var physical = school == 0;
        // Weapon strikes follow melee rules (crit for double, melee hit table) even when they deal holy damage.
        var weaponStrike = own.Any(x => x.Effect is WeaponFlat or WeaponSchool or WeaponNormalized or WeaponPercent);
        var melee = physical || weaponStrike;
        var power = physical ? sheet.AttackPower : sheet.SpellPowerFor(school);
        var damagePct = Pct(OpDamage) + sheet.DamagePctFor(school);
        var critChance = Math.Clamp((melee ? sheet.MeleeCrit : sheet.SpellCritFor(school)) + Flat(OpCritChance), 0, 100);
        var critBonus = (melee ? 1.0 : 0.5) * (1 + Pct(OpCritBonus) / 100);
        var baseHit = melee
            ? targetLevelDiff switch { <= 0 => 95, 1 => 94.5, 2 => 94, _ => 91.4 }
            : targetLevelDiff switch { <= 0 => 96, 1 => 95, 2 => 94, _ => 83 };
        var hitChance = Math.Min(melee ? 100 : 99, baseHit + (melee ? sheet.MeleeHit : sheet.SpellHit) + Flat(OpHit));

        var castMs = Math.Max(0, (spell.CastMs + Flat(OpCastTime)) * (1 + Pct(OpCastTime) / 100));
        var cost = spell.PowerCost > 0 ? spell.PowerCost : spell.PowerCostPct * sheet.BaseMana / 100;
        cost = Math.Max(0, (cost + Flat(OpCost)) * (1 + Pct(OpCost) / 100));

        var lines = new List<StatLine>();
        var notes = new List<string>();
        double expected = 0;
        var channelMs = 0;

        // ----- direct damage -----
        foreach (var e in own.Where(x => x.Effect == 2))
        {
            var (min, max, avg) = Hit(e, power, physical, Flat(OpDamage), damagePct);
            var perCast = avg * (1 + critChance / 100 * critBonus) * hitChance / 100;
            expected += perCast;
            lines.Add(new("Direct", "Hit", $"{N(min)} - {N(max)}  (avg {N(avg)})"));
            lines.Add(new("Direct", "Crit", $"{N(min * (1 + critBonus))} - {N(max * (1 + critBonus))}"));
            lines.Add(new("Direct", "Power bonus", $"+{N(power * Coefficient(e, physical))}  ({P(Coefficient(e, physical) * 100)} of {(physical ? "AP" : "SP")} {N(power)})"));
            lines.Add(new("Direct", "Average per cast", N(perCast)));
        }

        // ----- weapon strikes -----
        if (weaponStrike)
        {
            var isRanged = spell.Name.Contains("Shot", StringComparison.OrdinalIgnoreCase)
                           || spell.Name.Contains("Throw", StringComparison.OrdinalIgnoreCase);
            var weapon = isRanged ? ranged : mainHand;
            double wMin = 1, wMax = 2, speed = 2.0; // unarmed
            var weaponName = "no weapon (fists)";
            if (weapon is { IsWeapon: true })
            {
                wMin = weapon.DamageMin;
                wMax = weapon.DamageMax;
                speed = weapon.DelayMs / 1000.0;
                weaponName = weapon.Name;
            }
            else notes.Add(isRanged
                ? "No ranged weapon in the gear set; shots use 1-2 damage. Pick a gear set with a bow, gun or crossbow."
                : "No main-hand weapon in the gear set; strikes use fists (1-2 damage). Pick a gear set with a weapon.");

            var normalized = own.Any(x => x.Effect == WeaponNormalized);
            var apSpeed = normalized ? NormalizedSpeed(weapon, isRanged) : speed;
            var ap = isRanged ? sheet.RangedAttackPower : sheet.AttackPower;
            var apBonus = ap / 14 * apSpeed;
            var percent = own.FirstOrDefault(x => x.Effect == WeaponPercent) is { } p ? p.BasePoints / 100 : 1.0;
            var flatEffects = own.Where(x => x.Effect is WeaponFlat or WeaponSchool or WeaponNormalized).ToList();
            var bonus = flatEffects.Sum(x => Math.Abs(x.BasePoints));
            var variance = flatEffects.Select(x => x.Variance).DefaultIfEmpty(0).Max();
            // Holy/nature strikes add spell power to their bonus part (the data's coefficient is only meaningful there).
            var spellBonus = physical ? 0 : sheet.SpellPowerFor(school) * flatEffects.Sum(x => x.SpellCoefficient);
            var factor = 1 + damagePct / 100;
            var min = ((wMin + apBonus) * percent + bonus * (1 - variance / 2) + spellBonus + Flat(OpDamage)) * factor;
            var max = ((wMax + apBonus) * percent + bonus * (1 + variance / 2) + spellBonus + Flat(OpDamage)) * factor;
            var avg = (min + max) / 2;
            var perCast = avg * (1 + critChance / 100 * critBonus) * hitChance / 100;
            expected += perCast;

            lines.Add(new("Weapon strike", "Weapon", $"{weaponName}: {N(wMin)} - {N(wMax)}, {D(speed)} speed"));
            lines.Add(new("Weapon strike", "Attack power bonus", $"+{N(apBonus)}  ({(isRanged ? "RAP" : "AP")} {N(ap)} / 14 x {D(apSpeed)}{(normalized ? " normalized" : "")})"));
            if (Math.Abs(percent - 1) > 0.001) lines.Add(new("Weapon strike", "Weapon damage", $"{N(percent * 100)}%"));
            if (bonus > 0) lines.Add(new("Weapon strike", "Bonus damage", variance > 0 ? $"{N(bonus * (1 - variance / 2))} - {N(bonus * (1 + variance / 2))}" : N(bonus)));
            if (spellBonus > 0) lines.Add(new("Weapon strike", "Spell power bonus", $"+{N(spellBonus)}"));
            lines.Add(new("Weapon strike", "Hit", $"{N(min)} - {N(max)}  (avg {N(avg)})"));
            lines.Add(new("Weapon strike", "Crit", $"{N(min * (1 + critBonus))} - {N(max * (1 + critBonus))}"));
            lines.Add(new("Weapon strike", "Average per use", N(perCast)));
            notes.Add("Weapon strikes: DPS is per global cooldown, on top of auto attacks. Dodge and parry aren't counted.");
        }

        // ----- damage over time (doesn't crit in Classic) -----
        foreach (var e in own.Where(x => x.Effect == 6 && x.Aura == 3 && x.PeriodMs > 0))
        {
            var ticks = spell.DurationMs > 0 ? spell.DurationMs / e.PeriodMs : 1;
            var tick = (Math.Abs(e.BasePoints) + power * e.SpellCoefficient) * (1 + (damagePct + Pct(OpDot)) / 100);
            var total = tick * ticks;
            expected += total * hitChance / 100;
            lines.Add(new("Over time", "Per tick", $"{N(tick)} every {D(e.PeriodMs / 1000.0)} sec"));
            lines.Add(new("Over time", "Total", $"{N(total)} over {D(spell.DurationMs / 1000.0)} sec ({ticks} ticks)"));
            lines.Add(new("Over time", "Power bonus", $"+{N(power * e.SpellCoefficient * ticks)}  ({P(e.SpellCoefficient * ticks * 100)} total)"));
        }

        // ----- channeled missiles / periodic triggers (Arcane Missiles style) -----
        foreach (var e in own.Where(x => x.Effect == 6 && x.Aura == 23 && x.PeriodMs > 0 && x.TriggerSpell > 0))
        {
            var bolt = (effects.GetValueOrDefault(e.TriggerSpell) ?? new List<EffectRow>()).FirstOrDefault(x => x.Effect == 2);
            if (bolt is null) continue;
            var ticks = spell.DurationMs > 0 ? spell.DurationMs / e.PeriodMs : 1;
            var (min, max, avg) = Hit(bolt, power, physical, Flat(OpDamage), damagePct);
            var total = avg * ticks * (1 + critChance / 100 * critBonus) * hitChance / 100;
            expected += total;
            channelMs = Math.Max(channelMs, spell.DurationMs);
            lines.Add(new("Channeled", "Per missile", $"{N(min)} - {N(max)}  (avg {N(avg)})"));
            lines.Add(new("Channeled", "Missiles", $"{ticks} over {D(spell.DurationMs / 1000.0)} sec"));
            lines.Add(new("Channeled", "Average per cast", N(total)));
        }

        // ----- healing: direct heals, heals over time, absorb shields -----
        // Classic rules: heals always land, direct heals crit for +50%, periodic heals and shields don't crit.
        // Healing talents use the same spell modifiers as damage (aura 108, matched by class mask).
        var healPower = sheet.HealingPower;
        var healCrit = Math.Clamp(sheet.SpellCritFor(school) + Flat(OpCritChance), 0, 100);
        var healCritBonus = 0.5 * (1 + Pct(OpCritBonus) / 100);
        var healPct = Pct(OpDamage);
        double healed = 0;
        var directHeals = own.Where(x => x.Effect == 10).ToList();
        foreach (var e in directHeals)
        {
            var (min, max, avg) = Hit(e, healPower, false, Flat(OpDamage), healPct);
            // Chain Heal: each jump heals half as much as the one before.
            var jumps = Math.Max(1, e.ChainTargets);
            var chainFactor = Enumerable.Range(0, jumps).Sum(i => Math.Pow(0.5, i));
            var perCast = avg * (1 + healCrit / 100 * healCritBonus) * chainFactor;
            healed += perCast;
            lines.Add(new("Healing", "Heal", $"{N(min)} - {N(max)}  (avg {N(avg)})"));
            lines.Add(new("Healing", "Crit", $"{N(min * (1 + healCritBonus))} - {N(max * (1 + healCritBonus))}"));
            lines.Add(new("Healing", "Healing power bonus", $"+{N(healPower * e.SpellCoefficient)}  ({P(e.SpellCoefficient * 100)} of healing power {N(healPower)})"));
            if (jumps > 1) lines.Add(new("Healing", "Chain", $"{jumps} targets, each jump half: {D(chainFactor)}x the first heal"));
            lines.Add(new("Healing", "Average per cast", N(perCast)));
            if (IsGroupTarget(e.Target)) notes.Add("Group heal: the numbers are for one target; every party member in range gets it.");
        }

        var hots = own.Where(x => x.Effect is 6 or 35 && x.Aura == 8 && x.PeriodMs > 0).ToList();
        foreach (var e in hots)
        {
            var ticks = spell.DurationMs > 0 ? spell.DurationMs / e.PeriodMs : 1;
            var tick = (Math.Abs(e.BasePoints) + healPower * e.SpellCoefficient) * (1 + (healPct + Pct(OpDot)) / 100);
            var total = tick * ticks;
            healed += total;
            lines.Add(new("Heal over time", "Per tick", $"{N(tick)} every {D(e.PeriodMs / 1000.0)} sec"));
            lines.Add(new("Heal over time", "Total", $"{N(total)} over {D(spell.DurationMs / 1000.0)} sec ({ticks} ticks)"));
            lines.Add(new("Heal over time", "Healing power bonus", $"+{N(healPower * e.SpellCoefficient * ticks)}  ({P(e.SpellCoefficient * ticks * 100)} total)"));
            if (e.Effect == 35 || IsGroupTarget(e.Target)) notes.Add("Group heal over time: the numbers are for one target.");
        }

        foreach (var e in own.Where(x => x.Effect == 6 && x.Aura == 69))
        {
            var amount = (Math.Abs(e.BasePoints) + healPower * e.SpellCoefficient) * (1 + healPct / 100);
            healed += amount;
            lines.Add(new("Shield", "Absorbs", N(amount)));
            lines.Add(new("Shield", "Healing power bonus", $"+{N(healPower * e.SpellCoefficient)}  ({P(e.SpellCoefficient * 100)} of healing power {N(healPower)})"));
        }

        if (lines.Count == 0) notes.Add("No damage or healing effect this calculator understands yet.");

        var damages = expected > 0 || own.Any(e => e.Effect is 2 or WeaponFlat or WeaponPercent or WeaponSchool or WeaponNormalized || (e.Effect == 6 && e.Aura is 3 or 23));
        var heals = healed > 0;
        var timeMs = channelMs > 0 ? channelMs : Math.Max(castMs, spell.GcdMs > 0 ? spell.GcdMs : 1500);
        var dotOnly = own.All(e => e.Effect != 2) && channelMs == 0 && own.Any(e => e.Aura == 3);
        var hotOnly = !damages && directHeals.Count == 0 && hots.Count > 0;
        if ((dotOnly || hotOnly) && spell.DurationMs > 0) timeMs = spell.DurationMs;
        var perSecond = (double amount) => timeMs > 0 ? amount / (timeMs / 1000) : 0;

        lines.Add(new("Summary", "Cast time", castMs > 0 ? $"{D(castMs / 1000)} sec" : "Instant"));
        if (damages)
        {
            lines.Add(new("Summary", "Crit chance", P(critChance)));
            lines.Add(new("Summary", "Chance to land", P(hitChance)));
            lines.Add(new("Summary", "Expected per cast", N(expected)));
            lines.Add(new("Summary", dotOnly ? "DPS over duration" : "DPS (cast or GCD)", D(perSecond(expected))));
        }
        if (heals)
        {
            if (directHeals.Count > 0) lines.Add(new("Summary", "Heal crit chance", P(healCrit)));
            lines.Add(new("Summary", "Healing per cast", N(healed)));
            lines.Add(new("Summary", hotOnly ? "HPS over duration" : "HPS (cast or GCD)", D(perSecond(healed))));
        }
        if (damages && heals) notes.Add("This spell either damages or heals (or does both): the two parts are shown apart.");
        if (cost > 0)
        {
            lines.Add(new("Summary", "Cost", N(cost)));
            if (damages) lines.Add(new("Summary", "Damage per point of cost", D(expected / cost)));
            if (heals) lines.Add(new("Summary", "Healing per mana", D(healed / cost)));
        }

        if (applied.Count == 0) notes.Add("No talent modifies this spell (or none are picked for this class).");
        return new DamageResult(lines, applied, notes)
        {
            ExpectedPerCast = expected,
            Dps = perSecond(expected),
            HealPerCast = healed,
            Hps = perSecond(healed),
            Cost = cost,
        };
    }

    // Party / raid targets in SpellEffect.ImplicitTarget (around the caster, the target's party).
    private static bool IsGroupTarget(int target) => target is 20 or 33 or 37 or 56 or 61 or 132;

    private static (double Min, double Max, double Avg) Hit(EffectRow e, double power, bool physical, double flatBonus, double damagePct)
    {
        var value = Math.Abs(e.BasePoints);
        var min = value * (1 - e.Variance / 2);
        var max = value * (1 + e.Variance / 2);
        var bonus = power * Coefficient(e, physical) + flatBonus;
        var factor = 1 + damagePct / 100;
        min = (min + bonus) * factor;
        max = (max + bonus) * factor;
        return (min, max, (min + max) / 2);
    }

    private static double Coefficient(EffectRow e, bool physical) =>
        physical ? e.ApCoefficient : e.SpellCoefficient;

    private static bool Overlaps(int[] a, int[] b)
    {
        for (var i = 0; i < 4; i++)
            if ((a[i] & b[i]) != 0) return true;
        return false;
    }

    private static string Describe(int op, bool percent, double amount)
    {
        var inv = CultureInfo.InvariantCulture;
        var sign = amount >= 0 ? "+" : "";
        var value = percent ? $"{sign}{amount.ToString("0.#", inv)}%" : $"{sign}{amount.ToString("0.#", inv)}";
        return op switch
        {
            OpDamage => $"{value} damage",
            OpCritChance => $"{value}% crit chance",
            OpCastTime => percent ? $"{value} cast time" : $"{sign}{(amount / 1000).ToString("0.##", inv)} sec cast time",
            OpCost => $"{value} cost",
            OpCritBonus => $"{value} crit damage bonus",
            OpHit => $"{value}% hit",
            OpDot => $"{value} periodic damage",
            _ => $"{value} (modifier {op}, not used in damage)",
        };
    }
}
