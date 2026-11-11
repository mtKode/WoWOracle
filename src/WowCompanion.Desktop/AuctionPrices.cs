using System.Globalization;
using WowCompanion.Core;

namespace WowCompanion.Desktop;

/// <summary>
/// The Auctionator prices the app shows next to items and materials, when Auctionator is installed (else every
/// text here is empty and nothing shows). Swapped as a whole when Auctionator's file changes or another realm is picked.
/// </summary>
public static class AuctionPrices
{
    private static volatile AuctionPriceBook? _book;

    public static AuctionPriceBook? Book
    {
        get => _book;
        set => _book = value;
    }

    public static bool Any => _book is not null;

    /// <summary>The price per item in copper, or 0 when Auctionator has none.</summary>
    public static long Unit(int itemId) => _book?.Get(itemId)?.Copper ?? 0;

    /// <summary>All prices (item → copper), for the profession planner.</summary>
    public static IReadOnlyDictionary<int, long>? All() =>
        _book?.Prices.ToDictionary(p => p.Key, p => p.Value.Copper);

    /// <summary>"22c" for one, "6s 60c" for a quantity; "" when there is no price.</summary>
    public static string Text(int itemId, int quantity = 1)
    {
        var unit = Unit(itemId);
        return unit <= 0 || quantity <= 0 ? "" : ItemText.Money(unit * quantity);
    }

    /// <summary>"AH 22c" (with "each" for quantities above one); "" when there is no price.</summary>
    public static string Label(int itemId, int quantity = 1)
    {
        var unit = Unit(itemId);
        if (unit <= 0 || quantity <= 0) return "";
        return quantity == 1 ? $"AH {ItemText.Money(unit)}" : $"AH {ItemText.Money(unit * quantity)} ({ItemText.Money(unit)} each)";
    }

    /// <summary>"22c · RPPvP, scanned yesterday · 2,843 up" for the item page.</summary>
    public static string Detail(int itemId)
    {
        if (_book?.Get(itemId) is not { } p) return "";
        var parts = new List<string> { ItemText.Money(p.Copper), $"{_book.Realm}, scanned {AuctionPriceBook.Age(p.Day)}" };
        if (p.Available > 0) parts.Add(p.Available.ToString("N0", CultureInfo.InvariantCulture) + " up");
        return string.Join(" · ", parts);
    }

    /// <summary>"Auction prices: Auctionator, RPPvP, scanned yesterday" (empty without Auctionator).</summary>
    public static string Status =>
        _book is { } b ? $"Auction prices: Auctionator, {b.Realm}, scanned {AuctionPriceBook.Age(b.LatestDay)}" : "";
}
