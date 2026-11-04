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
}
