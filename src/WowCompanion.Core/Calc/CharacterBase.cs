namespace WowCompanion.Core.Calc;

/// <summary>
/// Naked character numbers. The client has no base-stat table (ChrBaseStats isn't shipped), so these are the
/// Classic level-60 values as best known: they are ESTIMATES. Enter your naked stats in the calculator to
/// override them. Other levels are scaled linearly from 20 at level 1, which is rough.
/// </summary>
public static class CharacterBase
{
    // Class base at 60 for a Human: Str, Agi, Sta, Int, Spi, base health, base mana (0 = no mana).
    private static readonly Dictionary<int, double[]> Classes = new()
    {
        [1] = new double[] { 120, 80, 110, 30, 47, 1689, 0 },      // Warrior
        [2] = new double[] { 105, 65, 100, 70, 74, 1381, 1512 },   // Paladin
        [3] = new double[] { 57, 125, 85, 65, 70, 1467, 1720 },    // Hunter
        [4] = new double[] { 85, 130, 75, 35, 50, 1523, 0 },       // Rogue
        [5] = new double[] { 40, 45, 50, 145, 150, 1397, 1436 },   // Priest
        [7] = new double[] { 95, 60, 105, 85, 95, 1423, 1520 },    // Shaman
        [8] = new double[] { 35, 40, 45, 145, 140, 1370, 1213 },   // Mage
        [9] = new double[] { 50, 55, 75, 130, 135, 1414, 1373 },   // Warlock
        [11] = new double[] { 60, 60, 70, 115, 125, 1483, 1244 },  // Druid
    };

    // Race modifiers relative to Human: Str, Agi, Sta, Int, Spi. From the Classic level-1 race stats
    // (Human 20/20/20/20/21, Orc 23/17/22/17/23, Dwarf 22/16/23/19/19, Night Elf 17/25/19/20/20,
    //  Undead 19/18/21/18/25, Tauren 25/15/22/15/22, Gnome 15/23/19/24/20, Troll 21/22/21/16/21).
    // Note: wiki "Base attributes" tables are from later expansions (Monks, level 100) and don't apply.
    private static readonly Dictionary<int, int[]> Races = new()
    {
        [1] = new int[] { 0, 0, 0, 0, 0 },     // Human
        [2] = new int[] { 3, -3, 2, -3, 2 },   // Orc
        [3] = new int[] { 2, -4, 3, -1, -2 },  // Dwarf
        [4] = new int[] { -3, 5, -1, 0, -1 },  // Night Elf
        [5] = new int[] { -1, -2, 1, -2, 4 },  // Undead
        [6] = new int[] { 5, -5, 2, -5, 1 },   // Tauren
        [7] = new int[] { -5, 3, -1, 4, -1 },  // Gnome
        [8] = new int[] { 1, 2, 1, -4, 0 },    // Troll
    };

    /// <summary>Racial % bonuses: Human Spirit (The Human Spirit) and Gnome Intellect (Expansive Mind), +5%.</summary>
    public static (Stat Stat, double Pct)? RacialPct(int raceId) => raceId switch
    {
        1 => (Stat.SpiritPct, 5),
        7 => (Stat.IntellectPct, 5),
        _ => null,
    };

    /// <summary>Classic class/race combinations (race ids 1-8), used for the race comparison.</summary>
    public static bool CanBe(int classId, int raceId) => classId switch
    {
        1 => raceId is >= 1 and <= 8,
        2 => raceId is 1 or 3,
        3 => raceId is 2 or 3 or 4 or 6 or 8,
        4 => raceId is 1 or 2 or 3 or 4 or 5 or 7 or 8,
        5 => raceId is 1 or 3 or 4 or 5 or 8,
        7 => raceId is 2 or 6 or 8,
        8 => raceId is 1 or 5 or 7 or 8,
        9 => raceId is 1 or 2 or 5 or 7,
        11 => raceId is 4 or 6,
        _ => true,
    };

    public static bool UsesMana(int classId) => Classes.TryGetValue(classId, out var c) && c[6] > 0;

    /// <summary>Str, Agi, Sta, Int, Spi for a naked character.</summary>
    public static double[] Primary(int classId, int raceId, int level)
    {
        var c = Classes.GetValueOrDefault(classId) ?? new double[] { 20, 20, 20, 20, 20, 1000, 0 };
        var r = Races.GetValueOrDefault(raceId) ?? new int[] { 0, 0, 0, 0, 0 };
        var scale = Math.Clamp(level, 1, 60) / 60.0;
        return Enumerable.Range(0, 5).Select(i => Math.Round(20 + (c[i] - 20) * scale) + r[i]).ToArray();
    }

    public static (double Health, double Mana) BaseVitals(int classId, int level)
    {
        var c = Classes.GetValueOrDefault(classId) ?? new double[] { 20, 20, 20, 20, 20, 1000, 0 };
        var scale = Math.Clamp(level, 1, 60) / 60.0;
        return (Math.Round(c[5] * scale), Math.Round(c[6] * scale));
    }

    // ---------------- conversions at level 60 ----------------
    // Ratings: WoW Forever items carry ratings (crit median 14, hit median 10), so 14 crit / 10 hit per 1%.

    public const double CritRatingPerPct = 14;
    public const double HitRatingPerPct = 10;
    public const double HasteRatingPerPct = 10;

    /// <summary>Intellect per 1% spell crit, and the class's base spell crit %.</summary>
    public static (double IntPerCrit, double BaseCrit) SpellCrit(int classId) => classId switch
    {
        2 => (54, 3.5),
        5 => (59.2, 0.8),
        7 => (59.5, 2.3),
        8 => (59.5, 0.2),
        9 => (60.6, 1.7),
        11 => (60, 1.8),
        _ => (0, 0),
    };

    /// <summary>Agility per 1% melee crit, and base melee crit %.</summary>
    public static (double AgiPerCrit, double BaseCrit) MeleeCrit(int classId) => classId switch
    {
        3 => (53, 0),
        4 => (29, 0),
        _ => (20, 0),
    };

    /// <summary>Ranged attack power from Agi, Classic formulas.</summary>
    public static double RangedAttackPower(int classId, int level, double agi) => classId switch
    {
        3 => agi * 2 + level * 2 - 10,
        1 or 4 => agi + level - 10,
        _ => agi - 10,
    };

    /// <summary>Melee attack power from Str/Agi, Classic formulas.</summary>
    public static double AttackPower(int classId, int level, double str, double agi) => classId switch
    {
        1 or 2 => str * 2 + level * 3 - 20,
        3 or 4 => str + agi + level * 2 - 20,
        7 => str * 2 + level * 2 - 20,
        11 => str * 2 - 20,
        _ => str - 10,
    };
}
