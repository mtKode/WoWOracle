using System.Globalization;
using System.Text.RegularExpressions;

namespace WowCompanion.Core;

/// <summary>A stat the search can filter and sort on: item stat types plus equip auras that give the same thing.</summary>
public sealed record StatFilter(string Label, int[] StatTypes, int[] Auras, int AuraMisc = -1);

/// <summary>
/// Search box syntax. Plain words search item names. Filters, in any order, all must match:
///   stat:str  stat:sp  stat:crit ...   item has the stat (several = all of them); results sort by it
///   neck / ring / 2h ... or slot:neck  equipment slot
///   type:plate  type:dagger            armor or weapon type
///   q:epic  q:rare+                    quality (exactly, or at least with +)
///   lvl:50-60  lvl:&lt;40  lvl:58+     required level;  ilvl: the same for item level
///   class:mage                         usable by that class
///   quest:defias  quest:"the defias brotherhood"   rewarded by a quest whose name contains the text
///   from:ragnaros  from:"mr. smite"     dropped by / sold by / found in an NPC or chest whose name contains it
/// "stat: str" (with a space) works too. Put a word in quotes ("chest") to search names instead of slots.
/// </summary>
public sealed partial class ItemQuery
{
    public string Text { get; private set; } = "";
    /// <summary>The quest: filter's text, so the search can list that quest too.</summary>
    public string QuestText { get; private set; } = "";
    public List<string> Conditions { get; } = [];
    public Dictionary<string, object> Parameters { get; } = new();
    public List<StatFilter> Stats { get; } = [];
    public List<string> Unknown { get; } = [];
    public bool HasFilters => Conditions.Count > 0;

    private static readonly Dictionary<string, StatFilter> StatAliases = BuildStats();

    private static Dictionary<string, StatFilter> BuildStats()
    {
        var d = new Dictionary<string, StatFilter>(StringComparer.OrdinalIgnoreCase);
        void Add(StatFilter f, params string[] names) { foreach (var n in names) d[n] = f; }
        Add(new("Str", [4], [29], 0), "str", "strength");
        Add(new("Agi", [3], [29], 1), "agi", "agility");
        Add(new("Sta", [7], [29], 2), "sta", "stam", "stamina");
        Add(new("Int", [5], [29], 3), "int", "intellect");
        Add(new("Spi", [6], [29], 4), "spi", "spirit");
        Add(new("Health", [1], [34]), "hp", "health");
        Add(new("Mana", [0], [35]), "mana");
        Add(new("Spell power", [45, 42], [13]), "sp", "spellpower", "spelldamage", "spelldmg", "dmg");
        Add(new("Healing", [41, 45], [135]), "heal", "healing");
        Add(new("Crit", [32], [52, 57]), "crit");
        Add(new("Hit", [31], [54, 55]), "hit");
        Add(new("Attack power", [38], [99]), "ap", "attackpower");
        Add(new("Ranged AP", [39], [124]), "rap");
        Add(new("Mp5", [43], [85]), "mp5", "regen");
        Add(new("Armor", [50], []), "armor");
        Add(new("Defense", [12], []), "def", "defense");
        Add(new("Dodge", [13], []), "dodge");
        Add(new("Parry", [14], []), "parry");
        Add(new("Block", [15, 48], []), "block");
        Add(new("Haste", [36], []), "haste");
        Add(new("Spell pen", [47], []), "pen", "spellpen");
        Add(new("Fire power", [85], []), "fire", "firesp");
        Add(new("Frost power", [87], []), "frost", "frostsp");
        Add(new("Shadow power", [88], []), "shadow", "shadowsp");
        Add(new("Nature power", [86], []), "nature", "naturesp");
        Add(new("Arcane power", [89], []), "arcane", "arcanesp");
        Add(new("Holy power", [84], []), "holy", "holysp");
        Add(new("Fire res", [51], [22], 4), "fr", "fireres", "fireresist");
        Add(new("Frost res", [52], [22], 16), "frr", "frostres", "frostresist");
        Add(new("Shadow res", [54], [22], 32), "sr", "shadowres", "shadowresist");
        Add(new("Nature res", [55], [22], 8), "nr", "natureres", "natureresist");
        Add(new("Arcane res", [56], [22], 64), "ar", "arcaneres", "arcaneresist");
        return d;
    }

