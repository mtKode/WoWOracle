using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;

namespace WowCompanion.Core;

/// <summary>What an enchant adds to an item.</summary>
public sealed record EnchantInfo(int Id, string Name, IReadOnlyList<ItemStat> Stats, int Damage, IReadOnlyList<int> ProcSpells, IReadOnlyList<int> EquipSpells);

/// <summary>One enchant that fits a slot, with the spell or item that applies it.</summary>
public sealed record EnchantOption(int EnchantId, string Label, string Summary, bool Temporary);

/// <summary>Enchants from the game data (enchants, enchant_options tables).</summary>
public sealed class EnchantService(string databasePath)
{
    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = databasePath,
        Mode = SqliteOpenMode.ReadOnly,
        Pooling = false,
    }.ToString();

    private readonly ConcurrentDictionary<int, EnchantInfo?> _cache = new();

    public bool HasData
    {
        get
        {
            try
            {
                using var connection = Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM enchant_options";
                return Convert.ToInt32(command.ExecuteScalar()) > 0;
            }
            catch (SqliteException) { return false; }
        }
    }

    /// <summary>Enchants that can go on a gear slot, permanent ones first, then by name.</summary>
    public IReadOnlyList<EnchantOption> OptionsFor(int slot)
    {
        try
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT o.enchant_id, MIN(o.label), e.name, MIN(o.temporary)
                FROM enchant_options o JOIN enchants e ON e.id = o.enchant_id
                WHERE o.slot = $slot
                GROUP BY o.enchant_id
                ORDER BY MIN(o.temporary), MIN(o.label)
                """;
            command.Parameters.AddWithValue("$slot", slot);
            var list = new List<EnchantOption>();
            using var r = command.ExecuteReader();
            while (r.Read())
                list.Add(new EnchantOption(r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetInt32(3) == 1));
            return list;
        }
        catch (SqliteException) { return []; }
    }

    public EnchantInfo? Get(int enchantId)
    {
        if (enchantId <= 0) return null;
        if (_cache.TryGetValue(enchantId, out var cached)) return cached;
        EnchantInfo? info = null;
        try
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT id, name, stats, damage, proc_spells, equip_spells FROM enchants WHERE id = $id";
            command.Parameters.AddWithValue("$id", enchantId);
            using var r = command.ExecuteReader();
            if (r.Read())
                info = new EnchantInfo(r.GetInt32(0), r.GetString(1), GearSetService.ParseStats(r.GetString(2)), r.GetInt32(3),
                    Ids(r.GetString(4)), Ids(r.GetString(5)));
        }
        catch (SqliteException) { }
        _cache[enchantId] = info;
        return info;
    }

    private static List<int> Ids(string text) =>
        text.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => int.TryParse(x, out var v) ? v : 0).Where(v => v > 0).ToList();

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }
}
