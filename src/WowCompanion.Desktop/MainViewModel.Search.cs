using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WowCompanion.Core;

namespace WowCompanion.Desktop;

/// <summary>A filter shown as a pill in the search box, e.g. "stat: Str". IsSelected: picked with the arrow keys.</summary>
public sealed partial class FilterChip(string token, string key, string value, Brush brush) : ObservableObject
{
    public string Token { get; } = token;
    public string Key { get; } = key;
    public string Value { get; } = value;
    public Brush Brush { get; } = brush;

    [ObservableProperty] private bool _isSelected;
}

/// <summary>
/// Search box filter chips (Discord style). A complete filter followed by a space becomes a chip; the free text
/// keeps going after the chips. Backspace at the start of the text takes the last chip back as text.
/// Left at the start of the text walks onto the chips (one is highlighted, the caret hides): Left/Right move,
/// Backspace/Delete remove it, Enter/F2 (or a double click) edit it, typing or Esc goes back to the text.
/// The search itself runs on chips + text together (ItemQuery).
/// </summary>
public partial class MainViewModel
{
    private const string PlaceholderText = "Search items. Add filters like stat:str  q:epic  (all filters under ?)";

    public ObservableCollection<FilterChip> SearchChips { get; } = [];
    public string SearchPlaceholder => SearchChips.Count == 0 ? PlaceholderText : "";

    /// <summary>Raised after the text was rewritten (chips taken out) so the window can put the caret at the end.</summary>
    public event Action? SearchTextReplaced;

    private string EffectiveQuery => string.Join(' ', SearchChips.Select(c => c.Token).Append(Query.Trim())).Trim();

    /// <summary>Moves complete filters out of the text into chips. Returns true when it changed anything.</summary>
    private bool ExtractChips(string text)
    {
        if (text.Length == 0 || !text.Any(char.IsWhiteSpace)) return false;

        // "stat: str" -> "stat:str" once the value is typed.
        var normalized = Regex.Replace(text, @"(\w+):\s+(?=[^\s""])", "$1:");
        var endsWithSpace = char.IsWhiteSpace(normalized[^1]);
        var parts = Regex.Matches(normalized, "\\w+:\"[^\"]*\"?|\"[^\"]*\"?|\\S+").Select(m => m.Value).ToList();
        var complete = endsWithSpace ? parts.Count : parts.Count - 1; // the word being typed isn't touched

        var keep = new List<string>();
        var added = false;
        for (var i = 0; i < parts.Count; i++)
        {
            // quest:"the defias  <- still typing inside the quotes
            var openQuote = parts[i].Count(c => c == '"') % 2 == 1;
            if (i < complete && !openQuote && ItemQuery.IsKeyFilter(parts[i]))
            {
                SearchChips.Add(MakeChip(parts[i]));
                added = true;
            }
            else keep.Add(parts[i]);
        }

        // Bare slot words become chips only next to another filter (so "Ring of Protection" stays a name).
        if (SearchChips.Count > 0)
        {
            var still = new List<string>();
            for (var i = 0; i < keep.Count; i++)
            {
                var isComplete = i < keep.Count - 1 || endsWithSpace;
                if (isComplete && ItemQuery.IsSlotWord(keep[i]))
                {
                    SearchChips.Add(MakeChip("slot:" + keep[i].ToLowerInvariant()));
                    added = true;
                }
                else still.Add(keep[i]);
            }
            keep = still;
        }
        if (!added) return false;

        _suppressSearch = true;
        Query = keep.Count == 0 ? "" : string.Join(' ', keep) + (endsWithSpace ? " " : "");
        _suppressSearch = false;
        OnPropertyChanged(nameof(SearchPlaceholder));
        SearchTextReplaced?.Invoke();
        _ = RunSearchAsync(EffectiveQuery);
        return true;
    }

    // ----- Walking the chips with the keyboard -----

    private int _selectedChip = -1;

    /// <summary>True while a chip is highlighted (the search box hides its caret then).</summary>
    public bool IsChipSelected => _selectedChip >= 0;

    private void SelectChip(int index)
    {
        if (_selectedChip >= 0 && _selectedChip < SearchChips.Count) SearchChips[_selectedChip].IsSelected = false;
        _selectedChip = index >= 0 && index < SearchChips.Count ? index : -1;
        if (_selectedChip >= 0) SearchChips[_selectedChip].IsSelected = true;
        OnPropertyChanged(nameof(IsChipSelected));
    }

