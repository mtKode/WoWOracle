using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WowCompanion.Core;
using WowCompanion.Core.Calc;

namespace WowCompanion.Desktop;

/// <summary>A row in the race comparison table.</summary>
public sealed record RaceCompareRow(string Race, string PerCast, string Dps, string Crit, bool IsCurrent);

/// <summary>
/// Calculator page. The character (class, race, level, naked stats, buffs) lives here; a gear set is only the
/// items. Talents come from a talent set of the chosen class. Works without gear too.
/// </summary>
public partial class MainViewModel
{
    private bool _loadingCalc;
    private (int ClassId, int Level) _spellListFor;
    private List<CalcSpell> _allCalcSpells = [];
    private CharacterSheet? _sheet;
    private List<TalentSpell> _talentSpells = [];
    private Dictionary<int, List<EffectRow>> _effects = new();
    private List<GearItem> _calcGear = [];
    private GearItem? _calcMainHand;
    private GearItem? _calcRanged;
    private bool _characterLoaded;

    public ObservableCollection<SheetRow> SheetLines { get; } = [];
    public ObservableCollection<SheetRow> DamageLines { get; } = [];
    public ObservableCollection<string> DamageModifiers { get; } = [];
    public ObservableCollection<string> CalcNotes { get; } = [];
    public ObservableCollection<BuffToggle> BuffToggles { get; } = [];
    public ObservableCollection<SpellRow> CalcSpells { get; } = [];
    public ObservableCollection<SpellRow> CalcRanks { get; } = [];
    public ObservableCollection<SheetRow> WeaponLines { get; } = [];
    public ObservableCollection<RaceCompareRow> RaceRows { get; } = [];
    public IReadOnlyList<string> TargetOptions { get; } = ["Same level", "+1 level", "+2 levels", "+3 levels (boss)"];

    // ----- Character -----
    [ObservableProperty] private ClassInfo? _calcClass;
    [ObservableProperty] private RaceInfo? _calcRace;
    [ObservableProperty] private int _calcLevel = 60;
    [ObservableProperty] private string _nakedStr = "";
    [ObservableProperty] private string _nakedAgi = "";
    [ObservableProperty] private string _nakedSta = "";
    [ObservableProperty] private string _nakedInt = "";
    [ObservableProperty] private string _nakedSpi = "";

    // Gear set used by the calculator; "No gear" is allowed.
    public ObservableCollection<GearSetInfo> CalcGearChoices { get; } = [];
    [ObservableProperty] private GearSetInfo? _calcGearSet;

    // Talent set used by the calculator (remembered per class; defaults to the class's active set).
    public ObservableCollection<TalentSetInfo> CalcTalentSets { get; } = [];
    [ObservableProperty] private TalentSetInfo? _selectedCalcTalentSet;
    private bool _loadingCalcTalents;

    // Saved calculations.
    public ObservableCollection<CalcSnapshotInfo> Snapshots { get; } = [];
    [ObservableProperty] private CalcSnapshotInfo? _selectedSnapshot;
    [ObservableProperty] private string _snapshotName = "";
    [ObservableProperty] private string _snapshotCompare = "";

    [ObservableProperty] private string _calcHeader = "";
    [ObservableProperty] private string _calcTalentLine = "";
    [ObservableProperty] private string _calcFilter = "";
    [ObservableProperty] private SpellRow? _selectedCalcSpell;
    [ObservableProperty] private SpellRow? _selectedCalcRank;
    [ObservableProperty] private bool _hasWeapons;
    [ObservableProperty] private bool _hasRaceRows;
    [ObservableProperty] private string _selectedSpellTitle = "Pick a spell";
    [ObservableProperty] private int _targetLevelDiff = 3;

    private static readonly GearSetInfo NoGear = new(0, "No gear", 0, 0, 60, "");

    partial void OnCurrentViewChanged(AppView value)
    {
        RecordNavigation();
        FlushTalentSave(); // the calculator reads talent sets from disk
        if (value is AppView.Calc or AppView.Gear) Recalculate();
        if (value == AppView.Loot) _ = EnsureLootAsync();
        if (value == AppView.Journal) _ = RefreshJournalAsync(); // the gear set may have changed
        if (value != AppView.Notes) SaveNoteIfDirty();
    }

