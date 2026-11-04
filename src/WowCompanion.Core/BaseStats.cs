using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;

namespace WowCompanion.Core;

/// <summary>
/// A character's base stats at one level, as the game client reports them (WoWOracle addon, "!WOWORACLE-BASE 1"):
/// Str, Agi, Sta, Int, Spi with nothing equipped and no buffs. Health and mana count only from a naked snapshot.
/// </summary>
public sealed record BaseStatRow(int ClassId, int RaceId, int Level, double[] Stats, int Health, int Mana, bool Naked, string Build)
{
    public const string Marker = "!WOWORACLE-BASE";

    public static BaseStatRow? Parse(string text)
    {
        string? baseLine = null, vitals = null, build = "";
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim('\r', ' ');
            if (line.StartsWith("base=", StringComparison.Ordinal)) baseLine = line[5..];
            else if (line.StartsWith("vitals=", StringComparison.Ordinal)) vitals = line[7..];
            else if (line.StartsWith("build=", StringComparison.Ordinal)) build = line[6..];
        }
        if (baseLine is null) return null;
        var p = baseLine.Split(';');
        if (p.Length < 8) return null;
        int I(string s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
        var (cls, race, level) = (I(p[0]), I(p[1]), I(p[2]));
        if (cls <= 0 || race <= 0 || level <= 0) return null;
        var stats = p[3..8].Select(s => (double)I(s)).ToArray();
        var vitalParts = (vitals ?? "").Split(';');
        var naked = vitalParts.Length > 2 && vitalParts[2] == "1";
        return new BaseStatRow(cls, race, level, stats, naked ? I(vitalParts[0]) : 0, naked ? I(vitalParts[1]) : 0, naked, build.Trim());
    }
}

/// <summary>Where the calculator's naked stats came from, for its notes.</summary>
public sealed record BaseStatsLookup(double[] Stats, string Source);

/// <summary>
/// Base stats recorded in game, kept in user.sqlite (base_stats), one row per class, race and level. The calculator
/// uses them instead of its estimate: the exact level when recorded, otherwise a straight line between the nearest
/// recorded levels below and above (base stats grow steadily per level).
/// </summary>
public sealed class BaseStatsStore(string userDatabasePath)
{
    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = userDatabasePath,
        Pooling = false,
    }.ToString();

    /// <summary>Saves new rows; a naked snapshot is never replaced by a dressed one. Returns how many rows were new.</summary>
    public int Save(IEnumerable<BaseStatRow> rows)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var transaction = connection.BeginTransaction();
        var added = 0;
        foreach (var row in rows)
        {
            using var check = connection.CreateCommand();
            check.Transaction = transaction;
            check.CommandText = "SELECT naked FROM base_stats WHERE class_id = $c AND race_id = $r AND level = $l";
            check.Parameters.AddWithValue("$c", row.ClassId);
            check.Parameters.AddWithValue("$r", row.RaceId);
            check.Parameters.AddWithValue("$l", row.Level);
            var existing = check.ExecuteScalar();
            if (existing is long wasNaked && wasNaked == 1 && !row.Naked) continue;
            if (existing is null) added++;

            using var upsert = connection.CreateCommand();
            upsert.Transaction = transaction;
            upsert.CommandText = """
                INSERT INTO base_stats (class_id, race_id, level, str, agi, sta, int, spi, health, mana, naked, build, updated_utc)
                VALUES ($c, $r, $l, $s0, $s1, $s2, $s3, $s4, $hp, $mana, $naked, $build, $now)
                ON CONFLICT(class_id, race_id, level) DO UPDATE SET
                    str = $s0, agi = $s1, sta = $s2, int = $s3, spi = $s4, health = $hp, mana = $mana, naked = $naked,
                    build = $build, updated_utc = $now
                """;
            upsert.Parameters.AddWithValue("$c", row.ClassId);
            upsert.Parameters.AddWithValue("$r", row.RaceId);
            upsert.Parameters.AddWithValue("$l", row.Level);
            for (var i = 0; i < 5; i++) upsert.Parameters.AddWithValue("$s" + i, row.Stats[i]);
            upsert.Parameters.AddWithValue("$hp", row.Health);
            upsert.Parameters.AddWithValue("$mana", row.Mana);
            upsert.Parameters.AddWithValue("$naked", row.Naked ? 1 : 0);
            upsert.Parameters.AddWithValue("$build", row.Build);
            upsert.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
            upsert.ExecuteNonQuery();
        }
        transaction.Commit();
        return added;
    }

    public List<BaseStatRow> All()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT class_id, race_id, level, str, agi, sta, int, spi, health, mana, naked, build
            FROM base_stats ORDER BY class_id, race_id, level
            """;
        var list = new List<BaseStatRow>();
        using var r = command.ExecuteReader();
        while (r.Read())
            list.Add(new BaseStatRow(r.GetInt32(0), r.GetInt32(1), r.GetInt32(2),
                [r.GetDouble(3), r.GetDouble(4), r.GetDouble(5), r.GetDouble(6), r.GetDouble(7)],
                r.GetInt32(8), r.GetInt32(9), r.GetInt32(10) == 1, r.GetString(11)));
        return list;
    }

    /// <summary>Base stats for a class, race and level from what was recorded, or null when nothing fits.</summary>
    public BaseStatsLookup? Find(int classId, int raceId, int level)
    {
        var rows = All().Where(r => r.ClassId == classId && r.RaceId == raceId).ToList();
        if (rows.FirstOrDefault(r => r.Level == level) is { } exact)
            return new BaseStatsLookup(exact.Stats, $"recorded in game at level {level}");
        var below = rows.Where(r => r.Level < level).MaxBy(r => r.Level);
        var above = rows.Where(r => r.Level > level).MinBy(r => r.Level);
        if (below is null || above is null) return null;
        var t = (level - below.Level) / (double)(above.Level - below.Level);
        var stats = Enumerable.Range(0, 5).Select(i => Math.Round(below.Stats[i] + (above.Stats[i] - below.Stats[i]) * t)).ToArray();
        return new BaseStatsLookup(stats, $"between levels {below.Level} and {above.Level} recorded in game");
    }

    /// <summary>Everything recorded as CSV (class, race, level, stats, naked health and mana), to share or check.</summary>
    public string Csv(Func<int, string> className, Func<int, string> raceName)
    {
        var b = new StringBuilder("class,race,level,str,agi,sta,int,spi,health_naked,mana_naked,build\n");
        foreach (var r in All())
        {
            b.Append(className(r.ClassId)).Append(',').Append(raceName(r.RaceId)).Append(',').Append(r.Level);
            foreach (var s in r.Stats) b.Append(',').Append(s.ToString("0", CultureInfo.InvariantCulture));
            b.Append(',').Append(r.Naked ? r.Health.ToString(CultureInfo.InvariantCulture) : "")
             .Append(',').Append(r.Naked ? r.Mana.ToString(CultureInfo.InvariantCulture) : "")
             .Append(',').Append(r.Build).Append('\n');
        }
        return b.ToString();
    }
}
