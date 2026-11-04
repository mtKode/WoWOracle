using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WowCompanion.Core;

namespace WowCompanion.Desktop;

/// <summary>Spells view: class spells with costs, cast times, coefficients and descriptions.</summary>
public partial class MainViewModel
{
    private const string AllClasses = "All classes";

    private SpellService? _spellService;
    private List<ClassInfo> _classes = [];

    [ObservableProperty] private string _selectedSpellClass = AllClasses;
    [ObservableProperty] private string _spellFilter = "";
    [ObservableProperty] private string _spellInfo = "";
    [ObservableProperty] private SpellRow? _selectedSpellRow;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSpell), nameof(NoSpell))]
    private SpellView? _selectedSpell;

    public ObservableCollection<string> SpellClasses { get; } = [];
    public ObservableCollection<SpellRow> Spells { get; } = [];
    public bool HasSpell => SelectedSpell is not null;
    public bool NoSpell => SelectedSpell is null;

    /// <summary>Fills the class chips for both the Spells and the Talents view.</summary>
    private void LoadClasses()
    {
        if (_spellService is null) return;
        _classes = _spellService.GetClasses().ToList();

        SpellClasses.Clear();
        SpellClasses.Add(AllClasses);
        foreach (var c in _classes) SpellClasses.Add(c.Name);
        SelectedSpellClass = AllClasses;
        RefreshSpells();

        var previous = SelectedTalentClass?.Id;
        TalentClasses.Clear();
        foreach (var c in _classes) TalentClasses.Add(c);
        SelectedTalentClass = TalentClasses.FirstOrDefault(c => c.Id == previous) ?? TalentClasses.FirstOrDefault();
    }

    partial void OnSelectedSpellClassChanged(string value) => RefreshSpells();

    partial void OnSpellFilterChanged(string value) => RefreshSpells();

    partial void OnSelectedSpellRowChanged(SpellRow? value)
    {
        if (value is null || _spellService is null) return;
        var detail = _spellService.GetDetail(value.Id);
        SelectedSpell = detail is null ? null : SpellView.From(detail);
    }

    private void RefreshSpells()
    {
        if (_spellService is null) return;

        const int limit = 400;
        var classId = _classes.FirstOrDefault(c => c.Name == SelectedSpellClass)?.Id;
        var found = _spellService.Search(classId, SpellFilter, limit);

        Spells.Clear();
        foreach (var spell in found) Spells.Add(SpellRow.From(spell));

        SpellInfo = found.Count >= limit
            ? $"Showing the first {limit}. Pick a class or type a filter to narrow it."
            : $"{found.Count} spells";
    }

    [RelayCommand]
    private void CopySpell()
    {
        if (SelectedSpell is null) return;
        try
        {
            Clipboard.SetText(SelectedSpell.ToPlainText());
            Toast($"Copied {SelectedSpell.Name}.");
        }
        catch (Exception)
        {
            Toast("Couldn't reach the clipboard.");
        }
    }
}
