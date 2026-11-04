using Microsoft.Data.Sqlite;

namespace WowCompanion.Core;

public sealed record TalentInfo(
    int Id, int TabId, int Tier, int Column, int MaxRank, IReadOnlyList<int> RankSpells,
    IReadOnlyList<string> RankDescriptions, int PrereqTalent, int PrereqRank, string Name, int IconId = 0);

public sealed record TalentTree(int Id, string Name, int OrderIndex, IReadOnlyList<TalentInfo> Talents)
{
    /// <summary>Classic talent frame art name, e.g. "DruidBalance".</summary>
    public string Background { get; init; } = "";
    public int IconId { get; init; }
}

/// <summary>Talent trees for one class, with every rank's description ready to show.</summary>
public sealed class TalentService(string databasePath)
{
    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = databasePath,
        Mode = SqliteOpenMode.ReadOnly,
        Pooling = false,
    }.ToString();

    /// <summary>
    /// Per-rank effect values of trait talents that set their values from a curve (effect index → value at the rank),
    /// or null when the talent simply scales its base points by rank. Older game data has no table: null.
    /// </summary>
    public IReadOnlyDictionary<int, double>? RankPoints(int spellId, int rank)
    {
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT effect_index, value FROM talent_points WHERE spell_id = $s AND rank = $r";
            command.Parameters.AddWithValue("$s", spellId);
            command.Parameters.AddWithValue("$r", rank);
            var result = new Dictionary<int, double>();
            using var reader = command.ExecuteReader();
            while (reader.Read()) result[reader.GetInt32(0)] = reader.GetDouble(1);
            return result.Count > 0 ? result : null;
        }
        catch (SqliteException)
        {
            return null;
        }
    }

    public IReadOnlyList<TalentTree> GetTrees(int classId)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var tabs = new List<(int Id, string Name, int Order, string Background, int Icon)>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id, name, order_index, background, icon_file_id FROM talent_tabs WHERE class_id = $c ORDER BY order_index";
            command.Parameters.AddWithValue("$c", classId);
            using var reader = command.ExecuteReader();
            while (reader.Read())
                tabs.Add((reader.GetInt32(0), reader.GetString(1), reader.GetInt32(2), reader.GetString(3), reader.GetInt32(4)));
        }

        var raw = new List<(int Id, int Tab, int Tier, int Col, int Max, int[] Spells, int Pre, int PreRank, string Name, string[] Texts)>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT t.id, t.tab_id, t.tier, t.col, t.max_rank, t.rank_spells, t.prereq_talent, t.prereq_rank, t.name,
                       t.rank_descriptions
                FROM talents t JOIN talent_tabs tt ON tt.id = t.tab_id
                WHERE tt.class_id = $c ORDER BY t.tier, t.col
                """;
            command.Parameters.AddWithValue("$c", classId);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var spells = reader.GetString(5).Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray();
                raw.Add((reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4),
                    spells, reader.GetInt32(6), reader.GetInt32(7), reader.GetString(8),
                    reader.GetString(9).Split('\u001f', StringSplitOptions.None)));
            }
        }

        var descriptions = new Dictionary<int, string>();
        var icons = new Dictionary<int, int>();
        var allSpells = raw.SelectMany(t => t.Spells).Distinct().ToList();
        foreach (var chunk in allSpells.Chunk(400))
        {
            using var command = connection.CreateCommand();
            var names = new List<string>();
            for (var i = 0; i < chunk.Length; i++)
            {
                command.Parameters.AddWithValue("$p" + i, chunk[i]);
                names.Add("$p" + i);
            }
            command.CommandText = $"SELECT id, description, icon_file_id FROM spells WHERE id IN ({string.Join(',', names)})";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                descriptions[reader.GetInt32(0)] = reader.GetString(1);
                icons[reader.GetInt32(0)] = reader.GetInt32(2);
            }
        }

        return tabs.Select(tab => new TalentTree(tab.Id, tab.Name, tab.Order,
                raw.Where(t => t.Tab == tab.Id)
                    .Select(t => new TalentInfo(t.Id, t.Tab, t.Tier, t.Col, t.Max, t.Spells,
                        // Trait talents store one rendered text per rank; classic talents have a spell per rank.
                        t.Texts.Length == t.Max && t.Texts.Any(x => x.Length > 0)
                            ? (IReadOnlyList<string>)t.Texts
                            : t.Spells.Select(s => descriptions.GetValueOrDefault(s, "")).ToList(),
                        t.Pre, t.PreRank, t.Name, icons.GetValueOrDefault(t.Spells.FirstOrDefault())))
                    .ToList())
            {
                Background = tab.Background,
                IconId = tab.Icon,
            })
            .ToList();
    }
}
