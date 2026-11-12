using Microsoft.Data.Sqlite;

namespace WowCompanion.Data;

/// <summary>
/// Places to search on the Map page, and the bosses of each dungeon, from the client's own tables:
/// - subzones (AreaTable rows with a parent zone), placed at the middle of their explored-area patch on the zone map
///   (WorldMapOverlay.AreaID_0-3): Freewind Post, The Shimmering Flats…;
/// - named points (AreaPOI: towns, landmarks) and flight masters (TaxiNodes), placed from their world position;
/// - dungeon bosses (DungeonEncounter), so dungeons without Classic world data (WoW Forever's own) still list them.
/// </summary>
public static partial class GameDatabaseBuilder
{
    private const string PlacesSchema = """

        -- x, y: map coordinates 0-100 on the zone's map; -1 when the place has no known spot (the zone still opens).
        CREATE TABLE map_places (
            name TEXT NOT NULL,
            kind TEXT NOT NULL,           -- subzone, place, flight
            zone TEXT NOT NULL,
            x REAL NOT NULL,
            y REAL NOT NULL
        );
        CREATE TABLE dungeon_bosses (
            map_id INTEGER NOT NULL,
            ord INTEGER NOT NULL,
            name TEXT NOT NULL,
            PRIMARY KEY (map_id, name)
        );
        """;

