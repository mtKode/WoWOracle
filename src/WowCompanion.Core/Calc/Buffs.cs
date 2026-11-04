namespace WowCompanion.Core.Calc;

/// <summary>A buff, consumable or aura you can switch on in the calculator.</summary>
public sealed record Buff(string Key, string Name, string Category, IReadOnlyList<(Stat Stat, double Amount)> Effects)
{
    /// <summary>Damage added to main-hand hits (poisons, weapon enchants).</summary>
    public OnHitProc? OnHit { get; init; }
}

/// <summary>
/// Extra damage on weapon hits. Either a chance per hit (poisons) or procs per minute (enchants, most weapons).
/// School is the index: 0 physical, 1 holy, 2 fire, 3 nature, 4 frost, 5 shadow, 6 arcane.
/// </summary>
public sealed record OnHitProc(string Name, double Min, double Max, int School, double ChancePct, double Ppm);

/// <summary>
/// Common Classic buffs and consumables. Add new ones here; Key is what gets saved, so don't rename it.
/// Values are the usual level-60 versions; check them against WoW Forever tooltips.
/// </summary>
public static class BuffCatalog
{
    private static Buff B(string key, string name, string category, params (Stat Stat, double Amount)[] effects) =>
        new(key, name, category, effects);

    public static readonly IReadOnlyList<Buff> All =
    [
        // Class buffs
        B("arcane_int", "Arcane Brilliance", "Class buffs", (Stat.Intellect, 31)),
        B("motw", "Mark of the Wild (improved)", "Class buffs",
            (Stat.Strength, 16), (Stat.Agility, 16), (Stat.Stamina, 16), (Stat.Intellect, 16), (Stat.Spirit, 16), (Stat.Armor, 384)),
        B("fortitude", "Power Word: Fortitude", "Class buffs", (Stat.Stamina, 54)),
        B("divine_spirit", "Divine Spirit", "Class buffs", (Stat.Spirit, 40)),
        B("kings", "Blessing of Kings", "Class buffs", (Stat.AllStatsPct, 10)),
        B("wisdom", "Blessing of Wisdom", "Class buffs", (Stat.Mp5, 33)),
        B("might", "Blessing of Might", "Class buffs", (Stat.AttackPower, 185)),
        B("battle_shout", "Battle Shout", "Class buffs", (Stat.AttackPower, 232)),
        B("moonkin", "Moonkin Aura", "Class buffs", (Stat.SpellCritPct, 3)),

        // World buffs
        B("ony", "Rallying Cry of the Dragonslayer", "World buffs", (Stat.SpellCritPct, 10), (Stat.MeleeCritPct, 5), (Stat.AttackPower, 140)),
        B("songflower", "Songflower Serenade", "World buffs",
            (Stat.SpellCritPct, 5), (Stat.MeleeCritPct, 5), (Stat.Strength, 15), (Stat.Agility, 15), (Stat.Stamina, 15),
            (Stat.Intellect, 15), (Stat.Spirit, 15)),
        B("zandalar", "Spirit of Zandalar", "World buffs", (Stat.AllStatsPct, 15)),
        B("moxie", "Mol'dar's Moxie", "World buffs", (Stat.StaminaPct, 15)),

        // Consumables
        B("supreme_power", "Flask of Supreme Power", "Consumables", (Stat.SpellPower, 150)),
        B("greater_arcane", "Greater Arcane Elixir", "Consumables", (Stat.SpellPower, 35)),
        B("greater_firepower", "Elixir of Greater Firepower", "Consumables", (Stat.FirePower, 40)),
        B("shadow_power", "Elixir of Shadow Power", "Consumables", (Stat.ShadowPower, 40)),
        B("frost_power", "Elixir of Frost Power", "Consumables", (Stat.FrostPower, 15)),
        B("wizard_oil", "Brilliant Wizard Oil", "Consumables", (Stat.SpellPower, 36), (Stat.SpellCritPct, 1)),
        B("mana_oil", "Brilliant Mana Oil", "Consumables", (Stat.Healing, 25), (Stat.Mp5, 12)),
        B("mongoose", "Elixir of the Mongoose", "Consumables", (Stat.Agility, 25), (Stat.MeleeCritPct, 2)),
        B("giants", "Elixir of Giants", "Consumables", (Stat.Strength, 25)),
        B("runn_tum", "Runn Tum Tuber Surprise", "Consumables", (Stat.Intellect, 10)),

        // Weapon enchants and poisons (main hand). Proc rates are the commonly quoted Classic ones; check them.
        new Buff("instant_poison", "Instant Poison VI", "Weapon enchants", [])
            { OnHit = new OnHitProc("Instant Poison VI", 112, 148, 3, 20, 0) },
        new Buff("fiery_weapon", "Enchant Weapon - Fiery Weapon", "Weapon enchants", [])
            { OnHit = new OnHitProc("Fiery Weapon", 40, 40, 2, 0, 6) },
        new Buff("crusader", "Enchant Weapon - Crusader (Str proc, average)", "Weapon enchants", [(Stat.Strength, 100 * 15.0 / 60)]),

        // Protection (resistances). Absorb potions need an absorb model; add them when that exists.
        B("fire_res_aura", "Fire Resistance Aura / Totem", "Protection", (Stat.FireResist, 60)),
        B("frost_res_aura", "Frost Resistance Aura / Totem", "Protection", (Stat.FrostResist, 60)),
        B("shadow_prot", "Prayer of Shadow Protection", "Protection", (Stat.ShadowResist, 60)),
        B("nature_res", "Nature Resistance Totem / Aspect of the Wild", "Protection", (Stat.NatureResist, 60)),
    ];

    public static Buff? Find(string key) => All.FirstOrDefault(b => b.Key == key);
}
