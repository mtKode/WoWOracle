using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using WowCompanion.Core;

namespace WowCompanion.Desktop;

/// <summary>
/// Loot page, AtlasLoot style: pick a dungeon or raid (or type a boss name), pick a boss, see its loot by quality,
/// split into gear, recipes, quest items and the rest. Item rows behave like search results (click opens,
/// Shift+click adds to gear, Alt compares).
/// </summary>
public partial class MainViewModel
{
    private string _gameDbPath = "";
    private LootService? _loot;
    private Task<LootService?>? _lootLoading;
    private List<LootInstance> _instances = [];
    private CancellationTokenSource? _lootSearchCts;

    public ObservableCollection<LootPlaceRow> LootPlaces { get; } = [];
    public ObservableCollection<LootBossRow> LootBosses { get; } = [];
    public ObservableCollection<LootSection> LootSections { get; } = [];

    [ObservableProperty] private string _lootFilter = "";
    [ObservableProperty] private LootPlaceRow? _selectedLootPlace;
    [ObservableProperty] private LootBossRow? _selectedLootBoss;
    [ObservableProperty] private bool _lootShowAll;
    [ObservableProperty] private string _lootStatus = "";
    [ObservableProperty] private string _lootTitle = "";
    [ObservableProperty] private string _lootSubtitle = "";

    private int _pendingBossNpc;
    private IReadOnlyList<LootQuest> _lootQuests = [];
    private int _lootMapShown;

    private void ResetLoot()
    {
        _loot = null;
        _lootLoading = null;
        _instances = [];
        _lootMapShown = 0;
        LootPlaces.Clear();
        LootBosses.Clear();
        LootSections.Clear();
        if (CurrentView == AppView.Loot) _ = EnsureLootAsync();
    }

    /// <summary>First visit: open the service off the UI thread (it may add an index to the game database once).</summary>
    private async Task EnsureLootAsync()
    {
        if (_loot is not null || _gameDbPath.Length == 0) return;
        var path = _gameDbPath;
        _lootLoading ??= Task.Run<LootService?>(() =>
        {
            try { return new LootService(path); }
            catch (Exception) { return null; }
        });
        LootStatus = "Opening loot tables…";
        var service = await _lootLoading;
        if (path != _gameDbPath) return; // database changed meanwhile
        _loot = service;
        if (_loot is null || !_loot.HasData)
        {
            LootStatus = "No loot data in this game database. It comes with the item sources download.";
            return;
        }
        _instances = (await Task.Run(() => _loot.GetInstances())).ToList();
        LootStatus = _instances.Count == 0 ? "No dungeons found (map names missing from the game data)." : "";
        RefreshLootPlaces();
    }

    partial void OnLootFilterChanged(string value) => RefreshLootPlaces();

    private async void RefreshLootPlaces()
    {
        var filter = LootFilter.Trim();
        var keep = SelectedLootPlace;
        LootPlaces.Clear();
        foreach (var i in _instances.Where(i => filter.Length == 0 || i.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)))
            LootPlaces.Add(PlaceRow(i));

