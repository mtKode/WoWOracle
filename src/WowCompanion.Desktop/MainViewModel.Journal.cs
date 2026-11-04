using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WowCompanion.Core;

namespace WowCompanion.Desktop;

/// <summary>
/// Journal (Ctrl+0): a page per zone, like the quest log. A short lore summary, a few "tales" (quest chains worth
/// following, picked by length, lore themes, dungeons and a hand-picked list), revealed a few at a time, and one gear
/// hint: a quest reward in the zone that beats what the active gear set wears. Kept apart from the other pages so it
/// can grow (map, rare spawns) on its own.
/// </summary>
public partial class MainViewModel
{
    private const int TalesAtFirst = 3;
    private ZoneJournalService? _journal;
    private int _journalRun;
    private List<TaleRow> _allTales = [];
    private int _shownTales = TalesAtFirst;
    private bool _journalReady;

    public ObservableCollection<ZoneLore> JournalZones { get; } = [];
    public ObservableCollection<TaleRow> Tales { get; } = [];
    public ObservableCollection<UpgradeRow> JournalGearHints { get; } = [];

    [ObservableProperty] private ZoneLore? _selectedJournalZone;
    /// <summary>0 both, 1 Alliance, 2 Horde.</summary>
    [ObservableProperty] private int _journalSideIndex;
    [ObservableProperty] private bool _canSeekTale;
    [ObservableProperty] private bool _journalBusy;
    [ObservableProperty] private string _journalTalesNote = "";
    [ObservableProperty] private string _journalGearNote = "";
    /// <summary>The gear hint stays hidden until asked for: the Journal is about the land first.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(JournalGearHidden))] private bool _journalGearRevealed;
    public bool JournalGearHidden => !JournalGearRevealed;

    public bool IsJournalView => CurrentView == AppView.Journal;

    public string JournalZoneLine => SelectedJournalZone is { } z
        ? $"{z.Continent} · {(z.Levels == "City" ? "city" : "levels " + z.Levels)} · {z.Faction}"
        : "";

    private JournalSide Side => JournalSideIndex switch { 1 => JournalSide.Alliance, 2 => JournalSide.Horde, _ => JournalSide.Both };

    private void UseJournal(string gameDbPath)
    {
        _journal = new ZoneJournalService(gameDbPath);
        if (JournalZones.Count == 0)
            foreach (var zone in ZoneLoreBook.All) JournalZones.Add(zone);

        if (!_journalReady)
        {
            _journalReady = true;
            var side = _talentBuilds.GetSetting("journal_side");
            JournalSideIndex = side is "1" or "2" or "0" ? int.Parse(side, CultureInfo.InvariantCulture) : DefaultSide();
            var remembered = _talentBuilds.GetSetting("journal_zone");
            SelectedJournalZone = JournalZones.FirstOrDefault(z => z.Zone == remembered) ?? DefaultZone();
        }
        else
        {
            _ = RefreshJournalAsync();
        }
    }

    /// <summary>The side of the active gear set's race (Human, Dwarf, Night Elf, Gnome = Alliance).</summary>
    private int DefaultSide() => SelectedGearSet?.RaceId switch
    {
        1 or 3 or 4 or 7 => 1,
        2 or 5 or 6 or 8 => 2,
        _ => 0,
    };

    private ZoneLore? DefaultZone()
    {
        var level = SelectedGearSet?.Level ?? 1;
        var faction = Side == JournalSide.Horde ? "Alliance" : "Horde"; // the other side's starting zones are skipped
        return JournalZones
            .Where(z => z.Levels != "City" && z.Faction != faction)
            .OrderBy(z => Math.Abs(z.SortLevel - (level - 5)))
            .FirstOrDefault() ?? JournalZones.FirstOrDefault();
    }

    partial void OnSelectedJournalZoneChanged(ZoneLore? value)
    {
        JournalGearRevealed = false;
        JournalGearHints.Clear();
        JournalGearNote = "";
        OnPropertyChanged(nameof(JournalZoneLine));
        if (value is not null && _journalReady) _talentBuilds.SetSetting("journal_zone", value.Zone);
        _ = RefreshJournalAsync();
    }

    partial void OnJournalSideIndexChanged(int value)
    {
        if (_journalReady) _talentBuilds.SetSetting("journal_side", value.ToString(CultureInfo.InvariantCulture));
        _ = RefreshJournalAsync();
    }

