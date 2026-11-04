using CommunityToolkit.Mvvm.ComponentModel;
using WowCompanion.Core;

namespace WowCompanion.Desktop;

/// <summary>
/// Browser-style history: every page change (and every item opened on the Items page) is remembered, and the
/// mouse's back/forward buttons walk it. Also the shared item hover card (Gear page, Loot page).
/// </summary>
public partial class MainViewModel
{
    private readonly List<(AppView View, int ItemId)> _history = [];
    private int _historyIndex = -1;
    private bool _navigating;

    partial void OnSelectedChanged(ItemView? value)
    {
        if (value is not null) SelectedQuest = null; // an item replaces a shown quest
        if (CurrentView == AppView.Items) RecordNavigation();
    }

    private void RecordNavigation()
    {
        if (_navigating) return;
        var entry = (CurrentView, CurrentView == AppView.Items ? Selected?.Id ?? 0 : 0);
        if (_historyIndex >= 0 && _history[_historyIndex] == entry) return;
        // Opening an item right after switching to Items replaces the bare "Items" entry.
        if (_historyIndex >= 0 && _history[_historyIndex] == (AppView.Items, 0) && entry.Item1 == AppView.Items)
        {
            _history[_historyIndex] = entry;
            return;
        }
        if (_historyIndex < _history.Count - 1) _history.RemoveRange(_historyIndex + 1, _history.Count - _historyIndex - 1);
        _history.Add(entry);
        if (_history.Count > 60) _history.RemoveAt(0);
        _historyIndex = _history.Count - 1;
    }

    public void NavigateBack()
    {
        if (_historyIndex > 0) Go(--_historyIndex);
    }

    public void NavigateForward()
    {
        if (_historyIndex < _history.Count - 1) Go(++_historyIndex);
    }

    private void Go(int index)
    {
        var (view, itemId) = _history[index];
        _navigating = true;
        try
        {
            if (view == AppView.Items && itemId > 0 && Selected?.Id != itemId && _search?.GetDetail(itemId) is { } detail)
                Selected = ItemView.From(detail);
            CurrentView = view;
        }
        finally
        {
            _navigating = false;
        }
    }

    // ----- item hover card -----

    private readonly Dictionary<int, ItemView> _cards = [];

    [ObservableProperty] private ItemView? _hoverCard;

    /// <summary>The item's card (tooltip), cached; null when the item isn't in the data.</summary>
    public ItemView? CardFor(int itemId)
    {
        if (itemId <= 0 || _search is null) return null;
        if (_cards.TryGetValue(itemId, out var card)) return card;
        if (_search.GetDetail(itemId) is not { } detail) return null;
        card = ItemView.From(detail);
        if (_cards.Count > 400) _cards.Clear();
        _cards[itemId] = card;
        return card;
    }

    public bool ShowHoverCard(int itemId)
    {
        HoverCard = CardFor(itemId);
        return HoverCard is not null;
    }
}
