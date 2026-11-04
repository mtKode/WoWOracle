using Microsoft.Data.Sqlite;

namespace WowCompanion.Core;

public sealed record TalentBuild(int Level, IReadOnlyDictionary<int, int> Ranks);

public sealed record TalentSetInfo(int Id, int ClassId, string Name, int Level);

/// <summary>
/// Named talent builds, any number per class, in user.sqlite. One set per class is "active": that's the one the
/// Talents page opens and the calculator uses unless told otherwise.
/// </summary>
public sealed class TalentBuildService(string userDatabasePath)
{
    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = userDatabasePath,
        Pooling = false,
    }.ToString();

    public IReadOnlyList<TalentSetInfo> List(int classId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, class_id, name, level FROM talent_sets WHERE class_id = $c ORDER BY name, id";
        command.Parameters.AddWithValue("$c", classId);
        var result = new List<TalentSetInfo>();
        using var r = command.ExecuteReader();
        while (r.Read()) result.Add(new TalentSetInfo(r.GetInt32(0), r.GetInt32(1), r.GetString(2), r.GetInt32(3)));
        return result;
    }

    /// <summary>The active set for a class, creating "Main" when the class has none yet.</summary>
    public int EnsureActive(int classId)
    {
        var sets = List(classId);
        if (sets.Count == 0)
        {
            var id = Create(classId, "Main", 60, new Dictionary<int, int>());
            SetActive(classId, id);
            return id;
        }
        var active = GetSetting(ActiveKey(classId));
        return int.TryParse(active, out var activeId) && sets.Any(s => s.Id == activeId) ? activeId : sets[0].Id;
    }

    public void SetActive(int classId, int setId) => SetSetting(ActiveKey(classId), setId.ToString());

    public int Create(int classId, string name, int level, IReadOnlyDictionary<int, int> ranks)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO talent_sets (class_id, name, level, ranks, updated_utc) VALUES ($c, $n, $l, $r, datetime('now'));
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$c", classId);
        command.Parameters.AddWithValue("$n", name);
        command.Parameters.AddWithValue("$l", level);
        command.Parameters.AddWithValue("$r", Encode(ranks));
        return Convert.ToInt32(command.ExecuteScalar());
    }

    public TalentBuild Load(int setId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT level, ranks FROM talent_sets WHERE id = $id";
        command.Parameters.AddWithValue("$id", setId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? new TalentBuild(reader.GetInt32(0), Decode(reader.GetString(1))) : new TalentBuild(60, new Dictionary<int, int>());
    }

    /// <summary>Build of the class's active set.</summary>
    public TalentBuild LoadActive(int classId) => Load(EnsureActive(classId));

    public void Save(int setId, int level, IReadOnlyDictionary<int, int> ranks) => Execute(
        "UPDATE talent_sets SET level = $l, ranks = $r, updated_utc = datetime('now') WHERE id = $id",
        ("$id", setId), ("$l", level), ("$r", Encode(ranks)));

    public void Rename(int setId, string name) =>
        Execute("UPDATE talent_sets SET name = $n WHERE id = $id", ("$id", setId), ("$n", name));

    public void Delete(int setId) => Execute("DELETE FROM talent_sets WHERE id = $id", ("$id", setId));

    // ----- settings (shared key/value table) -----

    public string? GetSetting(string key)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM settings WHERE key = $k";
        command.Parameters.AddWithValue("$k", key);
        return command.ExecuteScalar() as string;
    }

    public void SetSetting(string key, string value) => Execute(
        "INSERT INTO settings (key, value) VALUES ($k, $v) ON CONFLICT(key) DO UPDATE SET value = $v",
        ("$k", key), ("$v", value));

    private static string ActiveKey(int classId) => $"talent_set:{classId}";

    private static string Encode(IReadOnlyDictionary<int, int> ranks) =>
        string.Join(',', ranks.Where(r => r.Value > 0).Select(r => $"{r.Key}:{r.Value}"));

    private static Dictionary<int, int> Decode(string text)
    {
        var ranks = new Dictionary<int, int>();
        foreach (var pair in text.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split(':');
            if (parts.Length == 2 && int.TryParse(parts[0], out var talent) && int.TryParse(parts[1], out var rank) && rank > 0)
                ranks[talent] = rank;
        }
        return ranks;
    }

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