    public void SelectChip(FilterChip chip) => SelectChip(SearchChips.IndexOf(chip));

    /// <summary>Back to typing: no chip highlighted.</summary>
    public void LeaveChips() => SelectChip(-1);

    /// <summary>Left: onto the last chip from the text, or one chip further left. False when there's nowhere to go.</summary>
    public bool ChipLeft()
    {
        if (SearchChips.Count == 0) return false;
        SelectChip(_selectedChip < 0 ? SearchChips.Count - 1 : Math.Max(0, _selectedChip - 1));
        return true;
    }

    /// <summary>Right: next chip, or back into the text after the last one.</summary>
    public void ChipRight() => SelectChip(_selectedChip + 1 < SearchChips.Count ? _selectedChip + 1 : -1);

    public void ChipHome() => SelectChip(SearchChips.Count > 0 ? 0 : -1);

    /// <summary>Backspace (towards the left) or Delete on the highlighted chip: remove it, highlight its neighbour.</summary>
    public void DeleteSelectedChip(bool backwards)
    {
        if (_selectedChip < 0) return;
        var index = _selectedChip;
        SelectChip(-1);
        SearchChips.RemoveAt(index);
        var next = backwards ? index - 1 : index;
        if (next >= SearchChips.Count) next = -1; // Delete on the last chip: back to the text
        if (backwards && next < 0 && SearchChips.Count > 0) next = 0;
        SelectChip(next);
        OnPropertyChanged(nameof(SearchPlaceholder));
        _ = RunSearchAsync(EffectiveQuery);
    }

    /// <summary>Enter/F2 on the highlighted chip: it becomes text to edit (a space makes it a chip again).</summary>
    public bool EditSelectedChip()
    {
        if (_selectedChip < 0) return false;
        var chip = SearchChips[_selectedChip];
        SelectChip(-1);
        return EditChip(chip);
    }

    /// <summary>Backspace at the start of the text: the last chip becomes editable text again.</summary>
    public bool PopChip() => SearchChips.Count > 0 && EditChip(SearchChips[^1]);

    public bool EditChip(FilterChip chip)
    {
        if (!SearchChips.Contains(chip)) return false;
        SelectChip(-1);
        SearchChips.Remove(chip);
        _suppressSearch = true;
        // The chip goes back as the last word, with the caret after it, so it can be edited and re-chipped with a space.
        Query = (Query.Trim().Length > 0 ? Query.Trim() + " " : "") + chip.Token;
        _suppressSearch = false;
        OnPropertyChanged(nameof(SearchPlaceholder));
        SearchTextReplaced?.Invoke();
        _ = RunSearchAsync(EffectiveQuery);
        return true;
    }

    [RelayCommand]
    private void RemoveChip(FilterChip? chip)
    {
        SelectChip(-1);
        if (chip is null || !SearchChips.Remove(chip)) return;
        OnPropertyChanged(nameof(SearchPlaceholder));
        _ = RunSearchAsync(EffectiveQuery);
    }

    private static readonly Brush StatBrush = Freeze("#8FE07A");
    private static readonly Brush SlotBrush = Freeze("#8CC4F0");
    private static readonly Brush TypeBrush = Freeze("#E8B860");
    private static readonly Brush OtherBrush = Freeze("#FFD100");

    private static FilterChip MakeChip(string token)
    {
        var colon = token.IndexOf(':');
        var key = colon > 0 ? token[..colon].ToLowerInvariant() : "slot";
        var value = colon > 0 ? token[(colon + 1)..] : token;
        var brush = key switch
        {
            "stat" or "s" => StatBrush,
            "slot" => SlotBrush,
            "type" or "t" => TypeBrush,
            "q" or "quality" => ItemQuery.QualityOf(value) is { } q ? ItemQuality.BrushFor(q) : OtherBrush,
            _ => OtherBrush,
        };
        value = value.Trim('"');
        var shown = value.Length > 0 ? char.ToUpperInvariant(value[0]) + value[1..] : value;
        return new FilterChip(token, key + ":", shown, brush);
    }

    private static Brush Freeze(string hex)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        brush.Freeze();
        return brush;
    }
}
