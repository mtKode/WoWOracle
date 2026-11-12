using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace WowCompanion.Core;

/// <summary>A zone map: its picture size and the tiles (game file ids) it is made of.</summary>
public sealed record ZoneMap(int UiMapId, string Name, int Type, int ParentId, string Continent, int Width, int Height,
    int TileWidth, int TileHeight, IReadOnlyList<MapTile> Tiles)
{
    /// <summary>Explored areas, drawn over the base picture (all of them: the map shows fully discovered).</summary>
    public IReadOnlyList<MapOverlay> Overlays { get; init; } = [];
}

/// <summary>An explored area: its tiles (256 px, row/col) placed at the offset on the map picture.</summary>
public sealed record MapOverlay(int Id, int OffsetX, int OffsetY, int Width, int Height, IReadOnlyList<MapTile> Tiles);

public sealed record MapTile(int Row, int Col, int FileId);

/// <summary>A spot on a zone map, in map coordinates (0-100, like the game's).</summary>
/// <param name="Label">What is there ("Vendor: Kirsta Deepshadow"), from the text before the place.</param>
public sealed record MapSpot(string Zone, double X, double Y, string Label = "");

/// <summary>Reads places written as "Westfall (56.4, 47.6)" anywhere in a text.</summary>
public static partial class MapPlace
{
    [GeneratedRegex(@"([A-Z][A-Za-z' \-]*?) \((\d{1,3}(?:\.\d+)?), (\d{1,3}(?:\.\d+)?)\)")]
    private static partial Regex Pattern();

    public static MapSpot? Find(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var m = Pattern().Match(text);
        if (!m.Success) return null;
        var zone = m.Groups[1].Value.Trim();
        var label = text[..m.Index].Trim().TrimEnd('·', ',', '-', ' ').Trim();
        if (label.StartsWith("Begins with ", StringComparison.Ordinal)) label = label["Begins with ".Length..];
        return new MapSpot(zone, double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
            double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture), label);
    }
}

/// <summary>The zone maps the game data has a picture for (game data schema 19: map_art, map_tiles).</summary>
public sealed class MapService(string databasePath)
{
    private List<ZoneMap>? _maps;

    public IReadOnlyList<ZoneMap> Zones()
    {
        if (_maps is not null) return _maps;
        var maps = new List<ZoneMap>();
        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString());
            connection.Open();

