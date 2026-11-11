using System.Windows.Media;
using WowCompanion.Core;

namespace WowCompanion.Desktop;

/// <summary>A row in the Loot page's left list: an instance, or a boss that matched the search.</summary>
public sealed record LootPlaceRow(bool IsBoss, int MapId, int NpcId, string Title, string Meta, Brush TitleBrush);

public sealed record LootBossRow(int NpcId, string Name, string Meta, Brush NameBrush, bool IsObject = false)
{
    /// <summary>The "Quests" entry at the top of the boss list.</summary>
    public bool IsQuests { get; init; }
}

/// <summary>Gear, recipes, quest items, other: each a list of item rows (same rows as search, so clicks and Alt work).</summary>
public sealed record LootSection(string Heading, IReadOnlyList<ItemRow> Items)
{
    public string Header => CountInHeader ? $"{Heading} ({Items.Count})" : Heading;
    public bool CountInHeader { get; init; } = true;
    /// <summary>Second line under the heading (quests: level, giver and where).</summary>
    public string Subheading { get; init; } = "";
    public bool HasSubheading => Subheading.Length > 0;
    /// <summary>Quest sections (dungeon quests): the quest, so its heading opens the quest card.</summary>
    public int QuestId { get; init; }
    public bool IsQuest => QuestId > 0;
    /// <summary>"Giver · Place (x, y)" for View on map; empty when there are no coordinates.</summary>
    public string MapText { get; init; } = "";
    public bool HasMap => MapText.Length > 0;
}

/// <summary>One alternative in the upgrade finder panel.</summary>
public sealed record UpgradeRow(
    int ItemId, string Name, Brush NameBrush, int IconId, string GainText, Brush GainBrush,
    string StatLine, string SourceText, Brush SourceBrush, string LevelText)
{
    /// <summary>The source has map coordinates ("View on map").</summary>
    public bool HasMap => MapPlace.Find(SourceText) is not null;
    /// <summary>"AH 1s 85c" when Auctionator has a price for the item.</summary>
    public string PriceText => AuctionPrices.Label(ItemId);
}

/// <summary>A note in the Notes list.</summary>
public sealed record NoteRow(int Id, string Title, string Meta, bool Done, bool Overdue, Note Note)
{
    /// <summary>Empty box until the note is done.</summary>
    public string DoneMark => Done ? "✓" : "";
    public Brush TitleBrush => Done ? SourceColors.Normal : Overdue ? CompareColors.Down : SourceColors.Elite;
    public double TitleOpacity => Done ? 0.55 : 1;
}