    partial void OnCalcFilterChanged(string value) => FilterCalcSpells();

    partial void OnSelectedCalcSpellChanged(SpellRow? value)
    {
        // Ranks of the picked spell, highest first; the highest is selected.
        CalcRanks.Clear();
        if (value is not null)
        {
            foreach (var s in _allCalcSpells.Where(x => x.Name == value.Name))
                CalcRanks.Add(new SpellRow(s.Id, s.Rank.Length > 0 ? s.Rank : s.Name, $"level {s.SpellLevel}", s.IconId));
        }
        SelectedCalcRank = CalcRanks.FirstOrDefault();
        ComputeDamage();
    }

    partial void OnSelectedCalcRankChanged(SpellRow? value) => ComputeDamage();

    partial void OnTargetLevelDiffChanged(int value)
    {
        if (_loadingCalc) return;
        _gearSets.SetSetting("calc_target", value.ToString(CultureInfo.InvariantCulture));
        Recalculate();
    }

    partial void OnCalcClassChanged(ClassInfo? value)
    {
        if (_loadingCalc) return;
        LoadNaked();
        RefreshCalcTalentSets();
        SaveCharacter();
    }

    partial void OnCalcRaceChanged(RaceInfo? value)
    {
        if (_loadingCalc) return;
        LoadNaked();
        SaveCharacter();
    }

    partial void OnCalcLevelChanged(int value)
    {
        if (value is < 1 or > 60) { CalcLevel = Math.Clamp(value, 1, 60); return; }
        if (!_loadingCalc) SaveCharacter();
    }

    partial void OnCalcGearSetChanged(GearSetInfo? value)
    {
        if (_loadingCalc) return;
        _gearSets.SetSetting("calc_gear_set", (value?.Id ?? 0).ToString(CultureInfo.InvariantCulture));
        Recalculate();
    }

    partial void OnNakedStrChanged(string value) => NakedChanged();
    partial void OnNakedAgiChanged(string value) => NakedChanged();
    partial void OnNakedStaChanged(string value) => NakedChanged();
    partial void OnNakedIntChanged(string value) => NakedChanged();
    partial void OnNakedSpiChanged(string value) => NakedChanged();

    // Naked stats are remembered per class + race, so switching race shows that race's numbers (or estimates).
    private string NakedKey() => $"naked:{CalcClass?.Id ?? 0}:{CalcRace?.Id ?? 0}";

    private string NakedText() => string.Join(',', NakedStr.Trim(), NakedAgi.Trim(), NakedSta.Trim(), NakedInt.Trim(), NakedSpi.Trim());

    private void NakedChanged()
    {
        if (_loadingCalc) return;
        _gearSets.SetSetting(NakedKey(), NakedText());
        Recalculate();
    }

    private void LoadNaked()
    {
        var parts = (_gearSets.GetSetting(NakedKey()) ?? "").Split(',');
        string Part(int i) => i < parts.Length ? parts[i] : "";
        var was = _loadingCalc;
        _loadingCalc = true;
        (NakedStr, NakedAgi, NakedSta, NakedInt, NakedSpi) = (Part(0), Part(1), Part(2), Part(3), Part(4));
        _loadingCalc = was;
    }

    private void SaveCharacter()
    {
        _gearSets.SetSetting("calc_character",
            string.Join(',', CalcClass?.Id ?? 0, CalcRace?.Id ?? 0, CalcLevel));
        Recalculate();
    }

