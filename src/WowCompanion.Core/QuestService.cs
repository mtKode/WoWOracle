using Microsoft.Data.Sqlite;

namespace WowCompanion.Core;

/// <summary>A quest in the search list.</summary>
public sealed record QuestHit(int Id, string Title, int Level, string Giver, string Place, string Zone, string Side);

/// <summary>A quest's quick card: who, where, what for, and where it sits in its chain.</summary>
public sealed record QuestCard(
    int Id, string Title, int Level, int MinLevel, string Side, string Zone, string Giver, string GiverPlace, string Objectives,
    IReadOnlyList<QuestChainStep> Chain, IReadOnlyList<QuestReward> Rewards, int Money)
{
    /// <summary>The full quest text (what the giver tells you), placeholders made readable.</summary>
    public string Description { get; init; } = "";
}

public sealed record QuestChainStep(int Id, string Title, int Level, bool IsThis);

public sealed record QuestReward(int ItemId, string Name, int Quality, int IconId, bool IsChoice);

/// <summary>Quests for the big search: find by title, and a short card (no full quest text).</summary>
public sealed class QuestService(string databasePath)
{
    private const int AllianceRaces = 1 | 4 | 8 | 64;
    private const int HordeRaces = 2 | 16 | 32 | 128;

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

    public static string SideOf(int races)
    {
        var alliance = races == 0 || (races & AllianceRaces) != 0;
        var horde = races == 0 || (races & HordeRaces) != 0;
        return alliance && horde ? "" : alliance ? "Alliance" : "Horde";
    }

    /// <summary>Quests whose title matches (loosely: "peoples militia" finds "The People's Militia"), best first.</summary>
    public IReadOnlyList<QuestHit> Search(string text, int limit = 6)
    {
        var loose = ItemQuery.Loose(text.Trim());
        if (loose.Length < 3) return [];
        var hits = new List<QuestHit>();
        try
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT q.id, q.title, q.level, q.giver, q.races,
                       COALESCE(gn.zone, go.zone, ''), COALESCE(gn.zone_x, go.zone_x, 0), COALESCE(gn.zone_y, go.zone_y, 0),
                       COALESCE(a.name, '')
                FROM quests q
                LEFT JOIN npcs gn ON q.giver_kind = 'npc' AND gn.id = q.giver_id
                LEFT JOIN objects go ON q.giver_kind = 'object' AND go.id = q.giver_id
                LEFT JOIN areas a ON a.id = q.zone
                WHERE {ItemQuery.LooseSql("q.title")} LIKE $like
                ORDER BY ({ItemQuery.LooseSql("q.title")} = $exact) DESC, ({ItemQuery.LooseSql("q.title")} LIKE $prefix) DESC, q.level, q.title
                LIMIT $limit
                """;
            command.Parameters.AddWithValue("$like", "%" + loose + "%");
            command.Parameters.AddWithValue("$exact", loose);
            command.Parameters.AddWithValue("$prefix", loose + "%");
            command.Parameters.AddWithValue("$limit", limit);
            using var r = command.ExecuteReader();
            while (r.Read())
                hits.Add(new QuestHit(r.GetInt32(0), r.GetString(1), r.GetInt32(2), r.GetString(3),
                    ItemSearchService.Location(r.GetString(5), r.GetDouble(6), r.GetDouble(7), ""), r.GetString(8), SideOf(r.GetInt32(4))));
        }
        catch (SqliteException)
        {
        }
        return hits;
    }

    public QuestCard? Card(int questId)
    {
        using var connection = Open();
        QuestCard? card = null;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT q.id, q.title, q.level, q.min_level, q.races, COALESCE(a.name, ''), q.giver,
                       COALESCE(gn.zone, go.zone, ''), COALESCE(gn.zone_x, go.zone_x, 0), COALESCE(gn.zone_y, go.zone_y, 0),
                       q.objectives, q.details
                FROM quests q
                LEFT JOIN npcs gn ON q.giver_kind = 'npc' AND gn.id = q.giver_id
                LEFT JOIN objects go ON q.giver_kind = 'object' AND go.id = q.giver_id
                LEFT JOIN areas a ON a.id = q.zone
                WHERE q.id = $id
                """;
            command.Parameters.AddWithValue("$id", questId);
            using var r = command.ExecuteReader();
            if (!r.Read()) return null;
            card = new QuestCard(r.GetInt32(0), r.GetString(1).Trim(), r.GetInt32(2), r.GetInt32(3), SideOf(r.GetInt32(4)),
                r.GetString(5), r.GetString(6), ItemSearchService.Location(r.GetString(7), r.GetDouble(8), r.GetDouble(9), ""),
                ZoneJournalService.Teaser(r.GetString(10), 300), [], [], 0)
            {
                // Paragraphs ($B$B) kept, everything else as in the journal's teasers.
                Description = ZoneJournalService.Readable(r.GetString(11).Replace("$B$B", " ¶ ").Replace("$b$b", " ¶ "))
                    .Replace(" ¶ ", "\n\n").Replace("¶", "").Trim(),
            };
        }

