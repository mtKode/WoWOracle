using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WowCompanion.Core;

namespace WowCompanion.Desktop;

/// <summary>
/// Crafting page, "Level a profession": from your skill upward, the cheapest route (ProfessionPlanner), the materials for
/// it and what's still to get. With the addon, the character's profession ranks, known recipes and bag/bank counts come
/// from the game on their own (WoWOracle.lua is watched, so "/oracle sync" in game shows up here within seconds).
/// </summary>
public partial class MainViewModel
{
    private const string NoCharacter = "Type it in";
    private ProfessionPlanner? _planner;
    private List<CharacterSnapshot> _characters = [];
    private CancellationTokenSource? _planCts;
    private Dictionary<int, int> _manualHave = [];
    private bool _plannerReady;

    public ObservableCollection<string> PlanProfessions { get; } = [];
    public ObservableCollection<string> PlanCharacters { get; } = [];
    public ObservableCollection<PlanStepRow> PlanSteps { get; } = [];
    public ObservableCollection<PlanMaterialRow> PlanMaterials { get; } = [];
    public ObservableCollection<PlanSubCraftRow> PlanSubCrafts { get; } = [];
    public ObservableCollection<string> PlanNotes { get; } = [];

    /// <summary>0 = craft list, 1 = level a profession.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsCraftListMode), nameof(IsPlannerMode))] private int _craftModeIndex;
    public bool IsCraftListMode => CraftModeIndex == 0;
    public bool IsPlannerMode => CraftModeIndex == 1;

    [ObservableProperty] private string _planProfession = "";
    [ObservableProperty] private string _planCharacter = NoCharacter;
    [ObservableProperty] private string _planFromText = "1";
    [ObservableProperty] private string _planToText = "75";
    [ObservableProperty] private bool _planVendorRecipes = true;
    [ObservableProperty] private bool _planDropRecipes;
    [ObservableProperty] private bool _planSubParts = true;
    [ObservableProperty] private bool _planSmelt;
    [ObservableProperty] private string _planSummary = "";
    [ObservableProperty] private string _planCharacterNote = "";
    [ObservableProperty] private bool _planBusy;
    public bool HasPlanSubCrafts => PlanSubCrafts.Count > 0;
    public bool HasPlanNotes => PlanNotes.Count > 0;

    private void UsePlanner(string gameDbPath, IEnumerable<string> addonTexts)
    {
        _planner = new ProfessionPlanner(gameDbPath);
        var previous = PlanProfession;
        PlanProfessions.Clear();
        try { foreach (var p in _planner.Professions()) PlanProfessions.Add(p); }
        catch (Exception) { }

        if (!_plannerReady)
        {
            _plannerReady = true;
            _manualHave = ParseHave(_talentBuilds.GetSetting("craftplan_have"));
            PlanVendorRecipes = _talentBuilds.GetSetting("craftplan_vendor") != "0";
            PlanDropRecipes = _talentBuilds.GetSetting("craftplan_drop") == "1";
            PlanSmelt = _talentBuilds.GetSetting("craftplan_smelt") == "1";
            previous = _talentBuilds.GetSetting("craftplan_profession") ?? "";
            PlanFromText = _talentBuilds.GetSetting("craftplan_from") ?? "1";
            PlanToText = _talentBuilds.GetSetting("craftplan_to") ?? "75";
        }
        PlanProfession = PlanProfessions.Contains(previous) ? previous : PlanProfessions.FirstOrDefault() ?? "";
        LoadCharacters(addonTexts, log: false);
        QueuePlan();
    }

    // ----- characters from the addon -----

