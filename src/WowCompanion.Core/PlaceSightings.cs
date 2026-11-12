using System.Globalization;
using Microsoft.Data.Sqlite;

namespace WowCompanion.Core;

/// <summary>
/// Where the player was inside a subzone, averaged over several moments (WoWOracle addon 0.11+, "!WOWORACLE-PLACE 1":
/// place=zone;subzone;x;y;samples). Places subzones the game data has no spot for (WoW Forever's Bandarion Keep…).
/// </summary>
public sealed record PlaceSighting(string Zone, string Name, double X, double Y, int Samples)
{
    public const string Marker = "!WOWORACLE-PLACE";

    public static PlaceSighting? Parse(string text)
    {
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim('\r', ' ');
            if (!line.StartsWith("place=", StringComparison.Ordinal)) continue;
            var p = line[6..].Split(';');
            if (p.Length < 5) return null;
            static double D(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : -1;
            var (x, y) = (D(p[2]), D(p[3]));
            var samples = int.TryParse(p[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
            if (p[0].Trim().Length == 0 || p[1].Trim().Length == 0 || x is <= 0 or > 100 || y is <= 0 or > 100 || samples <= 0) return null;
            return new PlaceSighting(p[0].Trim(), p[1].Trim(), x, y, samples);
        }
        return null;
    }
}

/// <summary>Subzone spots from the addon, kept in user.sqlite (seen_places) and filled into the game data's map places.</summary>
public sealed class PlaceSightingStore(string userDatabasePath)
{
    /// <summary>Moments (8 seconds apart) in a subzone before its averaged spot is used.</summary>
    public const int MinSamples = 5;

    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = userDatabasePath,
        Pooling = false,
    }.ToString();

    /// <summary>Keeps the better-sampled spot per subzone. Returns how many were new.</summary>
    public int Save(IEnumerable<PlaceSighting> places)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var transaction = connection.BeginTransaction();
        var added = 0;
        foreach (var p in places.GroupBy(p => (p.Zone, p.Name)).Select(g => g.MaxBy(x => x.Samples)!))
        {
            using var exists = connection.CreateCommand();
            exists.Transaction = transaction;
            exists.CommandText = "SELECT 1 FROM seen_places WHERE zone = $z AND name = $n";
            exists.Parameters.AddWithValue("$z", p.Zone);
            exists.Parameters.AddWithValue("$n", p.Name);
            if (exists.ExecuteScalar() is null) added++;

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO seen_places (zone, name, x, y, samples, updated_utc) VALUES ($z, $n, $x, $y, $s, $now)
                ON CONFLICT(zone, name) DO UPDATE SET x = $x, y = $y, samples = $s, updated_utc = $now WHERE samples < $s
                """;
            command.Parameters.AddWithValue("$z", p.Zone);
            command.Parameters.AddWithValue("$n", p.Name);
            command.Parameters.AddWithValue("$x", p.X);
            command.Parameters.AddWithValue("$y", p.Y);
            command.Parameters.AddWithValue("$s", p.Samples);
            command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
            command.ExecuteNonQuery();
        }
        transaction.Commit();
        return added;
    }

    public List<PlaceSighting> All()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT zone, name, x, y, samples FROM seen_places";
        var list = new List<PlaceSighting>();
        using var r = command.ExecuteReader();
        while (r.Read()) list.Add(new PlaceSighting(r.GetString(0), r.GetString(1), r.GetDouble(2), r.GetDouble(3), r.GetInt32(4)));
        return list;
    }

    /// <summary>
    /// Fills the game data's map places: a subzone without a spot gets the recorded one, a subzone the data doesn't
    /// list at all is added. Spots from the game data are never replaced. Only spots from at least MinSamples moments:
    /// the first one is where you walked in, at the edge. Returns how many places changed.
    /// </summary>
    public static int ApplyTo(string gameDatabasePath, IReadOnlyList<PlaceSighting> all)
    {
        var places = all.Where(p => p.Samples >= MinSamples).ToList();
        if (places.Count == 0) return 0;
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
            var changed = 0;
            foreach (var p in places)
            {
                using var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = """
                    UPDATE map_places SET x = $x, y = $y WHERE zone = $z AND name = $n AND x < 0;
                    INSERT INTO map_places (name, kind, zone, x, y)
                    SELECT $n, 'subzone', $z, $x, $y WHERE NOT EXISTS (SELECT 1 FROM map_places WHERE zone = $z AND name = $n);
                    """;
                update.Parameters.AddWithValue("$z", p.Zone);
                update.Parameters.AddWithValue("$n", p.Name);
                update.Parameters.AddWithValue("$x", Math.Round(p.X, 1));
                update.Parameters.AddWithValue("$y", Math.Round(p.Y, 1));
                changed += update.ExecuteNonQuery();
            }
            transaction.Commit();
            return changed;
        }
        catch (SqliteException)
        {
            return 0; // game data before schema 29 has no map places
        }
    }
}
