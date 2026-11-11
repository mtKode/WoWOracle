using System.Globalization;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace WowCompanion.Desktop;

/// <summary>A leg of the leveling route.</summary>
public sealed record PlanStepRow(
    int SpellId, string Range, string Recipe, int IconId, string CraftsText, Brush ColorBrush, string ColorText,
    string Source, string Reagents, string CostText, int Crafts);

/// <summary>A material for the route; "have" can be typed (it starts from the addon's bag and bank counts).</summary>
public sealed partial class PlanMaterialRow(int itemId, string name, Brush nameBrush, int iconId, int total, int have, string source,
    Action<PlanMaterialRow> haveChanged) : ObservableObject
{
    public int ItemId { get; } = itemId;
    public string Name { get; } = name;
    public Brush NameBrush { get; } = nameBrush;
    public int IconId { get; } = iconId;
    public int Total { get; } = total;
    public string Source { get; } = source;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ToGet), nameof(Done), nameof(PriceText))] private int _have = have;

    public int ToGet => Math.Max(0, Total - Have);

    /// <summary>Auction price per item (Auctionator), 0 when unknown.</summary>
    public long UnitPrice { get; } = AuctionPrices.Unit(itemId);
    /// <summary>"AH 6s 60c (22c each)" for what's still to get, or the price of one when you have them all.</summary>
    public string PriceText => AuctionPrices.Label(ItemId, Math.Max(1, ToGet));
    public bool Done => ToGet == 0;

    /// <summary>Typed into the "have" box.</summary>
    public string HaveText
    {
        get => Have.ToString(CultureInfo.InvariantCulture);
        set
        {
            var parsed = int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? Math.Max(0, v) : 0;
            if (parsed == Have) return;
            Have = parsed;
            OnPropertyChanged();
            haveChanged(this);
        }
    }
}

public sealed record PlanSubCraftRow(string Name, Brush NameBrush, int IconId, string Text);
