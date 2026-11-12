namespace WowCompanion.Data;

/// <summary>
/// Turns a world position (map, x, y) into a zone name and map coordinates, the numbers players use ("56, 47").
/// Uses the client's UiMapAssignment (which rectangle of the world each map image shows) and UiMap (names, type).
/// World x runs north, y runs west; on the map image x runs east and y south, so both axes flip.
/// </summary>
public sealed class ZoneLocator
{
    private sealed record Region(int Map, double MinX, double MinY, double MaxX, double MaxY,
        double UiMinX, double UiMinY, double UiMaxX, double UiMaxY, string Name)
    ;

    private readonly Dictionary<int, List<Region>> _byMap = [];

    /// <summary>WoW Forever's own zone maps have ids from here on (Mount Hyjal 2482, Riverglades 2548, Shen'dralas 2652…).</summary>
    public const int FirstForeverUiMap = 2000;

    /// <summary>Null when the UiMap tables weren't downloaded.</summary>
    /// <param name="classicOnly">
    /// Leave out WoW Forever's own zones, for Classic world data (CMaNGOS): their map rectangles overlap the old zones
    /// next to them (Riverglades over the Burning Steppes and Badlands), so a Classic spawn would land in them.
    /// </param>
    public static ZoneLocator? TryLoad(Func<string, string> csv, bool classicOnly = false)
    {
        var mapPath = csv("UiMap");
        var assignmentPath = csv("UiMapAssignment");
        if (!File.Exists(mapPath) || !File.Exists(assignmentPath)) return null;

        // UiMap types: 2 continent, 3 zone, 4 dungeon, 5 micro (cave), 6 orphan. Zones and dungeons give the useful names.
        var maps = CsvTable.Read(mapPath)
            .Where(r => r.GetInt("Type") is 3 or 4)
            .ToDictionary(r => r.GetInt("ID"), r => r.Get("Name_lang"));

        var locator = new ZoneLocator();
        foreach (var r in CsvTable.Read(assignmentPath))
        {
            if (!maps.TryGetValue(r.GetInt("UiMapID"), out var name) || name.Length == 0) continue;
            if (classicOnly && r.GetInt("UiMapID") >= FirstForeverUiMap) continue;
            var region = new Region(
                r.GetInt("MapID"),
                r.GetDouble("Region_0"), r.GetDouble("Region_1"), r.GetDouble("Region_3"), r.GetDouble("Region_4"),
                r.GetDouble("UiMin_0"), r.GetDouble("UiMin_1"), r.GetDouble("UiMax_0"), r.GetDouble("UiMax_1"),
                name);
            if (region.MaxX <= region.MinX || region.MaxY <= region.MinY) continue;
            if (!locator._byMap.TryGetValue(region.Map, out var list)) locator._byMap[region.Map] = list = [];
            list.Add(region);
        }
        return locator._byMap.Count == 0 ? null : locator;
    }

    /// <returns>Zone name and coordinates 0-100, or ("", 0, 0) when no zone map covers the spot.</returns>
    public (string Zone, double X, double Y) Locate(int map, double worldX, double worldY)
    {
        if (map < 0 || (worldX == 0 && worldY == 0) || !_byMap.TryGetValue(map, out var regions)) return ("", 0, 0);
        // Zone rectangles overlap near their borders, so take the zone the spot is deepest inside.
        Region? best = null;
        double bestDepth = -1, bestFx = 0, bestFy = 0;
        foreach (var r in regions)
        {
            if (worldX < r.MinX || worldX > r.MaxX || worldY < r.MinY || worldY > r.MaxY) continue;
            var fx = (r.MaxY - worldY) / (r.MaxY - r.MinY); // east-west
            var fy = (r.MaxX - worldX) / (r.MaxX - r.MinX); // north-south
            var depth = Math.Min(Math.Min(fx, 1 - fx), Math.Min(fy, 1 - fy));
            if (depth <= bestDepth) continue;
            (best, bestDepth, bestFx, bestFy) = (r, depth, fx, fy);
        }
        if (best is null) return ("", 0, 0);
        var x = (best.UiMinX + bestFx * (best.UiMaxX - best.UiMinX)) * 100;
        var y = (best.UiMinY + bestFy * (best.UiMaxY - best.UiMinY)) * 100;
        return (best.Name, Math.Round(x, 1), Math.Round(y, 1));
    }
}
