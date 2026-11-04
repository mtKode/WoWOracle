using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace WowCompanion.Core;

/// <summary>A zone's page in the Journal: a short lore summary (written for the app) and chains to always feature.</summary>
public sealed record ZoneLore(string Zone, string Continent, string Levels, string Faction, string Summary, IReadOnlyList<string> Featured)
{
    /// <summary>Lowest level of the range ("18-30" → 18); cities sort last.</summary>
    public int SortLevel => int.TryParse(Levels.Split('-')[0], out var v) ? v : 100;
}

/// <summary>The zone lore that ships with the app (Lore/zones.json, embedded).</summary>
public static class ZoneLoreBook
{
    private sealed record Book(List<Entry> Zones);
    private sealed record Entry(string Zone, string Continent, string Levels, string Faction, string Summary, List<string>? Featured);

    public static IReadOnlyList<ZoneLore> All { get; } = Load();

    private static List<ZoneLore> Load()
    {
        try
        {
            var assembly = typeof(ZoneLoreBook).Assembly;
            var name = assembly.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("zones.json", StringComparison.OrdinalIgnoreCase));
            if (name is null) return [];
            using var stream = assembly.GetManifestResourceStream(name)!;
            var book = JsonSerializer.Deserialize<Book>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return book?.Zones.Select(z => new ZoneLore(z.Zone, z.Continent, z.Levels, z.Faction, z.Summary, z.Featured ?? []))
                .OrderBy(z => z.Continent).ThenBy(z => z.SortLevel).ThenBy(z => z.Zone).ToList() ?? [];
        }
        catch (Exception)
        {
            return [];
        }
    }
}

public enum JournalSide { Both, Alliance, Horde }

public sealed record TaleStep(int QuestId, string Title, int Level, string Zone);

/// <summary>A quest chain (or one notable quest) worth telling someone about.</summary>
public sealed record Tale(
    int QuestId, string Title, int MinLevel, int MaxLevel, IReadOnlyList<string> Themes, string Teaser,
    string Giver, string GiverPlace, IReadOnlyList<TaleStep> Steps, bool Featured, bool ReachesDungeon, string Side);

/// <summary>
/// Finds the "tales" of a zone from the quest data: chains (PrevQuestId / NextQuestInChain), scored by length, by lore
/// themes found in their text (lost civilizations, the Scourge, dragonflights…), by whether they lead into a dungeon,
/// and by the zone's hand-picked featured chains. Class and profession quests are left out.
/// </summary>
public sealed class ZoneJournalService(string databasePath)
{
    private const int AllianceRaces = 1 | 4 | 8 | 64;
    private const int HordeRaces = 2 | 16 | 32 | 128;

    /// <summary>Lore themes and the words that point to them (lower case; a theme needs two different words).</summary>
    public static readonly IReadOnlyDictionary<string, string[]> Themes = new Dictionary<string, string[]>
    {
        ["Lost civilizations"] = ["titan", "earthen", "uldaman", "norgannon", "tablet", "highborne", "azshara", "kaldorei",
            "eldre'thalas", "zul'farrak", "gurubashi", "zul'gurub", "atal'ai", "troll empire", "arathor", "strom", "excavation",
            "dig site", "fossil", "ironaya", "archaedas", "ruins of", "ancient", "empire"],
        ["Titans & Old Gods"] = ["titan", "keeper", "c'thun", "qiraj", "silithid", "old god", "ahn'qiraj", "watcher"],
        ["Dragonflights"] = ["dragonflight", "onyxia", "nefarian", "deathwing", "black dragon", "green dragon", "emerald dream",
            "ysera", "nozdormu", "dragonkin", "dragon", "brood"],
        ["The Scourge"] = ["scourge", "lich", "kel'thuzad", "plague", "necromancer", "cult of the damned", "undead", "stratholme"],
        ["The Scarlet Crusade"] = ["scarlet", "crusade", "crusader"],
        ["The Burning Legion"] = ["burning legion", "legion", "demon", "satyr", "eredar", "dreadlord", "kil'jaeden", "archimonde",
            "jaedenar", "fel "],
        ["The Defias"] = ["defias", "vancleef", "van cleef", "stonemason", "brotherhood"],
        ["Dark Irons & Ragnaros"] = ["dark iron", "ragnaros", "thaurissan", "molten core", "blackrock", "firelord"],
        ["Night elves & Elune"] = ["elune", "moonwell", "cenarion", "malfurion", "shan'do", "druid", "night elf"],
        ["Hauntings & curses"] = ["ghost", "haunt", "curse", "spirit", "stalvan", "worgen", "banshee", "restless"],
    };

