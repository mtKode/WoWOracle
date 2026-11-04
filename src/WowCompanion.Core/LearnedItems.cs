using System.Globalization;
using Microsoft.Data.Sqlite;

namespace WowCompanion.Core;

/// <summary>
/// An item as the game client describes it (from the WoWOracle addon). Used for items the downloaded game data
/// doesn't have yet, because the beta client is often a few builds ahead of wago.tools.
/// </summary>
public sealed record LearnedItem(
    int Id, string Name, int Quality, int ItemLevel, int RequiredLevel, int ClassId, int SubclassId, int InventoryType,
    int IconId, int Bonding, int SellPrice, int Armor, double DamageMin, double DamageMax, int DelayMs, string Stats)
{
    /// <summary>Parses an "itemdata=" value: id;quality;ilvl;req;class;subclass;inv;icon;bind;sell;armor;min;max;delay;stats;name</summary>
    public static LearnedItem? Parse(string value)
    {
        var p = value.Split(';');
        if (p.Length < 16) return null;
        int I(int i) => int.TryParse(p[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
        double D(int i) => double.TryParse(p[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
        var id = I(0);
        if (id <= 0) return null;
        return new LearnedItem(id, string.Join(";", p[15..]).Trim(), I(1), I(2), I(3), I(4), I(5), I(6), I(7), I(8), I(9), I(10),
            D(11), D(12), I(13), p[14]);
    }

    public string Serialize() => string.Join(";", new object[]
    {
        Id, Quality, ItemLevel, RequiredLevel, ClassId, SubclassId, InventoryType, IconId, Bonding, SellPrice, Armor,
        DamageMin.ToString(CultureInfo.InvariantCulture), DamageMax.ToString(CultureInfo.InvariantCulture), DelayMs, Stats, Name,
    });
}

/// <summary>
/// Items learned from the game, kept in user.sqlite so they survive game data rebuilds, and copied into the game
/// database's items table for every item id it lacks. Once wago.tools has the item, the real data wins.
/// </summary>
public sealed class LearnedItemStore(string userDatabasePath)
{
    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = userDatabasePath,
        Pooling = false,
    }.ToString();

    public void Save(IEnumerable<LearnedItem> items)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO learned_items (id, data, learned_utc) VALUES ($id, $data, $now)
            ON CONFLICT(id) DO UPDATE SET data = $data, learned_utc = $now
            """;
        var id = command.Parameters.Add("$id", SqliteType.Integer);
        var data = command.Parameters.Add("$data", SqliteType.Text);
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
        foreach (var item in items)
        {
            id.Value = item.Id;
            data.Value = item.Serialize();
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public List<LearnedItem> All()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT data FROM learned_items";
        var list = new List<LearnedItem>();
        using var r = command.ExecuteReader();
        while (r.Read())
            if (LearnedItem.Parse(r.GetString(0)) is { } item) list.Add(item);
        return list;
    }

    /// <summary>Adds learned items the game database doesn't have. Returns how many were added.</summary>
    public static int InjectInto(string gameDatabasePath, IReadOnlyCollection<LearnedItem> items)
    {
        if (items.Count == 0) return 0;
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = gameDatabasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        connection.Open();
        using var transaction = connection.BeginTransaction();

        using var exists = connection.CreateCommand();
        exists.Transaction = transaction;
        exists.CommandText = "SELECT 1 FROM items WHERE id = $id";
        var existsId = exists.Parameters.Add("$id", SqliteType.Integer);

        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO items (id, name, description, quality, item_level, required_level, class_id, subclass_id,
                               inventory_type, bonding, sell_price, buy_price, stack_size, stats, stats_raw,
                               delay_ms, dmg_min, dmg_max, armor, icon_file_id)
            VALUES ($id, $name, 'Learned from the game client (not in the downloaded data yet).', $quality, $ilvl, $req,
                    $cls, $sub, $inv, $bind, $sell, 0, 1, $stats, $stats, $delay, $min, $max, $armor, $icon);
            INSERT INTO items_fts (rowid, name, description)
            VALUES ($id, $name, 'Learned from the game client (not in the downloaded data yet).');
            """;

        var added = 0;
        foreach (var item in items)
        {
            existsId.Value = item.Id;
            if (exists.ExecuteScalar() is not null) continue;
            insert.Parameters.Clear();
            insert.Parameters.AddWithValue("$id", item.Id);
            insert.Parameters.AddWithValue("$name", item.Name);
            insert.Parameters.AddWithValue("$quality", item.Quality);
            insert.Parameters.AddWithValue("$ilvl", item.ItemLevel);
            insert.Parameters.AddWithValue("$req", item.RequiredLevel);
            insert.Parameters.AddWithValue("$cls", item.ClassId);
            insert.Parameters.AddWithValue("$sub", item.SubclassId);
            insert.Parameters.AddWithValue("$inv", item.InventoryType);
            insert.Parameters.AddWithValue("$bind", item.Bonding);
            insert.Parameters.AddWithValue("$sell", item.SellPrice);
            insert.Parameters.AddWithValue("$stats", item.Stats);
            insert.Parameters.AddWithValue("$delay", item.DelayMs);
            insert.Parameters.AddWithValue("$min", item.DamageMin);
            insert.Parameters.AddWithValue("$max", item.DamageMax);
            insert.Parameters.AddWithValue("$armor", item.Armor);
            insert.Parameters.AddWithValue("$icon", item.IconId);
            insert.ExecuteNonQuery();
            added++;
        }
        transaction.Commit();
        return added;
    }
}
