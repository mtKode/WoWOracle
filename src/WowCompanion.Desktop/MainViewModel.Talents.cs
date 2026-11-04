using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WowCompanion.Core;

namespace WowCompanion.Desktop;

/// <summary>
/// Talent planner. Classic rules: first point at level 10, one per level after (51 at 60), a tier opens after
/// 5 points per tier in that tree, and a prerequisite talent must reach its required rank.
/// Left click adds a rank, right click removes one. The build is saved per class as you go.
/// </summary>
public partial class MainViewModel
{
    private TalentService? _talentService;
    private bool _loadingTalents;
    private IReadOnlyList<TalentTree> _talentTrees = [];

    public ObservableCollection<ClassInfo> TalentClasses { get; } = [];
    public ObservableCollection<TalentTreeView> Trees { get; } = [];
    public ObservableCollection<TalentSetInfo> TalentSets { get; } = [];

    [ObservableProperty] private TalentSetInfo? _selectedTalentSet;
    [ObservableProperty] private string _talentSetName = "";

    /// <summary>All three trees side by side (default) or one tree with tabs.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowOneTree))]
    private bool _showAllTrees = true;

    public bool ShowOneTree => !ShowAllTrees;
    private bool _talentViewLoaded;

    [ObservableProperty] private ClassInfo? _selectedTalentClass;
    [ObservableProperty] private TalentTreeView? _currentTree;
    [ObservableProperty] private int _talentLevel = 60;
    [ObservableProperty] private string _talentSummary = "";
    [ObservableProperty] private string _pointsLeftText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTalentDetail), nameof(NoTalentDetail))]
    private TalentDetail? _talentDetail;

    public bool HasTalentDetail => TalentDetail is not null;
    public bool NoTalentDetail => TalentDetail is null;

    private int PointsAvailable => Math.Clamp(TalentLevel - 9, 0, 51);
    private int PointsSpent => Trees.Sum(t => t.Points);

    partial void OnSelectedTalentClassChanged(ClassInfo? value) => LoadTalentSets(null);

    partial void OnShowAllTreesChanged(bool value)
    {
        if (_talentViewLoaded) _talentBuilds.SetSetting("talents_all_trees", value ? "1" : "0");
    }

    /// <summary>Fills the set picker for the selected class and opens the active (or given) set.</summary>
    private void LoadTalentSets(int? select)
    {
        if (!_talentViewLoaded)
        {
            _talentViewLoaded = true;
            ShowAllTrees = _talentBuilds.GetSetting("talents_all_trees") != "0";
        }

        TalentSets.Clear();
        if (SelectedTalentClass is null) { SelectedTalentSet = null; return; }
        var active = _talentBuilds.EnsureActive(SelectedTalentClass.Id);
        foreach (var set in _talentBuilds.List(SelectedTalentClass.Id)) TalentSets.Add(set);
        var target = select ?? active;
        var chosen = TalentSets.FirstOrDefault(s => s.Id == target) ?? TalentSets.FirstOrDefault();
        if (chosen == SelectedTalentSet) LoadTalents(); // same record: the change handler won't fire
        SelectedTalentSet = chosen;
    }

    partial void OnSelectedTalentSetChanged(TalentSetInfo? value)
    {
        FlushTalentSave(); // finish the previous set's save before loading another
        TalentSetName = value?.Name ?? "";
        if (value is not null) _talentBuilds.SetActive(value.ClassId, value.Id);
        LoadTalents();
    }

    [RelayCommand]
    private void NewTalentSet()
    {
        if (SelectedTalentClass is null) return;
        var id = _talentBuilds.Create(SelectedTalentClass.Id, $"Set {TalentSets.Count + 1}", TalentLevel, new Dictionary<int, int>());
        LoadTalentSets(id);
        Toast("New empty talent set. Rename it above the tree.");
    }

    [RelayCommand]
    private void DuplicateTalentSet()
    {
        if (SelectedTalentClass is null || SelectedTalentSet is null) return;
        var ranks = Trees.SelectMany(t => t.Cells).Where(c => c.Rank > 0).ToDictionary(c => c.Id, c => c.Rank);
        var id = _talentBuilds.Create(SelectedTalentClass.Id, SelectedTalentSet.Name + " copy", TalentLevel, ranks);
        LoadTalentSets(id);
        Toast("Copied the talent set.");
    }

    [RelayCommand]
    private void RenameTalentSet()
    {
        if (SelectedTalentSet is not { } set || TalentSetName.Trim().Length == 0) return;
        _talentBuilds.Rename(set.Id, TalentSetName.Trim());
        LoadTalentSets(set.Id);
        Toast($"Renamed to {TalentSetName.Trim()}.");
    }

    [RelayCommand]
    private void DeleteTalentSet()
    {
        if (SelectedTalentSet is not { } set) return;
        if (TalentSets.Count <= 1)
        {
            Toast("A class keeps at least one talent set. Reset it instead.");
            return;
        }
        _talentBuilds.Delete(set.Id);
        LoadTalentSets(TalentSets.First(s => s.Id != set.Id).Id);
        Toast($"Deleted talent set {set.Name}.");
    }

    partial void OnTalentLevelChanged(int value)
    {
        if (value is < 1 or > 60)
        {
            TalentLevel = Math.Clamp(value, 1, 60);
            return;
        }
        Recompute();
        SaveTalents();
    }

    partial void OnCurrentTreeChanged(TalentTreeView? oldValue, TalentTreeView? newValue)
    {
        if (oldValue is not null) oldValue.IsSelected = false;
        if (newValue is not null) newValue.IsSelected = true;
        TalentDetail = null;
    }

    private void LoadTalents()
    {
        Trees.Clear();
        TalentDetail = null;
        _talentTrees = [];
        if (_talentService is null || SelectedTalentClass is null || SelectedTalentSet is null) return;

        _loadingTalents = true;
        var build = _talentBuilds.Load(SelectedTalentSet.Id);
        TalentLevel = build.Level;

        var all = new Dictionary<int, TalentCell>();
        _talentTrees = _talentService.GetTrees(SelectedTalentClass.Id);
        foreach (var tree in _talentTrees)
        {
            var view = new TalentTreeView(tree);
            foreach (var info in tree.Talents)
            {
                var cell = new TalentCell(info, view) { Rank = Math.Min(build.Ranks.GetValueOrDefault(info.Id), info.MaxRank) };
                view.Cells.Add(cell);
                all[info.Id] = cell;
            }
            Trees.Add(view);
            _ = view.LoadArtAsync();
        }
        foreach (var cell in all.Values)
            cell.Prereq = cell.Info.PrereqTalent > 0 ? all.GetValueOrDefault(cell.Info.PrereqTalent) : null;

        _loadingTalents = false;
        CurrentTree = Trees.FirstOrDefault();
        Recompute();
    }

    // ----- Rules -----

    private bool CanAdd(TalentCell cell) =>
        PointsSpent < PointsAvailable
        && cell.Rank < cell.MaxRank
        && cell.Tree.Points >= cell.Tier * 5
        && PrereqMet(cell);

    private static bool PrereqMet(TalentCell cell) =>
        cell.Prereq is null || cell.Prereq.Rank >= cell.Info.PrereqRank + 1;

    /// <summary>Every learned talent still has its tier unlocked by lower tiers, and its prerequisite.</summary>
    private static bool IsValid(TalentTreeView tree) =>
        tree.Cells.Where(c => c.Rank > 0).All(c => tree.PointsBelow(c.Tier) >= c.Tier * 5 && PrereqMet(c));

    private void Recompute()
    {
        if (_loadingTalents) return;
        foreach (var tree in Trees) tree.Points = tree.Cells.Sum(c => c.Rank);
        foreach (var tree in Trees)
        {
            foreach (var cell in tree.Cells)
            {
                cell.State = cell.Rank >= cell.MaxRank ? TalentState.Max
                    : cell.Rank > 0 ? TalentState.Partial
                    : CanAdd(cell) ? TalentState.Available
                    : TalentState.Locked;
            }
        }

        foreach (var tree in Trees)
        {
            tree.Arrows.Clear();
            foreach (var cell in tree.Cells)
            {
                if (cell.Prereq is { } pre && ReferenceEquals(pre.Tree, tree))
                    tree.Arrows.Add(TalentArrow.Between(pre, cell, PrereqMet(cell)));
            }
        }

        var left = PointsAvailable - PointsSpent;
        PointsLeftText = left >= 0 ? $"{left} points left" : $"{-left} points over for level {TalentLevel}";
        TalentSummary = SelectedTalentClass is null
            ? ""
            : $"{SelectedTalentClass.Name} {string.Join("/", Trees.Select(t => t.Points))}";
        if (IsSharePanelOpen) _ = RefreshShareLinksAsync();
    }

    // ----- Commands -----

    [RelayCommand]
    private void AddRank(TalentCell? cell)
    {
        if (cell is null) return;
        if (CanAdd(cell))
        {
            cell.Rank++;
            Changed();
        }
        HoverTalent(cell);
    }

    [RelayCommand]
    private void RemoveRank(TalentCell? cell)
    {
        if (cell is null || cell.Rank == 0) return;
        cell.Rank--;
        if (!IsValid(cell.Tree))
        {
            cell.Rank++;
            Toast($"Other talents depend on {cell.Name}. Remove those first.");
            return;
        }
        Changed();
        HoverTalent(cell);
    }

    [RelayCommand]
    private void ResetTree()
    {
        if (CurrentTree is null) return;
        foreach (var cell in CurrentTree.Cells) cell.Rank = 0;
        Changed();
    }

    [RelayCommand]
    private void ResetAllTalents()
    {
        foreach (var cell in Trees.SelectMany(t => t.Cells)) cell.Rank = 0;
        Changed();
    }

    /// <summary>Readable build for chat or notes: points per tree, each talent with its rank, then both calculator links.</summary>
    [RelayCommand]
    private async Task CopyTalents()
    {
        if (SelectedTalentClass is null) return;
        var lines = new List<string> { $"{TalentSummary} · level {TalentLevel}" };
        foreach (var tree in Trees.Where(t => t.Points > 0))
        {
            var picks = tree.Cells.Where(c => c.Rank > 0).OrderBy(c => c.Tier).ThenBy(c => c.Column)
                .Select(c => $"{c.Name} {c.Rank}/{c.MaxRank}");
            lines.Add("");
            lines.Add($"{tree.Name} ({tree.Points}): {string.Join(", ", picks)}");
        }
        if (TalentCode.Slug(SelectedTalentClass) is var slug && (await TalentLayouts.GetAsync(slug)).Layout is { } layout)
        {
            var (external, _) = TalentCode.ToExternal(_talentTrees, CurrentRanks(), layout, "Icy Veins/Wowhead");
            lines.Add("");
            lines.Add("Icy Veins: " + TalentCode.IcyVeinsLink(layout, external));
            lines.Add("Wowhead: " + TalentCode.WowheadLink(layout, external));
        }
        try
        {
            Clipboard.SetText(string.Join(Environment.NewLine, lines));
            Toast("Talent build copied.");
        }
        catch (Exception)
        {
            Toast("Couldn't reach the clipboard.");
        }
    }

    /// <summary>Called from the window when the mouse moves over a talent.</summary>
    public void HoverTalent(TalentCell cell)
    {
        // In the all-trees view, hovering a tree makes it the one "Reset tree" acts on.
        if (!ReferenceEquals(CurrentTree, cell.Tree)) CurrentTree = cell.Tree;
        var info = cell.Info;
        var descriptions = info.RankDescriptions;
        string Desc(int index) => index >= 0 && index < descriptions.Count ? descriptions[index] : "";

        var current = cell.Rank == 0 ? Desc(0) : Desc(cell.Rank - 1);
        var next = cell.Rank > 0 && cell.Rank < cell.MaxRank ? Desc(cell.Rank) : "";

        var requirements = new List<string>();
        if (cell.Tier > 0 && cell.Tree.Points < cell.Tier * 5)
            requirements.Add($"Requires {cell.Tier * 5} points in {cell.Tree.Name} Talents");
        if (cell.Prereq is { } pre && !PrereqMet(cell))
            requirements.Add($"Requires {info.PrereqRank + 1} point{(info.PrereqRank > 0 ? "s" : "")} in {pre.Name}");
        if (cell.Rank < cell.MaxRank && PointsSpent >= PointsAvailable)
            requirements.Add($"No points left at level {TalentLevel}");

        TalentDetail = new TalentDetail(
            cell.Name,
            $"Rank {cell.Rank}/{cell.MaxRank}",
            current,
            next.Length > 0 ? "Next rank:" : "",
            next,
            requirements);
        cell.Detail = TalentDetail;
    }

    private void Changed()
    {
        Recompute();
        SaveTalents();
    }

    private CancellationTokenSource? _talentSaveCts;
    private (int SetId, int Level, Dictionary<int, int> Ranks)? _pendingTalentSave;

    /// <summary>
    /// Saves 5 seconds after the last change, off the UI thread, so clicking talents stays instant.
    /// Flushed right away when you leave the Talents page, switch set or class, or close the window.
    /// </summary>
    private void SaveTalents()
    {
        if (_loadingTalents || SelectedTalentSet is null) return;
        var ranks = Trees.SelectMany(t => t.Cells).Where(c => c.Rank > 0).ToDictionary(c => c.Id, c => c.Rank);
        _pendingTalentSave = (SelectedTalentSet.Id, TalentLevel, ranks);

        _talentSaveCts?.Cancel();
        var cts = _talentSaveCts = new CancellationTokenSource();
        var save = _pendingTalentSave.Value;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), cts.Token);
                _talentBuilds.Save(save.SetId, save.Level, save.Ranks);
                if (!cts.IsCancellationRequested) _pendingTalentSave = null;
            }
            catch (TaskCanceledException) { }
            catch (Exception)
            {
                System.Windows.Application.Current?.Dispatcher.InvokeAsync(() => Toast("Couldn't save the talent build."));
            }
        });
    }

    /// <summary>Writes a waiting talent save right now (window closing, switching set before the delay ran out).</summary>
    public void FlushTalentSave()
    {
        _talentSaveCts?.Cancel();
        if (_pendingTalentSave is not { } save) return;
        _pendingTalentSave = null;
        try { _talentBuilds.Save(save.SetId, save.Level, save.Ranks); }
        catch (Exception) { }
    }
}
