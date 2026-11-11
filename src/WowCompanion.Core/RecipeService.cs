using Microsoft.Data.Sqlite;

namespace WowCompanion.Core;

public sealed record RecipeSummary(
    int SpellId, string Name, string Profession, int MinSkill, int CreatedItemId, int Quality, string ReagentText,
    int IconId = 0, int YellowAt = 0, int GreyAt = 0, bool SkillEstimated = false, string TaughtBy = "")
{
    public int GreenAt => YellowAt > 0 && GreyAt > 0 ? (YellowAt + GreyAt) / 2 : 0;

    // The crafted item, for showing its stats in the recipe list.
    public string Stats { get; init; } = "";
    public int Armor { get; init; }
    public double DamageMin { get; init; }
    public double DamageMax { get; init; }
    public int DelayMs { get; init; }
    public int InventoryType { get; init; }
    public int ClassId { get; init; }
    public int SubclassId { get; init; }
    public int ItemLevel { get; init; }
    public int RequiredLevel { get; init; }
    /// <summary>Reagents with their item quality, for coloured display.</summary>
    public IReadOnlyList<ReagentPart> Reagents { get; init; } = [];

    /// <summary>"trigger~text|trigger~text" for Use/Equip/Chance on hit.</summary>
    public string Effects { get; init; } = "";
}

public sealed record ReagentRow(int SpellId, int ItemId, string Name, int Quantity, int Quality = 1);

public enum RecipeSort { Skill, Name, Grey, ItemLevel }

public sealed record ReagentPart(int Quantity, string Name, int Quality, int ItemId = 0);

