using System.Globalization;
using Microsoft.Data.Sqlite;

namespace WowCompanion.Core;

/// <summary>
/// A spot where you came near an NPC the Classic data has no spawns for (WoWOracle addon 0.13+,
/// "!WOWORACLE-SPOT 1": spot=id;name;zone;x;y). Your spot within about 10 yards stands in for the NPC's.
/// </summary>
public sealed record NpcSpotSighting(int NpcId, string Name, string Zone, double X, double Y)
{
    public const string Marker = "!WOWORACLE-SPOT";

    public static NpcSpotSighting? Parse(string text)
    {
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim('\r', ' ');
            if (!line.StartsWith("spot=", StringComparison.Ordinal)) continue;
            var p = line[5..].Split(';');
            if (p.Length < 5) return null;
            static double D(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : -1;
            var id = int.TryParse(p[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
            var (name, zone) = (p[1].Trim(), p[2].Trim());
            var (x, y) = (D(p[3]), D(p[4]));
            if (id <= 0 || zone.Length == 0 || x is <= 0 or > 100 || y is <= 0 or > 100) return null;
            return new NpcSpotSighting(id, name, zone, x, y);
        }
        return null;
    }
}

/// <summary>NPC spots from the addon, kept in user.sqlite (seen_npc_spots) and added to the game data's npc_spawns.</summary>
public sealed class NpcSpotStore(string userDatabasePath)
{
    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = userDatabasePath,
        Pooling = false,
    }.ToString();

    /// <summary>Returns how many were new.</summary>
    public int Save(IEnumerable<NpcSpotSighting> spots)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var transaction = connection.BeginTransaction();
        var added = 0;
        foreach (var s in spots)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT OR IGNORE INTO seen_npc_spots (npc_id, name, zone, x, y, updated_utc) VALUES ($id, $n, $z, $x, $y, $now)
                """;
            command.Parameters.AddWithValue("$id", s.NpcId);
            command.Parameters.AddWithValue("$n", s.Name);
            command.Parameters.AddWithValue("$z", s.Zone);
            command.Parameters.AddWithValue("$x", Math.Round(s.X, 1));
            command.Parameters.AddWithValue("$y", Math.Round(s.Y, 1));
            command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
            added += command.ExecuteNonQuery();
        }
        transaction.Commit();
        return added;
    }

    public List<NpcSpotSighting> All()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT npc_id, name, zone, x, y FROM seen_npc_spots";
        var list = new List<NpcSpotSighting>();
        using var r = command.ExecuteReader();
        while (r.Read()) list.Add(new NpcSpotSighting(r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetDouble(3), r.GetDouble(4)));
        return list;
    }

    /// <summary>
    /// Puts all your spots into the game data (npc_spawns, seen = 1; the ones from before are replaced). An NPC the data
    /// doesn't have yet gets a row of its own, placed at its first spot. Returns how many spots there are.
    /// </summary>
    public static int ApplyTo(string gameDatabasePath, IReadOnlyList<NpcSpotSighting> all)
    {
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
            int Run(string sql, params (string Name, object Value)[] parameters)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = sql;
                foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
                return command.ExecuteNonQuery();
            }

            Run("DELETE FROM npc_spawns WHERE seen = 1");
            foreach (var s in all)
            {
                Run("""
                    INSERT INTO npcs (id, name, subname, min_level, max_level, rank, map, zone, zone_x, zone_y)
                    SELECT $id, $n, '', 0, 0, 0, -1, $z, $x, $y WHERE $n <> '' AND NOT EXISTS (SELECT 1 FROM npcs WHERE id = $id)
                    """, ("$id", s.NpcId), ("$n", s.Name), ("$z", s.Zone), ("$x", s.X), ("$y", s.Y));
                Run("UPDATE npcs SET zone = $z, zone_x = $x, zone_y = $y WHERE id = $id AND zone = ''",
                    ("$id", s.NpcId), ("$z", s.Zone), ("$x", s.X), ("$y", s.Y));
                Run("INSERT INTO npc_spawns (npc_id, zone, x, y, seen) VALUES ($id, $z, $x, $y, 1)",
                    ("$id", s.NpcId), ("$z", s.Zone), ("$x", Math.Round(s.X, 1)), ("$y", Math.Round(s.Y, 1)));
            }
            transaction.Commit();
            return all.Count;
        }
        catch (SqliteException)
        {
            return 0; // game data before schema 34
        }
    }
}
