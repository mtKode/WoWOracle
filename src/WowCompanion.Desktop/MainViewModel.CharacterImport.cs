using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WowCompanion.Core;
using WowCompanion.Core.Calc;

namespace WowCompanion.Desktop;

/// <summary>
/// Gear page, "Import character": paste what the WoWOracle addon copied in game (yourself or an inspected player).
/// Makes a gear set (items, class, race, level) and a talent set named after the character. Your own in-game stats
/// are kept with the gear set (settings key "gamesheet:{id}") for comparing with the calculator later.
/// </summary>
public partial class MainViewModel
{
    private readonly LearnedItemStore _learnedItems = new(paths.UserDbPath);

    private int InjectLearnedItems(string gameDbPath)
    {
        try { return LearnedItemStore.InjectInto(gameDbPath, _learnedItems.All()); }
        catch (Exception) { return 0; }
    }

    [ObservableProperty] private bool _isCharacterImportOpen;
    [ObservableProperty] private string _characterImportText = "";
    [ObservableProperty] private string _characterImportError = "";
    public ObservableCollection<string> CharacterImportNotes { get; } = [];

    [RelayCommand]
    private void ToggleCharacterImport()
    {
        IsCharacterImportOpen = !IsCharacterImportOpen;
        if (!IsCharacterImportOpen) return;
        CharacterImportError = "";
        CharacterImportNotes.Clear();
        try
        {
            if (Clipboard.ContainsText() && Clipboard.GetText() is { } clip && CharacterImport.LooksLikeExport(clip))
                CharacterImportText = clip;
        }
        catch (Exception) { }
    }

    [RelayCommand]
    private void ImportCharacter()
    {
        CharacterImportError = "";
        CharacterImportNotes.Clear();
        if (_calcData is null || _talentService is null) { CharacterImportError = "Game data isn't loaded yet."; return; }
        if (!CharacterImport.TryParse(CharacterImportText, out var export, out var error) || export is null)
        {
            CharacterImportError = error;
            return;
        }

        var cls = TalentClasses.FirstOrDefault(c => c.Id == export.ClassId);
        if (cls is null) { CharacterImportError = $"Unknown class {export.ClassName} ({export.ClassId}) in this game build."; return; }
        var race = Races.FirstOrDefault(r => r.Id == export.RaceId)
                   ?? Races.FirstOrDefault(r => string.Equals(r.Name, export.RaceName, StringComparison.OrdinalIgnoreCase));
        if (race is null) CharacterImportNotes.Add($"Race {export.RaceName} not found; set it in the Calculator.");
        var level = Math.Clamp(export.Level, 1, 60);
        var title = $"{export.Name} ({export.Level} {cls.Name})";

        try
        {
            var notes = new List<string>();
            var (setId, placed, talentNote) = ApplyExport(export, cls, race, title, replaceExact: false, notes);
            foreach (var note in notes) CharacterImportNotes.Add(note);
            ReloadGearSets(setId);
            if (SelectedTalentClass?.Id == cls.Id) LoadTalentSets(null);
            CharacterImportText = "";
            if (CharacterImportNotes.Count == 0) IsCharacterImportOpen = false;
            Toast($"Imported {export.Name}: gear set with {placed} items, {talentNote}.");
        }
        catch (Exception ex)
        {
            CharacterImportError = "Import failed: " + ex.Message;
        }
    }

