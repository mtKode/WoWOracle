using System.Globalization;
using Microsoft.Data.Sqlite;

namespace WowCompanion.Core;

/// <summary>
/// An ore or herb node you gathered (WoWOracle addon 0.12+, "!WOWORACLE-NODE 1": node=kind;name;zone;x;y). Teaches
/// the map's ore and herb layer the spots of zones the Classic data doesn't have (WoW Forever's own).
/// </summary>
public sealed record NodeSighting(string Kind, string Name, string Zone, double X, double Y)
{
    public const string Marker = "!WOWORACLE-NODE";

    public static NodeSighting? Parse(string text)
    {
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim('\r', ' ');
            if (!line.StartsWith("node=", StringComparison.Ordinal)) continue;
            var p = line[5..].Split(';');
            if (p.Length < 5) return null;
            static double D(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : -1;
            var (kind, name, zone) = (p[0].Trim(), p[1].Trim(), p[2].Trim());
            var (x, y) = (D(p[3]), D(p[4]));
            if (kind is not ("mining" or "herbalism") || name.Length == 0 || zone.Length == 0 || x is <= 0 or > 100 || y is <= 0 or > 100)
                return null;
            return new NodeSighting(kind, name, zone, x, y);
        }
        return null;
    }
}

/// <summary>Nodes you gathered, kept in user.sqlite (seen_nodes) and added to the game data's gather_spawns.</summary>
public sealed class NodeSightingStore(string userDatabasePath)
{
    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = userDatabasePath,
        Pooling = false,
    }.ToString();

    /// <summary>Returns how many were new.</summary>
    public int Save(IEnumerable<NodeSighting> nodes)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var transaction = connection.BeginTransaction();
        var added = 0;
        foreach (var n in nodes)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT OR IGNORE INTO seen_nodes (kind, name, zone, x, y, updated_utc) VALUES ($k, $n, $z, $x, $y, $now)
                """;
            command.Parameters.AddWithValue("$k", n.Kind);
            command.Parameters.AddWithValue("$n", n.Name);
            command.Parameters.AddWithValue("$z", n.Zone);
            command.Parameters.AddWithValue("$x", Math.Round(n.X, 1));
            command.Parameters.AddWithValue("$y", Math.Round(n.Y, 1));
            command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
            added += command.ExecuteNonQuery();
        }
        transaction.Commit();
        return added;
    }

    public List<NodeSighting> All()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT kind, name, zone, x, y FROM seen_nodes";
        var list = new List<NodeSighting>();
        using var r = command.ExecuteReader();
        while (r.Read()) list.Add(new NodeSighting(r.GetString(0), r.GetString(1), r.GetString(2), r.GetDouble(3), r.GetDouble(4)));
        return list;
    }

    /// <summary>
    /// Adds your nodes to the game data: a spot the data already has for that node (within 1 map unit) is skipped; a
    /// node name the data doesn't know gets its own entry (skill not known: -1). Returns how many spots were added.
    /// </summary>
    public static int ApplyTo(string gameDatabasePath, IReadOnlyList<NodeSighting> all)
    {
        if (all.Count == 0) return 0;
        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = gameDatabasePath,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false,
            }.ToString());
            connection.Open();
            using var transaction = connection.BeginTransaction();
            var added = 0;
            foreach (var n in all)
            {
                var id = NodeId(connection, transaction, n);
                using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO gather_spawns (node_id, zone, x, y)
                    SELECT $id, $z, $x, $y WHERE NOT EXISTS (
                        SELECT 1 FROM gather_spawns s JOIN gather_nodes g ON g.id = s.node_id
                        WHERE g.name = $n AND s.zone = $z AND abs(s.x - $x) < 1 AND abs(s.y - $y) < 1)
                    """;
                insert.Parameters.AddWithValue("$id", id);
                insert.Parameters.AddWithValue("$n", n.Name);
                insert.Parameters.AddWithValue("$z", n.Zone);
                insert.Parameters.AddWithValue("$x", Math.Round(n.X, 1));
                insert.Parameters.AddWithValue("$y", Math.Round(n.Y, 1));
                added += insert.ExecuteNonQuery();
            }
            transaction.Commit();
            return added;
        }
        catch (SqliteException)
        {
            return 0; // game data before schema 30 has no gather tables
        }
    }

    /// <summary>The data's node of that name, or a new one for a node only WoW Forever has (negative id, skill -1).</summary>
    private static long NodeId(SqliteConnection connection, SqliteTransaction transaction, NodeSighting n)
    {
        using (var find = connection.CreateCommand())
        {
            find.Transaction = transaction;
            find.CommandText = "SELECT id FROM gather_nodes WHERE name = $n AND kind = $k ORDER BY id DESC LIMIT 1";
            find.Parameters.AddWithValue("$n", n.Name);
            find.Parameters.AddWithValue("$k", n.Kind);
            if (find.ExecuteScalar() is long id) return id;
        }
        using var next = connection.CreateCommand();
        next.Transaction = transaction;
        next.CommandText = """
            INSERT INTO gather_nodes (id, name, kind, skill) VALUES ((SELECT MIN(0, COALESCE(MIN(id), 0)) - 1 FROM gather_nodes), $n, $k, -1);
            SELECT last_insert_rowid();
            """;
        next.Parameters.AddWithValue("$n", n.Name);
        next.Parameters.AddWithValue("$k", n.Kind);
        return (long)next.ExecuteScalar()!;
    }
}
