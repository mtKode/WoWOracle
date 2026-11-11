using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WowCompanion.Core;
using WowCompanion.Core.Calc;
using WowCompanion.Data;

namespace WowCompanion.Desktop;

public partial class MainViewModel(AppPaths paths, GameDataUpdater updater) : ObservableObject
{
    private const string AllProfessions = "All professions";

    private readonly CraftListService _craft = new(paths.UserDbPath);
    private readonly TalentBuildService _talentBuilds = new(paths.UserDbPath);
    private readonly GearSetService _gearSets = new(paths.UserDbPath);
    private readonly CalcSnapshotService _snapshots = new(paths.UserDbPath);
    private ItemSearchService? _search;
    private RecipeService? _recipes;
    private CancellationTokenSource? _searchCts;
    private bool _suppressSearch;
    private string _chosenQuery = "";

    // ----- Shell -----
    [ObservableProperty] private string _query = "";
    [ObservableProperty] private string _status = "Starting…";
    [ObservableProperty] private string _buildLabel = "";
    [ObservableProperty] private string _hint = "";
    [ObservableProperty] private bool _alwaysOnTop;
    /// <summary>No game data yet: a "gathering its knowledge" card in the middle of the window shows the progress.</summary>
    [ObservableProperty] private bool _isFirstLoad;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsItemsView), nameof(IsRecipesView), nameof(IsCraftView), nameof(IsSpellsView),
        nameof(IsTalentsView), nameof(IsGearView), nameof(IsCalcView), nameof(IsLootView), nameof(IsNotesView), nameof(IsJournalView), nameof(IsMapView), nameof(ViewIndex))]
    private AppView _currentView = AppView.Items;

    /// <summary>Position of the current view in the left rail list.</summary>
    public int ViewIndex
    {
        get => (int)CurrentView;
        set
        {
            if (value >= 0 && value < Enum.GetValues<AppView>().Length) CurrentView = (AppView)value;
        }
    }

    public bool IsItemsView => CurrentView == AppView.Items;
    public bool IsRecipesView => CurrentView == AppView.Recipes;
    public bool IsCraftView => CurrentView == AppView.Craft;
    public bool IsSpellsView => CurrentView == AppView.Spells;
    public bool IsTalentsView => CurrentView == AppView.Talents;
    public bool IsGearView => CurrentView == AppView.Gear;
    public bool IsCalcView => CurrentView == AppView.Calc;
    public bool IsLootView => CurrentView == AppView.Loot;
    public bool IsNotesView => CurrentView == AppView.Notes;

    // ----- Items view -----
    [ObservableProperty] private bool _isDropdownOpen;
    [ObservableProperty] private int _highlightIndex = -1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(ShowEmptyState))]
    private ItemView? _selected;

    /// <summary>A quest chosen in the search: shown as a quick card on the Items page instead of an item.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasQuestSelection), nameof(ShowEmptyState))]
    private QuestView? _selectedQuest;

    public bool HasQuestSelection => SelectedQuest is not null;
    private QuestService? _questService;

    public ObservableCollection<ItemRow> Results { get; } = [];
    public bool HasSelection => Selected is not null;
    public bool ShowEmptyState => Selected is null && SelectedQuest is null;

    // ----- Recipes view -----
    [ObservableProperty] private string _selectedProfession = AllProfessions;
    [ObservableProperty] private string _recipeFilter = "";
    [ObservableProperty] private int _recipeSortIndex;

    public IReadOnlyList<string> RecipeSorts { get; } = ["Required skill", "Name", "Turns grey", "Item level"];

    partial void OnRecipeSortIndexChanged(int value) => RefreshRecipes();
    [ObservableProperty] private string _recipeInfo = "";

    public ObservableCollection<string> Professions { get; } = [];
    public ObservableCollection<RecipeRow> Recipes { get; } = [];

    // ----- To craft view -----
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CraftHasItems))]
    private bool _craftIsEmpty = true;

    [ObservableProperty] private string _craftSummary = "";

    public bool CraftHasItems => !CraftIsEmpty;
    public ObservableCollection<CraftRow> CraftList { get; } = [];
    public ObservableCollection<ShoppingRow> Shopping { get; } = [];

    /// <summary>Use saved data right away, then check for a newer build in the background.</summary>
    public async Task InitializeAsync()
    {
        StartReminders();
        RecordNavigation(); // the first page, so Back can return to it
        CheckSetup(announce: true);
        if (GameDb.FindCurrent(paths.DataDir) is { } local)
        {
            await UseDatabaseAsync(local.Path, local.Version);
            CleanUpOldDatabases(local.Version); // left behind when they were still open last time
            Status = "Checking for new game data…";
        }
        else
        {
            // First start (or a data rebuild): say so in the middle of the window, not only in the status line.
            IsFirstLoad = true;
            Status = "Preparing game data…";
        }

        try
        {
            var progress = new Progress<string>(message => Status = message);
            var newVersion = await updater.EnsureCurrentAsync(progress);

            IsFirstLoad = false;
            if (newVersion is not null && GameDb.FindCurrent(paths.DataDir) is { } updated)
            {
                Log($"Game data updated to build {newVersion} (wago.tools).", "data");
                await UseDatabaseAsync(updated.Path, updated.Version);
                CleanUpOldDatabases(updated.Version); // only now: the old file was in use until the switch
                Status = $"Updated to build {newVersion}.";
            }
            else if (_search is not null)
            {
                Status = "Game data is up to date.";
                Log($"Game data is up to date (build {_dataVersion}).");
            }
        }
        catch (Exception ex)
        {
            IsFirstLoad = false;
            Status = _search is null
                ? $"Couldn't get game data: {ex.Message}"
                : "Couldn't check for updates. Using saved data.";
            Log(Status, "warn");
        }
        OpenLogIfNews();

        // Icons: the first run downloads the icon name list (one time, in the background).
        var iconStatus = await IconCache.PrepareAsync();
        if (iconStatus.Length > 0) Status = iconStatus;
    }

    // ----- Views -----

    [RelayCommand]
    private void ShowView(string name)
    {
        if (Enum.TryParse<AppView>(name, out var view)) CurrentView = view;
    }

    // ----- Dropdown navigation (called from the window's key handling) -----

    public void MoveHighlight(int delta)
    {
        if (Results.Count == 0) return;
        HighlightIndex = HighlightIndex < 0
            ? (delta > 0 ? 0 : Results.Count - 1)
            : (HighlightIndex + delta + Results.Count) % Results.Count;
    }

    public bool ChooseHighlighted()
    {
        if (!IsDropdownOpen || HighlightIndex < 0 || HighlightIndex >= Results.Count) return false;
        Choose(Results[HighlightIndex]);
        return true;
    }

    public void Choose(ItemRow row)
    {
        if (row.IsQuest)
        {
            if (!ShowQuest(row.QuestId)) return;
        }
        else
        {
            var detail = _search?.GetDetail(row.Id);
            if (detail is null) return;
            SelectedQuest = null;
            Selected = ItemView.From(detail);
        }
        CurrentView = AppView.Items;

        // Show the chosen name in the box without starting a new search.
        _suppressSearch = true;
        // With filter chips the typed text stays (it's part of the search); without, show the item's name.
        if (SearchChips.Count == 0) Query = row.Name;
        _chosenQuery = EffectiveQuery;
        _suppressSearch = false;

        IsDropdownOpen = false;
    }

    public void CloseDropdown() => IsDropdownOpen = false;

    public void ReopenDropdown()
    {
        if (EffectiveQuery.Length > 0 && EffectiveQuery != _chosenQuery) IsDropdownOpen = true;
    }

    /// <summary>First Escape closes the list, the second clears the box.</summary>
    public void Escape()
    {
        if (IsDropdownOpen) IsDropdownOpen = false;
        else if (IsLogOpen) IsLogOpen = false;
        else if (Query.Length > 0 || SearchChips.Count > 0) ClearQuery();
    }

    /// <summary>Used by the "/" shortcut: empty the box and start over. The shown item stays.</summary>
    public void ClearQuery()
    {
        _chosenQuery = "";
        LeaveChips();
        SearchChips.Clear();
        OnPropertyChanged(nameof(SearchPlaceholder));
        Query = "";
        IsDropdownOpen = false;
    }

    // ----- Search -----

    partial void OnQueryChanged(string value)
    {
        if (_suppressSearch || ExtractChips(value)) return;
        _ = RunSearchAsync(EffectiveQuery);
    }

    /// <summary>App version from Directory.Build.props (major.minor.patch).</summary>
    public static string AppVersion { get; } =
        System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "dev";

    private string _dataVersion = "";

    private async Task UseDatabaseAsync(string path, string version)
    {
        LoadVisibilitySetting();
        // Items, quests, sightings and talent trees the game client knows and the downloaded data lacks (background).
        var addonTexts = await UseAddonDataAsync(path, version);
        _search = new ItemSearchService(path);
        _questService = new QuestService(path);
        _cards.Clear();
        _recipes = new RecipeService(path);
        _spellService = new SpellService(path);
        _talentService = new TalentService(path);
        _calcData = new CalcDataService(path);
        _upgradeFinder = new UpgradeFinder(path, _calcData);
        _enchants = new EnchantService(path);
        _enchantOptions.Clear();
        _gameDbPath = path;
        _dataVersion = version;
        ResetLoot();
        BuildLabel = $"WoW Oracle v{AppVersion} · Build {version}";

        LoadClasses();
        LoadGear();
        LoadProfessions();
        RefreshRecipes();
        RefreshCraftList();
        UseJournal(path); // after the gear sets, so its side and zone can follow the active set
        UseMaps(path);
        SyncOwnGear(addonTexts); // your own gear from the addon, once classes and gear sets are loaded
        UnhideSeenItems(path);
        _ = LoadAuctionPricesAsync(); // Auctionator's prices, if it's installed

        if (EffectiveQuery.Length > 0 && EffectiveQuery != _chosenQuery) _ = RunSearchAsync(EffectiveQuery);
    }

    /// <summary>quest: and from: values from the game data: quest titles, and NPC / chest names that drop or sell.</summary>
    private static async Task<List<FilterSuggestion>> DataSuggestionsAsync(
        string? word, ItemSearchService search, QuestService? quests, CancellationToken ct)
    {
        var colon = word?.IndexOf(':') ?? -1;
        if (word is null || colon <= 0) return [];
        var key = word[..colon].ToLowerInvariant();
        var value = word[(colon + 1)..].Trim().Trim('"');
        if (key is "from" or "drop" or "boss" or "npc" && value.Length >= 2)
        {
            var names = await Task.Run(() => search.SourceNames(value, 5), ct);
            return names.Select(n => new FilterSuggestion(ItemQuery.NameValue(key, n), n, "drops, sells or holds items")).ToList();
        }
        if (key == "quest" && value.Length >= 3 && quests is not null)
        {
            var hits = await Task.Run(() => quests.Search(value, 4), ct);
            return hits.Select(q => new FilterSuggestion(ItemQuery.NameValue("quest", q.Title.Trim()), q.Title.Trim(),
                q.Level > 0 ? $"rewards of this quest (level {q.Level})" : "rewards of this quest")).ToList();
        }
        return [];
    }

    /// <summary>True when the highlighted line in the list is an autocomplete suggestion.</summary>
    public bool HighlightedIsSuggestion =>
        HighlightIndex >= 0 && HighlightIndex < Results.Count && Results[HighlightIndex].IsSuggestion;

    /// <summary>
    /// Puts a suggestion in place of the word being typed (the given one, else the highlighted one, else the first).
    /// A finished filter gets a space, so it becomes a chip; a filter name ("stat:") waits for its value.
    /// </summary>
    public bool AcceptSuggestion(ItemRow? row = null)
    {
        row ??= HighlightedIsSuggestion ? Results[HighlightIndex] : Results.FirstOrDefault(r => r.IsSuggestion);
        if (row is null || !row.IsSuggestion) return false;
        var before = ItemQuery.WordBeingTyped(Query) is { } typing
            ? Query[..typing.Start]
            : Query.Length > 0 && !char.IsWhiteSpace(Query[^1]) ? Query + " " : Query;
        Query = before + row.Completion + (row.Completion.EndsWith(':') ? "" : " ");
        SearchTextReplaced?.Invoke();
        return true;
    }

    /// <summary>Removes game databases other than the one in use (older builds and schemas).</summary>
    private void CleanUpOldDatabases(string keepVersion)
    {
        try { GameDb.DeleteOlder(paths.DataDir, keepVersion); }
        catch (Exception) { } // tried again next start
    }

    private async Task RunSearchAsync(string query)
    {
        _searchCts?.Cancel();
        var cts = _searchCts = new CancellationTokenSource();

        try
        {
            if (query.Trim().Length == 0)
            {
                Results.Clear();
                Hint = "";
                IsDropdownOpen = false;
                return;
            }

            await Task.Delay(120, cts.Token);

            var service = _search;
            if (service is null) return;

            var parsed = ItemQuery.Parse(query);
            // Autocomplete for the word being typed in the box (not the chips): filter names, then their values.
            var typing = ItemQuery.WordBeingTyped(Query);
            var suggestions = typing is { } typed ? ItemQuery.Suggest(typed.Word) : new List<FilterSuggestion>();
            var namesTask = DataSuggestionsAsync(typing?.Word, service, _questService, cts.Token);
            // Items and (by title) quests side by side: quests from a quest: filter, or plain text with no other filters.
            var itemsTask = Task.Run(() => service.Search(parsed, 50), cts.Token);
            var questText = parsed.QuestText.Length > 0 ? parsed.QuestText : !parsed.HasFilters ? parsed.Text : "";
            var questService = _questService;
            var questsTask = questService is null || questText.Trim().Length < 3
                ? Task.FromResult<IReadOnlyList<QuestHit>>([])
                : Task.Run(() => questService.Search(questText, 3), cts.Token);
            var found = await itemsTask;
            var quests = await questsTask;
            suggestions.AddRange(await namesTask);
            if (cts.IsCancellationRequested) return;

            Results.Clear();
            foreach (var suggestion in suggestions)
                Results.Add(new ItemRow(0, suggestion.Label, suggestion.Hint, MakeChip(suggestion.Completion).Brush)
                {
                    Completion = suggestion.Completion,
                    IconName = "inv_misc_spyglass_02",
                });
            foreach (var quest in quests) Results.Add(ItemRow.FromQuest(quest));
            foreach (var result in found) Results.Add(ItemRow.From(result));

            // The word still being typed isn't "not understood" yet: the suggestions cover it.
            var unknown = parsed.Unknown
                .Where(u => typing is null || !string.Equals(u.Replace(" ", ""), typing.Value.Word.Replace(" ", ""), StringComparison.OrdinalIgnoreCase))
                .ToList();
            Hint = unknown.Count > 0
                ? $"Didn't understand {string.Join(", ", unknown)}. Filters: stat:str  neck  type:plate  q:epic  lvl:50-60  ilvl:60+  class:mage"
                : Results.Count == 0 ? $"No items or quests match “{query.Trim()}”." : "";
            // Inside a filter (it has a colon) the first suggestion is highlighted, so Enter / Tab completes it;
            // while typing a plain word the first item stays highlighted (Tab still completes the filter name).
            var inFilter = typing is { } w && w.Word.Contains(':');
            HighlightIndex = Results.Count == 0 ? -1
                : suggestions.Count > 0 && (inFilter || Results.Count == suggestions.Count) ? 0
                : suggestions.Count;
            IsDropdownOpen = true;
        }
        catch (OperationCanceledException)
        {
            // A newer keystroke replaced this search.
        }
    }

    // ----- Recipes -----

    partial void OnSelectedProfessionChanged(string value) => RefreshRecipes();

    partial void OnRecipeFilterChanged(string value) => RefreshRecipes();

    private void LoadProfessions()
    {
        if (_recipes is null) return;

        Professions.Clear();
        Professions.Add(AllProfessions);
        foreach (var name in _recipes.GetProfessions()) Professions.Add(name);
        SelectedProfession = AllProfessions;
    }

    private void RefreshRecipes()
    {
        if (_recipes is null) return;

        const int limit = 300;
        var profession = string.IsNullOrEmpty(SelectedProfession) || SelectedProfession == AllProfessions
            ? null
            : SelectedProfession;

        var found = _recipes.Search(profession, RecipeFilter, limit, (RecipeSort)Math.Clamp(RecipeSortIndex, 0, 3));
        Recipes.Clear();
        foreach (var recipe in found) Recipes.Add(RecipeRow.From(recipe));

        RecipeInfo = found.Count >= limit
            ? $"Showing the first {limit}. Pick a profession or type a filter to narrow it."
            : $"{found.Count} recipes";
    }

    // ----- To craft -----

    [RelayCommand]
    private void AddToCraft(RecipeRow? row)
    {
        if (row is null) return;
        _craft.Add(row.SpellId);
        RefreshCraftList();
        Toast($"Added {row.Name} to the craft list.");
    }

    [RelayCommand]
    private void AddSelectedToCraft()
    {
        if (Selected?.CraftSpellId is not { } spellId) return;
        _craft.Add(spellId);
        RefreshCraftList();
        Toast($"Added {Selected.Name} to the craft list.");
    }

    [RelayCommand]
    private void IncreaseCraft(CraftRow? row)
    {
        if (row is null) return;
        _craft.SetQuantity(row.SpellId, row.Quantity + 1);
        RefreshCraftList();
    }

    [RelayCommand]
    private void DecreaseCraft(CraftRow? row)
    {
        if (row is null) return;
        _craft.SetQuantity(row.SpellId, row.Quantity - 1);
        RefreshCraftList();
    }

    [RelayCommand]
    private void RemoveCraft(CraftRow? row)
    {
        if (row is null) return;
        _craft.Remove(row.SpellId);
        RefreshCraftList();
    }

    [RelayCommand]
    private void ClearCraft()
    {
        _craft.Clear();
        RefreshCraftList();
    }

    [RelayCommand]
    private void CopyItem()
    {
        if (Selected is null) return;
        try
        {
            Clipboard.SetText(Selected.ToPlainText());
            Toast($"Copied {Selected.Name}.");
        }
        catch (Exception)
        {
            Toast("Couldn't reach the clipboard.");
        }
    }

    /// <summary>The shopping list at auction prices (Auctionator), "" without prices.</summary>
    [ObservableProperty] private string _shoppingCost = "";

    [RelayCommand]
    private void CopyShopping()
    {
        if (Shopping.Count == 0) return;
        try
        {
            Clipboard.SetText(string.Join(Environment.NewLine, Shopping.Select(s => s.Text)));
            Toast("Shopping list copied.");
        }
        catch (Exception)
        {
            Toast("Couldn't reach the clipboard.");
        }
    }

    private void RefreshCraftList()
    {
        var entries = _craft.Load();
        CraftList.Clear();
        Shopping.Clear();
        ShoppingCost = "";

        if (entries.Count == 0 || _recipes is null)
        {
            CraftIsEmpty = true;
            CraftSummary = "";
            HasDoneCrafts = false;
            return;
        }

        var ids = entries.Select(e => e.SpellId).ToList();
        var recipes = _recipes.GetMany(ids).ToDictionary(r => r.SpellId);
        var quantities = entries.ToDictionary(e => e.SpellId, e => e.Quantity);
        var done = entries.Where(e => e.Done).Select(e => e.SpellId).ToHashSet();

        foreach (var recipe in recipes.Values.OrderBy(r => done.Contains(r.SpellId)).ThenBy(r => r.Profession).ThenBy(r => r.MinSkill).ThenBy(r => r.Name))
        {
            CraftList.Add(new CraftRow(
                recipe.SpellId, recipe.Name,
                $"{recipe.Profession} · requires {(recipe.SkillEstimated ? "~" : "")}{recipe.MinSkill}"
                + (recipe.GreyAt > 0 ? $" · grey at {recipe.GreyAt}" : ""),
                quantities[recipe.SpellId],
                ItemQuality.BrushFor(recipe.CreatedItemId > 0 ? recipe.Quality : 1),
                RecipeIcons.Or(recipe.IconId, recipe.MinSkill))
            {
                Done = done.Contains(recipe.SpellId),
            });
        }

        var totals = new Dictionary<int, (string Name, int Quantity, int Quality)>(); // by item id
        foreach (var reagent in _recipes.GetReagentRows(ids))
        {
            if (!quantities.TryGetValue(reagent.SpellId, out var crafts) || done.Contains(reagent.SpellId)) continue;
            var add = reagent.Quantity * crafts;
            totals[reagent.ItemId] = totals.TryGetValue(reagent.ItemId, out var existing)
                ? (reagent.Name, existing.Quantity + add, reagent.Quality)
                : (reagent.Name, add, reagent.Quality);
        }
        foreach (var (itemId, (name, quantity, quality)) in totals.OrderBy(t => t.Value.Name))
            Shopping.Add(new ShoppingRow(name, quantity, ItemQuality.BrushFor(quality), itemId));
        var priced = Shopping.Where(s => s.PriceText.Length > 0).ToList();
        ShoppingCost = priced.Count == 0 ? "" :
            $"≈ {ItemText.Money(priced.Sum(s => AuctionPrices.Unit(s.ItemId) * s.Quantity))} at the auction house"
            + (priced.Count < Shopping.Count ? $" ({Shopping.Count - priced.Count} without a price)" : "");

        CraftIsEmpty = CraftList.Count == 0;
        var left = entries.Where(e => !e.Done).Sum(e => e.Quantity);
        CraftSummary = done.Count == 0
            ? $"{CraftList.Count} recipes, {quantities.Values.Sum()} crafts in total"
            : $"{done.Count} of {CraftList.Count} recipes done · {left} crafts left";
        HasDoneCrafts = done.Count > 0;
    }
}
