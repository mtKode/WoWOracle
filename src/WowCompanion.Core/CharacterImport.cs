using System.Globalization;

namespace WowCompanion.Core;

public sealed record ImportedItem(int Slot, int ItemId, int Enchant, int Suffix, string Name)
{
    /// <summary>Stats this copy has beyond the plain item (random suffix), "type:value,…".</summary>
    public string BonusStats { get; init; } = "";
}
public sealed record ImportedTalent(string Name, int Rank, int SpellId);

/// <summary>A character copied in game with the WoWOracle addon.</summary>
public sealed record CharacterExport(
    string Name, string Realm, int Level, int ClassId, string ClassName, int RaceId, string RaceName,
    bool FromInspect, string TalentSource,
    IReadOnlyList<ImportedItem> Items, IReadOnlyList<ImportedTalent> Talents, IReadOnlyDictionary<string, string> Stats)
{
    /// <summary>Items as the game client describes them (addon 0.3+).</summary>
    public IReadOnlyList<LearnedItem> ItemData { get; init; } = [];
}

/// <summary>
/// Reads the addon's text export:
/// <code>
/// !WOWORACLE 1
/// name=… / realm=… / level=26 / class=HUNTER;3;Hunter / race=Orc;2;Orc / source=inspect|self / talents=traits|classic
/// item=slot;itemId;enchant;suffix;name      (one per equipped slot, slots 1-19)
/// talent=name;rank;spellId;node            (only learned talents)
/// bonus=slot;type:value,…                 (extra stats of that copy, e.g. a random suffix)
/// itemdata=id;quality;ilvl;…;stats;name   (the client's own item data)
/// stat=key;value                          (yourself only)
/// !END
/// </code>
/// Unknown keys are ignored so newer addon versions still import.
/// </summary>
public static class CharacterImport
{
    public const string Marker = "!WOWORACLE";

    public static bool LooksLikeExport(string text) => text.Contains(Marker, StringComparison.Ordinal);

    public static bool TryParse(string text, out CharacterExport? export, out string error)
    {
        export = null;
        error = "";
        var start = text.IndexOf(Marker, StringComparison.Ordinal);
        if (start < 0)
        {
            error = "No WoW Oracle export found. In game, click the Oracle button (or type /oracle) and copy the text.";
            return false;
        }

        var lines = text[start..].Replace("\r", "").Split('\n');
        var header = lines[0].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (header.Length > 1 && int.TryParse(header[1], out var version) && version > 1)
        {
            error = $"This export is format {version}; this app reads format 1. Update WoW Oracle.";
            return false;
        }

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var items = new List<ImportedItem>();
        var talents = new List<ImportedTalent>();
        var stats = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var bonuses = new Dictionary<int, string>();
        var itemData = new List<LearnedItem>();
        var ended = false;

        foreach (var raw in lines.Skip(1))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("!END", StringComparison.Ordinal)) { ended = true; break; }
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq];
            var value = line[(eq + 1)..];
            var parts = value.Split(';');
            switch (key)
            {
                case "item" when parts.Length >= 2 && Int(parts[0]) is > 0 and <= 19 && Int(parts[1]) > 0:
                    items.Add(new ImportedItem(Int(parts[0]), Int(parts[1]), Part(parts, 2, Int), Part(parts, 3, Int), Part(parts, 4, s => s)));
                    break;
                case "talent" when parts.Length >= 2 && parts[0].Length > 0 && Int(parts[1]) > 0:
                    talents.Add(new ImportedTalent(parts[0], Int(parts[1]), Part(parts, 2, Int)));
                    break;
                case "stat" when parts.Length >= 2:
                    stats[parts[0]] = parts[1];
                    break;
                case "bonus" when parts.Length >= 2 && Int(parts[0]) > 0:
                    bonuses[Int(parts[0])] = parts[1];
                    break;
                case "itemdata":
                    if (LearnedItem.Parse(value) is { } data) itemData.Add(data);
                    break;
                default:
                    values[key] = value;
                    break;
            }
        }

        if (!ended)
        {
            error = "The export is cut off (no !END line). Copy the whole text from the game window.";
            return false;
        }

        items = items.Select(i => bonuses.TryGetValue(i.Slot, out var b) ? i with { BonusStats = b } : i).ToList();
        var cls = (values.GetValueOrDefault("class") ?? "").Split(';');
        var race = (values.GetValueOrDefault("race") ?? "").Split(';');
        export = new CharacterExport(
            values.GetValueOrDefault("name") ?? "Unknown",
            values.GetValueOrDefault("realm") ?? "",
            Int(values.GetValueOrDefault("level") ?? "0"),
            Part(cls, 1, Int),
            Part(cls, 2, s => s) is { Length: > 0 } cn ? cn : Part(cls, 0, s => s),
            Part(race, 1, Int),
            Part(race, 2, s => s) is { Length: > 0 } rn ? rn : Part(race, 0, s => s),
            string.Equals(values.GetValueOrDefault("source"), "inspect", StringComparison.OrdinalIgnoreCase),
            values.GetValueOrDefault("talents") ?? "",
            items, talents, stats) { ItemData = itemData };
        if (export.ClassId <= 0)
        {
            error = "The export has no class. Is it from the WoW Oracle addon?";
            return false;
        }
        return true;
    }

    private static int Int(string s) =>
        int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

    private static T Part<T>(string[] parts, int index, Func<string, T> read) =>
        read(index < parts.Length ? parts[index] : "");
}
