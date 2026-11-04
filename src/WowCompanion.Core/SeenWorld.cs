using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;

namespace WowCompanion.Core;

/// <summary>
/// NPCs, vendors and loot the WoWOracle addon saw in game that the downloaded data lacks (the addon only saves
/// what isn't in KnownIds). One "!WOWORACLE-NPC/VENDOR/LOOT 1 … !END" block per subject.
/// </summary>
public sealed record SeenBlock(string Kind, string Key, IReadOnlyDictionary<string, string> Values, IReadOnlyList<string> Items, string Text)
{
    public static readonly string[] Markers = ["!WOWORACLE-NPC", "!WOWORACLE-VENDOR", "!WOWORACLE-LOOT"];

    public static SeenBlock? Parse(string text)
    {
        var first = text.Split('\n', 2)[0].Trim();
        var kind = first.StartsWith("!WOWORACLE-NPC", StringComparison.Ordinal) ? "npc"
            : first.StartsWith("!WOWORACLE-VENDOR", StringComparison.Ordinal) ? "vendor"
            : first.StartsWith("!WOWORACLE-LOOT", StringComparison.Ordinal) ? "loot" : null;
        if (kind is null) return null;
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var items = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim('\r', ' ');
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            if (line[..eq] == "item") items.Add(line[(eq + 1)..]);
            else values[line[..eq]] = line[(eq + 1)..].Trim();
        }
        string key;
        if (kind == "loot")
        {
            var source = values.GetValueOrDefault("source", "").Split(';');
            if (source.Length < 2 || source[0] is not ("npc" or "object") || Int(source[1]) <= 0) return null;
            key = $"loot:{source[0]}:{Int(source[1])}";
        }
        else
        {
            var id = Int(values.GetValueOrDefault("id", ""));
            if (id <= 0) return null;
            key = $"{kind}:{id}";
        }
        return new SeenBlock(kind, key, values, items, text.Trim());
    }

    public string Get(string key) => Values.GetValueOrDefault(key, "");
    public int GetInt(string key) => Int(Get(key));
    public long Time => long.TryParse(Get("time"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var t) ? t : 0;

    /// <summary>Zone, x, y from "at=Zone;x;y".</summary>
    public (string Zone, double X, double Y) At
    {
        get
        {
            var at = Get("at").Split(';');
            return (at[0].Trim(), at.Length > 1 ? Real(at[1]) : 0, at.Length > 2 ? Real(at[2]) : 0);
        }
    }

    /// <summary>The better of two captures of the same subject: loot with more kills, otherwise the newer one.</summary>
    public SeenBlock Over(SeenBlock older) =>
        Kind == "loot" ? (GetInt("kills") >= older.GetInt("kills") ? this : older)
        : Time >= older.Time ? this : older;

    internal static int Int(string s) =>
        int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
    internal static double Real(string s) =>
        double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
}

/// <summary>What was added to the game data from the addon's sightings.</summary>
public sealed record SeenInjectResult(int Npcs, int Placed, int Vendors, int VendorItems, int LootSources, int LootItems)
{
    public bool Any => Npcs + Placed + VendorItems + LootItems > 0;
}