    /// <summary>First visit: restore the last character, gear choice, buffs and target.</summary>
    private void LoadCharacter()
    {
        _loadingCalc = true;
        var saved = (_gearSets.GetSetting("calc_character") ?? "").Split(',');
        int Saved(int i, int fallback) => i < saved.Length && int.TryParse(saved[i], out var v) && v > 0 ? v : fallback;

        CalcClass = TalentClasses.FirstOrDefault(c => c.Id == Saved(0, 0)) ?? SelectedTalentClass ?? TalentClasses.FirstOrDefault();
        CalcRace = Races.FirstOrDefault(r => r.Id == Saved(1, 0))
                   ?? Races.FirstOrDefault(r => CalcClass is null || CharacterBase.CanBe(CalcClass.Id, r.Id))
                   ?? Races.FirstOrDefault();
        CalcLevel = Saved(2, 60);
        TargetLevelDiff = int.TryParse(_gearSets.GetSetting("calc_target"), out var target) ? Math.Clamp(target, 0, 3) : 3;

        var on = (_gearSets.GetSetting("calc_buffs") ?? "").Split(',').ToHashSet();
        BuffToggles.Clear();
        foreach (var buff in BuffCatalog.All)
            BuffToggles.Add(new BuffToggle(buff, BuffsChanged) { IsOn = on.Contains(buff.Key) });

        LoadNaked();
        RefreshCalcGearChoices();
        _loadingCalc = false;
        _characterLoaded = true;
        RefreshCalcTalentSets();
    }

    /// <summary>"No gear" plus every gear set; keeps the remembered choice.</summary>
    private void RefreshCalcGearChoices()
    {
        var was = _loadingCalc;
        _loadingCalc = true;
        var wanted = CalcGearSet?.Id ?? (int.TryParse(_gearSets.GetSetting("calc_gear_set"), out var id) ? id : SelectedGearSet?.Id ?? 0);
        CalcGearChoices.Clear();
        CalcGearChoices.Add(NoGear);
        foreach (var set in GearSets) CalcGearChoices.Add(set);
        CalcGearSet = CalcGearChoices.FirstOrDefault(s => s.Id == wanted) ?? NoGear;
        _loadingCalc = was;
    }

    private void BuffsChanged()
    {
        if (_loadingCalc) return;
        _gearSets.SetSetting("calc_buffs", string.Join(',', BuffToggles.Where(b => b.IsOn).Select(b => b.Buff.Key)));
        Recalculate();
    }

    private string CalcTalentKey(int classId) => "calc_talent_set:" + classId.ToString(CultureInfo.InvariantCulture);

    /// <summary>Lists the talent sets of the calculator's class and picks the remembered (or active) one.</summary>
    private void RefreshCalcTalentSets()
    {
        if (CalcClass is not { } cls) return;
        var sets = _talentBuilds.List(cls.Id);
        if (sets.SequenceEqual(CalcTalentSets) && SelectedCalcTalentSet?.ClassId == cls.Id)
        {
            Recalculate();
            return;
        }

        _loadingCalcTalents = true;
        CalcTalentSets.Clear();
        foreach (var s in sets) CalcTalentSets.Add(s);
        var remembered = int.TryParse(_gearSets.GetSetting(CalcTalentKey(cls.Id)), out var id) ? id : _talentBuilds.EnsureActive(cls.Id);
        SelectedCalcTalentSet = CalcTalentSets.FirstOrDefault(s => s.Id == remembered) ?? CalcTalentSets.FirstOrDefault();
        _loadingCalcTalents = false;
        Recalculate();
    }

    partial void OnSelectedCalcTalentSetChanged(TalentSetInfo? value)
    {
        if (_loadingCalcTalents || value is null) return;
        _gearSets.SetSetting(CalcTalentKey(value.ClassId), value.Id.ToString(CultureInfo.InvariantCulture));
        Recalculate();
    }

    /// <summary>Talent spells of a talent set, plus a "points per tree" summary.</summary>
    private (List<TalentSpell> Spells, string Points) TalentSpellsFor(int classId, TalentSetInfo? set)
    {
        var spells = new List<TalentSpell>();
        var picked = new List<string>();
        if (_talentService is null) return (spells, "");
        var build = set is not null && set.ClassId == classId ? _talentBuilds.Load(set.Id) : _talentBuilds.LoadActive(classId);
        foreach (var tree in _talentService.GetTrees(classId))
        {
            var points = 0;
            foreach (var talent in tree.Talents)
            {
                var rank = Math.Min(build.Ranks.GetValueOrDefault(talent.Id), talent.MaxRank);
                if (rank <= 0) continue;
                points += rank;
                // Trait talents use one spell for every rank (scaled); classic ones have a spell per rank.
                var sameSpell = talent.RankSpells.Distinct().Count() == 1;
                spells.Add(sameSpell
                    ? new TalentSpell(talent.RankSpells[0], rank, $"{talent.Name} {rank}/{talent.MaxRank}")
                    {
                        Points = _talentService.RankPoints(talent.RankSpells[0], rank), // curve values (Improved Seals 5/10/15%)
                    }
                    : new TalentSpell(talent.RankSpells[rank - 1], 1, $"{talent.Name} {rank}/{talent.MaxRank}"));
            }
            picked.Add(points.ToString(CultureInfo.InvariantCulture));
        }
        return (spells, string.Join("/", picked));
    }

