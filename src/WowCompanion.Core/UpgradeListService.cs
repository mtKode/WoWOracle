using System.Globalization;
using Microsoft.Data.Sqlite;

namespace WowCompanion.Core;

/// <summary>An item on a gear set's upgrade list: the slot it's for, where it comes from (as shown when added).</summary>
public sealed record UpgradeListEntry(int ItemId, int Slot, string Source, string Gain, bool Done);

/// <summary>
/// Upgrades you want to go and get, per gear set ("+ List" in the upgrade finder), ticked off once you have them.
/// Lives in user.sqlite (upgrade_list), so game data rebuilds never touch it.
/// </summary>
public sealed class UpgradeListService(string userDatabasePath)
{
    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = userDatabasePath,
        Pooling = false,
    }.ToString();

    public IReadOnlyList<UpgradeListEntry> Load(int setId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT item_id, slot, source, gain, done FROM upgrade_list WHERE set_id = $s ORDER BY done, added_utc";
        command.Parameters.AddWithValue("$s", setId);
        var result = new List<UpgradeListEntry>();
        using var r = command.ExecuteReader();
        while (r.Read()) result.Add(new UpgradeListEntry(r.GetInt32(0), r.GetInt32(1), r.GetString(2), r.GetString(3), r.GetInt32(4) != 0));
        return result;
    }

    /// <summary>Adds the item (again: fresh source and gain, not done).</summary>
    public void Add(int setId, int itemId, int slot, string source, string gain) => Execute(
        """
        INSERT INTO upgrade_list (set_id, item_id, slot, source, gain, done, added_utc) VALUES ($s, $i, $slot, $src, $g, 0, $now)
        ON CONFLICT(set_id, item_id) DO UPDATE SET slot = $slot, source = $src, gain = $g, done = 0
        """,
        ("$s", setId), ("$i", itemId), ("$slot", slot), ("$src", source), ("$g", gain),
        ("$now", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)));

    public void SetDone(int setId, int itemId, bool done) =>
        Execute("UPDATE upgrade_list SET done = $d WHERE set_id = $s AND item_id = $i", ("$s", setId), ("$i", itemId), ("$d", done ? 1 : 0));

    public void Remove(int setId, int itemId) =>
        Execute("DELETE FROM upgrade_list WHERE set_id = $s AND item_id = $i", ("$s", setId), ("$i", itemId));

    public void ClearDone(int setId) => Execute("DELETE FROM upgrade_list WHERE set_id = $s AND done = 1", ("$s", setId));

    /// <summary>A deleted gear set takes its list with it.</summary>
    public void RemoveSet(int setId) => Execute("DELETE FROM upgrade_list WHERE set_id = $s", ("$s", setId));

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