    private sealed record QuestRow(
        int Id, string Title, int Level, int Zone, string Giver, string GiverPlace, int Prev, int NextInChain,
        int Races, int Classes, int Skill, string Details);

    private Dictionary<int, QuestRow>? _quests;
    private Dictionary<int, string>? _areaNames;
    private HashSet<int>? _dungeonAreas;

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    private void EnsureLoaded()
    {
        if (_quests is not null) return;
        using var connection = Open();
        var quests = new Dictionary<int, QuestRow>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT q.id, q.title, q.level, q.zone, q.giver,
                       COALESCE(gn.zone, go.zone, ''), COALESCE(gn.zone_x, go.zone_x, 0), COALESCE(gn.zone_y, go.zone_y, 0),
                       q.prev_quest, q.next_in_chain, q.races, q.classes, q.skill, q.details
                FROM quests q
                LEFT JOIN npcs gn ON q.giver_kind = 'npc' AND gn.id = q.giver_id
                LEFT JOIN objects go ON q.giver_kind = 'object' AND go.id = q.giver_id
                """;
            using var r = command.ExecuteReader();
            while (r.Read())
            {
                var place = ItemSearchService.Location(r.GetString(5), r.GetDouble(6), r.GetDouble(7), "");
                quests[r.GetInt32(0)] = new QuestRow(r.GetInt32(0), r.GetString(1), r.GetInt32(2), r.GetInt32(3), r.GetString(4),
                    place, r.GetInt32(8), r.GetInt32(9), r.GetInt32(10), r.GetInt32(11), r.GetInt32(12), r.GetString(13));
            }
        }
        var names = new Dictionary<int, string>();
        var dungeons = new HashSet<int>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT a.id, a.name, COALESCE(m.instance_type, 0) FROM areas a LEFT JOIN maps m ON m.id = a.map
                """;
            using var r = command.ExecuteReader();
            while (r.Read())
            {
                names[r.GetInt32(0)] = r.GetString(1);
                if (r.GetInt32(2) > 0) dungeons.Add(r.GetInt32(0));
            }
        }
        _quests = quests;
        _areaNames = names;
        _dungeonAreas = dungeons;
    }

    /// <summary>The area id for a zone name (as the quest data files quests under it), or 0.</summary>
    public int AreaId(string zone)
    {
        EnsureLoaded();
        return _areaNames!.Where(a => string.Equals(a.Value, zone, StringComparison.OrdinalIgnoreCase))
            .Select(a => a.Key).OrderBy(id => id).FirstOrDefault();
    }

    private static bool OnSide(int races, JournalSide side) => side switch
    {
        JournalSide.Alliance => races == 0 || (races & AllianceRaces) != 0,
        JournalSide.Horde => races == 0 || (races & HordeRaces) != 0,
        _ => true,
    };

    private static string SideOf(int races)
    {
        var alliance = races == 0 || (races & AllianceRaces) != 0;
        var horde = races == 0 || (races & HordeRaces) != 0;
        return alliance && horde ? "Both" : alliance ? "Alliance" : "Horde";
    }

    /// <summary>Where a quest is picked up ("Westfall (56.4, 47.6)"), or "".</summary>
    public string GiverPlace(int questId)
    {
        EnsureLoaded();
        return _quests!.TryGetValue(questId, out var q) ? q.GiverPlace : "";
    }

    /// <summary>Quests filed under the zone that this side can do (for the gear hint).</summary>
    public IReadOnlyList<int> QuestIds(string zone, JournalSide side)
    {
        EnsureLoaded();
        var area = AreaId(zone);
        return _quests!.Values.Where(q => q.Zone == area && q.Classes == 0 && OnSide(q.Races, side)).Select(q => q.Id).ToList();
    }

    /// <summary>The zone's tales, best first.</summary>
    public IReadOnlyList<Tale> Tales(ZoneLore lore, JournalSide side, int max = 8)
    {
        EnsureLoaded();
        var quests = _quests!;
        var area = AreaId(lore.Zone);
        if (area == 0) return [];

        // Links: a quest that requires another follows it; NextQuestInChain points forward.
        var next = new Dictionary<int, List<int>>();
        var hasPrevious = new HashSet<int>();
        void Link(int from, int to)
        {
            if (from == to || !quests.ContainsKey(from) || !quests.ContainsKey(to)) return;
            if (!next.TryGetValue(from, out var list)) next[from] = list = [];
            if (!list.Contains(to)) list.Add(to);
            hasPrevious.Add(to);
        }
        foreach (var q in quests.Values)
        {
            if (q.Prev > 0) Link(q.Prev, q.Id);
            if (q.NextInChain > 0) Link(q.Id, q.NextInChain);
        }

        List<int> Follow(int head)
        {
            var chain = new List<int> { head };
            var seen = new HashSet<int> { head };
            var current = head;
            while (next.TryGetValue(current, out var options))
            {
                var open = options.Where(o => !seen.Contains(o) && OnSide(quests[o].Races, side)).ToList();
                if (open.Count == 0) break;
                var preferred = quests[current].NextInChain;
                current = open.Contains(preferred) ? preferred : open[0];
                chain.Add(current);
                seen.Add(current);
            }
            return chain;
        }

        var featured = lore.Featured.Select(f => f.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<(Tale Tale, double Score)>();
        var seenTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var head in quests.Values.Where(q => q.Zone == area && q.Classes == 0 && q.Skill == 0 && OnSide(q.Races, side))
                     .OrderBy(q => q.Id))
        {
            if (hasPrevious.Contains(head.Id) && !featured.Contains(head.Title.Trim())) continue; // only chain starts
            var chain = Follow(head.Id);
            var text = string.Join(" ", chain.Select(id => quests[id].Title + " " + quests[id].Details)).ToLowerInvariant();
            var themes = Themes
                .Select(t => (Name: t.Key, Hits: t.Value.Count(text.Contains)))
                .Where(t => t.Hits >= 2)
                .OrderByDescending(t => t.Hits).Take(2).Select(t => t.Name).ToList();
            var isFeatured = featured.Contains(head.Title.Trim());
            var reachesDungeon = chain.Any(id => _dungeonAreas!.Contains(quests[id].Zone));
            if (chain.Count < 3 && themes.Count == 0 && !isFeatured) continue; // a lone errand
            if (!seenTitles.Add(head.Title.Trim())) continue;

            var score = Math.Min(chain.Count, 10) + 3 * themes.Count + (reachesDungeon ? 3 : 0) + (isFeatured ? 25 : 0);
            var steps = chain.Select(id => new TaleStep(id, quests[id].Title, quests[id].Level,
                _areaNames!.GetValueOrDefault(quests[id].Zone, ""))).ToList();
            var levels = chain.Select(id => quests[id].Level).Where(l => l > 0).DefaultIfEmpty(0).ToList();
            candidates.Add((new Tale(head.Id, head.Title.Trim(), levels.Min(), levels.Max(), themes, Teaser(head.Details),
                head.Giver, head.GiverPlace, steps, isFeatured, reachesDungeon, SideOf(head.Races)), score));
        }
        return candidates.OrderByDescending(c => c.Score).ThenBy(c => c.Tale.MinLevel).Take(max).Select(c => c.Tale).ToList();
    }

    /// <summary>The first sentence or two of a quest's text, with the game's placeholders made readable.</summary>
    public static string Teaser(string details, int maxLength = 240)
    {
        var text = Readable(details);
        if (text.Length <= maxLength) return text;
        var cut = text.LastIndexOfAny(['.', '!', '?'], maxLength);
        return cut > 60 ? text[..(cut + 1)] : text[..maxLength].TrimEnd() + "…";
    }

    /// <summary>"$N" → "traveler", "$C" → "adventurer", "$Gsir:madam;" → "sir", "$B" → line breaks to spaces.</summary>
    public static string Readable(string text)
    {
        text = Regex.Replace(text, @"\$[Gg]\s*([^:;]*):([^;]*);", "$1");
        text = Regex.Replace(text, @"\$[Bb]", " ");
        text = Regex.Replace(text, @"\$[Nn]", "traveler");
        text = Regex.Replace(text, @"\$[Cc]", "adventurer");
        text = Regex.Replace(text, @"\$[Rr]", "friend");
        return Regex.Replace(text, @"\s+", " ").Trim();
    }
}
