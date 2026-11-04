using System.Text.RegularExpressions;

namespace WowCompanion.Core;

/// <summary>
/// One autocomplete line for the search box. Completion replaces the word being typed ("stat:cr" → "stat:crit");
/// one ending in ':' is a filter name that still needs its value.
/// </summary>
public sealed record FilterSuggestion(string Completion, string Label, string Hint)
{
    public bool NeedsValue => Completion.EndsWith(':');
    /// <summary>The filter name ("stat", "q"…), for colouring.</summary>
    public string Key => Completion.IndexOf(':') is var i and > 0 ? Completion[..i].ToLowerInvariant() : "";
}

/// <summary>
/// Autocomplete for the search syntax: filter names while typing a word, and values once the word has a colon.
/// Values from the game data (quest titles for quest:, NPC and chest names for from:) are looked up by the app.
/// </summary>
public sealed partial class ItemQuery
{
    private static readonly (string Key, string Hint)[] Keys =
    [
        ("stat", "a stat; results sort by it, best first"),
        ("slot", "an equipment slot: neck, 2h, offhand…"),
        ("type", "armor or weapon type: plate, sword…"),
        ("q", "quality: epic, or rare+ for rare and better"),
        ("lvl", "required level: 60, 50-60, 40+, <20"),
        ("ilvl", "item level: 60, 55-65, 60+"),
        ("class", "items this class can use"),
        ("quest", "rewards of a quest"),
        ("from", "dropped or sold by an NPC, or found in a chest"),
    ];

    // The short name of every stat, in the order the list shows them.
    private static readonly string[] StatOrder =
    [
        "str", "agi", "sta", "int", "spi", "sp", "heal", "crit", "hit", "ap", "rap", "mp5", "hp", "mana", "armor", "def",
        "dodge", "parry", "block", "haste", "pen", "fire", "frost", "shadow", "nature", "arcane", "holy",
        "fr", "frr", "sr", "nr", "ar",
    ];

    private static readonly string[] SlotOrder =
    [
        "head", "neck", "shoulder", "back", "chest", "shirt", "tabard", "wrist", "hands", "waist", "legs", "feet", "ring",
        "trinket", "weapon", "melee", "1h", "2h", "mainhand", "offhand", "shield", "held", "ranged", "relic", "bag",
    ];

    private static readonly string[] TypeOrder =
    [
        "cloth", "leather", "mail", "plate", "shield", "axe", "mace", "sword", "dagger", "staff", "polearm", "fist", "bow",
        "gun", "crossbow", "wand", "thrown", "fishing", "consumable", "recipe", "reagent", "tradegoods", "quest", "container",
    ];

    private static readonly string[] QualityOrder = ["poor", "common", "uncommon", "rare", "epic", "legendary"];
    private static readonly string[] ClassOrder = ["warrior", "paladin", "hunter", "rogue", "priest", "shaman", "mage", "warlock", "druid"];

