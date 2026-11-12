using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WowCompanion.Core;

namespace WowCompanion.Desktop;

/// <summary>A marker on the map, in picture pixels. Kind: "focus" (what you asked to see) or "tale" (a tale's start).</summary>
public sealed record MapPin(double Left, double Top, string Label, string Kind, string Tip)
{
    public bool IsFocus => Kind == "focus";
    public bool IsTale => Kind == "tale";
}

/// <summary>
/// Map page (Ctrl+M): the game's own zone maps (tiles from wago.tools, MapImages). "View on map" buttons elsewhere
/// (item sources, upgrades, Journal tales) open the zone with a marker on the spot; the Journal's tales of the zone
/// are marked with a gold "!" where they begin.
/// </summary>
public partial class MainViewModel
{
    private MapService? _maps;
    private int _mapRun;
    private MapSpot? _mapFocus;

    public ObservableCollection<ZoneMap> MapZones { get; } = [];
    public ObservableCollection<MapPin> MapPins { get; } = [];

    [ObservableProperty] private ZoneMap? _selectedMapZone;
    [ObservableProperty] private ImageSource? _mapImage;
    [ObservableProperty] private bool _mapBusy;
    [ObservableProperty] private string _mapStatus = "";
    [ObservableProperty] private string _mapCursorText = "";
    [ObservableProperty] private bool _showTalePins = true;
    /// <summary>Picture size, for the marker layer.</summary>
    [ObservableProperty] private double _mapWidth = 1002;
    [ObservableProperty] private double _mapHeight = 668;

    public bool IsMapView => CurrentView == AppView.Map;

    /// <summary>Raised when the map should centre on a marker (code-behind scrolls there).</summary>
    public event Action<MapPin>? MapFocusRequested;

    private void UseMaps(string gameDbPath)
    {
        _maps = new MapService(gameDbPath);
        var previous = SelectedMapZone?.UiMapId;
        MapZones.Clear();
        // Continents come twice in Forever's data (a second, plain picture); keep the first one, with the zone lines.
        var shown = _maps.Zones().Where(m => m.Type is 2 or 3)
            .GroupBy(m => (m.Type, m.Name))
            .Select(g => g.OrderBy(m => m.UiMapId).First())
            .ToHashSet();
        foreach (var map in _maps.Zones().Where(shown.Contains)) MapZones.Add(map);
        var remembered = _talentBuilds.GetSetting("map_zone");
        SelectedMapZone = MapZones.FirstOrDefault(m => m.UiMapId == previous)
            ?? MapZones.FirstOrDefault(m => m.Name == remembered)
            ?? MapZones.FirstOrDefault(m => m.Type == 3);
        if (MapZones.Count == 0) MapStatus = "No maps in this game data yet. They come with the next data rebuild.";
    }

    partial void OnSelectedMapZoneChanged(ZoneMap? value)
    {
        if (value is not null) _talentBuilds.SetSetting("map_zone", value.Name);
        if (_mapFocus is not null && value is not null && !string.Equals(_mapFocus.Zone, value.Name, StringComparison.OrdinalIgnoreCase))
            _mapFocus = null;
        _ = LoadMapAsync();
    }

    partial void OnShowTalePinsChanged(bool value) => _ = LoadMapAsync();

    private async Task LoadMapAsync()
    {
        if (SelectedMapZone is not { } map) return;
        var run = ++_mapRun;
        MapWidth = map.Width;
        MapHeight = map.Height;
        MapPins.Clear();
        MapBusy = true;
        MapStatus = "Unrolling the map…";
        try
        {
            var image = await MapImages.GetAsync(map);
            if (run != _mapRun) return;
            MapImage = image;
            MapStatus = image is null ? "Couldn't get this map's picture (offline?). Markers still show where things are." : "";

            // Tales that begin here, if the Journal knows the zone.
            if (ShowTalePins && _journal is { } journal && map.Type == 3)
            {
                var lore = ZoneLoreBook.All.FirstOrDefault(z => z.Zone == map.Name)
                    ?? new ZoneLore(map.Name, map.Continent, "", "", "", []);
                var side = Side;
                var tales = await Task.Run(() => journal.Tales(lore, side, 6));
                if (run != _mapRun) return;
                foreach (var tale in tales)
                {
                    if (MapPlace.Find(tale.GiverPlace) is not { } spot || !SameZone(spot, map)) continue;
                    MapPins.Add(Pin(spot, map, tale.Title, "tale", $"{tale.Title}\nBegins with {tale.Giver}"));
                }
            }

            if (_mapFocus is { } focus && SameZone(focus, map))
            {
                var pin = Pin(focus, map, focus.Label.Length > 0 ? focus.Label : map.Name, "focus",
                    (focus.Label.Length > 0 ? focus.Label + "\n" : "") + ItemSearchService.Location(map.Name, focus.X, focus.Y, map.Name));
                MapPins.Add(pin);
                MapFocusRequested?.Invoke(pin);
            }
        }
        finally
        {
            if (run == _mapRun) MapBusy = false;
        }
    }