/// <summary>Recipe queries over the game database.</summary>
public sealed class RecipeService(string databasePath)
{
    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = databasePath,
        Mode = SqliteOpenMode.ReadOnly,
        Pooling = false,
    }.ToString();

    private const string SummarySelect = """
        SELECT r.spell_id, r.name, r.profession, r.min_skill, r.created_item_id,
               COALESCE(i.quality, 1),
               COALESCE((SELECT group_concat(rr.quantity || 'x ' || COALESCE(ri.name, 'Item ' || rr.item_id), ', ')
                         FROM recipe_reagents rr LEFT JOIN items ri ON ri.id = rr.item_id
                         WHERE rr.spell_id = r.spell_id), ''),
               COALESCE(i.icon_file_id, 0), r.trivial_low, r.trivial_high, r.skill_source = 'estimate',
               COALESCE((SELECT t.name FROM items t WHERE t.id = r.taught_by), ''),
               COALESCE(i.stats, ''), COALESCE(i.armor, 0), COALESCE(i.dmg_min, 0), COALESCE(i.dmg_max, 0),
               COALESCE(i.delay_ms, 0), COALESCE(i.inventory_type, 0), COALESCE(i.class_id, 0), COALESCE(i.subclass_id, 0),
               COALESCE(i.item_level, 0), COALESCE(i.required_level, 0),
               COALESCE((SELECT group_concat(e.trigger_type || '~' || COALESCE(NULLIF(s.description, ''), s.name), '|')
                         FROM item_effects e JOIN spells s ON s.id = e.spell_id
                         WHERE e.item_id = r.created_item_id AND e.trigger_type IN (0, 1, 2, 5)), ''),
               COALESCE((SELECT group_concat(rr.quantity || '~' || COALESCE(ri.name, 'Item ' || rr.item_id) || '~' || COALESCE(ri.quality, 1) || '~' || rr.item_id, '|')
                         FROM recipe_reagents rr LEFT JOIN items ri ON ri.id = rr.item_id
                         WHERE rr.spell_id = r.spell_id), '')
        FROM recipes r
        LEFT JOIN items i ON i.id = r.created_item_id
        """;

    public IReadOnlyList<string> GetProfessions()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT DISTINCT profession FROM recipes ORDER BY profession";
        var result = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) result.Add(reader.GetString(0));
        return result;
    }

    /// <summary>Filter matches the recipe name or any reagent name. Null profession means all.</summary>
    public IReadOnlyList<RecipeSummary> Search(string? profession, string filter, int limit = 300, RecipeSort sort = RecipeSort.Skill)
    {
        var text = filter.Trim();

        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = SummarySelect + $$"""

            WHERE ($profession IS NULL OR r.profession = $profession)
              AND {{ItemVisibility.Clause("r.created_item_id")}} AND {{ItemVisibility.Clause("r.taught_by")}}
              AND ($pattern IS NULL
                   OR r.name LIKE $pattern ESCAPE '\'
                   OR EXISTS (SELECT 1 FROM recipe_reagents rr JOIN items ri ON ri.id = rr.item_id
                              WHERE rr.spell_id = r.spell_id AND ri.name LIKE $pattern ESCAPE '\'))
            ORDER BY {{SortClause(sort)}}
            LIMIT $limit
            """;
        command.Parameters.AddWithValue("$profession", profession is null ? DBNull.Value : (object)profession);
        command.Parameters.AddWithValue("$pattern", text.Length == 0 ? DBNull.Value : (object)("%" + Escape(text) + "%"));
        command.Parameters.AddWithValue("$limit", limit);
        return ReadSummaries(command);
    }

    /// <summary>The (lowest-skill) recipe that creates an item, or null when nothing crafts it.</summary>
    public int? FindRecipeFor(int itemId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT spell_id FROM recipes WHERE created_item_id = $id ORDER BY min_skill LIMIT 1";
        command.Parameters.AddWithValue("$id", itemId);
        return command.ExecuteScalar() is long id ? (int)id : null;
    }

    public IReadOnlyList<RecipeSummary> GetMany(IReadOnlyCollection<int> spellIds)
    {
        if (spellIds.Count == 0) return [];

        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = SummarySelect + $" WHERE r.spell_id IN {InClause(command, spellIds)}";
        return ReadSummaries(command);
    }

    public IReadOnlyList<ReagentRow> GetReagentRows(IReadOnlyCollection<int> spellIds)
    {
        if (spellIds.Count == 0) return [];

        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT rr.spell_id, rr.item_id, COALESCE(i.name, 'Item ' || rr.item_id), rr.quantity, COALESCE(i.quality, 1)
            FROM recipe_reagents rr LEFT JOIN items i ON i.id = rr.item_id
            WHERE rr.spell_id IN {InClause(command, spellIds)}
            """;

        var rows = new List<ReagentRow>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            rows.Add(new ReagentRow(reader.GetInt32(0), reader.GetInt32(1), reader.GetString(2), reader.GetInt32(3), reader.GetInt32(4)));
        return rows;
    }

    private static List<RecipeSummary> ReadSummaries(SqliteCommand command)
    {
        var result = new List<RecipeSummary>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new RecipeSummary(
                reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3),
                reader.GetInt32(4), reader.GetInt32(5), reader.GetString(6), reader.GetInt32(7),
                reader.GetInt32(8), reader.GetInt32(9), reader.GetBoolean(10), reader.GetString(11))
            {
                Stats = reader.GetString(12),
                Armor = reader.GetInt32(13),
                DamageMin = reader.GetDouble(14),
                DamageMax = reader.GetDouble(15),
                DelayMs = reader.GetInt32(16),
                InventoryType = reader.GetInt32(17),
                ClassId = reader.GetInt32(18),
                SubclassId = reader.GetInt32(19),
                ItemLevel = reader.GetInt32(20),
                RequiredLevel = reader.GetInt32(21),
                Effects = reader.GetString(22),
                Reagents = reader.GetString(23).Split('|', StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => x.Split('~'))
                    .Where(p => p.Length >= 3)
                    .Select(p => new ReagentPart(int.TryParse(p[0], out var q) ? q : 1, p[1], int.TryParse(p[2], out var k) ? k : 1,
                        p.Length > 3 && int.TryParse(p[3], out var id) ? id : 0))
                    .ToList(),
            });
        }
        return result;
    }

    private static string InClause(SqliteCommand command, IReadOnlyCollection<int> ids)
    {
        var names = new List<string>();
        var index = 0;
        foreach (var id in ids)
        {
            var name = "$p" + index++;
            command.Parameters.AddWithValue(name, id);
            names.Add(name);
        }
        return "(" + string.Join(',', names) + ")";
    }

    private static string SortClause(RecipeSort sort) => sort switch
    {
        RecipeSort.Name => "r.name, r.min_skill",
        RecipeSort.Grey => "r.trivial_high, r.min_skill, r.name",
        RecipeSort.ItemLevel => "COALESCE(i.item_level, 0) DESC, r.min_skill, r.name",
        _ => "r.min_skill, r.trivial_low, r.name",
    };

    private static string Escape(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }
}
