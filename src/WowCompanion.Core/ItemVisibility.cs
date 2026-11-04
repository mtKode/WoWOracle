using Microsoft.Data.Sqlite;

namespace WowCompanion.Core;

/// <summary>
/// Season of Discovery leftovers and developer test items, hidden from search, recipes, upgrades and the planner
/// (on by default). The game data marks them at build time in hidden_items (reason "sod" or "test"); an item Forever
/// is seen to use (worn by a synced character, learned from the client, rewarded by a captured quest) is taken off
/// the list again on load. A hidden item still opens from a link.
/// </summary>
public static class ItemVisibility
{
    public static bool HideSod { get; set; } = true;

    /// <summary>SQL to keep hidden items out, for the given item id column ("" when nothing is hidden).</summary>
    public static string Clause(string idColumn) =>
        HideSod ? $"{idColumn} NOT IN (SELECT id FROM hidden_items)" : "1 = 1";

    /// <summary>Takes items off the hidden list (they're seen in Forever). Older game data without the table: no-op.</summary>
    public static int Unhide(string gameDatabasePath, IEnumerable<int> itemIds)
    {
        var ids = itemIds.Where(i => i > 0).Distinct().ToList();
        if (ids.Count == 0) return 0;
        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = gameDatabasePath,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false,
            }.ToString());
            connection.Open();
            var removed = 0;
            foreach (var chunk in ids.Chunk(500))
            {
                using var command = connection.CreateCommand();
                command.CommandText = $"DELETE FROM hidden_items WHERE id IN ({string.Join(',', chunk)})";
                removed += command.ExecuteNonQuery();
            }
            return removed;
        }
        catch (SqliteException)
        {
            return 0;
        }
    }

    /// <summary>The hidden item ids (empty when the filter is off).</summary>
    public static HashSet<int> HiddenIds(SqliteConnection connection)
    {
        var set = new HashSet<int>();
        if (!HideSod) return set;
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT id FROM hidden_items";
            using var r = command.ExecuteReader();
            while (r.Read()) set.Add(r.GetInt32(0));
        }
        catch (SqliteException) { }
        return set;
    }
}
