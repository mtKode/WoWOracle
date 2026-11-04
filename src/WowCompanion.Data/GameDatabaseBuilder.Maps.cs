using Microsoft.Data.Sqlite;

namespace WowCompanion.Data;

/// <summary>
/// The in-game zone maps: which tiles (BLP files, by file id) make up each UiMap's picture. The pictures themselves
/// are downloaded by the app when a map is opened (wago.tools file API) and cached.
/// UiMap → UiMapXMapArt → UiMapArt (style) → UiMapArtStyleLayer (size, tile size) and UiMapArtTile (row, col, file).
/// </summary>
public static partial class GameDatabaseBuilder
{
    private const string MapSchema = """

        CREATE TABLE map_art (
            ui_map INTEGER PRIMARY KEY,
            name TEXT NOT NULL,
            type INTEGER NOT NULL,        -- 1 world, 2 continent, 3 zone, 4 dungeon, 5 micro, 6 orphan
            parent INTEGER NOT NULL,
            width INTEGER NOT NULL,       -- picture size in pixels (the tiles are cropped to it)
            height INTEGER NOT NULL,
            tile_width INTEGER NOT NULL,
            tile_height INTEGER NOT NULL
        );
        CREATE TABLE map_tiles (
            ui_map INTEGER NOT NULL,
            row INTEGER NOT NULL,
            col INTEGER NOT NULL,
            file_id INTEGER NOT NULL,
            PRIMARY KEY (ui_map, row, col)
        );
        -- Explored areas: drawn over the base picture, all of them, so the map shows fully discovered.
        CREATE TABLE map_overlays (
            id INTEGER PRIMARY KEY,
            ui_map INTEGER NOT NULL,
            offset_x INTEGER NOT NULL,
            offset_y INTEGER NOT NULL,
            width INTEGER NOT NULL,
            height INTEGER NOT NULL
        );
        CREATE INDEX map_overlays_map ON map_overlays (ui_map);
        CREATE TABLE map_overlay_tiles (
            overlay_id INTEGER NOT NULL,
            row INTEGER NOT NULL,
            col INTEGER NOT NULL,
            file_id INTEGER NOT NULL,
            PRIMARY KEY (overlay_id, row, col)
        );
        """;