    private static readonly Dictionary<string, string> KeyAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["s"] = "stat", ["t"] = "type", ["quality"] = "q", ["level"] = "lvl", ["req"] = "lvl", ["itemlevel"] = "ilvl",
        ["c"] = "class", ["drop"] = "from", ["boss"] = "from", ["npc"] = "from",
    };

    /// <summary>
    /// The word being typed at the end of the search text, with where it starts; null after a space. A quoted
    /// value (quest:"the defias) counts as one word.
    /// </summary>
    public static (string Word, int Start)? WordBeingTyped(string text)
    {
        if (text.Length == 0 || char.IsWhiteSpace(text[^1])) return null;
        var last = Regex.Matches(text, "\\w+:\\s*\"[^\"]*\"?|\"[^\"]*\"?|\\S+").LastOrDefault();
        return last is null || last.Index + last.Length != text.Length ? null : (last.Value, last.Index);
    }

    /// <summary>
    /// Suggestions for a word being typed: filter names for a plain word (2+ letters, so names stay quiet), the
    /// values of a filter once it has a colon. quest: and from: values come from the game data (see the app).
    /// </summary>
    public static List<FilterSuggestion> Suggest(string word, int limit = 8)
    {
        var list = new List<FilterSuggestion>();
        var colon = word.IndexOf(':');
        if (colon < 0)
        {
            if (word.Length < 2 && !word.Equals("q", StringComparison.OrdinalIgnoreCase)) return list;
            foreach (var (keyName, keyHint) in Keys)
                if (keyName.StartsWith(word, StringComparison.OrdinalIgnoreCase)) list.Add(new(keyName + ":", keyName + ":", keyHint));
            return list;
        }

        var typedKey = word[..colon].ToLowerInvariant();
        var key = KeyAliases.TryGetValue(typedKey, out var real) ? real : typedKey;
        var value = word[(colon + 1)..].TrimStart();
        var prefix = typedKey + ":";

        IEnumerable<FilterSuggestion> Values(IEnumerable<string> names, Func<string, string> hint, string start = "") =>
            names.Where(n => n.StartsWith(value[start.Length..], StringComparison.OrdinalIgnoreCase))
                 .Select(n => new FilterSuggestion(prefix + start + n, prefix + start + n, hint(n)));

        switch (key)
        {
            case "stat":
            {
                // Several stats: stat:sp,crit → complete the part after the last comma.
                var comma = value.LastIndexOf(',');
                var start = comma >= 0 ? value[..(comma + 1)] : "";
                var part = value[start.Length..];
                var seen = new HashSet<string>();
                foreach (var name in StatOrder)
                {
                    var stat = StatAliases[name];
                    var matches = name.StartsWith(part, StringComparison.OrdinalIgnoreCase)
                        || StatAliases.Any(a => a.Value == stat && a.Key.StartsWith(part, StringComparison.OrdinalIgnoreCase));
                    if (matches && seen.Add(name)) list.Add(new(prefix + start + name, prefix + start + name, stat.Label));
                }
                break;
            }
            case "slot":
                list.AddRange(Values(SlotOrder, _ => "slot"));
                break;
            case "type":
                list.AddRange(Values(TypeOrder, n => Types[n].Class == 2 ? "weapon type" : Types[n].Class == 4 ? "armor type" : "item kind"));
                break;
            case "q":
            {
                var bare = value.TrimEnd('+');
                foreach (var name in QualityOrder.Where(n => n.StartsWith(bare, StringComparison.OrdinalIgnoreCase)))
                {
                    var label = char.ToUpperInvariant(name[0]) + name[1..];
                    if (!value.EndsWith('+')) list.Add(new(prefix + name, prefix + name, $"{label} only"));
                    if (name is not ("poor" or "legendary")) list.Add(new(prefix + name + "+", prefix + name + "+", $"{label} and better"));
                }
                break;
            }
            case "lvl" or "ilvl":
            {
                var what = key == "lvl" ? "required level" : "item level";
                if (value.Length == 0)
                {
                    list.Add(new(prefix + "60", prefix + "60", $"{what} exactly 60"));
                    list.Add(new(prefix + "50-60", prefix + "50-60", $"{what} 50 to 60"));
                    list.Add(new(prefix + "40+", prefix + "40+", $"{what} 40 and up"));
                    list.Add(new(prefix + "<20", prefix + "<20", $"{what} up to 20"));
                }
                else if (Regex.IsMatch(value, @"^\d+$"))
                {
                    list.Add(new(prefix + value, prefix + value, $"{what} exactly {value}"));
                    list.Add(new(prefix + value + "+", prefix + value + "+", $"{what} {value} and up"));
                    list.Add(new(prefix + "<" + value, prefix + "<" + value, $"{what} up to {value}"));
                }
                break;
            }
            case "class":
                list.AddRange(Values(ClassOrder, n => "usable by " + char.ToUpperInvariant(n[0]) + n[1..] + "s"));
                break;
        }
        return list.Take(limit).ToList();
    }

    /// <summary>A quest: or from: value from the data, quoted when it has spaces.</summary>
    public static string NameValue(string key, string name) =>
        key + ":" + (name.Any(char.IsWhiteSpace) ? "\"" + name.Replace("\"", "") + "\"" : name);
}
