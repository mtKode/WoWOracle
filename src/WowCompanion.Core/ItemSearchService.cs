using Microsoft.Data.Sqlite;

namespace WowCompanion.Core;

public sealed record ItemResult(
    int Id, string Name, int Quality, int ItemLevel, int RequiredLevel, int ClassId, int SubclassId, int InventoryType,
    int IconId = 0)
{
    /// <summary>The filtered stats, e.g. "+12 Str", shown in the result list.</summary>
    public string Highlight { get; init; } = "";
    public double SortValue { get; init; }
}

public sealed record ItemStat(int Type, int Value);
public sealed record RecipeReagent(int ItemId, string Name, int Quantity, int Quality = 1);
public sealed record RecipeInfo(int SpellId, string Name, string Profession, int MinSkill, IReadOnlyList<RecipeReagent> Reagents)
{
    public int YellowAt { get; init; }
    public int GreyAt { get; init; }
    public bool SkillEstimated { get; init; }
    public string TaughtBy { get; init; } = "";
}
public sealed record ReagentUse(string RecipeName, string Profession, int MinSkill, int Quality = 1);

public sealed record ItemDetail(
    int Id, string Name, string Description, int Quality, int ItemLevel, int RequiredLevel,
    int ClassId, int SubclassId, int InventoryType, int Bonding, long SellPrice, long BuyPrice,
    int StackSize, IReadOnlyList<ItemStat> Stats,
    IReadOnlyList<RecipeInfo> CraftedBy, IReadOnlyList<ReagentUse> UsedIn)
{
    public int DelayMs { get; init; }
    public double DamageMin { get; init; }
    public double DamageMax { get; init; }
    public int DamageSchool { get; init; }
    public int Armor { get; init; }
    public int MaxCount { get; init; }
    public int IconId { get; init; }
    /// <summary>Class names that may use the item; empty when every class can.</summary>
    public IReadOnlyList<string> Classes { get; init; } = [];
    public IReadOnlyList<ItemEffectInfo> Effects { get; init; } = [];
    public ItemSetInfo? Set { get; init; }
    public IReadOnlyList<ItemSource> Sources { get; init; } = [];
    /// <summary>False when the game database was built without the sources dump.</summary>
    public bool HasSourceData { get; init; }
}

/// <summary>Use / Equip / Chance on hit line. Trigger: 0 use, 1 equip, 2 chance on hit, 5 use, 6 learn.</summary>
public sealed record ItemEffectInfo(int Trigger, int SpellId, string SpellName, string Text, int Charges, int CooldownMs);
public sealed record SetPiece(int ItemId, string Name, int Quality);

/// <summary>One way to get an item. Kind: drop, vendor, quest, quest_choice, object, container, pickpocket, skinning, fishing.</summary>
public sealed record ItemSource(string Kind, int RefId, string Name, string Detail, double Chance)
{
    /// <summary>Map, dungeon or zone, shown on its own line.</summary>
    public string Place { get; init; } = "";
    /// <summary>NPC rank: 0 normal, 1 elite, 2 rare elite, 3 boss, 4 rare.</summary>
    public int Rank { get; init; }
    /// <summary>Item quality, for container sources.</summary>
    public int Quality { get; init; } = 1;
}
public sealed record SetBonus(int Threshold, int SpellId, string Text);
public sealed record ItemSetInfo(int Id, string Name, IReadOnlyList<SetPiece> Pieces, IReadOnlyList<SetBonus> Bonuses);