    private static readonly Dictionary<string, int[]> Slots = new(StringComparer.OrdinalIgnoreCase)
    {
        ["head"] = [1], ["helm"] = [1], ["neck"] = [2], ["necklace"] = [2], ["shoulder"] = [3], ["shoulders"] = [3],
        ["shirt"] = [4], ["chest"] = [5, 20], ["robe"] = [20], ["waist"] = [6], ["belt"] = [6], ["legs"] = [7],
        ["feet"] = [8], ["boots"] = [8], ["wrist"] = [9], ["bracers"] = [9], ["hands"] = [10], ["gloves"] = [10],
        ["finger"] = [11], ["ring"] = [11], ["trinket"] = [12], ["back"] = [16], ["cloak"] = [16], ["cape"] = [16],
        ["1h"] = [13, 21, 22], ["onehand"] = [13, 21, 22], ["one-hand"] = [13, 21, 22], ["1hand"] = [13, 21, 22],
        ["2h"] = [17], ["twohand"] = [17], ["two-hand"] = [17], ["2hand"] = [17],
        ["mainhand"] = [13, 21, 17], ["main-hand"] = [13, 21, 17], ["mh"] = [13, 21, 17],
        ["offhand"] = [13, 22, 23, 14], ["off-hand"] = [13, 22, 23, 14], ["oh"] = [13, 22, 23, 14],
        ["shield"] = [14], ["held"] = [23], ["holdable"] = [23],
        ["weapon"] = [13, 17, 21, 22, 15, 25, 26], ["weapons"] = [13, 17, 21, 22, 15, 25, 26],
        ["melee"] = [13, 17, 21, 22],
        ["ranged"] = [15, 25, 26], ["relic"] = [28], ["tabard"] = [19], ["bag"] = [18],
    };