    private static double? ParseStat(string text) =>
        double.TryParse(text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    private CharacterSheet SheetFor(int classId, int raceId, double?[] naked) => StatEngine.Compute(new CharacterInput
    {
        ClassId = classId,
        RaceId = raceId,
        Level = CalcLevel,
        NakedStats = naked,
        Gear = _calcGear,
        Talents = _talentSpells,
        Buffs = BuffToggles.Where(b => b.IsOn).Select(b => b.Buff).ToList(),
        Effects = _effects,
    });

    /// <summary>Rebuilds the character sheet and the damage numbers from everything above.</summary>
    private void Recalculate()
    {
        if (_loadingCalc || _loadingGear || _calcData is null) return;
        if (!_characterLoaded)
        {
            if (TalentClasses.Count == 0) return;
            LoadCharacter();
            return; // LoadCharacter ends by recalculating
        }

        SheetLines.Clear();
        GearTotals.Clear();
        CalcNotes.Clear();
        if (CalcClass is not { } cls || CalcRace is not { } race)
        {
            CalcHeader = "Pick a class and race.";
            return;
        }

        var gearSet = CalcGearSet is { Id: > 0 } g ? g : null;
        CalcHeader = $"{race.Name} {cls.Name}, level {CalcLevel}" + (gearSet is null ? ", no gear" : $", wearing {gearSet.Name}");
        if (!CharacterBase.CanBe(cls.Id, race.Id)) CalcHeader += " (not a Classic combination)";

        string points;
        (_talentSpells, points) = TalentSpellsFor(cls.Id, SelectedCalcTalentSet);
        CalcTalentLine = _talentSpells.Count > 0
            ? $"Talents {points} ({SelectedCalcTalentSet?.Name ?? "active set"})"
            : "No talents picked in this talent set (Talents page)";

        var bySlot = gearSet is null ? new Dictionary<int, GearItem>() : SetGearBySlot(gearSet.Id);
        _calcGear = bySlot.OrderBy(x => x.Key).Select(x => x.Value).ToList();
        _calcMainHand = bySlot.GetValueOrDefault(GearSlots.MainHand);
        _calcRanged = bySlot.GetValueOrDefault(18);
        var spellIds = _calcGear.SelectMany(x => x.EquipSpells.Concat(x.OnHitSpells)).Concat(_talentSpells.Select(t => t.SpellId)).ToList();
        _effects = _calcData.GetEffects(spellIds);

        _sheet = SheetFor(cls.Id, race.Id, [ParseStat(NakedStr), ParseStat(NakedAgi), ParseStat(NakedSta), ParseStat(NakedInt), ParseStat(NakedSpi)]);
        foreach (var row in SheetRow.FromLines(_sheet.Lines())) SheetLines.Add(row);

        // Weapons: white damage and on-hit procs.
        var weapons = new List<WeaponEngine.WeaponSlot>();
        foreach (var (slot, label, offHand, ranged) in new[]
                 {
                     (GearSlots.MainHand, "Main hand", false, false),
                     (GearSlots.OffHand, "Off hand", true, false),
                     (18, "Ranged", false, true),
                 })
        {
            if (bySlot.TryGetValue(slot, out var item) && item.IsWeapon)
                weapons.Add(new WeaponEngine.WeaponSlot(label, item, offHand, ranged));
        }
        var procs = BuffToggles.Where(b => b.IsOn && b.Buff.OnHit is not null).Select(b => b.Buff.OnHit!).ToList();
        WeaponLines.Clear();
        foreach (var row in SheetRow.FromLines(WeaponEngine.Compute(weapons, _sheet, _effects, procs, TargetLevelDiff)))
            WeaponLines.Add(row);
        HasWeapons = WeaponLines.Count > 0;
        foreach (var note in _sheet.Notes) CalcNotes.Add(note);

        // Gear page: what the selected gear set alone adds.
        RefreshGearTotals();

        if (_spellListFor != (cls.Id, CalcLevel))
        {
            _spellListFor = (cls.Id, CalcLevel);
            _allCalcSpells = _calcData.GetDamageSpells(cls.Id, CalcLevel).ToList();
            FilterCalcSpells();
        }
        ComputeDamage();
    }

    /// <summary>Gear page totals: the Gear page's selected set on its own (no character).</summary>
    private void RefreshGearTotals()
    {
        GearTotals.Clear();
        if (_calcData is null || SelectedGearSet is not { } set) return;
        var items = SetGear(set.Id);
        var effects = _calcData.GetEffects(items.SelectMany(i => i.EquipSpells).ToList());
        var bag = StatEngine.Compute(new CharacterInput { ClassId = 1, RaceId = 1, Gear = items, Effects = effects }).GearOnly;
        foreach (var (stat, value) in bag.NonZero.OrderBy(v => v.Key))
            GearTotals.Add(new SheetRow(Humanize(stat), "+" + Math.Round(value).ToString("0", CultureInfo.InvariantCulture), false));
    }

    /// <summary>The selected spell for every race this class can be (Classic combinations), same gear and talents.</summary>
    private void CompareRaces(CalcSpell spell, IReadOnlyDictionary<int, List<EffectRow>> effects)
    {
        RaceRows.Clear();
        if (CalcClass is not { } cls) return;
        var inv = CultureInfo.InvariantCulture;
        foreach (var race in Races.Where(r => CharacterBase.CanBe(cls.Id, r.Id)))
        {
            // Each race uses its own saved naked stats when you entered them, otherwise the estimate.
            var parts = (_gearSets.GetSetting($"naked:{cls.Id}:{race.Id}") ?? "").Split(',');
            var naked = Enumerable.Range(0, 5).Select(i => i < parts.Length ? ParseStat(parts[i]) : null).ToArray();
            var sheet = SheetFor(cls.Id, race.Id, naked);
            var result = DamageEngine.Compute(spell, effects, sheet, _talentSpells, TargetLevelDiff, _calcMainHand, _calcRanged);
            var physical = StatBag.Schools(spell.SchoolMask).All(s => s == 0);
            RaceRows.Add(new RaceCompareRow(
                race.Name,
                Math.Round(result.ExpectedPerCast).ToString("0", inv),
                result.Dps.ToString("0.0", inv),
                (physical ? sheet.MeleeCrit : sheet.SpellCrit).ToString("0.00", inv) + "%",
                race.Id == CalcRace?.Id));
        }
        HasRaceRows = RaceRows.Count > 1;
    }

    // ----- Saved calculations -----

    private void LoadSnapshots(int? select = null)
    {
        Snapshots.Clear();
        foreach (var s in _snapshots.List()) Snapshots.Add(s);
        SelectedSnapshot = Snapshots.FirstOrDefault(s => s.Id == select);
    }

    /// <summary>Everything shown right now, as "Group / Label: value" lines.</summary>
    private List<string> CurrentOutput()
    {
        var lines = new List<string> { CalcHeader, CalcTalentLine, SelectedSpellTitle };
        string? group = null;
        foreach (var row in SheetLines.Concat(WeaponLines).Concat(DamageLines))
        {
            if (row.IsHeader) { group = row.Label; continue; }
            lines.Add($"{group} / {row.Label}: {row.Value}");
        }
        lines.AddRange(DamageModifiers);
        return lines;
    }

    [RelayCommand]
    private void SaveSnapshot()
    {
        if (CalcClass is null || CalcRace is null) return;
        var spell = _allCalcSpells.FirstOrDefault(s => s.Id == (SelectedCalcRank ?? SelectedCalcSpell)?.Id);
        var name = SnapshotName.Trim().Length > 0
            ? SnapshotName.Trim()
            : $"{CalcRace.Name} {CalcClass.Name} - {spell?.Name ?? "no spell"} {spell?.Rank}".Trim();
        var id = _snapshots.Save(new CalcSnapshot
        {
            Name = name,
            SavedUtc = DateTime.UtcNow,
            ClassId = CalcClass.Id,
            RaceId = CalcRace.Id,
            Level = CalcLevel,
            GearSetId = CalcGearSet?.Id ?? 0,
            GearSetName = CalcGearSet?.Name ?? "",
            TalentSetId = SelectedCalcTalentSet?.Id ?? 0,
            TalentSetName = SelectedCalcTalentSet?.Name ?? "",
            SpellId = spell?.Id ?? 0,
            SpellName = spell?.Name ?? "",
            TargetLevelDiff = TargetLevelDiff,
            Buffs = BuffToggles.Where(b => b.IsOn).Select(b => b.Buff.Key).ToList(),
            NakedStats = [NakedStr, NakedAgi, NakedSta, NakedInt, NakedSpi],
            Output = CurrentOutput(),
        });
        SnapshotName = "";
        LoadSnapshots(id);
        Toast($"Saved calculation \"{name}\".");
    }

    [RelayCommand]
    private void LoadSnapshot()
    {
        if (SelectedSnapshot is null || _snapshots.Load(SelectedSnapshot.Id) is not { } snap) return;

        _loadingCalc = true;
        if (snap.ClassId > 0) CalcClass = TalentClasses.FirstOrDefault(c => c.Id == snap.ClassId) ?? CalcClass;
        if (snap.RaceId > 0) CalcRace = Races.FirstOrDefault(r => r.Id == snap.RaceId) ?? CalcRace;
        CalcLevel = snap.Level > 0 ? snap.Level : 60;
        CalcGearSet = CalcGearChoices.FirstOrDefault(g => g.Id == snap.GearSetId) ?? NoGear;
        TargetLevelDiff = snap.TargetLevelDiff;
        if (snap.NakedStats.Count == 5)
            (NakedStr, NakedAgi, NakedSta, NakedInt, NakedSpi) = (snap.NakedStats[0], snap.NakedStats[1], snap.NakedStats[2], snap.NakedStats[3], snap.NakedStats[4]);
        foreach (var toggle in BuffToggles) toggle.IsOn = snap.Buffs.Contains(toggle.Buff.Key);
        _loadingCalc = false;

        // Persist the restored state, then pick the talent set and spell.
        _gearSets.SetSetting(NakedKey(), NakedText());
        _gearSets.SetSetting("calc_buffs", string.Join(',', snap.Buffs));
        _gearSets.SetSetting("calc_gear_set", (CalcGearSet?.Id ?? 0).ToString(CultureInfo.InvariantCulture));
        _gearSets.SetSetting("calc_character", string.Join(',', CalcClass?.Id ?? 0, CalcRace?.Id ?? 0, CalcLevel));
        if (snap.GearSetId > 0 && CalcGearSet?.Id != snap.GearSetId)
            Toast($"The gear set \"{snap.GearSetName}\" no longer exists; loaded without gear.");

        CalcTalentSets.Clear(); // force a refresh for the restored class
        RefreshCalcTalentSets();
        if (CalcTalentSets.FirstOrDefault(t => t.Id == snap.TalentSetId) is { } talents) SelectedCalcTalentSet = talents;
        Recalculate();

        CalcFilter = "";
        if (CalcSpells.FirstOrDefault(s => s.Name == snap.SpellName) is { } row)
        {
            SelectedCalcSpell = row;
            SelectedCalcRank = CalcRanks.FirstOrDefault(r => r.Id == snap.SpellId) ?? CalcRanks.FirstOrDefault();
        }

        // Compare: the headline numbers then vs now.
        var now = CurrentOutput();
        var changed = snap.Output.Where(line => line.Contains("Expected per cast") || line.Contains("DPS"))
            .Select(line => (Then: line, Now: now.FirstOrDefault(n => n.Split(':')[0] == line.Split(':')[0]) ?? "(gone)"))
            .Where(p => p.Then != p.Now)
            .Select(p => $"{p.Then.Split(':')[0]}: was{p.Then[(p.Then.IndexOf(':') + 1)..]}, now{(p.Now.Contains(':') ? p.Now[(p.Now.IndexOf(':') + 1)..] : " " + p.Now)}")
            .ToList();
        SnapshotCompare = changed.Count == 0
            ? $"Loaded \"{snap.Name}\". Same results as when it was saved."
            : $"Loaded \"{snap.Name}\". Changed since saving (gear or talents edited):\n" + string.Join("\n", changed);
    }

    [RelayCommand]
    private void DeleteSnapshot()
    {
        if (SelectedSnapshot is null) return;
        _snapshots.Delete(SelectedSnapshot.Id);
        LoadSnapshots();
        SnapshotCompare = "";
    }

    [RelayCommand]
    private void CopyCalcResult()
    {
        try
        {
            var lines = CurrentOutput();
            if (RaceRows.Count > 1)
            {
                lines.Add("Race comparison:");
                lines.AddRange(RaceRows.Select(r => $"{r.Race}: {r.PerCast} per cast, {r.Dps} DPS, crit {r.Crit}"));
            }
            Clipboard.SetText(string.Join(Environment.NewLine, lines));
            Toast("Calculation copied.");
        }
        catch (Exception)
        {
            Toast("Couldn't reach the clipboard.");
        }
    }

    private void FilterCalcSpells()
    {
        var keep = SelectedCalcSpell?.Id;
        var filter = CalcFilter.Trim();
        CalcSpells.Clear();
        var shown = new HashSet<string>();
        foreach (var s in _allCalcSpells.Where(x => filter.Length == 0 || x.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)))
        {
            if (!shown.Add(s.Name)) continue; // one row per spell; ranks are picked next to the results
            var meta = string.Join(" · ", new[] { s.Rank, ItemText.SchoolMask(s.SchoolMask) }.Where(x => x.Length > 0));
            CalcSpells.Add(new SpellRow(s.Id, s.Name, meta, s.IconId));
        }
        SelectedCalcSpell = CalcSpells.FirstOrDefault(r => r.Id == keep);
        if (SelectedCalcSpell is null) CalcRanks.Clear();
    }

