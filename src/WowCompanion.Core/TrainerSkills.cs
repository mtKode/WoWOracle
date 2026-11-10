using System.Globalization;
using Microsoft.Data.Sqlite;

namespace WowCompanion.Core;

/// <summary>
/// The skill a trainer teaches a recipe at, read from a trainer window by the WoWOracle addon
/// ("!WOWORACLE-TRAINER 1": profession=Leatherworking, then recipe=skill;itemId;level;name per recipe).
/// The client data has no such number, so without it the app guesses (see GameDatabaseBuilder.FillRecipeRequirements).
/// </summary>
public sealed record TrainerRecipe(string Profession, string Name, int Skill, int ItemId, int Level)
{
    public const string Marker = "!WOWORACLE-TRAINER";

    public static List<TrainerRecipe> Parse(string text)
    {
        var list = new List<TrainerRecipe>();
        string? profession = null;
        var lines = text.Split('\n').Select(l => l.Trim('\r', ' ')).ToList();
        foreach (var line in lines)
            if (line.StartsWith("profession=", StringComparison.Ordinal)) profession = line[11..].Trim();
        if (string.IsNullOrEmpty(profession)) return list;

        static int I(string s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
        foreach (var line in lines.Where(l => l.StartsWith("recipe=", StringComparison.Ordinal)))
        {
            var p = line[7..].Split(';', 4);
            if (p.Length < 4) continue;
            var (skill, name) = (I(p[0]), p[3].Trim());
            if (skill <= 0 || name.Length == 0) continue;
            list.Add(new TrainerRecipe(profession, name, skill, I(p[1]), I(p[2])));
        }
        return list;
    }
}

/// <summary>Trainer skills read in game, kept in user.sqlite (trainer_skills) and laid over the game data.</summary>
public sealed class TrainerSkillStore(string userDatabasePath)
{
    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = userDatabasePath,
        Pooling = false,
    }.ToString();

    /// <summary>Saves what came in. Returns how many recipes were new or changed their skill.</summary>
    public int Save(IEnumerable<TrainerRecipe> recipes)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var transaction = connection.BeginTransaction();
        var changed = 0;
        foreach (var r in recipes.DistinctBy(r => (r.Profession, r.Name)))
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO trainer_skills (profession, name, skill, item_id, level, updated_utc)
                VALUES ($p, $n, $s, $i, $l, $now)
                ON CONFLICT(profession, name) DO UPDATE SET skill = $s, item_id = $i, level = $l, updated_utc = $now
                WHERE skill <> $s OR item_id <> $i OR level <> $l
                """;
            command.Parameters.AddWithValue("$p", r.Profession);
            command.Parameters.AddWithValue("$n", r.Name);
            command.Parameters.AddWithValue("$s", r.Skill);
            command.Parameters.AddWithValue("$i", r.ItemId);
            command.Parameters.AddWithValue("$l", r.Level);
            command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
            changed += command.ExecuteNonQuery();
        }
        transaction.Commit();
        return changed;
    }

    public List<TrainerRecipe> All()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT profession, name, skill, item_id, level FROM trainer_skills";
        var list = new List<TrainerRecipe>();
        using var r = command.ExecuteReader();
        while (r.Read()) list.Add(new TrainerRecipe(r.GetString(0), r.GetString(1), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4)));
        return list;
    }

    /// <summary>
    /// Puts the trainer skills into the game database's recipes (skill_source 'trainer'), matched by profession and
    /// name, and by the item it makes when the trainer named one. Recipes taught by an item keep that item's skill.
    /// Returns how many recipes changed.
    /// </summary>
    public static int ApplyTo(string gameDatabasePath, IReadOnlyList<TrainerRecipe> recipes)
    {
        if (recipes.Count == 0) return 0;
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = gameDatabasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        connection.Open();
        using var transaction = connection.BeginTransaction();
        var changed = 0;
        foreach (var r in recipes)
        {
            int Update(bool byItem)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = $"""
                    UPDATE recipes SET min_skill = $s, skill_source = 'trainer'
                    WHERE profession = $p AND name = $n AND skill_source <> 'recipe'
                      {(byItem ? "AND created_item_id = $i" : "")}
                      AND (min_skill <> $s OR skill_source <> 'trainer')
                    """;
                command.Parameters.AddWithValue("$s", r.Skill);
                command.Parameters.AddWithValue("$p", r.Profession);
                command.Parameters.AddWithValue("$n", r.Name);
                command.Parameters.AddWithValue("$i", r.ItemId);
                return command.ExecuteNonQuery();
            }

            // Several recipes can share a name (an old and a new version): the item it makes picks the right one.
            var n = r.ItemId > 0 ? Update(byItem: true) : 0;
            if (n == 0 && (r.ItemId == 0 || !MakesItem(connection, transaction, r))) n = Update(byItem: false);
            changed += n;
        }
        transaction.Commit();
        return changed;
    }

    /// <summary>Whether a recipe of this name already makes the trainer's item (then it's simply up to date).</summary>
    private static bool MakesItem(SqliteConnection connection, SqliteTransaction transaction, TrainerRecipe r)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM recipes WHERE profession = $p AND name = $n AND created_item_id = $i LIMIT 1";
        command.Parameters.AddWithValue("$p", r.Profession);
        command.Parameters.AddWithValue("$n", r.Name);
        command.Parameters.AddWithValue("$i", r.ItemId);
        return command.ExecuteScalar() is not null;
    }
}
