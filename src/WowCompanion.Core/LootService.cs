using Microsoft.Data.Sqlite;

namespace WowCompanion.Core;

/// <summary>A dungeon (type 1) or raid (type 2) with the level range of its elite NPCs.</summary>
public sealed record LootInstance(int MapId, string Name, int Type, int MinLevel, int MaxLevel)
{
    public bool IsRaid => Type == 2;
}

/// <summary>NPC rank: 1 elite, 2 rare elite, 3 boss, 4 rare. IsObject: a boss chest (Cache of the Firelord), NpcId is then the object id.</summary>
public sealed record LootBoss(int NpcId, string Name, int Rank, int MinLevel, int MaxLevel, int MapId, string MapName, bool IsObject = false);

/// <summary>One item a boss drops. Chance 0 means it shares a loot group (one of the group drops).</summary>
public sealed record LootItem(
    int ItemId, string Name, int Quality, int IconId, int ClassId, int SubclassId, int InventoryType,
    int ItemLevel, int RequiredLevel, double Chance, int Droppers = 1)
{
    /// <summary>Random world loot (shared by many NPCs), not this boss's own table.</summary>
    public bool IsWorldDrop => Droppers > 15;
}

/// <summary>A quest that belongs to an instance: picked up for it, done inside it.</summary>
public sealed record LootQuest(int Id, string Title, int Level, int MinLevel, string Giver, string GiverPlace)
{
    public List<LootItem> Rewards { get; } = [];
    public List<LootItem> Choices { get; } = [];
}

/// <summary>
/// AtlasLoot-style browsing over the CMaNGOS Classic data: instances, their bosses, each boss's loot.
/// The dump has no "boss" flag for most dungeon bosses, so a boss is an elite in the instance that has a rank of
/// boss, or drops a green-or-better item few other NPCs drop (trash shares its loot tables widely).
/// </summary>
public sealed class LootService
{
    private readonly string _connectionString;