            var tiles = new Dictionary<int, List<MapTile>>();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT ui_map, row, col, file_id FROM map_tiles";
                using var r = command.ExecuteReader();
                while (r.Read())
                {
                    if (!tiles.TryGetValue(r.GetInt32(0), out var list)) tiles[r.GetInt32(0)] = list = [];
                    list.Add(new MapTile(r.GetInt32(1), r.GetInt32(2), r.GetInt32(3)));
                }
            }

            // Explored areas (game data schema 20; older data simply has none).
            var overlays = new Dictionary<int, List<MapOverlay>>();
            try
            {
                var pieces = new Dictionary<int, List<MapTile>>();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT overlay_id, row, col, file_id FROM map_overlay_tiles";
                    using var r = command.ExecuteReader();
                    while (r.Read())
                    {
                        if (!pieces.TryGetValue(r.GetInt32(0), out var list)) pieces[r.GetInt32(0)] = list = [];
                        list.Add(new MapTile(r.GetInt32(1), r.GetInt32(2), r.GetInt32(3)));
                    }
                }
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT id, ui_map, offset_x, offset_y, width, height FROM map_overlays ORDER BY id";
                    using var r = command.ExecuteReader();
                    while (r.Read())
                    {
                        if (!overlays.TryGetValue(r.GetInt32(1), out var list)) overlays[r.GetInt32(1)] = list = [];
                        list.Add(new MapOverlay(r.GetInt32(0), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4), r.GetInt32(5),
                            pieces.GetValueOrDefault(r.GetInt32(0)) ?? []));
                    }
                }
            }
            catch (SqliteException)
            {
            }

            var rows = new List<(int Id, string Name, int Type, int Parent, int W, int H, int TW, int TH)>();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT ui_map, name, type, parent, width, height, tile_width, tile_height FROM map_art";
                using var r = command.ExecuteReader();
                while (r.Read())
                    rows.Add((r.GetInt32(0), r.GetString(1), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4), r.GetInt32(5),
                        r.GetInt32(6), r.GetInt32(7)));
            }
            var names = rows.ToDictionary(r => r.Id, r => (r.Name, r.Type, r.Parent));
            string ContinentOf(int parent)
            {
                for (var i = 0; i < 6 && names.TryGetValue(parent, out var p); i++)
                {
                    if (p.Type == 2) return p.Name;
                    parent = p.Parent;
                }
                return "Other";
            }
            foreach (var r in rows)
                maps.Add(new ZoneMap(r.Id, r.Name, r.Type, r.Parent, r.Type <= 2 ? "World" : ContinentOf(r.Parent), r.W, r.H, r.TW, r.TH,
                    tiles.GetValueOrDefault(r.Id) ?? [])
                {
                    Overlays = overlays.GetValueOrDefault(r.Id) ?? [],
                });
        }
        catch (SqliteException)
        {
            // Older game data without maps.
        }
        _maps = maps
            .OrderBy(m => m.Continent == "World" ? 0 : 1).ThenBy(m => m.Continent).ThenBy(m => m.Type == 2 ? 0 : 1).ThenBy(m => m.Name)
            .ThenBy(m => m.UiMapId)
            .ToList();
        return _maps;
    }

    public ZoneMap? Find(string zone) =>
        Zones().Where(m => string.Equals(m.Name, zone, StringComparison.OrdinalIgnoreCase))
            .OrderBy(m => m.Type == 3 ? 0 : 1).ThenBy(m => m.UiMapId).FirstOrDefault();

    /// <summary>
    /// The Map page's search: zones, subzones, towns and flight masters (map_places, game data schema 29) and NPCs
    /// with a known spot, whose name contains the text. Names starting with it first, then zones before places
    /// before NPCs. Only places on a zone map the app has.
    /// </summary>
    public IReadOnlyList<MapSearchHit> Search(string text, int limit = 30)
    {
        text = text.Trim();
        if (text.Length < 2) return [];
        var zones = Zones().Where(m => m.Type == 3).Select(m => m.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var hits = new List<MapSearchHit>();
        foreach (var z in zones.Where(z => z.Contains(text, StringComparison.OrdinalIgnoreCase)))
            hits.Add(new MapSearchHit(z, "zone", z, -1, -1, ""));

        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString());
            connection.Open();
            var like = "%" + text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            try
            {
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT name, kind, zone, x, y FROM map_places WHERE name LIKE $q ESCAPE '\\' LIMIT 200";
                command.Parameters.AddWithValue("$q", like);
                using var r = command.ExecuteReader();
                while (r.Read())
                    if (zones.Contains(r.GetString(2)))
                        hits.Add(new MapSearchHit(r.GetString(0), r.GetString(1), r.GetString(2), r.GetDouble(3), r.GetDouble(4), ""));
            }
            catch (SqliteException) { } // game data before schema 29: no places yet

            using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    SELECT name, subname, min_level, max_level, zone, zone_x, zone_y FROM npcs
                    WHERE name LIKE $q ESCAPE '\' AND zone <> '' AND zone_x > 0
                    GROUP BY name, zone LIMIT 200
                    """;
                command.Parameters.AddWithValue("$q", like);
                using var r = command.ExecuteReader();
                while (r.Read())
                {
                    if (!zones.Contains(r.GetString(4))) continue;
                    var (min, max) = (r.GetInt32(2), r.GetInt32(3));
                    var detail = new List<string>();
                    // The Classic data writes a missing title as the text "NULL".
                    if (!r.IsDBNull(1) && r.GetString(1) is { Length: > 0 } title && title != "NULL") detail.Add(title);
                    if (max > 0) detail.Add(min == max ? $"level {max}" : $"level {min}-{max}");
                    hits.Add(new MapSearchHit(r.GetString(0), "npc", r.GetString(4), r.GetDouble(5), r.GetDouble(6), string.Join(", ", detail)));
                }
            }

            // A subzone the game data can't place (new in WoW Forever: Bandarion Keep) often has NPCs named after it
            // ("Bandarion Keep Paladin"): their spot stands in for the subzone's.
            for (var i = 0; i < hits.Count; i++)
            {
                var hit = hits[i];
                if (hit.Kind != "subzone" || hit.HasSpot) continue;
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT name, zone_x, zone_y FROM npcs
                    WHERE zone = $z AND zone_x > 0 AND name LIKE $n ESCAPE '\'
                    ORDER BY length(name) LIMIT 1
                    """;
                command.Parameters.AddWithValue("$z", hit.Zone);
                command.Parameters.AddWithValue("$n", hit.Name.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + " %");
                using var r = command.ExecuteReader();
                if (r.Read()) hits[i] = hit with { X = r.GetDouble(1), Y = r.GetDouble(2), Detail = $"near {r.GetString(0)}" };
            }
        }
        catch (SqliteException) { }

        static int Rank(string kind) => kind switch { "zone" => 0, "subzone" => 1, "place" or "flight" => 2, _ => 3 };
        return hits
            .OrderBy(h => h.Name.StartsWith(text, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(h => Rank(h.Kind)).ThenBy(h => h.Name).ThenBy(h => h.Zone)
            .Take(limit).ToList();
    }
}

/// <summary>One answer of the map search. X and Y are map coordinates (0-100), -1 when only the zone is known.</summary>
/// <param name="Kind">zone, subzone, place (town, landmark), flight (flight master) or npc.</param>
public sealed record MapSearchHit(string Name, string Kind, string Zone, double X, double Y, string Detail)
{
    public bool HasSpot => X >= 0 && Y >= 0;

    /// <summary>"Subzone · Thousand Needles", "Flight master · Thousand Needles", "NPC · Innkeeper, level 30 · Ironforge".</summary>
    public string Meta
    {
        get
        {
            var kind = Kind switch
            {
                "zone" => "Zone",
                "subzone" => "Subzone",
                "place" => "Place",
                "flight" => "Flight master",
                _ => Detail.Length > 0 ? $"NPC · {Detail}" : "NPC",
            };
            if (Kind == "zone") return kind;
            var where = !HasSpot ? " (whole zone)" : Kind != "npc" && Detail.Length > 0 ? $" ({Detail})" : "";
            return $"{kind} · {Zone}{where}";
        }
    }
}
