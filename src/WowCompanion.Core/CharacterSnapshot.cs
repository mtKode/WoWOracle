using System.Globalization;

namespace WowCompanion.Core;

/// <summary>
/// What the addon saved about a character (!WOWORACLE-CHAR blocks in WoWOracle.lua): profession ranks, known recipes
/// per profession, and item counts from bags and bank. Used by the crafting planner.
/// </summary>
public sealed record CharacterSnapshot(
    string Name, string ClassFile, int Level, long Time, bool BankSeen,
    IReadOnlyDictionary<string, (int Rank, int Max)> Skills,
    IReadOnlyDictionary<string, IReadOnlySet<int>> Recipes,
    IReadOnlyDictionary<int, int> Items)
{
    public const string Marker = "!WOWORACLE-CHAR";

    public static CharacterSnapshot? Parse(string text)
    {
        string name = "", classFile = "";
        int level = 0;
        long time = 0;
        var bank = false;
        var skills = new Dictionary<string, (int, int)>(StringComparer.OrdinalIgnoreCase);
        var recipes = new Dictionary<string, IReadOnlySet<int>>(StringComparer.OrdinalIgnoreCase);
        var items = new Dictionary<int, int>();
        static int I(string s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim('\r', ' ');
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq];
            var value = line[(eq + 1)..];
            switch (key)
            {
                case "name": name = value.Trim(); break;
                case "class": classFile = value.Trim(); break;
                case "level": level = I(value); break;
                case "time": time = long.TryParse(value, out var t) ? t : 0; break;
                case "bank": bank = value.Trim() == "seen"; break;
                case "skill":
                {
                    var p = value.Split(';');
                    if (p.Length >= 3) skills[p[0].Trim()] = (I(p[1]), I(p[2]));
                    break;
                }
                case "recipes":
                {
                    var cut = value.IndexOf(';');
                    if (cut <= 0) break;
                    recipes[value[..cut].Trim()] = value[(cut + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries)
                        .Select(I).Where(v => v > 0).ToHashSet();
                    break;
                }
                case "items":
                    foreach (var pair in value.Split(',', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var p = pair.Split(':');
                        if (p.Length == 2 && I(p[0]) > 0) items[I(p[0])] = items.GetValueOrDefault(I(p[0])) + I(p[1]);
                    }
                    break;
            }
        }
        return name.Length == 0 ? null : new CharacterSnapshot(name, classFile, level, time, bank, skills, recipes, items);
    }

    /// <summary>The newest snapshot per character from all of the addon's saved files.</summary>
    public static List<CharacterSnapshot> ReadAll(IEnumerable<string> addonTexts) =>
        addonTexts.SelectMany(text => AddonSavedData.Blocks(text, Marker))
            .Select(Parse).OfType<CharacterSnapshot>()
            .GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(c => c.Time).First())
            .OrderByDescending(c => c.Time).ToList();
}