    private void ComputeDamage()
    {
        DamageLines.Clear();
        DamageModifiers.Clear();
        if (_calcData is null || _sheet is null || SelectedCalcSpell is not { } row)
        {
            SelectedSpellTitle = "Pick a spell";
            RaceRows.Clear();
            HasRaceRows = false;
            return;
        }
        var spell = _allCalcSpells.FirstOrDefault(s => s.Id == (SelectedCalcRank ?? row).Id);
        if (spell is null) return;

        SelectedSpellTitle = spell.Rank.Length > 0 ? $"{spell.Name} ({spell.Rank})" : spell.Name;
        var needed = new List<int> { spell.Id };
        needed.AddRange(_talentSpells.Select(t => t.SpellId));
        var effects = _calcData.GetEffects(needed);

        var result = DamageEngine.Compute(spell, effects, _sheet, _talentSpells, TargetLevelDiff, _calcMainHand, _calcRanged);
        foreach (var line in SheetRow.FromLines(result.Lines)) DamageLines.Add(line);
        foreach (var modifier in result.Modifiers) DamageModifiers.Add(modifier);
        foreach (var note in result.Notes) DamageModifiers.Add(note);
        CompareRaces(spell, effects);
    }

    /// <summary>"SpellCritPct" -> "Spell crit %".</summary>
    private static string Humanize(Stat stat)
    {
        var text = stat.ToString().Replace("Pct", " %");
        var builder = new System.Text.StringBuilder();
        for (var i = 0; i < text.Length; i++)
        {
            if (i > 0 && char.IsUpper(text[i]) && text[i - 1] != ' ') builder.Append(' ');
            builder.Append(builder.Length == 0 ? text[i] : char.ToLowerInvariant(text[i]));
        }
        return builder.ToString();
    }
}