    private void LoadCharacters(IEnumerable<string> addonTexts, bool log)
    {
        var before = _characters.ToDictionary(c => c.Name, c => c.Time);
        _characters = CharacterSnapshot.ReadAll(addonTexts);
        var chosen = PlanCharacter;
        PlanCharacters.Clear();
        PlanCharacters.Add(NoCharacter);
        foreach (var c in _characters) PlanCharacters.Add(c.Name);
        var remembered = _talentBuilds.GetSetting("craftplan_character");
        PlanCharacter = PlanCharacters.Contains(chosen) && chosen != NoCharacter ? chosen
            : remembered is not null && PlanCharacters.Contains(remembered) ? remembered
            : _characters.FirstOrDefault()?.Name ?? NoCharacter;

        if (!log) return;
        foreach (var c in _characters.Where(c => !before.TryGetValue(c.Name, out var t) || t != c.Time))
        {
            var skills = string.Join(", ", c.Skills.Where(s => PlanProfessions.Contains(s.Key)).Select(s => $"{s.Key} {s.Value.Rank}/{s.Value.Max}"));
            Log($"Synced from the game: {c.Name}{(skills.Length > 0 ? " — " + skills : "")}, {c.Items.Values.Sum()} items in bags{(c.BankSeen ? " and bank" : "")}.", "addon");
        }
    }

    private CharacterSnapshot? Character => _characters.FirstOrDefault(c => c.Name == PlanCharacter);

    partial void OnPlanCharacterChanged(string value)
    {
        if (_plannerReady && value != NoCharacter) _talentBuilds.SetSetting("craftplan_character", value);
        ApplyCharacterSkill();
        QueuePlan();
    }

    partial void OnPlanProfessionChanged(string value)
    {
        if (_plannerReady && value.Length > 0) _talentBuilds.SetSetting("craftplan_profession", value);
        ApplyCharacterSkill();
        QueuePlan();
    }

    /// <summary>The character's rank in the profession becomes the start; the target is the next trainer cap.</summary>
    private void ApplyCharacterSkill()
    {
        if (Character is not { } c)
        {
            PlanCharacterNote = _characters.Count == 0
                ? "With the WoWOracle addon, your skill, known recipes and bags fill in by themselves."
                : "";
            return;
        }
        if (c.Skills.TryGetValue(PlanProfession, out var skill))
        {
            PlanFromText = skill.Rank.ToString(CultureInfo.InvariantCulture);
            var cap = ProfessionPlanner.Ranks.Select(r => r.Cap).FirstOrDefault(c2 => c2 > skill.Rank, 300);
            PlanToText = Math.Max(cap, skill.Max > skill.Rank ? skill.Max : cap).ToString(CultureInfo.InvariantCulture);
        }
        var known = c.Recipes.TryGetValue(PlanProfession, out var r) ? $"{r.Count} known recipes" : "recipes: open the profession window once";
        PlanCharacterNote = c.Skills.TryGetValue(PlanProfession, out var s2)
            ? $"{c.Name}: {PlanProfession} {s2.Rank}/{s2.Max} · {known} · bags{(c.BankSeen ? " + bank" : " (open the bank once)")}"
            : $"{c.Name} doesn't have {PlanProfession}.";
    }

    partial void OnPlanFromTextChanged(string value) => SaveAndQueue("craftplan_from", value);
    partial void OnPlanToTextChanged(string value) => SaveAndQueue("craftplan_to", value);
    partial void OnPlanVendorRecipesChanged(bool value) => SaveAndQueue("craftplan_vendor", value ? "1" : "0");
    partial void OnPlanDropRecipesChanged(bool value) => SaveAndQueue("craftplan_drop", value ? "1" : "0");
    partial void OnPlanSmeltChanged(bool value) => SaveAndQueue("craftplan_smelt", value ? "1" : "0");
    partial void OnPlanSubPartsChanged(bool value) => QueuePlan();

    private void SaveAndQueue(string key, string value)
    {
        if (_plannerReady) _talentBuilds.SetSetting(key, value);
        QueuePlan();
    }

    // ----- the plan -----

    private void QueuePlan()
    {
        if (!_plannerReady) return;
        _planCts?.Cancel();
        var cts = _planCts = new CancellationTokenSource();
        _ = RunPlanAsync(cts.Token);
    }

    private Dictionary<int, int> Owned()
    {
        var owned = new Dictionary<int, int>(Character?.Items ?? new Dictionary<int, int>());
        foreach (var (item, count) in _manualHave) owned[item] = count; // typed counts win
        return owned;
    }

