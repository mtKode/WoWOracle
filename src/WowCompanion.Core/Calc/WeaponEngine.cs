using System.Globalization;

namespace WowCompanion.Core.Calc;

/// <summary>
/// White (auto-attack) damage per weapon, Classic rules:
///   hit = weapon damage + attack power / 14 x speed;  crit = 2x;  off hand does 50%.
///   misses vs target level: 5 / 5.5 / 6 / 8.6% (+3), +19% when dual wielding, minus your hit.
/// Not modelled yet: dodge, parry, glancing blows, weapon skill.
/// On-hit procs: the weapon's own "Chance on hit" spells (rate isn't in the client data, so 1 per minute is
/// assumed) and main-hand enchants/poisons switched on as buffs.
/// </summary>
public static class WeaponEngine
{
    public const double DefaultPpm = 1.0;

    public sealed record WeaponSlot(string Label, GearItem Item, bool OffHand, bool Ranged);

    public static IReadOnlyList<StatLine> Compute(
        IReadOnlyList<WeaponSlot> weapons,
        CharacterSheet sheet,
        IReadOnlyDictionary<int, List<EffectRow>> effects,
        IReadOnlyList<OnHitProc> mainHandProcs,
        int targetLevelDiff)
    {
        var inv = CultureInfo.InvariantCulture;
        string N(double v) => Math.Round(v).ToString("0", inv);
        string D(double v) => v.ToString("0.0", inv);
        var lines = new List<StatLine>();
        if (weapons.Count == 0) return lines;

        var dualWield = weapons.Count(w => !w.Ranged) >= 2;
        var baseMiss = targetLevelDiff switch { <= 0 => 5.0, 1 => 5.5, 2 => 6.0, _ => 8.6 };
        var spellLand = targetLevelDiff switch { <= 0 => 96.0, 1 => 95.0, 2 => 94.0, _ => 83.0 };
        double meleeDps = 0;

        foreach (var weapon in weapons)
        {
            var item = weapon.Item;
            var speed = item.DelayMs / 1000.0;
            var power = weapon.Ranged ? sheet.RangedAttackPower : sheet.AttackPower;
            var bonus = power / 14 * speed;
            var factor = weapon.OffHand ? 0.5 : 1.0;
            var min = (item.DamageMin + bonus) * factor;
            var max = (item.DamageMax + bonus) * factor;
            var avg = (min + max) / 2;

            var miss = Math.Max(0, baseMiss + (dualWield && !weapon.Ranged ? 19 : 0) - sheet.MeleeHit);
            var crit = Math.Clamp(sheet.MeleeCrit, 0, 100 - miss);
            var perSwing = avg * ((100 - miss - crit) / 100 + crit / 100 * 2);
            var dps = perSwing / speed;

            var group = $"{weapon.Label}: {item.Name}";
            lines.Add(new(group, "Hit", $"{N(min)} - {N(max)}  (avg {N(avg)})"));
            lines.Add(new(group, "Speed", $"{speed.ToString("0.00", inv)} sec, +{N(bonus * factor)} from {(weapon.Ranged ? "RAP" : "AP")}"));
            lines.Add(new(group, "Crit / miss", $"{D(crit)}% / {D(miss)}%"));
            lines.Add(new(group, "White DPS", D(dps)));

            // Procs: chance per hit or per minute; landed hits only.
            var hitsPerSecond = (100 - miss) / 100 / speed;
            var procs = new List<(string Name, double Avg, double PerSecond)>();
            foreach (var spellId in item.OnHitSpells)
            {
                var damage = (effects.GetValueOrDefault(spellId) ?? new List<EffectRow>()).FirstOrDefault(e => e.Effect == 2);
                if (damage is null) continue;
                var procAvg = Math.Abs(damage.BasePoints);
                procs.Add(($"Chance on hit (~{DefaultPpm.ToString("0", inv)}/min assumed)", procAvg, DefaultPpm / 60 * (100 - miss) / 100));
            }
            if (!weapon.OffHand && !weapon.Ranged)
            {
                foreach (var proc in mainHandProcs)
                {
                    var perSecond = proc.ChancePct > 0 ? hitsPerSecond * proc.ChancePct / 100 : proc.Ppm / 60 * (100 - miss) / 100;
                    procs.Add((proc.Name, (proc.Min + proc.Max) / 2, perSecond));
                }
            }
            foreach (var (name, procAvg, perSecond) in procs)
            {
                // Magic procs can be resisted like spells.
                var procDps = procAvg * perSecond * spellLand / 100;
                lines.Add(new(group, name, $"{N(procAvg)} per proc, {D(procDps)} DPS"));
                dps += procDps;
            }
            if (procs.Count > 0) lines.Add(new(group, "DPS with procs", D(dps)));
            if (!weapon.Ranged) meleeDps += dps;
        }

        if (dualWield) lines.Add(new("Weapons total", "Melee DPS (both hands)", D(meleeDps)));
        return lines;
    }
}