    /// <returns>Number of places.</returns>
    private static int BuildPlaces(SqliteConnection connection, SqliteTransaction transaction, Func<string, string> csv)
    {
        if (!File.Exists(csv("AreaTable"))) return 0;
        var areas = CsvTable.Read(csv("AreaTable")).ToDictionary(r => r.GetInt("ID"),
            r => (Name: r.Get("AreaName_lang").Trim(), Parent: r.GetInt("ParentAreaID")));

        // Zone maps (map_art, built just before): size and type per UiMap, and which UiMaps show each map art.
        var mapSize = new Dictionary<int, (string Name, int Type, int W, int H)>();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT ui_map, name, type, width, height FROM map_art";
            using var r = command.ExecuteReader();
            while (r.Read()) mapSize[r.GetInt32(0)] = (r.GetString(1), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4));
        }
        var mapsOfArt = new Dictionary<int, List<int>>();
        if (File.Exists(csv("UiMapXMapArt")))
            foreach (var row in CsvTable.Read(csv("UiMapXMapArt")))
            {
                if (!mapsOfArt.TryGetValue(row.GetInt("UiMapArtID"), out var list)) mapsOfArt[row.GetInt("UiMapArtID")] = list = [];
                list.Add(row.GetInt("UiMapID"));
            }

        var places = new Dictionary<(string Name, string Zone), (string Kind, double X, double Y)>();
        void Add(string name, string kind, string zone, double x, double y)
        {
            name = name.Trim();
            if (name.Length == 0 || zone.Length == 0 || string.Equals(name, zone, StringComparison.OrdinalIgnoreCase)) return;
            var key = (name, zone);
            if (places.TryGetValue(key, out var have) && have.X >= 0)
            {
                // A town or flight master is an exact point: better than the middle of the subzone's patch.
                if (kind != "subzone" && have.Kind == "subzone" && x >= 0) places[key] = (have.Kind, Math.Round(x, 1), Math.Round(y, 1));
                return;
            }
            places[key] = (have.Kind ?? kind, Math.Round(x, 1), Math.Round(y, 1));
        }

        // Subzones at the middle of their explored-area patch.
        if (File.Exists(csv("WorldMapOverlay")))
            foreach (var row in CsvTable.Read(csv("WorldMapOverlay")))
            {
                var zoneMap = (mapsOfArt.GetValueOrDefault(row.GetInt("UiMapArtID")) ?? [])
                    .Where(mapSize.ContainsKey).OrderBy(m => mapSize[m].Type == 3 ? 0 : 1).ThenBy(m => m).FirstOrDefault();
                if (zoneMap == 0 || mapSize[zoneMap] is not { Type: 3 or 4, W: > 0, H: > 0 } size) continue;
                var x = (row.GetInt("OffsetX") + row.GetInt("TextureWidth") / 2.0) / size.W * 100;
                var y = (row.GetInt("OffsetY") + row.GetInt("TextureHeight") / 2.0) / size.H * 100;
                for (var i = 0; i < 4; i++)
                    if (areas.TryGetValue(row.GetInt($"AreaID_{i}"), out var area) && area.Parent > 0)
                        Add(area.Name, "subzone", size.Name, x, y);
            }

        // Named points and flight masters, from their world position.
        if (ZoneLocator.TryLoad(csv) is { } locator)
        {
            if (File.Exists(csv("AreaPOI")))
                foreach (var row in CsvTable.Read(csv("AreaPOI")))
                {
                    var (zone, x, y) = locator.Locate(row.GetInt("ContinentID"), row.GetDouble("Pos_0"), row.GetDouble("Pos_1"));
                    if (zone.Length > 0) Add(row.Get("Name_lang"), "place", zone, x, y);
                }
            if (File.Exists(csv("TaxiNodes")))
                foreach (var row in CsvTable.Read(csv("TaxiNodes")))
                {
                    var (zone, x, y) = locator.Locate(row.GetInt("ContinentID"), row.GetDouble("Pos_0"), row.GetDouble("Pos_1"));
                    // "Lakeshire, Redridge": the place before the comma.
                    var name = row.Get("Name_lang").Split(',')[0];
                    if (zone.Length > 0) Add(name, "flight", zone, x, y);
                }
        }

        // Subzones with no spot: still findable (their zone opens), when the zone has a map.
        var zoneNames = mapSize.Values.Where(m => m.Type == 3).Select(m => m.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var area in areas.Values)
            if (area.Parent > 0 && areas.TryGetValue(area.Parent, out var parent) && parent.Parent == 0 && zoneNames.Contains(parent.Name))
                Add(area.Name, "subzone", parent.Name, -1, -1);

        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = "INSERT INTO map_places (name, kind, zone, x, y) VALUES ($n, $k, $z, $x, $y)";
        var pName = insert.Parameters.Add("$n", SqliteType.Text);
        var pKind = insert.Parameters.Add("$k", SqliteType.Text);
        var pZone = insert.Parameters.Add("$z", SqliteType.Text);
        var pX = insert.Parameters.Add("$x", SqliteType.Real);
        var pY = insert.Parameters.Add("$y", SqliteType.Real);
        foreach (var ((name, zone), (kind, x, y)) in places)
        {
            pName.Value = name;
            pKind.Value = kind;
            pZone.Value = zone;
            pX.Value = x;
            pY.Value = y;
            insert.ExecuteNonQuery();
        }
        return places.Count;
    }

    /// <returns>Number of bosses.</returns>
    private static int BuildDungeonBosses(SqliteConnection connection, SqliteTransaction transaction, Func<string, string> csv)
    {
        if (!File.Exists(csv("DungeonEncounter"))) return 0;
        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = "INSERT OR IGNORE INTO dungeon_bosses (map_id, ord, name) VALUES ($m, $o, $n)";
        var count = 0;
        // One row per difficulty: the lowest order index per name wins.
        foreach (var row in CsvTable.Read(csv("DungeonEncounter")).OrderBy(r => r.GetInt("OrderIndex")))
        {
            var name = row.Get("Name_lang").Trim();
            if (name.Length == 0 || row.GetInt("MapID") <= 0) continue;
            insert.Parameters.Clear();
            insert.Parameters.AddWithValue("$m", row.GetInt("MapID"));
            insert.Parameters.AddWithValue("$o", row.GetInt("OrderIndex"));
            insert.Parameters.AddWithValue("$n", name);
            count += insert.ExecuteNonQuery();
        }
        return count;
    }
}