    private async Task RunPlanAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(250, ct);
            if (_planner is not { } planner || PlanProfession.Length == 0) return;
            var from = int.TryParse(PlanFromText, out var f) ? Math.Clamp(f, 1, 299) : 1;
            var to = int.TryParse(PlanToText, out var t) ? Math.Clamp(t, from + 1, 300) : 300;
            var known = Character?.Recipes.GetValueOrDefault(PlanProfession);
            var options = new PlanOptions(PlanProfession, from, to, PlanVendorRecipes, PlanDropRecipes, PlanSubParts, PlanSmelt,
                known, Owned());
            PlanBusy = true;
            var plan = await Task.Run(() => planner.Plan(options), ct);
            if (ct.IsCancellationRequested) return;
            ShowPlan(plan, from, to);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { PlanSummary = "Couldn't plan: " + ex.Message; }
        finally { if (!ct.IsCancellationRequested) PlanBusy = false; }
    }

    private void ShowPlan(ProfessionPlan plan, int from, int to)
    {
        PlanSteps.Clear();
        foreach (var s in plan.Steps)
        {
            var (brush, text) = s.ColorAtStart switch
            {
                "orange" => (SkillBands.Orange, "orange"),
                "yellow" => (SkillBands.Yellow, "yellow"),
                "green" => (SkillBands.Green, "green"),
                _ => (SkillBands.Grey, "grey"),
            };
            PlanSteps.Add(new PlanStepRow(s.SpellId, $"{s.FromSkill}–{s.ToSkill}", s.Makes > 1 ? $"{s.Recipe}  (makes {s.Makes} each, {s.Crafts * s.Makes} in all)" : s.Recipe, s.IconId,
                $"×{s.Crafts}" + (s.SafeCrafts > s.Crafts ? $" (safe {s.SafeCrafts})" : ""), brush,
                $"starts {text} · yellow {s.Yellow}, green {s.Green}, grey {s.Grey}", s.Source, s.Reagents.Count > 0 ? s.ReagentText : "",
                Money(s.Cost), s.Crafts));
        }

        PlanMaterials.Clear();
        foreach (var m in plan.Materials)
            PlanMaterials.Add(new PlanMaterialRow(m.ItemId, m.Name, ItemQuality.BrushFor(m.Quality), m.IconId, m.Total, m.Owned, m.Source,
                OnHaveTyped));
        PlanSubCrafts.Clear();
        foreach (var s in plan.SubCrafts)
            PlanSubCrafts.Add(new PlanSubCraftRow(s.Name, ItemQuality.BrushFor(s.Quality), s.IconId, $"make {s.Count} · {s.How}"));
        PlanNotes.Clear();
        foreach (var n in plan.Notes) PlanNotes.Add(n.Text);
        OnPropertyChanged(nameof(HasPlanSubCrafts));
        OnPropertyChanged(nameof(HasPlanNotes));

        var crafts = plan.Steps.Sum(s => s.Crafts);
        PlanSummary = plan.Steps.Count == 0
            ? "Nothing to plan for this range."
            : $"{PlanProfession} {from} → {plan.ReachedSkill}: {plan.Steps.Count} recipes, about {crafts} crafts · materials worth ≈ {Money(plan.TotalCost)} (vendor values, no auction prices)"
              + (plan.ReachedSkill < to ? $" · stops at {plan.ReachedSkill}" : "");
    }

    private void OnHaveTyped(PlanMaterialRow row)
    {
        _manualHave[row.ItemId] = row.Have;
        _talentBuilds.SetSetting("craftplan_have", string.Join(",", _manualHave.Select(h => $"{h.Key}:{h.Value}")));
    }

    private static Dictionary<int, int> ParseHave(string? text)
    {
        var result = new Dictionary<int, int>();
        foreach (var pair in (text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var p = pair.Split(':');
            if (p.Length == 2 && int.TryParse(p[0], out var id) && int.TryParse(p[1], out var n)) result[id] = n;
        }
        return result;
    }

    /// <summary>"1g 23s 45c".</summary>
    private static string Money(long copper)
    {
        var g = copper / 10000;
        var s = copper / 100 % 100;
        var c = copper % 100;
        return g > 0 ? $"{g}g {s}s" : s > 0 ? $"{s}s {c}c" : $"{c}c";
    }

    [RelayCommand]
    private void ReplanFromHave()
    {
        QueuePlan(); // typed "have" counts also change which recipes are cheapest
    }

    [RelayCommand]
    private void ClearTypedHave()
    {
        _manualHave.Clear();
        _talentBuilds.SetSetting("craftplan_have", "");
        QueuePlan();
    }

    /// <summary>Every step into the craft list, with its number of crafts.</summary>
    [RelayCommand]
    private void SendPlanToCraft()
    {
        if (PlanSteps.Count == 0) return;
        var current = _craft.Load().ToDictionary(e => e.SpellId, e => e.Quantity);
        foreach (var step in PlanSteps)
        {
            if (!current.ContainsKey(step.SpellId)) _craft.Add(step.SpellId);
            current[step.SpellId] = current.GetValueOrDefault(step.SpellId) + step.Crafts;
            _craft.SetQuantity(step.SpellId, current[step.SpellId]);
        }
        RefreshCraftList();
        Toast($"Added {PlanSteps.Count} recipes to the craft list.");
    }

    [RelayCommand]
    private void CopyPlan()
    {
        var b = new StringBuilder();
        b.AppendLine(PlanSummary);
        foreach (var s in PlanSteps) b.AppendLine($"{s.Range}: {s.Recipe} {s.CraftsText} ({s.Source})");
        b.AppendLine();
        b.AppendLine("Materials to get:");
        foreach (var m in PlanMaterials.Where(m => m.ToGet > 0)) b.AppendLine($"{m.ToGet}× {m.Name}");
        try
        {
            Clipboard.SetText(b.ToString());
            Toast("Plan copied.");
        }
        catch (Exception) { Toast("Couldn't reach the clipboard."); }
    }

    // ----- live sync: re-read the addon's file when the game writes it -----

    private readonly List<FileSystemWatcher> _addonWatchers = [];
    private CancellationTokenSource? _addonReadCts;

    /// <summary>
    /// Watches every client's WTF folder for WoWOracle.lua (also before it exists: a fresh install writes it on the
    /// first logout or /reload).
    /// </summary>
    private void WatchAddonFiles()
    {
        if (_addonWatchers.Count > 0) return;
        try
        {
            foreach (var root in WowInstall.Roots(paths.Root))
            foreach (var client in WowInstall.ForeverClients(root))
            {
                // A fresh client has no WTF folder until the game's first start: watch the whole client then.
                var wtf = Path.Combine(client, "WTF");
                var watched = Directory.Exists(wtf) ? wtf : client;
                var watcher = new FileSystemWatcher(watched, "WoWOracle.lua")
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                    EnableRaisingEvents = true,
                };
                watcher.Changed += (_, _) => AddonFileChanged();
                watcher.Created += (_, _) => AddonFileChanged();
                watcher.Renamed += (_, _) => AddonFileChanged();
                _addonWatchers.Add(watcher);
            }
        }
        catch (Exception)
        {
            // No live sync; the next start reads the file anyway.
        }
    }

    /// <summary>
    /// The addon's file changed (logout, /reload, /oracle sync): the same pass as at start-up (quests, items, sightings;
    /// talent trees stay a start-up thing) in the background, then characters, own gear and the planner.
    /// </summary>
    private void AddonFileChanged()
    {
        _addonReadCts?.Cancel();
        var cts = _addonReadCts = new CancellationTokenSource();
        var gameDbPath = _gameDbPath;
        var version = _dataVersion;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(1500, cts.Token); // the game writes the file in pieces
                AddonPassResult result;
                await _addonPassLock.WaitAsync(cts.Token);
                try { result = AddonPass(gameDbPath, version, talents: false); }
                finally { _addonPassLock.Release(); }

                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    LoadCharacters(result.Texts, log: true);
                    ApplyCharacterSkill();
                    QueuePlan();
                    SyncOwnGear(result.Texts);
                    if (gameDbPath.Length > 0) UnhideSeenItems(gameDbPath);
                    ReportAddonPass(result, version);
                });
            }
            catch (OperationCanceledException) { } // a newer change replaced this one
            catch (Exception) { } // addon data is a bonus; the next change or start tries again
        });
    }
}