    // type: (item class, subclasses)
    private static readonly Dictionary<string, (int Class, int[] Subs)> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cloth"] = (4, new[] { 1 }), ["leather"] = (4, new[] { 2 }), ["mail"] = (4, new[] { 3 }), ["plate"] = (4, new[] { 4 }), ["shield"] = (4, new[] { 6 }),
        ["axe"] = (2, new[] { 0, 1 }), ["mace"] = (2, new[] { 4, 5 }), ["sword"] = (2, new[] { 7, 8 }), ["dagger"] = (2, new[] { 15 }),
        ["staff"] = (2, new[] { 10 }), ["polearm"] = (2, new[] { 6 }), ["fist"] = (2, new[] { 13 }), ["bow"] = (2, new[] { 2 }), ["gun"] = (2, new[] { 3 }),
        ["crossbow"] = (2, new[] { 18 }), ["wand"] = (2, new[] { 19 }), ["thrown"] = (2, new[] { 16 }), ["fishing"] = (2, new[] { 20 }),
        ["weapon"] = (2, Array.Empty<int>()), ["armor"] = (4, Array.Empty<int>()), ["consumable"] = (0, Array.Empty<int>()), ["recipe"] = (9, Array.Empty<int>()), ["reagent"] = (5, Array.Empty<int>()),
        ["tradegoods"] = (7, Array.Empty<int>()), ["quest"] = (12, Array.Empty<int>()), ["container"] = (1, Array.Empty<int>()),
    };

    private static readonly Dictionary<string, int> Qualities = new(StringComparer.OrdinalIgnoreCase)
    {
        ["poor"] = 0, ["grey"] = 0, ["gray"] = 0, ["common"] = 1, ["white"] = 1, ["uncommon"] = 2, ["green"] = 2,
        ["rare"] = 3, ["blue"] = 3, ["epic"] = 4, ["purple"] = 4, ["legendary"] = 5, ["orange"] = 5, ["artifact"] = 6,
    };

    private static readonly Dictionary<string, int> ClassIds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["warrior"] = 1, ["paladin"] = 2, ["hunter"] = 3, ["rogue"] = 4, ["priest"] = 5, ["shaman"] = 7,
        ["mage"] = 8, ["warlock"] = 9, ["druid"] = 11,
    };

    public static ItemQuery Parse(string input)
    {
        var q = new ItemQuery();
        // "stat: str" -> "stat:str"
        var normalized = Regex.Replace(input, @"(\w+):\s+", "$1:");
        var words = new List<string>();
        var slotWords = new List<(int Position, string Word, int[] Types)>();

        // key:"several words" stays one token
        foreach (Match token in Regex.Matches(normalized, "\\w+:\"[^\"]*\"?|\"[^\"]*\"|\\S+"))
        {
            var t = token.Value;
            if (t.StartsWith('"')) { words.Add(t.Trim('"')); continue; }

            var colon = t.IndexOf(':');
            if (colon > 0)
            {
                var key = t[..colon].ToLowerInvariant();
                var value = t[(colon + 1)..];
                if (!q.AddFilter(key, value)) q.Unknown.Add(t);
                continue;
            }
            if (Slots.TryGetValue(t, out var slotTypes)) { slotWords.Add((words.Count, t, slotTypes)); continue; }
            words.Add(t);
        }

        // A bare slot word ("neck") is a filter only next to another filter, so "Ring of Protection" stays a name.
        if (q.HasFilters)
            foreach (var (_, _, types) in slotWords) q.AddSlot(types);
        else
            foreach (var (position, word, _) in slotWords.OrderByDescending(s => s.Position)) words.Insert(position, word);

        q.Text = string.Join(' ', words);
        return q;
    }

    /// <summary>A whole "key:value" filter this parser understands (for turning it into a chip).</summary>
    public static bool IsKeyFilter(string token)
    {
        if (token.StartsWith('"') || !token.Contains(':')) return false;
        var q = Parse(token);
        return q.HasFilters && q.Unknown.Count == 0 && q.Text.Length == 0;
    }

    /// <summary>A bare slot word like "neck".</summary>
    public static bool IsSlotWord(string token) => Slots.ContainsKey(token);

    /// <summary>Quality number for a q: value ("epic", "rare+"), or null.</summary>
    public static int? QualityOf(string value) => Qualities.TryGetValue(value.TrimEnd('+'), out var q) ? q : null;

    private bool AddFilter(string key, string value)
    {
        switch (key)
        {
            case "stat" or "s":
                foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!StatAliases.TryGetValue(part, out var stat)) return false;
                    AddStat(stat);
                }
                return true;
            case "slot":
                // slot: takes slots (neck, weapon, 2h, offhand, shield...) and weapon/armor types (sword, dagger, bow...).
                if (Slots.TryGetValue(value, out var slots)) { AddSlot(slots); return true; }
                return AddFilter("type", value);
            case "type" or "t":
                if (!Types.TryGetValue(value, out var type)) return false;
                var cls = Param(type.Class);
                Conditions.Add(type.Subs.Length == 0
                    ? $"i.class_id = {cls}"
                    : $"i.class_id = {cls} AND i.subclass_id IN ({string.Join(',', type.Subs.Select(s => Param(s)))})");
                return true;
            case "q" or "quality":
                var atLeast = value.EndsWith('+');
                if (!Qualities.TryGetValue(value.TrimEnd('+'), out var quality)) return false;
                Conditions.Add($"i.quality {(atLeast ? ">=" : "=")} {Param(quality)}");
                return true;
            case "lvl" or "level" or "req":
                return AddRange("i.required_level", value);
            case "ilvl" or "itemlevel":
                return AddRange("i.item_level", value);
            case "quest":
            {
                var text = Loose(value);
                if (text.Length < 2) return false;
                QuestText = value;
                Conditions.Add($"""
                    i.id IN (SELECT s.item_id FROM item_sources s
                             WHERE s.kind IN ('quest', 'quest_choice')
                               AND s.ref_id IN (SELECT qu.id FROM quests qu WHERE {LooseSql("qu.title")} LIKE {Param("%" + text + "%")}))
                    """);
                return true;
            }
            case "from" or "drop" or "boss" or "npc":
            {
                var text = Loose(value);
                if (text.Length < 2) return false;
                var like = Param("%" + text + "%");
                Conditions.Add($"""
                    i.id IN (SELECT s.item_id FROM item_sources s
                             WHERE s.kind IN ('drop', 'vendor', 'pickpocket', 'skinning')
                               AND s.ref_id IN (SELECT n.id FROM npcs n WHERE {LooseSql("n.name")} LIKE {like})
                             UNION
                             SELECT s.item_id FROM item_sources s
                             WHERE s.kind = 'object'
                               AND s.ref_id IN (SELECT o.id FROM objects o WHERE {LooseSql("o.name")} LIKE {like}))
                    """);
                return true;
            }
            case "class" or "c":
                if (!ClassIds.TryGetValue(value, out var classId)) return false;
                Conditions.Add($"(i.allowable_class IN (-1, 0) OR (i.allowable_class & {Param(1 << (classId - 1))}) != 0)");
                return true;
            default:
                return false;
        }
    }

    private void AddSlot(int[] inventoryTypes) =>
        Conditions.Add($"i.inventory_type IN ({string.Join(',', inventoryTypes.Select(s => Param(s)))})");

    private void AddStat(StatFilter stat)
    {
        Stats.Add(stat);
        var parts = stat.StatTypes.Select(type => $"(',' || i.stats) LIKE {Param($"%,{type}:%")}").ToList();
        if (stat.Auras.Length > 0)
        {
            var misc = stat.AuraMisc >= 0 ? $" AND (se.misc0 = {Param(stat.AuraMisc)} OR se.misc0 = -1)" : "";
            parts.Add($"""
                EXISTS (SELECT 1 FROM item_effects e JOIN spell_effects se ON se.spell_id = e.spell_id
                        WHERE e.item_id = i.id AND e.trigger_type = 1 AND se.effect = 6
                          AND se.aura IN ({string.Join(',', stat.Auras)}){misc})
                """);
        }
        Conditions.Add("(" + string.Join(" OR ", parts) + ")");
    }

    /// <summary>"60", "50-60", "50+", "&lt;40", "&gt;55".</summary>
    private bool AddRange(string column, string value)
    {
        int ToInt(string s) => int.Parse(s, CultureInfo.InvariantCulture);
        var m = Regex.Match(value, @"^(\d+)-(\d+)$");
        if (m.Success) { Conditions.Add($"{column} BETWEEN {Param(ToInt(m.Groups[1].Value))} AND {Param(ToInt(m.Groups[2].Value))}"); return true; }
        m = Regex.Match(value, @"^(\d+)\+$");
        if (m.Success) { Conditions.Add($"{column} >= {Param(ToInt(m.Groups[1].Value))}"); return true; }
        m = Regex.Match(value, @"^([<>])=?(\d+)$");
        if (m.Success) { Conditions.Add($"{column} {(m.Groups[1].Value == "<" ? "<=" : ">=")} {Param(ToInt(m.Groups[2].Value))}"); return true; }
        m = Regex.Match(value, @"^(\d+)$");
        if (m.Success) { Conditions.Add($"{column} = {Param(ToInt(m.Groups[1].Value))}"); return true; }
        return false;
    }

    // Name matching for quest:/from: ignores case, spaces and punctuation, so "mr.smite" finds "Mr. Smite"
    // and "vancleef" finds "Edwin VanCleef".
    private static readonly char[] LooseDrop = [' ', '.', '\'', '-', ',', ':', '!', '"'];

    internal static string Loose(string text) =>
        new(text.ToLowerInvariant().Where(c => !LooseDrop.Contains(c)).ToArray());

    internal static string LooseSql(string column) =>
        LooseDrop.Aggregate($"lower({column})",
            (sql, c) => $"replace({sql}, '{(c == '\'' ? "''" : c.ToString())}', '')");

    private string Param(object value)
    {
        var name = "$f" + Parameters.Count;
        Parameters[name] = value;
        return name;
    }
}
