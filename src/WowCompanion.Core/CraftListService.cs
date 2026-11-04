using Microsoft.Data.Sqlite;

namespace WowCompanion.Core;

/// <param name="Done">Ticked off while crafting; stays on the list (greyed) until cleared.</param>
public sealed record CraftEntry(int SpellId, int Quantity, bool Done = false);

/// <summary>The "to craft" list. Lives in user.sqlite, so game data rebuilds never touch it.</summary>
public sealed class CraftListService(string userDatabasePath)
{
    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = userDatabasePath,
        Pooling = false,
    }.ToString();

    public IReadOnlyList<CraftEntry> Load()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT spell_id, quantity, done FROM craft_list ORDER BY added_utc, spell_id";
        var result = new List<CraftEntry>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) result.Add(new CraftEntry(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2) != 0));
        return result;
    }

    /// <summary>Adds one craft of the recipe, or one more if it is already on the list (a done recipe starts over).</summary>
    public void Add(int spellId) => Execute(
        """
        INSERT INTO craft_list (spell_id, quantity) VALUES ($id, 1)
        ON CONFLICT(spell_id) DO UPDATE SET quantity = CASE WHEN done = 1 THEN 1 ELSE quantity + 1 END, done = 0
        """,
        ("$id", spellId));

    public void SetDone(int spellId, bool done) =>
        Execute("UPDATE craft_list SET done = $d WHERE spell_id = $id", ("$id", spellId), ("$d", done ? 1 : 0));

    public void CompleteAll() => Execute("UPDATE craft_list SET done = 1");

    public void ClearCompleted() => Execute("DELETE FROM craft_list WHERE done = 1");

    /// <summary>Zero or less removes the recipe.</summary>
    public void SetQuantity(int spellId, int quantity)
    {
        if (quantity <= 0) Remove(spellId);
        else Execute("UPDATE craft_list SET quantity = $q WHERE spell_id = $id", ("$id", spellId), ("$q", quantity));
    }

    public void Remove(int spellId) => Execute("DELETE FROM craft_list WHERE spell_id = $id", ("$id", spellId));

    public void Clear() => Execute("DELETE FROM craft_list");

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
