using System.Globalization;
using System.Windows.Media;
using WowCompanion.Core;

namespace WowCompanion.Desktop;

/// <summary>The quest card on the Items page: who gives it, where, what for, its chain and rewards.</summary>
public sealed record QuestView(
    int Id, string Title, string LevelLine, string Giver, string Place, bool HasMap, string Objectives, bool HasObjectives,
    IReadOnlyList<QuestStepRow> Chain, string ChainTitle, IReadOnlyList<QuestRewardRow> Rewards, IReadOnlyList<QuestRewardRow> Choices,
    string MoneyText)
{
    public bool HasChain => Chain.Count > 1;
    public bool HasRewards => Rewards.Count > 0;
    public bool HasChoices => Choices.Count > 0;
    public bool HasGiver => Giver.Length > 0;
    public string Description { get; init; } = "";
    public bool HasDescription => Description.Length > 0;
    /// <summary>"Gryan Stoutmantle · Westfall (56.4, 47.6)" for View on map.</summary>
    public string MapText => $"{Giver} · {Place}";

    public static QuestView From(QuestCard card)
    {
        var level = new List<string> { card.Level > 0 ? $"Level {card.Level}" : "Quest" };
        if (card.MinLevel > 0 && card.MinLevel != card.Level) level.Add($"from level {card.MinLevel}");
        if (card.Zone.Length > 0) level.Add(card.Zone);
        if (card.Side.Length > 0) level.Add(card.Side);
        var place = card.GiverPlace.Length > 0 ? card.GiverPlace : card.Zone;
        var chain = card.Chain.Select((s, i) => new QuestStepRow(s.Id, $"{i + 1}. {s.Title}", s.Level > 0 ? $"level {s.Level}" : "", s.IsThis)).ToList();
        var position = card.Chain.ToList().FindIndex(s => s.IsThis);
        var chainTitle = card.Chain.Count > 1 ? $"Part {position + 1} of {card.Chain.Count} in a chain" : "";
        QuestRewardRow Row(QuestReward r) => new(r.ItemId, r.Name, ItemQuality.BrushFor(r.Quality), r.IconId);
        return new QuestView(card.Id, card.Title, string.Join(" · ", level), card.Giver, place,
            MapPlace.Find(place) is not null && card.Giver.Length > 0, card.Objectives, card.Objectives.Length > 0,
            chain, chainTitle, card.Rewards.Where(r => !r.IsChoice).Select(Row).ToList(),
            card.Rewards.Where(r => r.IsChoice).Select(Row).ToList(), "")
        {
            Description = card.Description,
        };
    }
}

public sealed record QuestStepRow(int QuestId, string Title, string Meta, bool IsThis)
{
    public Brush TitleBrush => IsThis ? SourceColors.Quest : (Brush)Brushes.Gainsboro;
}

/// <summary>A reward item on the quest card (opens the item; hover shows its card).</summary>
public sealed record QuestRewardRow(int ItemId, string Name, Brush NameBrush, int IconId);
