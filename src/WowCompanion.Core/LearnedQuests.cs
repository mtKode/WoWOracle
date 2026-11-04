using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;

namespace WowCompanion.Core;

/// <summary>
/// A quest as the game client showed it (from the WoWOracle addon): who gives it, where it was picked up, its rewards.
/// Fills the gap the CMaNGOS Classic data leaves for Forever-only quests.
/// </summary>
public sealed record LearnedQuest(
    int Id, string Title, int Level, string Zone, string GiverKind, int GiverId, string GiverName,
    string At, double X, double Y, IReadOnlyList<int> Rewards, IReadOnlyList<int> Choices, int Money, int Xp, int Build,
    IReadOnlyList<string> ItemData)
{
    public const string Marker = "!WOWORACLE-QUEST";

    /// <summary>Parses one "!WOWORACLE-QUEST 1 … !END" block.</summary>
    public static LearnedQuest? Parse(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var items = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim('\r', ' ');
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq];
            var value = line[(eq + 1)..];
            if (key == "itemdata") items.Add(value);
            else values[key] = value;
        }
        string S(string key) => values.GetValueOrDefault(key, "").Trim();
        int I(string s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
        double D(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
        List<int> Ids(string s) => s.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(I).Where(v => v > 0).ToList();

        var id = I(S("id"));
        var title = S("title");
        if (id <= 0 || title.Length == 0) return null;
        var giver = S("giver").Split(';', 3);
        var at = S("at").Split(';', 3);
        return new LearnedQuest(id, title, I(S("level")), S("zone"),
            giver[0], giver.Length > 1 ? I(giver[1]) : 0, giver.Length > 2 ? giver[2].Trim() : "",
            at[0].Trim(), at.Length > 1 ? D(at[1]) : 0, at.Length > 2 ? D(at[2]) : 0,
            Ids(S("reward")), Ids(S("choice")), I(S("money")), I(S("xp")), I(S("build")), items);
    }

    public string Serialize()
    {
        var b = new StringBuilder();
        b.Append(Marker).Append(" 1\n");
        b.Append("id=").Append(Id).Append('\n');
        b.Append("title=").Append(Title).Append('\n');
        b.Append("level=").Append(Level).Append('\n');
        b.Append("zone=").Append(Zone).Append('\n');
        b.Append("giver=").Append(GiverKind).Append(';').Append(GiverId).Append(';').Append(GiverName).Append('\n');
        b.Append("at=").Append(At).Append(';').Append(X.ToString(CultureInfo.InvariantCulture)).Append(';')
            .Append(Y.ToString(CultureInfo.InvariantCulture)).Append('\n');
        b.Append("reward=").Append(string.Join(",", Rewards)).Append('\n');
        b.Append("choice=").Append(string.Join(",", Choices)).Append('\n');
        b.Append("money=").Append(Money).Append('\n');
        b.Append("xp=").Append(Xp).Append('\n');
        b.Append("build=").Append(Build).Append('\n');
        foreach (var item in ItemData) b.Append("itemdata=").Append(item).Append('\n');
        b.Append("!END");
        return b.ToString();
    }

    /// <summary>This capture, with blanks filled from an older one (the addon may have seen the quest twice).</summary>
    public LearnedQuest Over(LearnedQuest older) => new(
        Id, Title.Length > 0 ? Title : older.Title, Level > 0 ? Level : older.Level, Zone.Length > 0 ? Zone : older.Zone,
        GiverKind.Length > 0 || GiverName.Length > 0 ? GiverKind : older.GiverKind,
        GiverKind.Length > 0 || GiverName.Length > 0 ? GiverId : older.GiverId,
        GiverKind.Length > 0 || GiverName.Length > 0 ? GiverName : older.GiverName,
        At.Length > 0 ? At : older.At, At.Length > 0 ? X : older.X, At.Length > 0 ? Y : older.Y,
        Rewards.Count > 0 || Choices.Count > 0 ? Rewards : older.Rewards,
        Rewards.Count > 0 || Choices.Count > 0 ? Choices : older.Choices,
        Money > 0 ? Money : older.Money, Xp > 0 ? Xp : older.Xp, Math.Max(Build, older.Build),
        ItemData.Count > 0 ? ItemData : older.ItemData);

    /// <summary>Client item data for the rewards (items newer than the downloaded data).</summary>
    public IEnumerable<LearnedItem> RewardItems() => ItemData.Select(LearnedItem.Parse).OfType<LearnedItem>();
}

