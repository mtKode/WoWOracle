using Microsoft.Data.Sqlite;

namespace WowCompanion.Core;

public sealed record GearSetInfo(int Id, string Name, int ClassId, int RaceId, int Level, string BaseOverrides);

/// <summary>Saved gear sets (user.sqlite): which item sits in which slot, plus the character they are for.</summary>
public sealed class GearSetService(string userDatabasePath)
{
    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = userDatabasePath,
        Pooling = false,
    }.ToString();

    public IReadOnlyList<GearSetInfo> List()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name, class_id, race_id, level, base_overrides FROM gear_sets ORDER BY name, id";
        var result = new List<GearSetInfo>();
        using var r = command.ExecuteReader();
        while (r.Read())
            result.Add(new GearSetInfo(r.GetInt32(0), r.GetString(1), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4), r.GetString(5)));
        return result;
    }

    public int Create(string name, int classId, int raceId, int level)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO gear_sets (name, class_id, race_id, level, base_overrides, created_utc)
            VALUES ($n, $c, $r, $l, '', datetime('now'));
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$n", name);
        command.Parameters.AddWithValue("$c", classId);
        command.Parameters.AddWithValue("$r", raceId);
        command.Parameters.AddWithValue("$l", level);
        return Convert.ToInt32(command.ExecuteScalar());
    }

    public void Update(GearSetInfo set) => Execute(
        "UPDATE gear_sets SET name = $n, class_id = $c, race_id = $r, level = $l, base_overrides = $b WHERE id = $id",
        ("$id", set.Id), ("$n", set.Name), ("$c", set.ClassId), ("$r", set.RaceId), ("$l", set.Level), ("$b", set.BaseOverrides));

    public void Delete(int setId)
    {
        Execute("DELETE FROM gear_set_items WHERE set_id = $id", ("$id", setId));
        Execute("DELETE FROM gear_sets WHERE id = $id", ("$id", setId));
    }

    /// <summary>Slot -> item id.</summary>
    public IReadOnlyDictionary<int, int> GetItems(int setId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT slot, item_id FROM gear_set_items WHERE set_id = $id";
        command.Parameters.AddWithValue("$id", setId);
        var result = new Dictionary<int, int>();
        using var r = command.ExecuteReader();
        while (r.Read()) result[r.GetInt32(0)] = r.GetInt32(1);
        return result;
    }

    /// <summary>Puts an item in a slot. Any extra stats recorded for the old item in that slot are dropped.</summary>
    public void SetItem(int setId, int slot, int itemId)
    {
        Execute(
            """
            INSERT INTO gear_set_items (set_id, slot, item_id) VALUES ($s, $slot, $item)
            ON CONFLICT(set_id, slot) DO UPDATE SET item_id = $item
            """,
            ("$s", setId), ("$slot", slot), ("$item", itemId));
        ClearSlotBonus(setId, slot);
        ClearSlotEnchant(setId, slot); // a new item comes without the old one's enchant, like in game
    }

    public void RemoveItem(int setId, int slot)
    {
        Execute("DELETE FROM gear_set_items WHERE set_id = $s AND slot = $slot", ("$s", setId), ("$slot", slot));
        ClearSlotBonus(setId, slot);
        ClearSlotEnchant(setId, slot);
    }

    // ----- per-slot enchant (enchant id from the game data) -----

    private static string EnchantKey(int setId, int slot) => $"slotenchant:{setId}:{slot}";

    public void SetSlotEnchant(int setId, int slot, int enchantId) => SetSetting(EnchantKey(setId, slot), enchantId.ToString());

    public void ClearSlotEnchant(int setId, int slot) =>
        Execute("DELETE FROM settings WHERE key = $k", ("$k", EnchantKey(setId, slot)));

    /// <summary>Enchant id per slot for a set.</summary>
    public IReadOnlyDictionary<int, int> GetSlotEnchants(int setId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT key, value FROM settings WHERE key LIKE $p";
        command.Parameters.AddWithValue("$p", $"slotenchant:{setId}:%");
        var result = new Dictionary<int, int>();
        using var r = command.ExecuteReader();
        while (r.Read())
            if (int.TryParse(r.GetString(0).Split(':')[^1], out var slot) && int.TryParse(r.GetString(1), out var id) && id > 0)
                result[slot] = id;
        return result;
    }

    // ----- per-slot extra stats (random suffix "of the Eagle" read in game, beyond the plain item) -----

    private static string BonusKey(int setId, int slot) => $"slotbonus:{setId}:{slot}";

    /// <summary>Stats as "type:value,type:value" (item stat types), added on top of the item in that slot.</summary>
    public void SetSlotBonus(int setId, int slot, string stats) => SetSetting(BonusKey(setId, slot), stats);

    public void ClearSlotBonus(int setId, int slot) =>
        Execute("DELETE FROM settings WHERE key = $k", ("$k", BonusKey(setId, slot)));

    /// <summary>Extra stats per slot for a set.</summary>
    public IReadOnlyDictionary<int, IReadOnlyList<ItemStat>> GetSlotBonuses(int setId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT key, value FROM settings WHERE key LIKE $p";
        command.Parameters.AddWithValue("$p", $"slotbonus:{setId}:%");
        var result = new Dictionary<int, IReadOnlyList<ItemStat>>();
        using var r = command.ExecuteReader();
        while (r.Read())
        {
            if (!int.TryParse(r.GetString(0).Split(':')[^1], out var slot)) continue;
            result[slot] = ParseStats(r.GetString(1));
        }
        return result;
    }

    public static List<ItemStat> ParseStats(string text) =>
        text.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split(':'))
            .Where(p => p.Length == 2 && int.TryParse(p[0], out _)
                        && double.TryParse(p[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _))
            .Select(p => new ItemStat(int.Parse(p[0]), (int)Math.Round(double.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture))))
            .ToList();

    // ----- small key/value settings (active set, buffs per set) -----

    public string? GetSetting(string key)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM settings WHERE key = $k";
        command.Parameters.AddWithValue("$k", key);
        return command.ExecuteScalar() as string;
    }

    public void SetSetting(string key, string value) => Execute(
        "INSERT INTO settings (key, value) VALUES ($k, $v) ON CONFLICT(key) DO UPDATE SET value = $v",
        ("$k", key), ("$v", value));

    private void Execute(string sql, params (string Name, object Value)[] parameters)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        command.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }
}
