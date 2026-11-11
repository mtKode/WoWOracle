using System.Windows.Media;
using WowCompanion.Core;

namespace WowCompanion.Desktop;

/// <summary>One line in the search dropdown.</summary>
public sealed record ItemRow(int Id, string Name, string Summary, Brush NameBrush, int IconId = 0)
{
    /// <summary>Stats line shown under the name in the loot list ("+12 Stamina · 45 Armor").</summary>
    public string StatText { get; init; } = "";
    /// <summary>Green equip / use effects.</summary>
    public string EffectText { get; init; } = "";
    public bool HasStatText => StatText.Length > 0;
    /// <summary>A quest in the search list (Id is then 0): opens the quest card instead of an item.</summary>
    public int QuestId { get; init; }
    public bool IsQuest => QuestId > 0;
    /// <summary>Icon by game name (quests have no item icon).</summary>
    public string IconName { get; init; } = "";
    /// <summary>An autocomplete line for the search syntax: the text that replaces the word being typed.</summary>
    public string Completion { get; init; } = "";
    public bool IsSuggestion => Completion.Length > 0;

    public static ItemRow FromQuest(QuestHit quest)
    {
        var parts = new List<string> { quest.Level > 0 ? $"Quest, level {quest.Level}" : "Quest" };
        if (quest.Giver.Length > 0) parts.Add(quest.Giver);
        parts.Add(quest.Place.Length > 0 ? quest.Place : quest.Zone);
        if (quest.Side.Length > 0) parts.Add(quest.Side);
        return new ItemRow(0, quest.Title.Trim(), string.Join(" · ", parts.Where(p => p.Length > 0)), SourceColors.Quest)
        {
            QuestId = quest.Id,
            IconName = "inv_letter_15",
        };
    }
    public bool HasEffectText => EffectText.Length > 0;
    /// <summary>"AH 1s 85c" when Auctionator has a price for the item.</summary>
    public string PriceText => Id > 0 ? AuctionPrices.Label(Id) : "";

    public static ItemRow From(ItemResult result)
    {
        var kind = ItemText.Kind(result.ClassId, result.SubclassId, result.InventoryType);
        var summary = kind.Length > 0
            ? $"{kind}, item level {result.ItemLevel}"
            : $"Item level {result.ItemLevel}";
        if (result.Highlight.Length > 0) summary += "   " + result.Highlight;
        return new ItemRow(result.Id, result.Name, summary, ItemQuality.BrushFor(result.Quality), result.IconId);
    }
}
