using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace WowCompanion.Data;

/// <summary>
/// Marks Season of Discovery leftovers and developer test items (hidden_items). There is no "SoD" flag in the data:
/// SoD's items are in Forever's own tables. Signals: SoD's item id range (≈202000–238999; Forever's own new items
/// start after it), SoD item families by name (Waylaid Supplies, Supply Shipment, Spell Notes), and test/placeholder
/// names. The app takes back off the list what it sees Forever use.
/// </summary>
public static partial class GameDatabaseBuilder
{
    private const string HiddenSchema = """

        CREATE TABLE hidden_items (
            id INTEGER PRIMARY KEY,
            reason TEXT NOT NULL      -- 'sod' or 'test'
        );
        """;

    private const int SodFirstId = 202000, SodLastId = 238999;

    [GeneratedRegex(@"^(Waylaid Supplies|Waylaid Crate|Supply Shipment|Spell Notes:)", RegexOptions.IgnoreCase)]
    private static partial Regex SodName();

    [GeneratedRegex(@"\bTest\b|\bQA|^Monster - |\bOLD\b|\(OLD\)|No Longer Used|Deprecated|DEPRECATED|\[PH\]|\bPH\b|^zz|^ZZ|^Unused\b",
        RegexOptions.None)]
    private static partial Regex TestName();

    private static int MarkHiddenItems(SqliteConnection connection, SqliteTransaction transaction)
    {
        var hidden = new List<(int Id, string Reason)>();
        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT id, name FROM items";
            using var r = read.ExecuteReader();
            while (r.Read())
            {
                var (id, name) = (r.GetInt32(0), r.GetString(1));
                if (TestName().IsMatch(name)) hidden.Add((id, "test"));
                else if (id is >= SodFirstId and <= SodLastId || id >= 200000 && SodName().IsMatch(name)) hidden.Add((id, "sod"));
            }
        }
        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = "INSERT OR REPLACE INTO hidden_items (id, reason) VALUES ($id, $r)";
        var pId = insert.Parameters.Add("$id", SqliteType.Integer);
        var pReason = insert.Parameters.Add("$r", SqliteType.Text);
        foreach (var (id, reason) in hidden)
        {
            pId.Value = id;
            pReason.Value = reason;
            insert.ExecuteNonQuery();
        }
        return hidden.Count;
    }
}