    private async Task RefreshJournalAsync()
    {
        if (_journal is not { } journal || SelectedJournalZone is not { } zone) return;
        var run = ++_journalRun;
        var side = Side;
        JournalBusy = true;
        try
        {
            var tales = await Task.Run(() => journal.Tales(zone, side));
            if (run != _journalRun) return;

            _allTales = tales.Select(ToRow).ToList();
            _shownTales = TalesAtFirst;
            ShowTales();
            JournalTalesNote = _allTales.Count == 0 ? "The Oracle hears no great tales here. Perhaps the land itself is the story." : "";
            if (JournalGearRevealed) await RefreshJournalGearAsync(run);
        }
        catch (Exception ex)
        {
            if (run == _journalRun) JournalTalesNote = "Couldn't read the quest data: " + ex.Message;
        }
        finally
        {
            if (run == _journalRun) JournalBusy = false;
        }

        TaleRow ToRow(Tale t)
        {
            var inv = CultureInfo.InvariantCulture;
            var levels = t.MinLevel == t.MaxLevel ? $"level {t.MinLevel}" : $"levels {t.MinLevel}–{t.MaxLevel}";
            var meta = new List<string> { t.Steps.Count > 1 ? $"A chain of {t.Steps.Count} quests" : "A single quest", levels };
            if (side == JournalSide.Both && t.Side != "Both") meta.Add(t.Side);
            if (t.ReachesDungeon) meta.Add("leads into a dungeon");
            var start = t.Giver.Length > 0
                ? $"Begins with {t.Giver}{(t.GiverPlace.Length > 0 ? ", " + t.GiverPlace : "")}."
                : "Begins with something found, not someone met.";
            var steps = t.Steps.Select((s, i) => new TaleStepRow(i + 1, s.Title,
                s.Level > 0 ? $"level {s.Level.ToString(inv)}" + (s.Zone.Length > 0 && s.Zone != zone.Zone ? $" · {s.Zone}" : "") : s.Zone,
                s.QuestId)).ToList();
            return new TaleRow(t.Title, string.Join(" · ", meta), string.Join(" · ", t.Themes),
                t.Teaser.Length > 0 ? $"“{t.Teaser}”" : "", start, t.Featured, steps);
        }
    }

    private void ShowTales()
    {
        Tales.Clear();
        foreach (var tale in _allTales.Take(_shownTales)) Tales.Add(tale);
        CanSeekTale = _allTales.Count > _shownTales;
    }

    /// <summary>One more tale at a time: the journal hints, it doesn't dump everything at once.</summary>
    [RelayCommand]
    private void SeekTale()
    {
        _shownTales++;
        ShowTales();
    }

    [RelayCommand]
    private void ToggleTale(TaleRow? tale)
    {
        if (tale is not null) tale.IsOpen = !tale.IsOpen;
    }

    /// <summary>A quest in a trail: search for what it rewards.</summary>
    [RelayCommand]
    private void ShowQuestRewards(TaleStepRow? step)
    {
        if (step is null) return;
        Query = $"quest:\"{step.Title}\" ";
    }

    [RelayCommand]
    private async Task RevealGearHint()
    {
        JournalGearRevealed = true;
        JournalGearNote = "The Oracle searches…";
        await RefreshJournalGearAsync(_journalRun);
    }

    private async Task RefreshJournalGearAsync(int run)
    {
        JournalGearHints.Clear();
        if (SelectedGearSet is not { } set)
        {
            JournalGearNote = "Choose a gear set on the Gear page and the Oracle will look for upgrades here.";
            return;
        }
        if (_journal is not { } journal || _upgradeFinder is not { } finder || SelectedJournalZone is not { } zone) return;

        var side = Side;
        var maxLevel = set.Level + 3;
        var hints = await Task.Run(() =>
        {
            var worn = SetGearBySlot(set.Id);
            var quests = journal.QuestIds(zone.Zone, side);
            return finder.QuestRewardUpgrades(set.ClassId, maxLevel, worn, quests, 2);
        });
        if (run != _journalRun) return;

        var inv = CultureInfo.InvariantCulture;
        foreach (var hint in hints)
        {
            var o = hint.Option;
            var gain = o.Gain is { } g ? "+" + g.ToString("0", inv) + "%" : "empty slot";
            var place = journal.GiverPlace(hint.QuestId);
            JournalGearHints.Add(new UpgradeRow(o.ItemId, o.Name, ItemQuality.BrushFor(o.Quality), o.IconId, gain, CompareColors.Up,
                o.StatLine, $"{GearSlots.NameOf(hint.Slot)} · from \"{hint.QuestTitle}\"" + (place.Length > 0 ? $" · {place}" : ""),
                SourceColors.Quest, $"ilvl {o.ItemLevel} · req {o.RequiredLevel}"));
        }
        JournalGearNote = JournalGearHints.Count > 0
            ? $"For {set.Name} (level {set.Level}): quest rewards here that beat what you wear."
            : $"Nothing here beats what {set.Name} wears. Your gear is ahead of this land.";
    }
}