/// <summary>
/// Quests learned from the game, kept in user.sqlite (so the addon's file can be cleared or the addon removed),
/// and added to the game database for every quest id it doesn't have. Quests the downloaded data knows win.
/// </summary>
public sealed class LearnedQuestStore(string userDatabasePath)
{
    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = userDatabasePath,
        Pooling = false,
    }.ToString();

    /// <summary>Saves new or more complete captures. Returns how many quests were new to the store.</summary>
    public int Save(IEnumerable<LearnedQuest> quests)
    {
        var known = All().ToDictionary(q => q.Id);
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO learned_quests (id, data, learned_utc) VALUES ($id, $data, $now)
            ON CONFLICT(id) DO UPDATE SET data = $data
            """;
        var id = command.Parameters.Add("$id", SqliteType.Integer);
        var data = command.Parameters.Add("$data", SqliteType.Text);
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
        var added = 0;
        foreach (var quest in quests)
        {
            var merged = known.TryGetValue(quest.Id, out var old) ? quest.Over(old) : quest;
            var text = merged.Serialize();
            if (old is not null && old.Serialize() == text) continue;
            if (old is null) added++;
            known[quest.Id] = merged;
            id.Value = quest.Id;
            data.Value = text;
            command.ExecuteNonQuery();
        }
        transaction.Commit();
        return added;
    }

    public List<LearnedQuest> All()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT data FROM learned_quests";
        var list = new List<LearnedQuest>();
        using var r = command.ExecuteReader();
        while (r.Read())
            if (LearnedQuest.Parse(r.GetString(0)) is { } quest) list.Add(quest);
        return list;
    }

    /// <summary>
    /// Adds learned quests the game database doesn't have: the quest, its rewards as item sources and, when the
    /// giver is unknown too, the giver with the spot it was found at. Quests added by an earlier start are refreshed when a later capture differs.
    /// </summary>
    public static QuestInjectResult InjectInto(string gameDatabasePath, IReadOnlyCollection<LearnedQuest> quests)
    {
        if (quests.Count == 0) return new QuestInjectResult([], []);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = gameDatabasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        connection.Open();
        using var transaction = connection.BeginTransaction();

        SqliteCommand Command(string sql)
        {
            var c = connection.CreateCommand();
            c.Transaction = transaction;
            c.CommandText = sql;
            return c;
        }
        object? Scalar(string sql, params (string Name, object Value)[] args)
        {
            using var c = Command(sql);
            foreach (var (name, value) in args) c.Parameters.AddWithValue(name, value);
            return c.ExecuteScalar();
        }
        void Run(string sql, params (string Name, object Value)[] args)
        {
            using var c = Command(sql);
            foreach (var (name, value) in args) c.Parameters.AddWithValue(name, value);
            c.ExecuteNonQuery();
        }
        // Area by name (quest log header or the zone it was picked up in): id and continent map.
        (int Id, int Map)? Area(string name)
        {
            if (name.Length == 0) return null;
            using var c = Command("SELECT id, map FROM areas WHERE name = $n COLLATE NOCASE ORDER BY (map < 0), id LIMIT 1");
            c.Parameters.AddWithValue("$n", name);
            using var r = c.ExecuteReader();
            return r.Read() ? (r.GetInt32(0), r.GetInt32(1)) : null;
        }

        // Quests added by an earlier start are refreshed (a later capture may have the rewards); downloaded ones are left alone.
        var ours = (Scalar("SELECT value FROM meta WHERE key = 'learned_quests'") as string ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(v => int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : 0)
            .ToHashSet();
        if (Scalar("SELECT 1 FROM meta WHERE key = 'learned_quests'") is null)
        {
            // Databases from before this list: learned quests are the ones written with min_level 0.
            foreach (var q in quests)
                if (Scalar("SELECT 1 FROM quests WHERE id = $id AND min_level = 0", ("$id", q.Id)) is not null) ours.Add(q.Id);
        }

        // What a quest row says, to tell whether a refresh changes anything.
        string? Stored(int id)
        {
            using var c = Command("""
                SELECT q.title || '|' || q.level || '|' || q.zone || '|' || q.giver || '|' || q.giver_kind || '|' || q.giver_id || '|' ||
                       COALESCE((SELECT group_concat(kind || ':' || item_id, ',') FROM
                                 (SELECT kind, item_id FROM item_sources WHERE kind IN ('quest', 'quest_choice') AND ref_id = q.id
                                  ORDER BY kind, item_id)), '')
                FROM quests q WHERE q.id = $id
                """);
            c.Parameters.AddWithValue("$id", id);
            return c.ExecuteScalar() as string;
        }

        var result = new QuestInjectResult([], []);
        foreach (var q in quests)
        {
            var zone = Area(q.Zone) ?? Area(q.At);
            var place = Area(q.At) ?? zone;
            var giverKind = q.GiverKind is "npc" or "object" && q.GiverId > 0 ? q.GiverKind : "";

            if (Stored(q.Id) is { } stored)
            {
                if (!ours.Contains(q.Id)) continue;
                var rewards = q.Rewards.Distinct().Order().Select(i => "quest:" + i)
                    .Concat(q.Choices.Distinct().Order().Select(i => "quest_choice:" + i));
                var wanted = $"{q.Title}|{q.Level}|{zone?.Id ?? 0}|{q.GiverName}|{giverKind}|{q.GiverId}|{string.Join(",", rewards)}";
                if (wanted == stored) continue;
                Run("DELETE FROM quests WHERE id = $id", ("$id", q.Id));
                Run("DELETE FROM item_sources WHERE kind IN ('quest', 'quest_choice') AND ref_id = $id", ("$id", q.Id));
                result.Updated.Add(q.Title);
            }
            else result.Added.Add(q.Title);
            ours.Add(q.Id);
            var hasSpot = q.At.Length > 0 && (q.X > 0 || q.Y > 0);

            if (q.GiverKind == "npc" && q.GiverId > 0)
            {
                if (Scalar("SELECT 1 FROM npcs WHERE id = $id", ("$id", q.GiverId)) is null)
                    Run("INSERT INTO npcs (id, name, subname, min_level, max_level, rank, map, zone, zone_x, zone_y) VALUES ($id, $n, '', 0, 0, 0, $m, $z, $x, $y)",
                        ("$id", q.GiverId), ("$n", q.GiverName), ("$m", place?.Map ?? -1),
                        ("$z", hasSpot ? q.At : ""), ("$x", hasSpot ? q.X : 0.0), ("$y", hasSpot ? q.Y : 0.0));
                else if (hasSpot)
                    Run("UPDATE npcs SET zone = $z, zone_x = $x, zone_y = $y WHERE id = $id AND zone = ''",
                        ("$id", q.GiverId), ("$z", q.At), ("$x", q.X), ("$y", q.Y));
            }
            else if (q.GiverKind == "object" && q.GiverId > 0)
            {
                if (Scalar("SELECT 1 FROM objects WHERE id = $id", ("$id", q.GiverId)) is null)
                    Run("INSERT INTO objects (id, name, type, map, zone, zone_x, zone_y) VALUES ($id, $n, 2, $m, $z, $x, $y)",
                        ("$id", q.GiverId), ("$n", q.GiverName), ("$m", place?.Map ?? -1),
                        ("$z", hasSpot ? q.At : ""), ("$x", hasSpot ? q.X : 0.0), ("$y", hasSpot ? q.Y : 0.0));
                else if (hasSpot)
                    Run("UPDATE objects SET zone = $z, zone_x = $x, zone_y = $y WHERE id = $id AND zone = ''",
                        ("$id", q.GiverId), ("$z", q.At), ("$x", q.X), ("$y", q.Y));
            }

            Run("INSERT INTO quests (id, title, level, min_level, zone, giver, giver_kind, giver_id) VALUES ($id, $t, $l, 0, $z, $g, $k, $gid)",
                ("$id", q.Id), ("$t", q.Title), ("$l", q.Level), ("$z", zone?.Id ?? 0), ("$g", q.GiverName),
                ("$k", giverKind), ("$gid", q.GiverId));
            foreach (var item in q.Rewards.Distinct())
                Run("INSERT INTO item_sources (item_id, kind, ref_id, chance) VALUES ($i, 'quest', $q, 100)", ("$i", item), ("$q", q.Id));
            foreach (var item in q.Choices.Distinct())
                Run("INSERT INTO item_sources (item_id, kind, ref_id, chance) VALUES ($i, 'quest_choice', $q, 100)", ("$i", item), ("$q", q.Id));
        }
        Run("INSERT OR REPLACE INTO meta (key, value) VALUES ('learned_quests', $v)",
            ("$v", string.Join(",", ours.Order())));
        transaction.Commit();
        return result;
    }
}

/// <summary>Titles of the quests added to, and refreshed in, the game data.</summary>
public sealed record QuestInjectResult(List<string> Added, List<string> Updated);
