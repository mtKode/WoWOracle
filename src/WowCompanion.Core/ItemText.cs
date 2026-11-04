namespace WowCompanion.Core;

/// <summary>Turns the numeric codes in the item tables into the words players know.</summary>
public static class ItemText
{
    private static readonly string[] Slots =
    [
        "", "Head", "Neck", "Shoulder", "Shirt", "Chest", "Waist", "Legs", "Feet", "Wrist", "Hands",
        "Finger", "Trinket", "One-Hand", "Shield", "Ranged", "Back", "Two-Hand", "Bag", "Tabard", "Chest",
        "Main Hand", "Off Hand", "Held In Off-hand", "Ammo", "Thrown", "Ranged", "Quiver", "Relic",
    ];

    private static readonly string[] Classes =
    [
        "Consumable", "Container", "Weapon", "Gem", "Armor", "Reagent", "Projectile", "Trade Goods",
        "Item Enhancement", "Recipe", "Money", "Quiver", "Quest", "Key", "Permanent", "Miscellaneous", "Glyph",
    ];

    private static readonly string[] ArmorTypes =
        ["Miscellaneous", "Cloth", "Leather", "Mail", "Plate", "Cosmetic", "Shield", "Libram", "Idol", "Totem", "Sigil", "Relic"];

    private static readonly Dictionary<int, string> WeaponTypes = new()
    {
        [0] = "One-Handed Axe", [1] = "Two-Handed Axe", [2] = "Bow", [3] = "Gun", [4] = "One-Handed Mace",
        [5] = "Two-Handed Mace", [6] = "Polearm", [7] = "One-Handed Sword", [8] = "Two-Handed Sword",
        [9] = "Warglaive", [10] = "Staff", [13] = "Fist Weapon", [14] = "Miscellaneous", [15] = "Dagger",
        [16] = "Thrown", [18] = "Crossbow", [19] = "Wand", [20] = "Fishing Pole",
    };

    private static readonly string[] Qualities =
        ["Poor", "Common", "Uncommon", "Rare", "Epic", "Legendary", "Artifact", "Heirloom"];

    public static string Slot(int inventoryType) =>
        inventoryType > 0 && inventoryType < Slots.Length ? Slots[inventoryType] : "";

    public static string QualityName(int quality) =>
        quality >= 0 && quality < Qualities.Length ? Qualities[quality] : "Unknown";

    public static string Binding(int bonding) => bonding switch
    {
        1 => "Binds when picked up",
        2 => "Binds when equipped",
        3 => "Binds when used",
        4 => "Quest item",
        _ => "",
    };

    /// <summary>"Cloth Chest", "One-Handed Sword", "Consumable".</summary>
    public static string Kind(int classId, int subclassId, int inventoryType)
    {
        var slot = Slot(inventoryType);
        switch (classId)
        {
            case 2:
                return WeaponTypes.GetValueOrDefault(subclassId, "Weapon");
            case 4:
                var armor = subclassId >= 0 && subclassId < ArmorTypes.Length ? ArmorTypes[subclassId] : "Armor";
                if (subclassId == 0 || armor == slot) return slot.Length > 0 ? slot : armor;
                return slot.Length > 0 ? $"{armor} {slot}" : armor;
            default:
                return classId >= 0 && classId < Classes.Length ? Classes[classId] : "";
        }
    }

    /// <summary>"Plate", "Shield"; empty for rings, necks, cloaks and other "Miscellaneous" armor.</summary>
    public static string ArmorType(int subclassId, int inventoryType)
    {
        if (inventoryType is 2 or 11 or 12 or 16 or 4 or 19 || subclassId == 0) return "";
        return subclassId > 0 && subclassId < ArmorTypes.Length ? ArmorTypes[subclassId] : "";
    }