/// <summary>
/// Sightings kept in user.sqlite (the addon's list can be cleared) and added to the game database where it has
/// gaps: new NPCs and objects (with where they were seen), vendor items, drops with chances from kill counts.
/// Downloaded rows always win; only rows this store added (meta 'seen_rows') are ever replaced.
/// </summary>
public sealed class SeenWorldStore(string userDatabasePath)
{
    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = userDatabasePath,
        Pooling = false,
    }.ToString();

    /// <summary>Saves new or better captures. Returns how many subjects were new to the store.</summary>
    public int Save(IEnumerable<SeenBlock> blocks)
    {
        var known = All().ToDictionary(b => b.Key);
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO seen_data (key, kind, data, updated_utc) VALUES ($key, $kind, $data, $now)
            ON CONFLICT(key) DO UPDATE SET data = $data, updated_utc = $now
            """;
        var key = command.Parameters.Add("$key", SqliteType.Text);
        var kind = command.Parameters.Add("$kind", SqliteType.Text);
        var data = command.Parameters.Add("$data", SqliteType.Text);
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
        var added = 0;
        foreach (var block in blocks)
        {
            var merged = known.TryGetValue(block.Key, out var old) ? block.Over(old) : block;
            if (old is not null && old.Text == merged.Text) continue;
            if (old is null) added++;
            known[block.Key] = merged;
            key.Value = merged.Key;
            kind.Value = merged.Kind;
            data.Value = merged.Text;
            command.ExecuteNonQuery();
        }
        transaction.Commit();
        return added;
    }

    public List<SeenBlock> All()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT data FROM seen_data";
        var list = new List<SeenBlock>();
        using var r = command.ExecuteReader();
        while (r.Read())
            if (SeenBlock.Parse(r.GetString(0)) is { } block) list.Add(block);
        return list;
    }

    // UnitClassification → the CMaNGOS rank numbers the npcs table uses.
    private static int Rank(string classification) => classification switch
    {
        "elite" => 1,
        "rareelite" => 2,
        "worldboss" => 3,
        "rare" => 4,
        _ => 0,
    };

    public static SeenInjectResult InjectInto(string gameDatabasePath, IReadOnlyCollection<SeenBlock> blocks)
    {
        if (blocks.Count == 0) return new SeenInjectResult(0, 0, 0, 0, 0, 0);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = gameDatabasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        connection.Open();
        using var transaction = connection.BeginTransaction();

        object? Scalar(string sql, params (string Name, object Value)[] args)
        {
            using var c = connection.CreateCommand();
            c.Transaction = transaction;
            c.CommandText = sql;
            foreach (var (name, value) in args) c.Parameters.AddWithValue(name, value);
            return c.ExecuteScalar();
        }
        int Run(string sql, params (string Name, object Value)[] args)
        {
            using var c = connection.CreateCommand();
            c.Transaction = transaction;
            c.CommandText = sql;
            foreach (var (name, value) in args) c.Parameters.AddWithValue(name, value);
            return c.ExecuteNonQuery();
        }
        int MapOf(string zone) =>
            zone.Length == 0 ? -1
            : Scalar("SELECT map FROM areas WHERE name = $n COLLATE NOCASE ORDER BY (map < 0), id LIMIT 1", ("$n", zone)) is long m ? (int)m : -1;

        // Item-source rows this store added before: replaced on every run, so kill counts stay current.
        var ours = (Scalar("SELECT value FROM meta WHERE key = 'seen_rows'") as string ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
        foreach (var row in ours)
        {
            var p = row.Split(':');
            if (p.Length != 3) continue;
            Run("DELETE FROM item_sources WHERE kind = $k AND ref_id = $r AND item_id = $i",
                ("$k", p[0]), ("$r", SeenBlock.Int(p[1])), ("$i", SeenBlock.Int(p[2])));
        }
        var before = ours.ToHashSet();
        var rows = new List<string>();
        // True when the row is new (not just re-added from an earlier run).
        bool AddSource(int item, string kind, int refId, double chance)
        {
            if (item <= 0) return false;
            if (Scalar("SELECT 1 FROM item_sources WHERE kind = $k AND ref_id = $r AND item_id = $i",
                    ("$k", kind), ("$r", refId), ("$i", item)) is not null) return false; // downloaded data has it
            Run("INSERT INTO item_sources (item_id, kind, ref_id, chance) VALUES ($i, $k, $r, $c)",
                ("$i", item), ("$k", kind), ("$r", refId), ("$c", Math.Round(chance, 2)));
            var key = $"{kind}:{refId}:{item}";
            rows.Add(key);
            return !before.Contains(key);
        }

        int npcs = 0, placed = 0;
        // A new NPC (or object), or a spot for one the data has without a place. Never changes a downloaded spot.
        void Ensure(string kind, int id, string name, int level, int rank, string zone, double x, double y)
        {
            var hasSpot = zone.Length > 0 && (x > 0 || y > 0);
            var table = kind == "object" ? "objects" : "npcs";
            if (Scalar($"SELECT 1 FROM {table} WHERE id = $id", ("$id", id)) is null)
            {
                if (kind == "object")
                    Run("INSERT INTO objects (id, name, type, map, zone, zone_x, zone_y) VALUES ($id, $n, 3, $m, $z, $x, $y)",
                        ("$id", id), ("$n", name), ("$m", MapOf(zone)), ("$z", hasSpot ? zone : ""), ("$x", hasSpot ? x : 0.0), ("$y", hasSpot ? y : 0.0));
                else
                    Run("INSERT INTO npcs (id, name, subname, min_level, max_level, rank, map, zone, zone_x, zone_y) VALUES ($id, $n, '', $l, $l, $r, $m, $z, $x, $y)",
                        ("$id", id), ("$n", name), ("$l", Math.Max(level, 0)), ("$r", rank), ("$m", MapOf(zone)),
                        ("$z", hasSpot ? zone : ""), ("$x", hasSpot ? x : 0.0), ("$y", hasSpot ? y : 0.0));
                npcs++;
                return;
            }
            if (name.Length > 0)
                Run($"UPDATE {table} SET name = $n WHERE id = $id AND name = ''", ("$id", id), ("$n", name));
            if (kind == "npc" && level > 0)
                Run("UPDATE npcs SET min_level = $l, max_level = $l WHERE id = $id AND max_level = 0", ("$id", id), ("$l", level));
            if (hasSpot && Run($"UPDATE {table} SET zone = $z, zone_x = $x, zone_y = $y, map = CASE WHEN map < 0 THEN $m ELSE map END WHERE id = $id AND zone = ''",
                    ("$id", id), ("$z", zone), ("$x", x), ("$y", y), ("$m", MapOf(zone))) > 0)
                placed++;
        }

        int vendors = 0, vendorItems = 0, lootSources = 0, lootItems = 0;
        foreach (var b in blocks.Where(b => b.Kind == "npc"))
        {
            var (zone, x, y) = b.At;
            Ensure("npc", b.GetInt("id"), b.Get("name"), b.GetInt("level"), Rank(b.Get("rank")), zone, x, y);
        }
        foreach (var b in blocks.Where(b => b.Kind == "vendor"))
        {
            var (zone, x, y) = b.At;
            var id = b.GetInt("id");
            Ensure("npc", id, b.Get("name"), 0, 0, zone, x, y);
            var added = b.Items.Select(i => SeenBlock.Int(i.Split(';')[0])).Distinct().Count(item => AddSource(item, "vendor", id, 100));
            if (added > 0) { vendors++; vendorItems += added; }
        }
        foreach (var b in blocks.Where(b => b.Kind == "loot"))
        {
            var source = b.Get("source").Split(';', 3);
            var kind = source[0];
            var id = SeenBlock.Int(source[1]);
            var kills = Math.Max(b.GetInt("kills"), 1);
            var (zone, x, y) = b.At;
            Ensure(kind, id, source.Length > 2 ? source[2].Trim() : "", 0, 0, zone, x, y); // you stand next to what you loot
            var added = 0;
            foreach (var line in b.Items)
            {
                var p = line.Split(';');
                var drops = p.Length > 1 ? SeenBlock.Int(p[1]) : 1;
                if (AddSource(SeenBlock.Int(p[0]), kind == "object" ? "object" : "drop", id, Math.Min(100.0, 100.0 * drops / kills))) added++;
            }
            if (added > 0) { lootSources++; lootItems += added; }
        }

        Run("INSERT OR REPLACE INTO meta (key, value) VALUES ('seen_rows', $v)", ("$v", string.Join(",", rows)));
        transaction.Commit();
        return new SeenInjectResult(npcs, placed, vendors, vendorItems, lootSources, lootItems);
    }
}

/// <summary>
/// The ids the game data already has, written as Known.lua into the installed addon, so the addon saves only what's
/// new: quests and NPCs it doesn't know, vendors it doesn't know, loot it has no source for.
/// </summary>
public static class KnownIds
{
    public const string FileName = "Known.lua";

    public static string Build(string gameDatabasePath)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = gameDatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();

        List<int> Ids(string sql)
        {
            using var c = connection.CreateCommand();
            c.CommandText = sql;
            var list = new List<int>();
            using var r = c.ExecuteReader();
            while (r.Read()) list.Add(r.GetInt32(0));
            return list;
        }
        string Meta(string key)
        {
            using var c = connection.CreateCommand();
            c.CommandText = "SELECT value FROM meta WHERE key = $k";
            c.Parameters.AddWithValue("$k", key);
            return c.ExecuteScalar() as string ?? "";
        }

        // Quests the addon reported stay "new", so it keeps filling in their rewards when you hand them in.
        var learnedQuests = Meta("learned_quests").Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(SeenBlock.Int).ToHashSet();
        var lists = new (string Name, List<int> Ids)[]
        {
            ("npcs", Ids("SELECT id FROM npcs")),
            ("placed", Ids("SELECT id FROM npcs WHERE zone <> '' AND max_level > 0")),
            ("objects", Ids("SELECT id FROM objects")),
            ("quests", Ids("SELECT id FROM quests").Where(id => !learnedQuests.Contains(id)).ToList()),
            ("vendors", Ids("SELECT DISTINCT ref_id FROM item_sources WHERE kind = 'vendor'")),
            ("sourced", Ids("SELECT DISTINCT item_id FROM item_sources")),
        };

        var b = new StringBuilder();
        b.Append("-- Written by the WoW Oracle app: the ids its game data already has, so the addon only saves what's new.\n");
        b.Append("-- Rewritten whenever the app's data changes; don't edit.\n");
        b.Append("WoWOracleKnown = {\n");
        b.Append("    build = \"").Append(Meta("build_version")).Append("\",\n");
        foreach (var (name, ids) in lists) b.Append("    ").Append(name).Append(" = \"").Append(Ranges(ids)).Append("\",\n");
        b.Append("}\n");
        return b.ToString();
    }

    /// <summary>Sorted ids as "1-5,9,12-14".</summary>
    public static string Ranges(IEnumerable<int> ids)
    {
        var sorted = ids.Where(i => i > 0).Distinct().Order().ToList();
        var b = new StringBuilder();
        for (var i = 0; i < sorted.Count;)
        {
            var start = sorted[i];
            var end = start;
            while (i + 1 < sorted.Count && sorted[i + 1] == end + 1) { i++; end++; }
            i++;
            if (b.Length > 0) b.Append(',');
            b.Append(start.ToString(CultureInfo.InvariantCulture));
            if (end > start) b.Append('-').Append(end.ToString(CultureInfo.InvariantCulture));
        }
        return b.ToString();
    }

    /// <summary>Writes the list into an addon folder when it differs. True when the file changed.</summary>
    public static bool WriteTo(string addonFolder, string content)
    {
        var path = Path.Combine(addonFolder, FileName);
        if (!Directory.Exists(addonFolder)) return false;
        if (File.Exists(path) && File.ReadAllText(path) == content) return false;
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return true;
    }
}
