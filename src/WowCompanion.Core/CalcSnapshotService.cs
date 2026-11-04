using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace WowCompanion.Core;

/// <summary>Everything needed to bring a calculation back, plus what it showed when it was saved.</summary>
public sealed record CalcSnapshot
{
    public string Name { get; init; } = "";
    public DateTime SavedUtc { get; init; }
    public int ClassId { get; init; }
    public int RaceId { get; init; }
    public int Level { get; init; } = 60;
    public int GearSetId { get; init; }
    public string GearSetName { get; init; } = "";
    public int TalentSetId { get; init; }
    public string TalentSetName { get; init; } = "";
    public string SpellName { get; init; } = "";
    public int SpellId { get; init; }
    public int TargetLevelDiff { get; init; }
    public List<string> Buffs { get; init; } = [];
    public List<string> NakedStats { get; init; } = [];
    /// <summary>The result lines at save time, "Label: value", for comparing later.</summary>
    public List<string> Output { get; init; } = [];
}

public sealed record CalcSnapshotInfo(int Id, string Name, string SavedText);

/// <summary>Saved calculator states (user.sqlite, JSON per row).</summary>
public sealed class CalcSnapshotService(string userDatabasePath)
{
    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = userDatabasePath,
        Pooling = false,
    }.ToString();

    public IReadOnlyList<CalcSnapshotInfo> List()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name, created_utc FROM calc_snapshots ORDER BY id DESC";
        var result = new List<CalcSnapshotInfo>();
        using var r = command.ExecuteReader();
        while (r.Read()) result.Add(new CalcSnapshotInfo(r.GetInt32(0), r.GetString(1), r.GetString(2)));
        return result;
    }

    public int Save(CalcSnapshot snapshot)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO calc_snapshots (name, created_utc, data) VALUES ($n, datetime('now'), $d);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$n", snapshot.Name);
        command.Parameters.AddWithValue("$d", JsonSerializer.Serialize(snapshot));
        return Convert.ToInt32(command.ExecuteScalar());
    }

    public CalcSnapshot? Load(int id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT data FROM calc_snapshots WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteScalar() is string json ? JsonSerializer.Deserialize<CalcSnapshot>(json) : null;
    }

    public void Delete(int id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM calc_snapshots WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }
}