    /// <summary>
    /// Puts an export into a gear set and a talent set named <paramref name="title"/>. With
    /// <paramref name="replaceExact"/> (auto sync from the addon) the sets with exactly that name are updated;
    /// otherwise new sets are made. Notes about what didn't fit go to <paramref name="notes"/>.
    /// </summary>
    private (int SetId, int Placed, string TalentNote) ApplyExport(
        CharacterExport export, ClassInfo cls, RaceInfo? race, string title, bool replaceExact, List<string> notes)
    {
        if (_calcData is null || _talentService is null) return (0, 0, "no game data");
        var level = Math.Clamp(export.Level, 1, 60);

        // ----- items the downloaded data doesn't have: learn them from the game client -----
        if (export.ItemData.Count > 0 && _gameDbPath.Length > 0)
        {
            var have = _calcData.GetGearItems(export.ItemData.Select(i => i.Id).ToList()).Select(i => i.Id).ToHashSet();
            var newItems = export.ItemData.Where(i => !have.Contains(i.Id)).ToList();
            if (newItems.Count > 0)
            {
                _learnedItems.Save(newItems);
                var added = InjectLearnedItems(_gameDbPath);
                if (added > 0) notes.Add($"Learned {added} item(s) from the game that the downloaded data doesn't have yet: " +
                                                        string.Join(", ", newItems.Take(6).Select(i => i.Name)) + (newItems.Count > 6 ? "…" : "") +
                                                        ". Equip effects (\"Equip: …\") of these aren't known.");
            }
        }

        // ----- gear: a new set, or (auto sync) the set with exactly this name, emptied and refilled -----
        var raceId = race?.Id ?? Races.FirstOrDefault()?.Id ?? 1;
        var existing = replaceExact ? _gearSets.List().FirstOrDefault(g => string.Equals(g.Name, title, StringComparison.Ordinal)) : null;
        int setId;
        if (existing is not null)
        {
            setId = existing.Id;
            foreach (var slot in _gearSets.GetItems(setId).Keys.ToList()) _gearSets.RemoveItem(setId, slot);
            _gearSets.Update(existing with { ClassId = cls.Id, RaceId = raceId, Level = level });
        }
        else
        {
            setId = _gearSets.Create(title, cls.Id, raceId, level);
        }
        var known = _calcData.GetGearItems(export.Items.Select(i => i.ItemId).Distinct().ToList()).Select(i => i.Id).ToHashSet();
        var placed = 0;
        int enchantedKnown = 0, enchantedUnknown = 0;
        foreach (var item in export.Items)
        {
            if (!known.Contains(item.ItemId))
            {
                notes.Add($"{GearSlots.NameOf(item.Slot)}: {(item.Name.Length > 0 ? item.Name : "item " + item.ItemId)} isn't in this game build's data.");
                continue;
            }
            _gearSets.SetItem(setId, item.Slot, item.ItemId);
            if (item.BonusStats.Length > 0) _gearSets.SetSlotBonus(setId, item.Slot, item.BonusStats);
            if (item.Enchant > 0)
            {
                if (_enchants?.Get(item.Enchant) is { } enchant)
                {
                    _gearSets.SetSlotEnchant(setId, item.Slot, item.Enchant);
                    enchantedKnown++;
                }
                else enchantedUnknown++;
            }
            placed++;
        }

        var withBonus = export.Items.Count(i => i.BonusStats.Length > 0);
        if (enchantedKnown > 0) notes.Add($"{enchantedKnown} enchant(s) imported; they count in the stats.");
        if (enchantedUnknown > 0) notes.Add($"{enchantedUnknown} enchant(s) aren't in this game build's data and were left off.");
        if (withBonus > 0) notes.Add($"{withBonus} item(s) with a random bonus (\"of the Eagle\"): the extra stats are kept with the gear set.");

        // ----- in-game stats (yourself only) -----
        if (export.Stats.Count > 0)
        {
            _gearSets.SetSetting($"gamesheet:{setId}",
                string.Join(";", export.Stats.Select(s => $"{s.Key}={s.Value}")));
            string Stat(string key) => export.Stats.GetValueOrDefault(key) ?? "?";
            notes.Add($"In-game sheet: Str {Stat("str")}, Agi {Stat("agi")}, Sta {Stat("sta")}, Int {Stat("int")}, Spi {Stat("spi")}, " +
                                     $"AP {Stat("ap")}, crit {Stat("crit")}%, spell power {Stat("sp")}, HP {Stat("hp")}, mana {Stat("mana")}.");
        }
        else if (export.FromInspect)
        {
            notes.Add("Inspected players don't send their character sheet, so there are no stats to compare.");
        }

        // ----- talents -----
        var talentNote = "no talents";
        if (export.Talents.Count > 0)
        {
            var warnings = new List<string>();
            var ranks = TalentCode.MapByName(_talentService.GetTrees(cls.Id), export.Talents.Select(t => (t.Name, t.Rank)), warnings);
            foreach (var w in warnings) notes.Add(w);
            if (ranks.Count > 0)
            {
                FlushTalentSave();
                var sameName = replaceExact
                    ? _talentBuilds.List(cls.Id).FirstOrDefault(t => string.Equals(t.Name, title, StringComparison.Ordinal))
                    : null;
                if (sameName is not null) _talentBuilds.Save(sameName.Id, level, ranks);
                else _talentBuilds.Create(cls.Id, title, level, ranks);
                talentNote = $"talent set with {ranks.Values.Sum()} points";
            }
        }

        return (setId, placed, talentNote);
    }
}