/// <summary>Reads the game database. Opens a short-lived read-only connection per call.</summary>
public sealed class ItemSearchService(string databasePath)
{
    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = databasePath,
        Mode = SqliteOpenMode.ReadOnly,
        Pooling = false,
    }.ToString();

    /// <summary>
    /// Name search plus filters (see ItemQuery). With a stat filter, results are sorted by that stat, highest first.
    /// </summary>
    public IReadOnlyList<ItemResult> Search(string query, int limit = 50) => Search(ItemQuery.Parse(query), limit);

    public IReadOnlyList<ItemResult> Search(ItemQuery query, int limit = 50)
    {
        var match = BuildMatch(query.Text);
        if (match is null && !query.HasFilters) return [];

        using var connection = Open();
        using var command = connection.CreateCommand();
        var where = new List<string>(query.Conditions) { ItemVisibility.Clause("i.id") };
        string from, order;
        if (match is not null)
        {
            from = "items_fts JOIN items i ON i.id = items_fts.rowid";
            where.Insert(0, "items_fts MATCH $match");
            order = "bm25(items_fts, 10.0, 1.0), i.item_level DESC";
            command.Parameters.AddWithValue("$match", match);
        }
        else
        {
            from = "items i";
            order = "i.item_level DESC, i.quality DESC";
        }
        foreach (var (name, value) in query.Parameters) command.Parameters.AddWithValue(name, value);

        // Sorting by a stat happens after loading, so fetch a wider set first.
        var fetch = query.Stats.Count > 0 ? Math.Max(limit, 1500) : limit;
        command.CommandText = $"""
            SELECT i.id, i.name, i.quality, i.item_level, i.required_level, i.class_id, i.subclass_id, i.inventory_type,
                   i.icon_file_id, i.stats
            FROM {from}
            WHERE {string.Join(" AND ", where)}
            ORDER BY {order}
            LIMIT $limit
            """;
        command.Parameters.AddWithValue("$limit", fetch);

        var results = new List<ItemResult>();
        var stats = new Dictionary<int, string>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var id = reader.GetInt32(0);
                results.Add(new ItemResult(
                    id, reader.GetString(1), reader.GetInt32(2), reader.GetInt32(3),
                    reader.GetInt32(4), reader.GetInt32(5), reader.GetInt32(6), reader.GetInt32(7), reader.GetInt32(8)));
                stats[id] = reader.GetString(9);
            }
        }
        if (query.Stats.Count == 0) return results;

        // Value of the asked-for stats per item: item stats plus matching equip auras.
        var auraValues = AuraValues(connection, results.Select(r => r.Id).ToList(), query.Stats);
        double Value(ItemResult r, StatFilter f)
        {
            var total = ParseStats(stats[r.Id]).Where(s => f.StatTypes.Contains(s.Type)).Sum(s => (double)s.Value);
            return total + auraValues.GetValueOrDefault((r.Id, f.Label));
        }
        return results
            .Select(r => r with { Highlight = string.Join("  ", query.Stats.Select(f => $"+{Value(r, f):0} {f.Label}")) , SortValue = query.Stats.Sum(f => Value(r, f)) })
            .OrderByDescending(r => r.SortValue)
            .ThenByDescending(r => r.ItemLevel)
            .Take(limit)
            .ToList();
    }

    private static Dictionary<(int Item, string Stat), double> AuraValues(SqliteConnection connection, List<int> ids, List<StatFilter> filters)
    {
        var result = new Dictionary<(int, string), double>();
        var withAuras = filters.Where(f => f.Auras.Length > 0).ToList();
        if (withAuras.Count == 0 || ids.Count == 0) return result;

        foreach (var chunk in ids.Chunk(500))
        {
            using var command = connection.CreateCommand();
            var names = chunk.Select((id, i) => { command.Parameters.AddWithValue("$i" + i, id); return "$i" + i; });
            command.CommandText = $"""
                SELECT e.item_id, se.aura, se.misc0, se.base_points
                FROM item_effects e JOIN spell_effects se ON se.spell_id = e.spell_id
                WHERE e.trigger_type = 1 AND se.effect = 6 AND e.item_id IN ({string.Join(',', names)})
                """;
            using var r = command.ExecuteReader();
            while (r.Read())
            {
                var (item, aura, misc, value) = (r.GetInt32(0), r.GetInt32(1), r.GetInt32(2), r.GetDouble(3));
                foreach (var f in withAuras)
                {
                    if (!f.Auras.Contains(aura)) continue;
                    if (f.AuraMisc >= 0 && misc != f.AuraMisc && misc != -1) continue;
                    result[(item, f.Label)] = result.GetValueOrDefault((item, f.Label)) + value;
                }
            }
        }
        return result;
    }

    public ItemDetail? GetDetail(int id)
    {
        using var connection = Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT name, description, quality, item_level, required_level, class_id, subclass_id,
                   inventory_type, bonding, sell_price, buy_price, stack_size, stats,
                   delay_ms, dmg_min, dmg_max, dmg_school, armor, item_set, allowable_class, max_count, icon_file_id
            FROM items WHERE id = $id
            """;
        command.Parameters.AddWithValue("$id", id);

        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;

        var detail = new ItemDetail(
            id, reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetInt32(3),
            reader.GetInt32(4), reader.GetInt32(5), reader.GetInt32(6), reader.GetInt32(7),
            reader.GetInt32(8), reader.GetInt64(9), reader.GetInt64(10), reader.GetInt32(11),
            ParseStats(reader.GetString(12)),
            [], [])
        {
            DelayMs = reader.GetInt32(13),
            DamageMin = reader.GetDouble(14),
            DamageMax = reader.GetDouble(15),
            DamageSchool = reader.GetInt32(16),
            Armor = reader.GetInt32(17),
            MaxCount = reader.GetInt32(19),
            IconId = reader.GetInt32(21),
        };
        var setId = reader.GetInt32(18);
        var allowedClasses = reader.GetInt32(20);
        reader.Close();

        return detail with
        {
            CraftedBy = ReadCraftedBy(connection, id),
            UsedIn = ReadUsedIn(connection, id),
            Effects = ReadEffects(connection, id),
            Set = setId > 0 ? ReadSet(connection, setId) : null,
            Classes = ReadClasses(connection, allowedClasses),
            Sources = ReadSources(connection, id),
            HasSourceData = HasRows(connection, "item_sources"),
        };
    }

    private static bool HasRows(SqliteConnection connection, string table)
    {
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT EXISTS (SELECT 1 FROM {table})";
            return Convert.ToInt32(command.ExecuteScalar()) == 1;
        }
        catch (SqliteException) { return false; }
    }

    private static readonly string[] Ranks = ["", "Elite", "Rare Elite", "Boss", "Rare"];

    /// <summary>"Westfall (56.4, 47.6)", or the fallback (map / zone name) when no position is known.</summary>
    public static string Location(string zone, double x, double y, string fallback)
    {
        if (zone.Length == 0) return fallback;
        if (x <= 0 && y <= 0) return zone;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        return $"{zone} ({x.ToString("0.#", inv)}, {y.ToString("0.#", inv)})";
    }

    private static List<ItemSource> ReadSources(SqliteConnection connection, int itemId)
    {
        var result = new List<ItemSource>();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.kind, s.ref_id, s.chance,
                   CASE WHEN s.kind IN ('drop', 'pickpocket', 'skinning', 'vendor') THEN COALESCE(n.name, 'NPC ' || s.ref_id)
                        WHEN s.kind = 'object' THEN COALESCE(o.name, 'Object ' || s.ref_id)
                        WHEN s.kind IN ('quest', 'quest_choice') THEN COALESCE(q.title, 'Quest ' || s.ref_id)
                        WHEN s.kind = 'container' THEN COALESCE(ci.name, 'Item ' || s.ref_id)
                        WHEN s.kind = 'fishing' THEN COALESCE(a.name, 'Zone ' || s.ref_id)
                        ELSE '' END,
                   COALESCE(n.subname, ''), COALESCE(n.min_level, 0), COALESCE(n.max_level, 0), COALESCE(n.rank, 0),
                   COALESCE(nm.name, om.name, ''),
                   COALESCE(q.level, 0), COALESCE(qa.name, ''), COALESCE(q.giver, ''), COALESCE(ci.quality, 1),
                   COALESCE(n.zone, o.zone, ''), COALESCE(n.zone_x, o.zone_x, 0), COALESCE(n.zone_y, o.zone_y, 0),
                   COALESCE(gn.zone, go.zone, ''), COALESCE(gn.zone_x, go.zone_x, 0), COALESCE(gn.zone_y, go.zone_y, 0)
            FROM item_sources s
            LEFT JOIN npcs n ON s.kind IN ('drop', 'pickpocket', 'skinning', 'vendor') AND n.id = s.ref_id
            LEFT JOIN maps nm ON nm.id = n.map
            LEFT JOIN objects o ON s.kind = 'object' AND o.id = s.ref_id
            LEFT JOIN maps om ON om.id = o.map
            LEFT JOIN quests q ON s.kind IN ('quest', 'quest_choice') AND q.id = s.ref_id
            LEFT JOIN areas qa ON qa.id = q.zone
            LEFT JOIN npcs gn ON q.giver_kind = 'npc' AND gn.id = q.giver_id
            LEFT JOIN objects go ON q.giver_kind = 'object' AND go.id = q.giver_id
            LEFT JOIN items ci ON s.kind = 'container' AND ci.id = s.ref_id
            LEFT JOIN areas a ON s.kind = 'fishing' AND a.id = s.ref_id
            WHERE s.item_id = $id
            ORDER BY s.kind, s.chance DESC
            LIMIT 3000
            """;
        command.Parameters.AddWithValue("$id", itemId);
        try
        {
            using var r = command.ExecuteReader();
            while (r.Read())
            {
                var kind = r.GetString(0);
                var detail = new List<string>();
                var place = "";
                var npcRank = 0;
                switch (kind)
                {
                    case "drop" or "pickpocket" or "skinning" or "vendor":
                        if (kind == "vendor" && r.GetString(4).Length > 0) detail.Add($"<{r.GetString(4)}>");
                        var (min, max, rank) = (r.GetInt32(5), r.GetInt32(6), r.GetInt32(7));
                        if (min > 0) detail.Add(min == max ? $"level {min}" : $"level {min}-{max}");
                        if (rank > 0 && rank < Ranks.Length) detail.Add(Ranks[rank]);
                        npcRank = rank;
                        place = Location(r.GetString(13), r.GetDouble(14), r.GetDouble(15), r.GetString(8));
                        break;
                    case "object":
                        place = Location(r.GetString(13), r.GetDouble(14), r.GetDouble(15), r.GetString(8));
                        break;
                    case "quest" or "quest_choice":
                        if (r.GetInt32(9) > 0) detail.Add($"level {r.GetInt32(9)}");
                        // Where the quest is picked up: the giver's spot, else the quest's zone.
                        place = Location(r.GetString(16), r.GetDouble(17), r.GetDouble(18), r.GetString(10));
                        if (r.GetString(11).Length > 0) detail.Add($"from {r.GetString(11)}");
                        if (kind == "quest_choice") detail.Add("pick one reward");
                        break;
                }
                result.Add(new ItemSource(kind, r.GetInt32(1), r.GetString(3), string.Join(" · ", detail), r.GetDouble(2))
                {
                    Place = place,
                    Rank = npcRank,
                    Quality = r.GetInt32(12),
                });
            }
        }
        catch (SqliteException) { } // older game database without source tables
        return result;
    }

    private static List<ItemEffectInfo> ReadEffects(SqliteConnection connection, int itemId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT e.trigger_type, e.spell_id, COALESCE(s.name, ''), COALESCE(s.description, ''), e.charges, e.cooldown_ms
            FROM item_effects e LEFT JOIN spells s ON s.id = e.spell_id
            WHERE e.item_id = $id ORDER BY e.slot
            """;
        command.Parameters.AddWithValue("$id", itemId);
        var result = new List<ItemEffectInfo>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            result.Add(new ItemEffectInfo(reader.GetInt32(0), reader.GetInt32(1), reader.GetString(2),
                reader.GetString(3), reader.GetInt32(4), reader.GetInt32(5)));
        return result;
    }

    private static ItemSetInfo? ReadSet(SqliteConnection connection, int setId)
    {
        string? name;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT name FROM item_sets WHERE id = $id";
            command.Parameters.AddWithValue("$id", setId);
            name = command.ExecuteScalar() as string;
        }
        if (name is null) return null;

        var pieces = new List<SetPiece>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT p.item_id, COALESCE(i.name, 'Item ' || p.item_id), COALESCE(i.quality, 1)
                FROM item_set_items p LEFT JOIN items i ON i.id = p.item_id
                WHERE p.set_id = $id ORDER BY p.position
                """;
            command.Parameters.AddWithValue("$id", setId);
            using var reader = command.ExecuteReader();
            while (reader.Read()) pieces.Add(new SetPiece(reader.GetInt32(0), reader.GetString(1), reader.GetInt32(2)));
        }

        var bonuses = new List<SetBonus>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT b.threshold, b.spell_id, COALESCE(NULLIF(s.description, ''), s.name, 'Spell ' || b.spell_id)
                FROM item_set_bonuses b LEFT JOIN spells s ON s.id = b.spell_id
                WHERE b.set_id = $id ORDER BY b.threshold
                """;
            command.Parameters.AddWithValue("$id", setId);
            using var reader = command.ExecuteReader();
            while (reader.Read()) bonuses.Add(new SetBonus(reader.GetInt32(0), reader.GetInt32(1), reader.GetString(2)));
        }
        return new ItemSetInfo(setId, name, pieces, bonuses);
    }

    /// <summary>AllowableClass is a bit mask (bit 0 = class 1). -1 or a mask covering every class means "all".</summary>
    private static List<string> ReadClasses(SqliteConnection connection, int mask)
    {
        if (mask is -1 or 0) return [];
        var all = new List<(int Id, string Name)>();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT c.id, c.name FROM classes c WHERE EXISTS (SELECT 1 FROM talent_tabs t WHERE t.class_id = c.id) ORDER BY c.id";
        using var reader = command.ExecuteReader();
        while (reader.Read()) all.Add((reader.GetInt32(0), reader.GetString(1)));

        var allowed = all.Where(c => (mask & (1 << (c.Id - 1))) != 0).Select(c => c.Name).ToList();
        return allowed.Count == all.Count ? [] : allowed;
    }

    private static List<RecipeInfo> ReadCraftedBy(SqliteConnection connection, int itemId)
    {
        var recipes = new List<(int Spell, string Name, string Profession, int MinSkill, int Yellow, int Grey, bool Estimated, string TaughtBy)>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT r.spell_id, r.name, r.profession, r.min_skill, r.trivial_low, r.trivial_high,
                       r.skill_source = 'estimate', COALESCE(t.name, '')
                FROM recipes r LEFT JOIN items t ON t.id = r.taught_by
                WHERE r.created_item_id = $id ORDER BY r.min_skill, r.profession
                """;
            command.Parameters.AddWithValue("$id", itemId);
            using var reader = command.ExecuteReader();
            while (reader.Read())
                recipes.Add((reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3),
                    reader.GetInt32(4), reader.GetInt32(5), reader.GetBoolean(6), reader.GetString(7)));
        }

        var result = new List<RecipeInfo>();
        foreach (var (spell, name, profession, minSkill, yellow, grey, estimated, taughtBy) in recipes)
        {
            var reagents = new List<RecipeReagent>();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT r.item_id, COALESCE(i.name, 'Item ' || r.item_id), r.quantity, COALESCE(i.quality, 1)
                FROM recipe_reagents r LEFT JOIN items i ON i.id = r.item_id
                WHERE r.spell_id = $spell
                """;
            command.Parameters.AddWithValue("$spell", spell);
            using var reader = command.ExecuteReader();
            while (reader.Read())
                reagents.Add(new RecipeReagent(reader.GetInt32(0), reader.GetString(1), reader.GetInt32(2), reader.GetInt32(3)));
            result.Add(new RecipeInfo(spell, name, profession, minSkill, reagents)
            {
                YellowAt = yellow,
                GreyAt = grey,
                SkillEstimated = estimated,
                TaughtBy = taughtBy,
            });
        }
        return result;
    }

    private static List<ReagentUse> ReadUsedIn(SqliteConnection connection, int itemId)
    {
        var uses = new List<ReagentUse>();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT rc.name, rc.profession, rc.min_skill, COALESCE(ci.quality, 1)
            FROM recipe_reagents rr JOIN recipes rc ON rc.spell_id = rr.spell_id
            LEFT JOIN items ci ON ci.id = rc.created_item_id
            WHERE rr.item_id = $id
            ORDER BY rc.profession, rc.min_skill, rc.name
            LIMIT 2000
            """;
        command.Parameters.AddWithValue("$id", itemId);
        using var reader = command.ExecuteReader();
        while (reader.Read())
            uses.Add(new ReagentUse(reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetInt32(3)));
        return uses;
    }

    /// <summary>
    /// NPC and chest names for the from: filter's autocomplete: only ones that drop or sell something, bosses and
    /// names that start with the text first. Matching ignores case, spaces and punctuation, like the filter.
    /// </summary>
    public IReadOnlyList<string> SourceNames(string text, int limit = 6)
    {
        var loose = ItemQuery.Loose(text.Trim('"', ' '));
        if (loose.Length < 2) return [];
        try
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT name FROM (
                    SELECT n.name AS name, n.rank AS rank FROM npcs n
                    WHERE {ItemQuery.LooseSql("n.name")} LIKE $like
                      AND EXISTS (SELECT 1 FROM item_sources s WHERE s.kind IN ('drop', 'vendor') AND s.ref_id = n.id)
                    UNION ALL
                    SELECT o.name, 0 FROM objects o
                    WHERE {ItemQuery.LooseSql("o.name")} LIKE $like
                      AND EXISTS (SELECT 1 FROM item_sources s WHERE s.kind = 'object' AND s.ref_id = o.id))
                WHERE name <> ''
                GROUP BY name
                ORDER BY ({ItemQuery.LooseSql("name")} LIKE $prefix) DESC, max(rank IN (1, 2, 3)) DESC, length(name), name
                LIMIT $limit
                """;
            command.Parameters.AddWithValue("$like", "%" + loose + "%");
            command.Parameters.AddWithValue("$prefix", loose + "%");
            command.Parameters.AddWithValue("$limit", limit);
            var names = new List<string>();
            using var r = command.ExecuteReader();
            while (r.Read()) names.Add(r.GetString(0));
            return names;
        }
        catch (SqliteException)
        {
            return [];
        }
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private static List<ItemStat> ParseStats(string text) =>
        text.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split(':'))
            .Where(p => p.Length == 2 && int.TryParse(p[0], out _) && int.TryParse(p[1], out _))
            .Select(p => new ItemStat(int.Parse(p[0]), int.Parse(p[1])))
            .ToList();

    /// <summary>"edgemaster's hand" becomes "edgemaster"* "s"* "hand"* (every word is a prefix, all must match).</summary>
    internal static string? BuildMatch(string query)
    {
        var cleaned = new string(query.Select(c => char.IsLetterOrDigit(c) ? c : ' ').ToArray());
        var tokens = cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(t => $"\"{t}\"*");
        var match = string.Join(' ', tokens);
        return match.Length == 0 ? null : match;
    }
}