        // Boss names too, once there are a few letters.
        _lootSearchCts?.Cancel();
        if (_loot is null || filter.Length < 3) { Reselect(keep); return; }
        var cts = _lootSearchCts = new CancellationTokenSource();
        var loot = _loot;
        try
        {
            await Task.Delay(150, cts.Token);
            var bosses = await Task.Run(() => loot.FindBosses(filter), cts.Token);
            if (cts.IsCancellationRequested) return;
            foreach (var b in bosses)
                LootPlaces.Add(new LootPlaceRow(true, b.MapId, b.NpcId, b.Name, $"Boss · {b.MapName}", SourceColors.Boss));
        }
        catch (OperationCanceledException) { return; }
        catch (Exception) { return; } // boss search is a bonus; the instance list is already shown
        Reselect(keep);
    }

    private void Reselect(LootPlaceRow? keep)
    {
        if (keep is not null && LootPlaces.FirstOrDefault(p => p == keep) is { } same) SelectedLootPlace = same;
    }

    private static LootPlaceRow PlaceRow(LootInstance i)
    {
        var kind = i.IsRaid ? "Raid" : "Dungeon";
        var levels = i.MinLevel == i.MaxLevel ? $"{i.MaxLevel}" : $"{i.MinLevel}-{i.MaxLevel}";
        var meta = i.MaxLevel > 0 ? $"{kind} · level {levels}" : $"{kind} · new in WoW Forever";
        return new LootPlaceRow(false, i.MapId, 0, i.Name, meta, i.IsRaid ? SourceColors.Boss : SourceColors.Place);
    }

    /// <summary>Arrow keys / Tab in the loot search box walk the place list below it.</summary>
    public void MoveLootPlace(int delta)
    {
        if (LootPlaces.Count == 0) return;
        var index = SelectedLootPlace is { } current ? LootPlaces.IndexOf(current) : -1;
        index = index < 0 ? (delta > 0 ? 0 : LootPlaces.Count - 1) : (index + delta + LootPlaces.Count) % LootPlaces.Count;
        SelectedLootPlace = LootPlaces[index];
    }

    /// <summary>Ctrl+arrows in the loot search box walk the boss list.</summary>
    public void MoveLootBoss(int delta)
    {
        if (LootBosses.Count == 0) return;
        var index = SelectedLootBoss is { } current ? LootBosses.IndexOf(current) : -1;
        index = index < 0 ? (delta > 0 ? 0 : LootBosses.Count - 1) : (index + delta + LootBosses.Count) % LootBosses.Count;
        SelectedLootBoss = LootBosses[index];
    }

    /// <summary>Enter in the loot search: open the first match.</summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void OpenFirstLootPlace()
    {
        if (SelectedLootPlace is null && LootPlaces.FirstOrDefault() is { } first) SelectedLootPlace = first;
    }

    partial void OnSelectedLootPlaceChanged(LootPlaceRow? value)
    {
        if (value is null) return;
        // Re-selected after the list was refiltered: the same instance is already showing.
        if (!value.IsBoss && value.MapId == _lootMapShown && LootBosses.Count > 0) return;
        _pendingBossNpc = value.IsBoss ? value.NpcId : 0;
        _ = LoadBossesAsync(value.MapId);
    }

    private async Task LoadBossesAsync(int mapId)
    {
        if (_loot is not { } loot) return;
        var (bosses, quests) = await Task.Run(() => (loot.GetBosses(mapId), loot.GetQuests(mapId)));
        _lootMapShown = mapId;
        _lootQuests = quests;
        LootBosses.Clear();
        if (quests.Count > 0)
            LootBosses.Add(new LootBossRow(-1, $"Quests ({quests.Count})", "Picked up for this instance", SourceColors.Quest) { IsQuests = true });
        foreach (var b in bosses)
        {
            if (b.IsObject)
            {
                LootBosses.Add(new LootBossRow(b.NpcId, b.Name, "Chest", SourceColors.Profession, IsObject: true));
                continue;
            }
            var levels = b.MinLevel == b.MaxLevel ? $"{b.MaxLevel}" : $"{b.MinLevel}-{b.MaxLevel}";
            var meta = b.MaxLevel > 0 ? $"Level {levels}" : b.NpcId > 0 ? "Boss" : "Boss · not seen yet";
            LootBosses.Add(new LootBossRow(b.NpcId, b.Name, meta, b.Rank == 3 ? SourceColors.Boss : SourceColors.Elite));
        }
        var instance = _instances.FirstOrDefault(i => i.MapId == mapId);
        LootTitle = instance?.Name ?? bosses.FirstOrDefault()?.MapName ?? "";
        LootStatus = bosses.Count == 0 && quests.Count == 0 ? "No bosses with loot found here." : "";
        var target = _pendingBossNpc;
        _pendingBossNpc = 0;
        SelectedLootBoss = LootBosses.FirstOrDefault(b => b.NpcId == target && !b.IsObject && !b.IsQuests)
                           ?? LootBosses.FirstOrDefault(b => bosses.Any(x => x.NpcId == target && x.Name == b.Name))
                           ?? LootBosses.FirstOrDefault(b => !b.IsQuests) // a boss first; quests are one click away
                           ?? LootBosses.FirstOrDefault();
        if (SelectedLootBoss is null) LootSections.Clear();
    }

    partial void OnSelectedLootBossChanged(LootBossRow? value) => _ = LoadBossLootAsync();

    partial void OnLootShowAllChanged(bool value) => _ = LoadBossLootAsync();

    private async Task LoadBossLootAsync()
    {
        if (_loot is not { } loot || SelectedLootBoss is not { } boss) { LootSections.Clear(); return; }
        if (boss.IsQuests) { await ShowQuestsAsync(boss); return; }
        var items = await Task.Run(() => loot.GetLoot(boss.NpcId, boss.IsObject));
        if (SelectedLootBoss != boss) return;

        var inv = CultureInfo.InvariantCulture;
        var search = _search;
        ItemRow Row(LootItem i, ItemDetail? detail)
        {
            var kind = ItemText.Kind(i.ClassId, i.SubclassId, i.InventoryType);
            var parts = new List<string>();
            if (kind.Length > 0) parts.Add(kind);
            if (i.ClassId is 2 or 4 && i.InventoryType > 0) parts.Add($"item level {i.ItemLevel}");
            parts.Add(i.Chance > 0 ? ChanceText(i.Chance) + " drop chance" : "drop chance unknown");
            var row = new ItemRow(i.ItemId, i.Name, string.Join(" · ", parts), ItemQuality.BrushFor(i.Quality), i.IconId);
            if (detail is null) return row;

            // Stats right in the list, so there's no need to open every item.
            var card = ItemView.From(detail);
            _cards[i.ItemId] = card;
            var stats = new List<string>();
            if (card.DamageLine.Length > 0) stats.Add($"{card.DamageLine} {card.SpeedLine}".Trim());
            if (card.DpsLine.Length > 0) stats.Add(card.DpsLine);
            if (card.ArmorLine.Length > 0) stats.Add(card.ArmorLine);
            stats.AddRange(card.StatLines.Where(x => x.Length > 0));
            return row with
            {
                StatText = string.Join("  ·  ", stats),
                EffectText = string.Join("   ", card.EffectLines.Where(x => x.Length > 0)),
            };
        }

        var shown = items.Where(i => LootShowAll || ((i.Quality >= 2 || i.ClassId == 12) && !i.IsWorldDrop)).ToList();
        var ordered = shown.OrderByDescending(i => i.Quality).ThenByDescending(i => i.Chance).ThenBy(i => i.Name).ToList();
        var groups = new (string Heading, Func<LootItem, bool> Test)[]
        {
            ("Gear", i => i.ClassId is 2 or 4 && i.InventoryType > 0),
            ("Recipes and patterns", i => i.ClassId == 9),
            ("Quest items", i => i.ClassId == 12),
        };

        // Item details (stats, effects) off the UI thread; gear and recipes are what people look at.
        var details = await Task.Run(() =>
        {
            var map = new Dictionary<int, ItemDetail>();
            if (search is null) return map;
            foreach (var i in ordered.Where(x => x.ClassId is 2 or 4 or 9 or 0).Take(80))
                if (search.GetDetail(i.ItemId) is { } d) map[i.ItemId] = d;
            return map;
        });
        if (SelectedLootBoss != boss) return;

        LootSections.Clear();
        var used = new HashSet<int>();
        foreach (var (heading, test) in groups)
        {
            var rows = ordered.Where(i => !used.Contains(i.ItemId) && test(i)).ToList();
            foreach (var r in rows) used.Add(r.ItemId);
            if (rows.Count > 0) LootSections.Add(new LootSection(heading, rows.Select(r => Row(r, details.GetValueOrDefault(r.ItemId))).ToList()));
        }
        var rest = ordered.Where(i => !used.Contains(i.ItemId)).ToList();
        if (rest.Count > 0) LootSections.Add(new LootSection("Consumables and other", rest.Select(r => Row(r, details.GetValueOrDefault(r.ItemId))).ToList()));

        var hidden = items.Count - shown.Count;
        LootSubtitle = $"{boss.Name} · {shown.Count} items" + (hidden > 0 ? $" ({hidden} grey/white or world drops hidden)" : "");
        if (items.Count == 0)
            LootSubtitle = boss.NpcId > 0
                ? $"{boss.Name} has no loot in the data yet. Kill them with the WoW Oracle addon on and the drops are recorded."
                : $"No loot known for {boss.Name} yet (new in WoW Forever). Kill them with the WoW Oracle addon on and the drops show up here.";
        else if (shown.Count == 0 && !LootShowAll)
            LootSubtitle = $"{boss.Name} has no loot of their own, only random world drops and grey/white items ({hidden}). Tick the box above to see them.";
    }

    /// <summary>The instance's quests: one section per quest (level, giver, where), its rewards as item rows.</summary>
    private async Task ShowQuestsAsync(LootBossRow entry)
    {
        var quests = _lootQuests;
        var search = _search;
        var details = await Task.Run(() =>
        {
            var map = new Dictionary<int, ItemDetail>();
            if (search is null) return map;
            foreach (var item in quests.SelectMany(q => q.Rewards.Concat(q.Choices)).DistinctBy(i => i.ItemId).Take(150))
                if (search.GetDetail(item.ItemId) is { } d) map[item.ItemId] = d;
            return map;
        });
        if (SelectedLootBoss != entry) return;

        ItemRow Reward(LootItem i, bool choice)
        {
            var kind = ItemText.Kind(i.ClassId, i.SubclassId, i.InventoryType);
            var summary = string.Join(" · ", new[] { kind, choice ? "choose one" : "reward" }.Where(x => x.Length > 0));
            var row = new ItemRow(i.ItemId, i.Name, summary, ItemQuality.BrushFor(i.Quality), i.IconId);
            if (!details.TryGetValue(i.ItemId, out var detail)) return row;
            var card = ItemView.From(detail);
            _cards[i.ItemId] = card;
            var stats = new List<string>();
            if (card.DamageLine.Length > 0) stats.Add($"{card.DamageLine} {card.SpeedLine}".Trim());
            if (card.DpsLine.Length > 0) stats.Add(card.DpsLine);
            if (card.ArmorLine.Length > 0) stats.Add(card.ArmorLine);
            stats.AddRange(card.StatLines.Where(x => x.Length > 0));
            return row with
            {
                StatText = string.Join("  ·  ", stats),
                EffectText = string.Join("   ", card.EffectLines.Where(x => x.Length > 0)),
            };
        }

        LootSections.Clear();
        foreach (var q in quests)
        {
            var who = q.Giver.Length > 0 ? $"from {q.Giver}" + (q.GiverPlace.Length > 0 ? $", {q.GiverPlace}" : "") : "giver unknown";
            var level = q.MinLevel > 0 && q.MinLevel != q.Level ? $"level {q.Level} (from {q.MinLevel})" : $"level {q.Level}";
            var rows = q.Rewards.Select(i => Reward(i, false)).Concat(q.Choices.Select(i => Reward(i, true))).ToList();
            LootSections.Add(new LootSection(q.Title, rows)
            {
                CountInHeader = false,
                Subheading = $"{level} · {who}" + (rows.Count == 0 ? " · no item reward" : ""),
                QuestId = q.Id,
                MapText = MapPlace.Find(q.GiverPlace) is not null ? $"{q.Giver} · {q.GiverPlace}" : "",
            });
        }
        LootSubtitle = $"{quests.Count} quests · where they're picked up and what they give";
    }

    /// <summary>"18%", "2.5%", "0.04%", "&lt;0.01%".</summary>
    public static string ChanceText(double chance)
    {
        var inv = CultureInfo.InvariantCulture;
        if (chance < 0.01) return "<0.01%";
        return chance.ToString(chance >= 10 ? "0" : chance >= 1 ? "0.#" : "0.##", inv) + "%";
    }

    /// <summary>Opens an item card from anywhere (loot list, upgrade finder).</summary>
    public void OpenItem(int itemId)
    {
        if (_search?.GetDetail(itemId) is not { } detail) return;
        SelectedQuest = null;
        Selected = ItemView.From(detail);
        CurrentView = AppView.Items;
    }

    public void AddItemIdToGear(int itemId, string name) => AddItemToGear(itemId, name);
}