    public static string Stat(int type, int value)
    {
        var name = type switch
        {
            0 => "Mana",
            1 => "Health",
            3 => "Agility",
            4 => "Strength",
            5 => "Intellect",
            6 => "Spirit",
            7 => "Stamina",
            12 => "Defense Rating",
            13 => "Dodge Rating",
            14 => "Parry Rating",
            15 => "Block Rating",
            31 => "Hit Rating",
            32 => "Critical Strike Rating",
            35 => "Resilience Rating",
            36 => "Haste Rating",
            37 => "Expertise Rating",
            38 => "Attack Power",
            39 => "Ranged Attack Power",
            40 => "Feral Attack Power",
            41 => "Healing Power",
            42 => "Spell Damage",
            43 => "Mana per 5 sec",
            45 => "Spell Power",
            46 => "Health per 5 sec",
            47 => "Spell Penetration",
            48 => "Block Value",
            50 => "Armor",
            51 => "Fire Resistance",
            52 => "Frost Resistance",
            53 => "Holy Resistance",
            54 => "Shadow Resistance",
            55 => "Nature Resistance",
            56 => "Arcane Resistance",
            84 => "Holy Spell Damage",
            85 => "Fire Spell Damage",
            86 => "Nature Spell Damage",
            87 => "Frost Spell Damage",
            88 => "Shadow Spell Damage",
            89 => "Arcane Spell Damage",
            117 => "Fishing",
            127 => "Attack Power vs Demons",
            131 => "Attack Power vs Beasts",
            _ => $"Stat {type}",
        };
        return value >= 0 ? $"+{value} {name}" : $"{value} {name}";
    }

    /// <summary>Stats shown as green "Equip:" lines in the game rather than white "+X Stat" lines.</summary>
    public static bool IsGreenStat(int type) => type is not (0 or 1 or 3 or 4 or 5 or 6 or 7 or 50 or 51 or 52 or 53 or 54 or 55 or 56);

    private static readonly string[] SchoolNames = ["Physical", "Holy", "Fire", "Nature", "Frost", "Shadow", "Arcane"];

    /// <summary>DamageType on items is a school index (0 physical, 2 fire ...).</summary>
    public static string SchoolIndex(int index) => index >= 0 && index < SchoolNames.Length ? SchoolNames[index] : "";

    /// <summary>SchoolMask on spells is a bit mask (1 physical, 4 fire ...).</summary>
    public static string SchoolMask(int mask)
    {
        if (mask == 0) return "";
        var names = new List<string>();
        for (var i = 0; i < SchoolNames.Length; i++)
            if ((mask & (1 << i)) != 0) names.Add(SchoolNames[i]);
        return string.Join(", ", names);
    }

    public static string Trigger(int trigger) => trigger switch
    {
        0 or 5 => "Use: ",
        1 => "Equip: ",
        2 => "Chance on hit: ",
        6 => "Use: ",
        _ => "",
    };

    public static string PowerName(int powerType) => powerType switch
    {
        0 => "Mana",
        1 => "Rage",
        2 => "Focus",
        3 => "Energy",
        5 => "Runes",
        6 => "Runic Power",
        -2 => "Health",
        _ => "",
    };

    /// <summary>
    /// Skill-up ranges, Classic style: orange from the learn skill (always +1), yellow from YellowAt (usually),
    /// green halfway to grey (sometimes), grey from GreyAt (never). "~" marks an estimated learn skill.
    /// </summary>
    public static string SkillUps(int learn, int yellow, int grey, bool estimated)
    {
        if (yellow <= 0 || grey <= 0) return learn > 0 ? $"Requires {learn}" : "";
        if (yellow == grey)
            return $"Learned at {Math.Max(learn, yellow)}. The game data has no yellow/green/grey range for this recipe (smelting and similar), so it isn't shown.";
        var green = (yellow + grey) / 2;
        var start = estimated ? $"~{learn}" : learn.ToString();
        return $"Orange {start}-{Math.Max(learn, yellow - 1)} (always +1) · Yellow {yellow}-{green - 1} · Green {green}-{grey - 1} · Grey {grey}+";
    }

    /// <summary>"1.5 sec", "2 min".</summary>
    public static string Time(int ms)
    {
        if (ms <= 0) return "";
        var seconds = ms / 1000.0;
        if (seconds >= 3600 && seconds % 3600 == 0) return $"{seconds / 3600:0} hr";
        if (seconds >= 60 && seconds % 60 == 0) return $"{seconds / 60:0} min";
        return seconds.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + " sec";
    }

    public static string Money(long copper)
    {
        if (copper <= 0) return "";
        var parts = new List<string>();
        if (copper >= 10000) parts.Add($"{copper / 10000}g");
        if (copper >= 100) parts.Add($"{copper / 100 % 100}s");
        if (copper % 100 > 0) parts.Add($"{copper % 100}c");
        return string.Join(' ', parts);
    }
}