        var rewards = new List<QuestReward>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT i.id, i.name, i.quality, i.icon_file_id, s.kind FROM item_sources s
                JOIN items i ON i.id = s.item_id
                WHERE s.kind IN ('quest', 'quest_choice') AND s.ref_id = $id
                ORDER BY s.kind, i.quality DESC, i.name
                """;
            command.Parameters.AddWithValue("$id", questId);
            using var r = command.ExecuteReader();
            while (r.Read())
                rewards.Add(new QuestReward(r.GetInt32(0), r.GetString(1), r.GetInt32(2), r.GetInt32(3), r.GetString(4) == "quest_choice"));
        }

        return card with { Chain = Chain(connection, questId), Rewards = rewards };
    }

    /// <summary>The chain the quest belongs to, start to end (empty for a lone quest).</summary>
    private static List<QuestChainStep> Chain(SqliteConnection connection, int questId)
    {
        var info = new Dictionary<int, (string Title, int Level, int Prev, int Next)>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id, title, level, prev_quest, next_in_chain FROM quests WHERE prev_quest > 0 OR next_in_chain > 0 OR id = $id";
            command.Parameters.AddWithValue("$id", questId);
            using var r = command.ExecuteReader();
            while (r.Read()) info[r.GetInt32(0)] = (r.GetString(1).Trim(), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4));
        }
        // Quests that only appear as a target still need a title.
        var missing = info.Values.SelectMany(v => new[] { v.Prev, v.Next }).Where(id => id > 0 && !info.ContainsKey(id)).Distinct().ToList();
        if (missing.Count > 0)
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT id, title, level, prev_quest, next_in_chain FROM quests WHERE id IN ({string.Join(',', missing)})";
            using var r = command.ExecuteReader();
            while (r.Read()) info[r.GetInt32(0)] = (r.GetString(1).Trim(), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4));
        }

        int? Before(int id)
        {
            if (info.TryGetValue(id, out var q) && q.Prev > 0 && info.ContainsKey(q.Prev)) return q.Prev;
            var pointing = info.FirstOrDefault(p => p.Value.Next == id).Key;
            return pointing > 0 ? pointing : null;
        }
        int? After(int id)
        {
            if (info.TryGetValue(id, out var q) && q.Next > 0 && info.ContainsKey(q.Next)) return q.Next;
            var following = info.Where(p => p.Value.Prev == id).Select(p => p.Key).OrderBy(k => k).FirstOrDefault();
            return following > 0 ? following : null;
        }

        var start = questId;
        var seen = new HashSet<int> { start };
        while (Before(start) is { } previous && seen.Add(previous)) start = previous;
        var steps = new List<int> { start };
        seen = [start];
        var current = start;
        while (After(current) is { } next && seen.Add(next))
        {
            steps.Add(next);
            current = next;
        }
        if (!steps.Contains(questId) || steps.Count < 2) return [];
        return steps.Select(id => new QuestChainStep(id, info[id].Title, info[id].Level, id == questId)).ToList();
    }
}
