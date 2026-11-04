using CommunityToolkit.Mvvm.ComponentModel;

namespace WowCompanion.Desktop;

/// <summary>A tale in the Journal: a quest chain told like an entry in the quest log; its steps open on request.</summary>
public sealed partial class TaleRow(
    string title, string meta, string themes, string teaser, string start, bool featured, IReadOnlyList<TaleStepRow> steps)
    : ObservableObject
{
    public string Title { get; } = title;
    public string Meta { get; } = meta;
    public string Themes { get; } = themes;
    public bool HasThemes => Themes.Length > 0;
    public string Teaser { get; } = teaser;
    public bool HasTeaser => Teaser.Length > 0;
    public string Start { get; } = start;
    public bool HasMap => WowCompanion.Core.MapPlace.Find(Start) is not null;
    public bool Featured { get; } = featured;
    /// <summary>A small star after the title of the zone's great tales.</summary>
    public string Star => Featured ? "  ✦" : "";
    public IReadOnlyList<TaleStepRow> Steps { get; } = steps;
    public bool HasSteps => Steps.Count > 1;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(TrailText))] private bool _isOpen;

    public string TrailText => IsOpen ? "Hide the trail" : $"Follow the trail ({Steps.Count} quests)";
}

/// <summary>One quest in a tale's trail.</summary>
public sealed record TaleStepRow(int Number, string Title, string Meta, int QuestId);