    /// <summary>
    /// Bosses that are summoned or spawned by a script have no spawn point in the data, so their instance is unknown.
    /// These are placed by hand.
    /// </summary>
    private static readonly Dictionary<string, int> Summoned = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Ragnaros"] = 409, ["Majordomo Executus"] = 409, ["Nefarian"] = 469, ["Gahz'ranka"] = 309,
        ["Kirtonos the Herald"] = 289, ["Death Knight Darkreaver"] = 289, ["Postmaster Malown"] = 329,
        ["Jarien"] = 329, ["Sothos"] = 329, ["Lord Valthalak"] = 229, ["Mor Grayhoof"] = 229, ["Isalien"] = 429,
        ["Avatar of Hakkar"] = 109, ["Ouro"] = 531, ["General Rajaxx"] = 509,
    };

    /// <summary>WoW Forever's own instances have map ids from here on (The Drowned City 2970, Blackmaw Hold 2974…).</summary>
    public const int FirstForeverMap = 2900;

    public LootService(string databasePath)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString();
        EnsureIndex(databasePath);
    }

    /// <summary>Databases built before the loot tab lack an index on (kind, ref_id); add it once.</summary>
    private static void EnsureIndex(string databasePath)
    {
        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false,
            }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE INDEX IF NOT EXISTS item_sources_ref ON item_sources (kind, ref_id);
                CREATE INDEX IF NOT EXISTS npcs_map ON npcs (map);
                """;
            command.ExecuteNonQuery();
        }
        catch (SqliteException)
        {
            // No sources in this database (or read-only file): queries still work, just slower or empty.
        }
    }

    public bool HasData
    {
        get
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name IN ('item_sources', 'npcs', 'maps')";
            return Convert.ToInt32(command.ExecuteScalar()) == 3;
        }
    }

    public IReadOnlyList<LootInstance> GetInstances()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT m.id, m.name, m.instance_type, MIN(n.min_level), MAX(n.max_level)
            FROM maps m JOIN npcs n ON n.map = m.id
            WHERE m.instance_type IN (1, 2) AND n.rank >= 1 AND n.max_level > 0
            GROUP BY m.id, m.name, m.instance_type
            ORDER BY m.instance_type, MAX(n.max_level), m.name
            """;
        var result = new List<LootInstance>();
        using (var r = command.ExecuteReader())
            while (r.Read())
                result.Add(new LootInstance(r.GetInt32(0), r.GetString(1), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4)));

        // Dungeons the Classic world data doesn't have (WoW Forever's own: The Drowned City, Blackmaw Hold…): the
        // client lists their bosses (game data schema 29). Level unknown (0); each type's new ones come after the rest.
        // Only maps from FirstForeverMap on: the client still carries Season of Discovery's (Demon Fall Canyon,
        // Karazhan Crypts…, ids 2700-2899) and test maps.
        try
        {
            using var extra = connection.CreateCommand();
            extra.CommandText = """
                SELECT m.id, m.name, m.instance_type FROM maps m
                WHERE m.instance_type IN (1, 2) AND m.id >= $first AND EXISTS (SELECT 1 FROM dungeon_bosses b WHERE b.map_id = m.id)
                ORDER BY m.instance_type, m.name
                """;
            extra.Parameters.AddWithValue("$first", FirstForeverMap);
            var known = result.Select(i => i.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var have = result.Select(i => i.MapId).ToHashSet();
            using var r = extra.ExecuteReader();
            while (r.Read())
            {
                if (have.Contains(r.GetInt32(0)) || known.Contains(r.GetString(1))) continue; // a Classic one under a new id
                var instance = new LootInstance(r.GetInt32(0), r.GetString(1), r.GetInt32(2), 0, 0);
                var at = result.FindLastIndex(i => i.Type == instance.Type) + 1;
                result.Insert(at == 0 ? (instance.Type == 1 ? 0 : result.Count) : at, instance);
            }
        }
        catch (SqliteException) { } // older game data without the boss list
        return result;
    }

    public IReadOnlyList<LootBoss> GetBosses(int mapId)
    {
        using var connection = Open();
        int instanceType;
        string mapName;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT instance_type, name FROM maps WHERE id = $m";
            command.Parameters.AddWithValue("$m", mapId);
            using var r = command.ExecuteReader();
            if (!r.Read()) return [];
            instanceType = r.GetInt32(0);
            mapName = r.GetString(1);
        }

        var summoned = Summoned.Where(x => x.Value == mapId).Select(x => x.Key).ToList();
        List<LootBoss> npcs;
        using (var command = connection.CreateCommand())
        {
            var names = new List<string>();
            for (var i = 0; i < summoned.Count; i++)
            {
                command.Parameters.AddWithValue("$s" + i, summoned[i]);
                names.Add("$s" + i);
            }
            var extra = names.Count > 0 ? $" OR n.name IN ({string.Join(',', names)})" : "";
            command.CommandText = $"""
                SELECT n.id, n.name, n.rank, n.min_level, n.max_level, $m, $mn
                FROM npcs n WHERE (n.map = $m AND n.rank >= 1){extra}
                """;
            command.Parameters.AddWithValue("$m", mapId);
            command.Parameters.AddWithValue("$mn", mapName);
            npcs = ReadNpcs(command);
        }

        if (npcs.Count == 0 && summoned.Count == 0) return ClientBosses(connection, mapId, mapName);

        var bosses = Bosses(connection, npcs, forced: summoned.ToHashSet(StringComparer.OrdinalIgnoreCase));

        // Dungeons: named elites a level above the usual trash (Ragefire's Oggleflint) are bosses even when
        // their loot is ordinary. Raids skip this (lots of high-level trash).
        if (instanceType == 1 && npcs.Count > 0)
        {
            var usual = npcs.GroupBy(n => n.MaxLevel).OrderByDescending(g => g.Count()).First().Key;
            var known = bosses.Select(b => b.Name).ToHashSet();
            foreach (var n in npcs.Where(n => n.MinLevel == n.MaxLevel && n.MaxLevel > usual && !known.Contains(n.Name))
                         .GroupBy(n => n.Name).Select(g => g.First()))
                bosses.Add(n);
        }

        bosses.AddRange(Chests(connection, mapId, mapName));
        return bosses.OrderBy(b => b.IsObject).ThenBy(b => b.MaxLevel).ThenBy(b => b.MinLevel).ThenBy(b => b.Name).ToList();
    }

    /// <summary>
    /// A dungeon without Classic world data: its bosses from the client's list, in encounter order. A boss the addon
    /// has seen (an NPC of that name) brings its id, so the drops recorded from your kills show; otherwise NpcId is 0.
    /// </summary>
    private static List<LootBoss> ClientBosses(SqliteConnection connection, int mapId, string mapName)
    {
        var list = new List<LootBoss>();
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT b.name,
                       (SELECT n.id FROM npcs n WHERE n.name = b.name
                        ORDER BY (SELECT COUNT(*) FROM item_sources s WHERE s.kind = 'drop' AND s.ref_id = n.id) DESC LIMIT 1)
                FROM dungeon_bosses b WHERE b.map_id = $m ORDER BY b.ord, b.name
                """;
            command.Parameters.AddWithValue("$m", mapId);
            var ids = new List<(string Name, int Id)>();
            using (var r = command.ExecuteReader())
                while (r.Read()) ids.Add((r.GetString(0), r.IsDBNull(1) ? 0 : r.GetInt32(1)));
            foreach (var (name, id) in ids)
            {
                int min = 0, max = 0;
                if (id > 0)
                {
                    using var levels = connection.CreateCommand();
                    levels.CommandText = "SELECT min_level, max_level FROM npcs WHERE id = $id";
                    levels.Parameters.AddWithValue("$id", id);
                    using var r = levels.ExecuteReader();
                    if (r.Read()) (min, max) = (r.GetInt32(0), r.GetInt32(1));
                }
                list.Add(new LootBoss(id, name, 3, min, max, mapId, mapName));
            }
        }
        catch (SqliteException) { }
        return list;
    }

    /// <summary>Boss chests in the instance (Cache of the Firelord, Chest of The Seven, Dark Coffer).</summary>
    private static List<LootBoss> Chests(SqliteConnection connection, int mapId, string mapName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT o.id, o.name FROM objects o
            WHERE o.map = $m AND o.type = 3
              AND (o.name LIKE '%Cache%' OR o.name LIKE '%Coffer%' OR o.name LIKE 'Chest of%' OR o.name LIKE '%Horsemen%')
              AND EXISTS (SELECT 1 FROM item_sources s JOIN items i ON i.id = s.item_id
                          WHERE s.kind = 'object' AND s.ref_id = o.id AND i.quality >= 2)
            """;
        command.Parameters.AddWithValue("$m", mapId);
        var list = new List<LootBoss>();
        using var r = command.ExecuteReader();
        while (r.Read()) list.Add(new LootBoss(r.GetInt32(0), r.GetString(1), 0, 0, 0, mapId, mapName, IsObject: true));
        return list.GroupBy(c => c.Name).Select(g => g.First()).ToList();
    }

    /// <summary>Bosses whose name contains the text, across all instances.</summary>
    public IReadOnlyList<LootBoss> FindBosses(string text, int limit = 25)
    {
        if (text.Trim().Length < 2) return [];
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT n.id, n.name, n.rank, n.min_level, n.max_level, n.map, COALESCE(m.name, '')
            FROM npcs n LEFT JOIN maps m ON m.id = n.map
            WHERE (m.instance_type IN (1, 2) OR n.map = -1 OR m.id IS NULL) AND n.rank >= 1 AND n.name LIKE $q ESCAPE '\'
            LIMIT 300
            """;
        command.Parameters.AddWithValue("$q", "%" + text.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%");
        // Summoned bosses get their instance from the hand-made list; other unplaced NPCs are dropped.
        var mapNames = new Dictionary<int, string>();
        using (var names = connection.CreateCommand())
        {
            names.CommandText = "SELECT id, name FROM maps WHERE instance_type IN (1, 2)";
            using var r = names.ExecuteReader();
            while (r.Read()) mapNames[r.GetInt32(0)] = r.GetString(1);
        }
        var npcs = ReadNpcs(command)
            .Select(n => Summoned.TryGetValue(n.Name, out var map) ? n with { MapId = map, MapName = mapNames.GetValueOrDefault(map, "") } : n)
            .Where(n => mapNames.ContainsKey(n.MapId))
            .ToList();
        var forced = npcs.Where(n => Summoned.ContainsKey(n.Name)).Select(n => n.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Bosses(connection, npcs, forced).OrderBy(b => b.Name).Take(limit).ToList();
    }

    public IReadOnlyList<LootItem> GetLoot(int npcId, bool isObject = false)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT i.id, i.name, i.quality, i.icon_file_id, i.class_id, i.subclass_id, i.inventory_type,
                   i.item_level, i.required_level, MAX(s.chance),
                   (SELECT COUNT(*) FROM item_sources x WHERE x.kind = s.kind AND x.item_id = i.id)
            FROM item_sources s JOIN items i ON i.id = s.item_id
            WHERE s.kind = $k AND s.ref_id = $n
            GROUP BY i.id
            """;
        command.Parameters.AddWithValue("$n", npcId);
        command.Parameters.AddWithValue("$k", isObject ? "object" : "drop");
        var result = new List<LootItem>();
        using var r = command.ExecuteReader();
        while (r.Read())
            result.Add(new LootItem(r.GetInt32(0), r.GetString(1), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4),
                r.GetInt32(5), r.GetInt32(6), r.GetInt32(7), r.GetInt32(8), r.GetDouble(9), r.GetInt32(10)));
        return result;
    }

    /// <summary>
    /// Quests filed under the instance (the quest's zone is one of the instance's areas), lowest level first,
    /// with who gives them, where, and the reward items. Empty for game databases built before quest locations.
    /// </summary>
    public IReadOnlyList<LootQuest> GetQuests(int mapId)
    {
        try
        {
            using var connection = Open();
            var quests = new Dictionary<int, LootQuest>();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    SELECT q.id, q.title, q.level, q.min_level, q.giver,
                           COALESCE(gn.zone, go.zone, ''), COALESCE(gn.zone_x, go.zone_x, 0), COALESCE(gn.zone_y, go.zone_y, 0)
                    FROM quests q
                    LEFT JOIN npcs gn ON q.giver_kind = 'npc' AND gn.id = q.giver_id
                    LEFT JOIN objects go ON q.giver_kind = 'object' AND go.id = q.giver_id
                    WHERE q.zone IN (SELECT id FROM areas WHERE map = $m)
                    ORDER BY q.level, q.title
                    """;
                command.Parameters.AddWithValue("$m", mapId);
                using var r = command.ExecuteReader();
                while (r.Read())
                {
                    var place = ItemSearchService.Location(r.GetString(5), r.GetDouble(6), r.GetDouble(7), "");
                    quests[r.GetInt32(0)] = new LootQuest(r.GetInt32(0), r.GetString(1), r.GetInt32(2), r.GetInt32(3), r.GetString(4), place);
                }
            }
            if (quests.Count == 0) return [];

            using (var command = connection.CreateCommand())
            {
                command.CommandText = $"""
                    SELECT s.ref_id, s.kind, i.id, i.name, i.quality, i.icon_file_id, i.class_id, i.subclass_id, i.inventory_type,
                           i.item_level, i.required_level
                    FROM item_sources s JOIN items i ON i.id = s.item_id
                    WHERE s.kind IN ('quest', 'quest_choice') AND s.ref_id IN {In(command, quests.Keys)}
                    """;
                using var r = command.ExecuteReader();
                while (r.Read())
                {
                    if (!quests.TryGetValue(r.GetInt32(0), out var quest)) continue;
                    var item = new LootItem(r.GetInt32(2), r.GetString(3), r.GetInt32(4), r.GetInt32(5), r.GetInt32(6),
                        r.GetInt32(7), r.GetInt32(8), r.GetInt32(9), r.GetInt32(10), 100);
                    (r.GetString(1) == "quest_choice" ? quest.Choices : quest.Rewards).Add(item);
                }
            }

            // The dump often has the same quest twice (Alliance and Horde versions): keep one per title and giver.
            return quests.Values
                .GroupBy(q => (q.Title, q.Giver))
                .Select(g => g.OrderByDescending(q => q.Rewards.Count + q.Choices.Count).First())
                .OrderBy(q => q.Level).ThenBy(q => q.Title)
                .ToList();
        }
        catch (SqliteException)
        {
            return [];
        }
    }

    // ----- boss detection -----

    private static List<LootBoss> ReadNpcs(SqliteCommand command)
    {
        var list = new List<LootBoss>();
        using var r = command.ExecuteReader();
        while (r.Read())
            list.Add(new LootBoss(r.GetInt32(0), r.GetString(1), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4), r.GetInt32(5), r.GetString(6)));
        return list;
    }

    private static List<LootBoss> Bosses(SqliteConnection connection, List<LootBoss> npcs, ISet<string>? forced = null)
    {
        if (npcs.Count == 0) return npcs;

        // Good drops (green+; low dungeons like Ragefire Chasm only drop greens) of these NPCs.
        var drops = new List<(int Npc, int Item, double Chance)>();
        foreach (var chunk in npcs.Select(n => n.NpcId).Chunk(500))
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT s.ref_id, s.item_id, s.chance FROM item_sources s JOIN items i ON i.id = s.item_id
                WHERE s.kind = 'drop' AND i.quality >= 2 AND s.ref_id IN {In(command, chunk)}
                """;
            using var r = command.ExecuteReader();
            while (r.Read()) drops.Add((r.GetInt32(0), r.GetInt32(1), r.GetDouble(2)));
        }

        // How many NPCs drop each of those items: few droppers = boss loot, many = shared trash/world loot.
        var droppers = new Dictionary<int, int>();
        foreach (var chunk in drops.Select(d => d.Item).Distinct().Chunk(500))
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT item_id, COUNT(*) FROM item_sources WHERE kind = 'drop' AND item_id IN {In(command, chunk)} GROUP BY item_id";
            using var r = command.ExecuteReader();
            while (r.Read()) droppers[r.GetInt32(0)] = r.GetInt32(1);
        }

        var bossIds = drops
            .Where(d => droppers.GetValueOrDefault(d.Item, 99) <= 4 && (d.Chance >= 4 || d.Chance == 0))
            .Select(d => d.Npc)
            .ToHashSet();

        // One row per name: the dump often has several entries for the same boss (versions, phases).
        return npcs.Where(n => n.Rank == 3 || bossIds.Contains(n.NpcId) || forced?.Contains(n.Name) == true)
            .GroupBy(n => (n.Name, n.MapId))
            .Select(g => g.OrderByDescending(n => bossIds.Contains(n.NpcId)).ThenBy(n => n.NpcId).First())
            .ToList();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private static string In(SqliteCommand command, IEnumerable<int> ids)
    {
        var names = new List<string>();
        foreach (var id in ids)
        {
            var name = "$p" + names.Count;
            command.Parameters.AddWithValue(name, id);
            names.Add(name);
        }
        return "(" + (names.Count == 0 ? "NULL" : string.Join(',', names)) + ")";
    }
}