    /// <returns>Number of maps with a picture (0 when the tables weren't downloaded).</returns>
    private static int BuildMaps(SqliteConnection connection, SqliteTransaction transaction, Func<string, string> csv)
    {
        string[] needed = ["UiMap", "UiMapXMapArt", "UiMapArt", "UiMapArtStyleLayer", "UiMapArtTile"];
        if (needed.Any(t => !File.Exists(csv(t)))) return 0;

        var styles = new Dictionary<int, (int W, int H, int TileW, int TileH)>();
        foreach (var row in CsvTable.Read(csv("UiMapArtStyleLayer")))
            if (row.GetInt("LayerIndex") == 0)
                styles[row.GetInt("UiMapArtStyleID")] = (row.GetInt("LayerWidth"), row.GetInt("LayerHeight"),
                    Math.Max(1, row.GetInt("TileWidth")), Math.Max(1, row.GetInt("TileHeight")));

        var artStyle = CsvTable.Read(csv("UiMapArt")).ToDictionary(r => r.GetInt("ID"), r => r.GetInt("UiMapArtStyleID"));

        // One picture per map: the default phase first.
        var artFor = new Dictionary<int, int>();
        foreach (var row in CsvTable.Read(csv("UiMapXMapArt")).OrderBy(r => r.GetInt("PhaseID")))
            artFor.TryAdd(row.GetInt("UiMapID"), row.GetInt("UiMapArtID"));

        var tiles = CsvTable.Read(csv("UiMapArtTile"))
            .Where(r => r.GetInt("LayerIndex") == 0)
            .GroupBy(r => r.GetInt("UiMapArtID"))
            .ToDictionary(g => g.Key, g => g.Select(r => (Row: r.GetInt("RowIndex"), Col: r.GetInt("ColIndex"), File: r.GetInt("FileDataID"))).ToList());

        using var insertMap = connection.CreateCommand();
        insertMap.Transaction = transaction;
        insertMap.CommandText = """
            INSERT OR REPLACE INTO map_art (ui_map, name, type, parent, width, height, tile_width, tile_height)
            VALUES ($id, $name, $type, $parent, $w, $h, $tw, $th)
            """;
        using var insertTile = connection.CreateCommand();
        insertTile.Transaction = transaction;
        insertTile.CommandText = "INSERT OR REPLACE INTO map_tiles (ui_map, row, col, file_id) VALUES ($id, $row, $col, $file)";

        // Explored-area overlays per map art (optional tables).
        var overlays = new Dictionary<int, List<(int Id, int X, int Y, int W, int H)>>();
        var overlayTiles = new Dictionary<int, List<(int Row, int Col, int File)>>();
        if (File.Exists(csv("WorldMapOverlay")) && File.Exists(csv("WorldMapOverlayTile")))
        {
            foreach (var row in CsvTable.Read(csv("WorldMapOverlay")))
            {
                var art = row.GetInt("UiMapArtID");
                if (!overlays.TryGetValue(art, out var list)) overlays[art] = list = [];
                list.Add((row.GetInt("ID"), row.GetInt("OffsetX"), row.GetInt("OffsetY"), row.GetInt("TextureWidth"), row.GetInt("TextureHeight")));
            }
            foreach (var row in CsvTable.Read(csv("WorldMapOverlayTile")).Where(r => r.GetInt("LayerIndex") == 0))
            {
                var id = row.GetInt("WorldMapOverlayID");
                if (!overlayTiles.TryGetValue(id, out var list)) overlayTiles[id] = list = [];
                list.Add((row.GetInt("RowIndex"), row.GetInt("ColIndex"), row.GetInt("FileDataID")));
            }
        }
        using var insertOverlay = connection.CreateCommand();
        insertOverlay.Transaction = transaction;
        insertOverlay.CommandText = """
            INSERT OR REPLACE INTO map_overlays (id, ui_map, offset_x, offset_y, width, height) VALUES ($id, $map, $x, $y, $w, $h)
            """;
        using var insertOverlayTile = connection.CreateCommand();
        insertOverlayTile.Transaction = transaction;
        insertOverlayTile.CommandText = "INSERT OR REPLACE INTO map_overlay_tiles (overlay_id, row, col, file_id) VALUES ($id, $row, $col, $file)";

        var count = 0;
        foreach (var map in CsvTable.Read(csv("UiMap")))
        {
            var id = map.GetInt("ID");
            if (!artFor.TryGetValue(id, out var art) || !artStyle.TryGetValue(art, out var style)
                || !styles.TryGetValue(style, out var size) || !tiles.TryGetValue(art, out var mapTiles)) continue;

            insertMap.Parameters.Clear();
            insertMap.Parameters.AddWithValue("$id", id);
            insertMap.Parameters.AddWithValue("$name", map.Get("Name_lang"));
            insertMap.Parameters.AddWithValue("$type", map.GetInt("Type"));
            insertMap.Parameters.AddWithValue("$parent", map.GetInt("ParentUiMapID"));
            insertMap.Parameters.AddWithValue("$w", size.W);
            insertMap.Parameters.AddWithValue("$h", size.H);
            insertMap.Parameters.AddWithValue("$tw", size.TileW);
            insertMap.Parameters.AddWithValue("$th", size.TileH);
            insertMap.ExecuteNonQuery();

            foreach (var tile in mapTiles)
            {
                insertTile.Parameters.Clear();
                insertTile.Parameters.AddWithValue("$id", id);
                insertTile.Parameters.AddWithValue("$row", tile.Row);
                insertTile.Parameters.AddWithValue("$col", tile.Col);
                insertTile.Parameters.AddWithValue("$file", tile.File);
                insertTile.ExecuteNonQuery();
            }
            foreach (var overlay in overlays.GetValueOrDefault(art) ?? [])
            {
                if (!overlayTiles.TryGetValue(overlay.Id, out var pieces)) continue;
                insertOverlay.Parameters.Clear();
                insertOverlay.Parameters.AddWithValue("$id", overlay.Id);
                insertOverlay.Parameters.AddWithValue("$map", id);
                insertOverlay.Parameters.AddWithValue("$x", overlay.X);
                insertOverlay.Parameters.AddWithValue("$y", overlay.Y);
                insertOverlay.Parameters.AddWithValue("$w", overlay.W);
                insertOverlay.Parameters.AddWithValue("$h", overlay.H);
                insertOverlay.ExecuteNonQuery();
                foreach (var piece in pieces)
                {
                    insertOverlayTile.Parameters.Clear();
                    insertOverlayTile.Parameters.AddWithValue("$id", overlay.Id);
                    insertOverlayTile.Parameters.AddWithValue("$row", piece.Row);
                    insertOverlayTile.Parameters.AddWithValue("$col", piece.Col);
                    insertOverlayTile.Parameters.AddWithValue("$file", piece.File);
                    insertOverlayTile.ExecuteNonQuery();
                }
            }
            count++;
        }
        return count;
    }
}
