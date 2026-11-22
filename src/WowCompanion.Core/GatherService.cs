using Microsoft.Data.Sqlite;

namespace WowCompanion.Core;

/// <summary>An ore vein or herb (by name: several node types share one), with the skill it needs and its spots.</summary>
/// <param name="Kind">"mining" or "herbalism".</param>
/// <param name="Skill">Skill needed to gather it; 0 for quest plants anyone can pick, -1 when not known (a WoW Forever
/// node the addon saw you gather, not in the Classic data).</param>
public sealed record GatherNode(string Name, string Kind, int Skill, int Spots)
{
    public string Profession => Kind == "mining" ? "Mining" : "Herbalism";

    /// <summary>Veins that spawn now and then on the common ones' spots (the data lists them on every spot they can take).</summary>
    public bool IsRare => GatherService.RareNodes.Contains(Name);

    public string SkillText => Skill switch
    {
        > 0 => $"{Profession} {Skill}",
        0 => "anyone can gather it (quest)",
        _ => $"{Profession}, skill not known yet",
    };
}

/// <summary>How many spots a node has in a zone.</summary>
public sealed record GatherZone(string Zone, int Spots);

/// <summary>A row of the gathering guide: what to gather from a skill on, and the zones with the most of it.</summary>
public sealed record GatherStep(int Skill, GatherNode Node, IReadOnlyList<GatherZone> BestZones)
{
    public string SkillText => Node.Skill > 0 ? Node.Skill.ToString(System.Globalization.CultureInfo.InvariantCulture) : "?";
    public string Meta => (Node.IsRare ? "rare · " : "") + (BestZones.Count == 0 ? "no spots known" : string.Join(", ", BestZones.Select(z => z.Zone)));
}

/// <summary>
/// Ore and herb nodes (game data schema 30: gather_nodes, gather_spawns from the Classic world data, plus what the addon
/// saw you gather). Every spot a node can spawn, in zone coordinates.
/// </summary>
public sealed class GatherService(string databasePath)
{
    /// <summary>
    /// Rare veins: in Classic they share their spots with the common ore of the area and spawn instead of it now and then.
    /// The data lists them on every such spot, so their spot counts say where to look, not how many there are.
    /// </summary>
    public static readonly IReadOnlySet<string> RareNodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Silver Vein", "Gold Vein", "Truesilver Deposit", "Ooze Covered Silver Vein", "Ooze Covered Gold Vein",
        "Ooze Covered Truesilver Deposit", "Black Lotus",
    };

    private List<GatherNode>? _nodes;

    /// <summary>Every node name with spots, cheapest skill first.</summary>
    public IReadOnlyList<GatherNode> Nodes()
    {
        if (_nodes is not null) return _nodes;
        var list = new List<GatherNode>();
        try
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT n.name, n.kind, MIN(n.skill), COUNT(*)
                FROM gather_nodes n JOIN gather_spawns s ON s.node_id = n.id
                GROUP BY n.name, n.kind
                """;
            using var r = command.ExecuteReader();
            while (r.Read()) list.Add(new GatherNode(r.GetString(0), r.GetString(1), r.GetInt32(2), r.GetInt32(3)));
        }
        catch (SqliteException)
        {
            // game data before schema 30
        }
        _nodes = list.OrderBy(n => n.Kind).ThenBy(n => n.Skill <= 0 ? int.MaxValue - 1 + n.Skill : n.Skill).ThenBy(n => n.IsRare).ThenBy(n => n.Name).ToList();
        return _nodes;
    }

    public GatherNode? Find(string name) =>
        Nodes().FirstOrDefault(n => string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Spots (x, y in 0-100) of a node in a zone. Spots closer than 0.3 are one spot.</summary>
    public IReadOnlyList<(double X, double Y)> Spots(string name, string zone)
    {
        var spots = new List<(double X, double Y)>();
        try
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT DISTINCT s.x, s.y FROM gather_spawns s JOIN gather_nodes n ON n.id = s.node_id
                WHERE n.name = $n AND s.zone = $z COLLATE NOCASE
                """;
            command.Parameters.AddWithValue("$n", name);
            command.Parameters.AddWithValue("$z", zone);
            using var r = command.ExecuteReader();
            while (r.Read())
            {
                var (x, y) = (r.GetDouble(0), r.GetDouble(1));
                if (!spots.Any(s => Math.Abs(s.X - x) < 0.3 && Math.Abs(s.Y - y) < 0.3)) spots.Add((x, y));
            }
        }
        catch (SqliteException) { }
        return spots;
    }

    /// <summary>Zones with spots of a node, most first.</summary>
    public IReadOnlyList<GatherZone> Zones(string name, int limit = 30) => Query(
        """
        SELECT s.zone, COUNT(DISTINCT s.x || ',' || s.y) AS spots FROM gather_spawns s JOIN gather_nodes n ON n.id = s.node_id
        WHERE n.name = $n GROUP BY s.zone ORDER BY spots DESC, s.zone LIMIT $limit
        """, name, limit);

    /// <summary>
    /// The guide for Mining or Herbalism: one row per node, cheapest first, with the zones that have the most spots.
    /// You gather a node until it turns grey (about 100 skill above what it needs); move on when the next one opens.
    /// </summary>
    public IReadOnlyList<GatherStep> Guide(string kind, int zonesPerStep = 3) =>
        // Quest plants (skill 0) and the Ooze Covered veins of a few caves are left out; Forever nodes (skill unknown) stay.
        Nodes().Where(n => n.Kind == kind && n.Skill != 0 && (n.Skill < 0 || n.Spots >= 10)
                           && !n.Name.StartsWith("Ooze Covered", StringComparison.Ordinal))
            .Select(n => new GatherStep(n.Skill, n, Zones(n.Name, zonesPerStep)))
            .ToList();

    /// <summary>Node names containing the text (map search).</summary>
    public IReadOnlyList<GatherNode> Search(string text, int limit = 6)
    {
        var t = text.Trim();
        if (t.Length < 2) return [];
        return Nodes().Where(n => n.Name.Contains(t, StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n.Name.StartsWith(t, StringComparison.OrdinalIgnoreCase) ? 0 : 1).ThenBy(n => n.IsRare).ThenBy(n => n.Skill)
            .Take(limit).ToList();
    }

    private List<GatherZone> Query(string sql, string name, int limit)
    {
        var list = new List<GatherZone>();
        try
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("$n", name);
            command.Parameters.AddWithValue("$limit", limit);
            using var r = command.ExecuteReader();
            while (r.Read()) list.Add(new GatherZone(r.GetString(0), r.GetInt32(1)));
        }
        catch (SqliteException) { }
        return list;
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }
}