    private static bool SameZone(MapSpot spot, ZoneMap map) => string.Equals(spot.Zone, map.Name, StringComparison.OrdinalIgnoreCase);

    private static MapPin Pin(MapSpot spot, ZoneMap map, string label, string kind, string tip) =>
        new(spot.X / 100 * map.Width, spot.Y / 100 * map.Height, label, kind, tip);

    /// <summary>
    /// "View on map": the parameter is any text with a place in it ("Vendor: X · Westfall (56.4, 47.6)"), a MapSpot,
    /// or a row that carries one.
    /// </summary>
    [RelayCommand]
    private void ViewOnMap(object? target)
    {
        var spot = target switch
        {
            MapSpot s => s,
            string text => MapPlace.Find(text),
            SourceLine line => MapPlace.Find($"{line.Title} · {line.Place}"),
            UpgradeRow up => MapPlace.Find(up.SourceText) is { } s2 ? s2 with { Label = up.Name + " · " + s2.Label } : null,
            TaleRow tale => MapPlace.Find(tale.Start) is { } s3 ? s3 with { Label = $"{tale.Title} · {s3.Label}" } : null,
            _ => null,
        };
        if (spot is null || _maps?.Find(spot.Zone) is not { } map)
        {
            Toast("That place isn't on any map the Oracle has.");
            return;
        }
        _mapFocus = spot;
        CurrentView = AppView.Map;
        if (SelectedMapZone?.UiMapId == map.UiMapId) _ = LoadMapAsync();
        else SelectedMapZone = MapZones.FirstOrDefault(m => m.UiMapId == map.UiMapId) ?? map;
    }

    /// <summary>Journal → Map: the zone, with its tales marked.</summary>
    [RelayCommand]
    private void OpenZoneMap(string? zone)
    {
        if (zone is null || _maps?.Find(zone) is not { } map)
        {
            Toast("No map for this zone yet.");
            return;
        }
        _mapFocus = null;
        CurrentView = AppView.Map;
        SelectedMapZone = MapZones.FirstOrDefault(m => m.UiMapId == map.UiMapId) ?? map;
    }

    // ----- search: zones, subzones, towns, flight masters, NPCs -----

    public ObservableCollection<MapSearchHit> MapHits { get; } = [];
    private CancellationTokenSource? _mapSearchCts;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsMapSearching), nameof(IsNotMapSearching))] private string _mapSearchText = "";
    [ObservableProperty] private MapSearchHit? _selectedMapHit;

    /// <summary>While there's search text the results replace the zone list.</summary>
    public bool IsMapSearching => MapSearchText.Trim().Length >= 2;
    public bool IsNotMapSearching => !IsMapSearching;

    partial void OnMapSearchTextChanged(string value) => _ = SearchMapAsync(value);

    private async Task SearchMapAsync(string text)
    {
        _mapSearchCts?.Cancel();
        var cts = _mapSearchCts = new CancellationTokenSource();
        if (_maps is not { } maps || text.Trim().Length < 2) { MapHits.Clear(); return; }
        try
        {
            await Task.Delay(150, cts.Token);
            var hits = await Task.Run(() => maps.Search(text), cts.Token);
            if (cts.IsCancellationRequested) return;
            MapHits.Clear();
            foreach (var hit in hits) MapHits.Add(hit);
        }
        catch (OperationCanceledException) { }
    }

    partial void OnSelectedMapHitChanged(MapSearchHit? value)
    {
        if (value is not null) GoToMapHit(value);
    }

    /// <summary>Enter in the search box: the first answer.</summary>
    [RelayCommand]
    private void PickFirstMapHit()
    {
        if (MapHits.FirstOrDefault() is { } hit) GoToMapHit(hit);
    }

    private void GoToMapHit(MapSearchHit hit)
    {
        if (hit.HasSpot) ViewOnMap(new MapSpot(hit.Zone, hit.X, hit.Y, hit.Kind == "zone" ? "" : hit.Name));
        else OpenZoneMap(hit.Zone);
    }

    /// <summary>Esc in the search box, or "Clear": back to the zone list.</summary>
    [RelayCommand]
    private void ClearMapSearch() => MapSearchText = "";

    /// <summary>Map coordinates under the mouse, like the game's map (x, y in 0-100).</summary>
    public void SetMapCursor(double x, double y) =>
        MapCursorText = x < 0 ? "" : $"{x.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)}, {y.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)}";
}
