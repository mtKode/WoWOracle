using Microsoft.Data.Sqlite;

namespace WowCompanion.Core.Calc;

/// <summary>One spell effect with what the calculator needs, including the class mask talents match on.</summary>
public sealed record EffectRow(
    int SpellId, int Index, int Effect, int Aura, double BasePoints, double Variance, double SpellCoefficient,
    double ApCoefficient, int PeriodMs, int TriggerSpell, int Misc0, int[] Mask);

public sealed record GearItem(
    int Id, string Name, int Quality, int IconId, int InventoryType, int Armor,
    IReadOnlyList<ItemStat> Stats, IReadOnlyList<int> EquipSpells)
{
    public int DelayMs { get; init; }
    public double DamageMin { get; init; }
    public double DamageMax { get; init; }
    public int DamageSchool { get; init; }
    /// <summary>"Chance on hit" spells.</summary>
    public IReadOnlyList<int> OnHitSpells { get; init; } = [];
    public bool IsWeapon => DelayMs > 0 && DamageMax > 0;
}

public sealed record CalcSpell(
    int Id, string Name, string Rank, int SchoolMask, int CastMs, int DurationMs, int PowerCost, int PowerCostPct,
    int GcdMs, int SpellLevel, int IconId, int[] Mask);

public sealed record RaceInfo(int Id, string Name);

/// <summary>Read-only queries for gear sets and the calculator.</summary>
public sealed class CalcDataService(string databasePath)
{
    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = databasePath,
        Mode = SqliteOpenMode.ReadOnly,
        Pooling = false,
    }.ToString();

    public IReadOnlyList<RaceInfo> GetRaces()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name FROM races WHERE playable = 1 ORDER BY id";
        var result = new List<RaceInfo>();
        using var r = command.ExecuteReader();
        while (r.Read()) result.Add(new RaceInfo(r.GetInt32(0), r.GetString(1)));
        return result;
    }

    public int GetInventoryType(int itemId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT inventory_type FROM items WHERE id = $id";
        command.Parameters.AddWithValue("$id", itemId);
        return Convert.ToInt32(command.ExecuteScalar() ?? 0);
    }

    public IReadOnlyList<GearItem> GetGearItems(IReadOnlyCollection<int> itemIds)
    {
        if (itemIds.Count == 0) return [];
        using var connection = Open();

        var equip = new Dictionary<int, List<int>>();
        var onHit = new Dictionary<int, List<int>>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"SELECT item_id, spell_id, trigger_type FROM item_effects WHERE trigger_type IN (1, 2) AND item_id IN {In(command, itemIds)}";
            using var r = command.ExecuteReader();
            while (r.Read())
            {
                var target = r.GetInt32(2) == 1 ? equip : onHit;
                if (!target.TryGetValue(r.GetInt32(0), out var list)) target[r.GetInt32(0)] = list = [];
                list.Add(r.GetInt32(1));
            }
        }

        var result = new List<GearItem>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"""
                SELECT id, name, quality, icon_file_id, inventory_type, armor, stats, delay_ms, dmg_min, dmg_max, dmg_school
                FROM items WHERE id IN {In(command, itemIds)}
                """;
            using var r = command.ExecuteReader();
            while (r.Read())
            {
                var id = r.GetInt32(0);
                result.Add(new GearItem(id, r.GetString(1), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4), r.GetInt32(5),
                    ParseStats(r.GetString(6)), equip.GetValueOrDefault(id) ?? new List<int>())
                {
                    DelayMs = r.GetInt32(7),
                    DamageMin = r.GetDouble(8),
                    DamageMax = r.GetDouble(9),
                    DamageSchool = r.GetInt32(10),
                    OnHitSpells = onHit.GetValueOrDefault(id) ?? new List<int>(),
                });
            }
        }
        return result;
    }

    /// <summary>Effects of the given spells, plus the effects of any spells they trigger.</summary>
    public Dictionary<int, List<EffectRow>> GetEffects(IReadOnlyCollection<int> spellIds)
    {
        var result = new Dictionary<int, List<EffectRow>>();
        var pending = spellIds.Where(id => id > 0).Distinct().ToList();
        using var connection = Open();
        for (var round = 0; round < 2 && pending.Count > 0; round++)
        {
            var next = new List<int>();
            foreach (var chunk in pending.Chunk(400))
            {
                using var command = connection.CreateCommand();
                command.CommandText = $"""
                    SELECT e.spell_id, e.idx, e.effect, e.aura, e.base_points, e.variance, e.sp_coef, e.ap_coef,
                           e.period_ms, e.trigger_spell, e.misc0, e.class_mask
                    FROM spell_effects e WHERE e.spell_id IN {In(command, chunk)}
                    ORDER BY e.spell_id, e.idx
                    """;
                using var r = command.ExecuteReader();
                while (r.Read())
                {
                    var row = new EffectRow(r.GetInt32(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3), r.GetDouble(4),
                        r.GetDouble(5), r.GetDouble(6), r.GetDouble(7), r.GetInt32(8), r.GetInt32(9), r.GetInt32(10),
                        ParseMask(r.GetString(11)));
                    if (!result.TryGetValue(row.SpellId, out var list)) result[row.SpellId] = list = [];
                    list.Add(row);
                    if (row.TriggerSpell > 0 && !result.ContainsKey(row.TriggerSpell)) next.Add(row.TriggerSpell);
                }
            }
            pending = next.Distinct().Where(id => !result.ContainsKey(id)).ToList();
        }
        return result;
    }

    /// <summary>Damage spells of a class learned by the given level, every rank, highest rank first per name.</summary>
    public IReadOnlyList<CalcSpell> GetDamageSpells(int classId, int level)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.id, s.name, s.rank, s.school_mask, s.cast_ms, s.duration_ms, s.power_cost, s.power_cost_pct,
                   s.gcd_ms, s.spell_level, s.icon_file_id, s.class_mask
            FROM spells s
            WHERE s.is_class_spell = 1 AND s.class_id = $c AND s.spell_level <= $level
              AND EXISTS (SELECT 1 FROM spell_effects e WHERE e.spell_id = s.id
                          AND (e.effect IN (2, 17, 31, 58, 121) OR (e.effect = 6 AND e.aura IN (3, 23))))
            ORDER BY s.name, s.spell_level DESC, s.id DESC
            """;
        command.Parameters.AddWithValue("$c", classId);
        command.Parameters.AddWithValue("$level", level);

        var result = new List<CalcSpell>();
        using var r = command.ExecuteReader();
        while (r.Read())
        {
            result.Add(new CalcSpell(r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetInt32(3), r.GetInt32(4),
                r.GetInt32(5), r.GetInt32(6), r.GetInt32(7), r.GetInt32(8), r.GetInt32(9), r.GetInt32(10),
                ParseMask(r.GetString(11))));
        }
        return result;
    }

    private static int[] ParseMask(string text)
    {
        var parts = text.Split(',');
        var mask = new int[4];
        for (var i = 0; i < 4 && i < parts.Length; i++) int.TryParse(parts[i], out mask[i]);
        return mask;
    }

    private static List<ItemStat> ParseStats(string text) =>
        text.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split(':'))
            .Where(p => p.Length == 2 && int.TryParse(p[0], out _) && int.TryParse(p[1], out _))
            .Select(p => new ItemStat(int.Parse(p[0]), int.Parse(p[1])))
            .ToList();

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

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }
}
