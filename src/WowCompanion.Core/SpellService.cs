using Microsoft.Data.Sqlite;

namespace WowCompanion.Core;

public sealed record ClassInfo(int Id, string Name, int IconId = 0);

public sealed record SpellSummary(
    int Id, string Name, string Rank, int ClassId, int SpellLevel, int SchoolMask, string SkillLine, int IconId = 0);

public sealed record SpellEffectInfo(
    int Index, int Effect, int Aura, double BasePoints, double PerLevel, double Variance,
    double SpellCoefficient, double ApCoefficient, int PeriodMs, int TriggerSpell, int Misc0, int ChainTargets);

public sealed record SpellDetail(
    int Id, string Name, string Rank, string Description, string AuraDescription, int SchoolMask,
    int CastMs, int DurationMs, double RangeMax, string RangeName, int CooldownMs, int CategoryCooldownMs, int GcdMs,
    int PowerType, int PowerCost, int PowerCostPct, int PowerPerSecond,
    int BaseLevel, int SpellLevel, int MaxLevel, int ProcChance, int ClassId, string ClassName, string SkillLine,
    IReadOnlyList<SpellEffectInfo> Effects)
{
    public int IconId { get; init; }
}

/// <summary>Spell queries over the game database.</summary>
public sealed class SpellService(string databasePath)
{
    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = databasePath,
        Mode = SqliteOpenMode.ReadOnly,
        Pooling = false,
    }.ToString();

    /// <summary>The playable classes (the ones with talent trees), by name.</summary>
    public IReadOnlyList<ClassInfo> GetClasses()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.id, c.name, c.icon_file_id FROM classes c
            WHERE EXISTS (SELECT 1 FROM talent_tabs t WHERE t.class_id = c.id)
            ORDER BY c.name
            """;
        var result = new List<ClassInfo>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) result.Add(new ClassInfo(reader.GetInt32(0), reader.GetString(1), reader.GetInt32(2)));
        return result;
    }

    /// <summary>Class spells. Filter matches the name or the description. Null class means all classes.</summary>
    public IReadOnlyList<SpellSummary> Search(int? classId, string filter, int limit = 400)
    {
        var text = filter.Trim();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, name, rank, class_id, spell_level, school_mask, skill_line, icon_file_id
            FROM spells
            WHERE is_class_spell = 1
              AND ($class IS NULL OR class_id = $class)
              AND ($pattern IS NULL OR name LIKE $pattern ESCAPE '\' OR description LIKE $pattern ESCAPE '\')
            ORDER BY name, spell_level, id
            LIMIT $limit
            """;
        command.Parameters.AddWithValue("$class", classId is null ? DBNull.Value : (object)classId.Value);
        command.Parameters.AddWithValue("$pattern", text.Length == 0 ? DBNull.Value : (object)("%" + Escape(text) + "%"));
        command.Parameters.AddWithValue("$limit", limit);

        var result = new List<SpellSummary>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new SpellSummary(reader.GetInt32(0), reader.GetString(1), reader.GetString(2),
                reader.GetInt32(3), reader.GetInt32(4), reader.GetInt32(5), reader.GetString(6), reader.GetInt32(7)));
        }
        return result;
    }

    public SpellDetail? GetDetail(int id)
    {
        using var connection = Open();
        SpellDetail detail;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT s.name, s.rank, s.description, s.aura_description, s.school_mask, s.cast_ms, s.duration_ms,
                       s.range_max, s.range_name, s.cooldown_ms, s.category_cooldown_ms, s.gcd_ms, s.power_type,
                       s.power_cost, s.power_cost_pct, s.power_per_second, s.base_level, s.spell_level, s.max_level,
                       s.proc_chance, s.class_id, COALESCE(c.name, ''), s.skill_line, s.icon_file_id
                FROM spells s LEFT JOIN classes c ON c.id = s.class_id
                WHERE s.id = $id
                """;
            command.Parameters.AddWithValue("$id", id);
            using var r = command.ExecuteReader();
            if (!r.Read()) return null;
            detail = new SpellDetail(
                id, r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt32(4), r.GetInt32(5),
                r.GetInt32(6), r.GetDouble(7), r.GetString(8), r.GetInt32(9), r.GetInt32(10), r.GetInt32(11),
                r.GetInt32(12), r.GetInt32(13), r.GetInt32(14), r.GetInt32(15), r.GetInt32(16), r.GetInt32(17),
                r.GetInt32(18), r.GetInt32(19), r.GetInt32(20), r.GetString(21), r.GetString(22), [])
            {
                IconId = r.GetInt32(23),
            };
        }

        var effects = new List<SpellEffectInfo>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT idx, effect, aura, base_points, per_level, variance, sp_coef, ap_coef, period_ms,
                       trigger_spell, misc0, chain_targets
                FROM spell_effects WHERE spell_id = $id ORDER BY idx
                """;
            command.Parameters.AddWithValue("$id", id);
            using var r = command.ExecuteReader();
            while (r.Read())
            {
                effects.Add(new SpellEffectInfo(r.GetInt32(0), r.GetInt32(1), r.GetInt32(2), r.GetDouble(3),
                    r.GetDouble(4), r.GetDouble(5), r.GetDouble(6), r.GetDouble(7), r.GetInt32(8), r.GetInt32(9),
                    r.GetInt32(10), r.GetInt32(11)));
            }
        }
        return detail with { Effects = effects };
    }

    /// <summary>Rendered descriptions for many spells at once (talent ranks, set bonuses).</summary>
    public IReadOnlyDictionary<int, string> GetDescriptions(IReadOnlyCollection<int> ids)
    {
        var result = new Dictionary<int, string>();
        if (ids.Count == 0) return result;
        using var connection = Open();
        using var command = connection.CreateCommand();
        var names = new List<string>();
        var i = 0;
        foreach (var id in ids)
        {
            var name = "$p" + i++;
            command.Parameters.AddWithValue(name, id);
            names.Add(name);
        }
        command.CommandText = $"SELECT id, description FROM spells WHERE id IN ({string.Join(',', names)})";
        using var reader = command.ExecuteReader();
        while (reader.Read()) result[reader.GetInt32(0)] = reader.GetString(1);
        return result;
    }

    private static string Escape(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }
}
