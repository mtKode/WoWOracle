using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using WowCompanion.Core;

namespace WowCompanion.Desktop;

/// <summary>
/// Auction prices from the Auctionator addon (optional): read at start-up and whenever its saved file changes, shown
/// next to items and materials everywhere, and used by the profession planner for what materials really cost.
/// Auctionator keeps one database per realm; the newest scan is used unless another realm is picked.
/// </summary>
public partial class MainViewModel
{
    private const string AuctionRealmKey = "auction_realm";
    private readonly List<FileSystemWatcher> _auctionWatchers = [];
    private CancellationTokenSource? _auctionReadCts;
    private bool _loadingAuctionRealms;

    public ObservableCollection<string> AuctionRealms { get; } = [];

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasAuctionPrices))] private string _auctionStatus = "";
    [ObservableProperty] private string? _auctionRealm;

    public bool HasAuctionPrices => AuctionStatus.Length > 0;
    public bool HasAuctionRealmChoice => AuctionRealms.Count > 1;

    partial void OnAuctionRealmChanged(string? value)
    {
        if (_loadingAuctionRealms || value is null || value == AuctionPrices.Book?.Realm) return;
        _gearSets.SetSetting(AuctionRealmKey, value);
        _ = LoadAuctionPricesAsync();
    }

    /// <summary>Reads Auctionator's prices (off the UI thread) and refreshes what shows them.</summary>
    private async Task LoadAuctionPricesAsync()
    {
        AuctionPriceBook? book;
        try
        {
            var preferred = _gearSets.GetSetting(AuctionRealmKey);
            book = await Task.Run(() => AuctionatorData.Load(paths.Root, preferred));
        }
        catch (Exception)
        {
            return; // prices are a bonus
        }

        var hadPrices = AuctionPrices.Any;
        AuctionPrices.Book = book;
        _loadingAuctionRealms = true;
        AuctionRealms.Clear();
        foreach (var realm in book?.Realms ?? []) AuctionRealms.Add(realm);
        AuctionRealm = book?.Realm;
        _loadingAuctionRealms = false;
        OnPropertyChanged(nameof(HasAuctionRealmChoice));
        AuctionStatus = AuctionPrices.Status;
        if (book is not null && !hadPrices)
            Log($"Auction prices from Auctionator: {book.Prices.Count} items ({book.Realm}, scanned {AuctionPriceBook.Age(book.LatestDay)}).", "note");

        // Everything that shows a price is built from it: rebuild.
        _cards.Clear();
        RefreshCraftList();
        QueuePlan();
        if (Selected is { Id: > 0 } shown && _search?.GetDetail(shown.Id) is { } detail)
        {
            _navigating = true;
            try { Selected = ItemView.From(detail); }
            finally { _navigating = false; }
        }
        WatchAuctionator();
    }

    /// <summary>Auctionator writes its file at logout and /reload, like every addon: read it again then.</summary>
    private void WatchAuctionator()
    {
        if (_auctionWatchers.Count > 0) return;
        try
        {
            foreach (var root in WowInstall.Roots(paths.Root))
            foreach (var client in WowInstall.ForeverClients(root))
            {
                var wtf = Path.Combine(client, "WTF");
                if (!Directory.Exists(wtf)) continue;
                var watcher = new FileSystemWatcher(wtf, "Auctionator.lua")
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                    EnableRaisingEvents = true,
                };
                watcher.Changed += (_, _) => AuctionatorChanged();
                watcher.Created += (_, _) => AuctionatorChanged();
                watcher.Renamed += (_, _) => AuctionatorChanged();
                _auctionWatchers.Add(watcher);
            }
        }
        catch (Exception)
        {
            // The next start reads the prices anyway.
        }
    }

    private void AuctionatorChanged()
    {
        _auctionReadCts?.Cancel();
        var cts = _auctionReadCts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(2000, cts.Token); // the game writes the file in pieces
                await Application.Current.Dispatcher.InvokeAsync(() => _ = LoadAuctionPricesAsync());
            }
            catch (OperationCanceledException) { }
        });
    }
}
