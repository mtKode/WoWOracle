using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using WowCompanion.Core;

namespace WowCompanion.Desktop;

public enum TalentState { Locked, Available, Partial, Max }

/// <summary>One talent square in a tree.</summary>
public partial class TalentCell(TalentInfo info, TalentTreeView tree) : ObservableObject
{
    public TalentInfo Info { get; } = info;
    public TalentTreeView Tree { get; } = tree;

    public int Id => Info.Id;
    public string Name => Info.Name;
    public int Tier => Info.Tier;
    public int Column => Info.Column;
    public int MaxRank => Info.MaxRank;
    public int IconId => Info.IconId;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RankText))]
    private int _rank;

    [ObservableProperty] private TalentState _state;

    /// <summary>Hover card contents; filled when the mouse enters the talent.</summary>
    [ObservableProperty] private TalentDetail? _detail;

    public string RankText => $"{Rank}/{MaxRank}";

    /// <summary>Prerequisite talent in the same tree, if any.</summary>
    public TalentCell? Prereq { get; set; }
}

/// <summary>One of a class's three trees.</summary>
public partial class TalentTreeView(TalentTree tree) : ObservableObject
{
    public int Id { get; } = tree.Id;
    public string Name { get; } = tree.Name;
    public string Background { get; } = tree.Background;
    public int IconId { get; } = tree.IconId;

    /// <summary>Tree art from the game files, or the spec icon as a faint watermark when there is none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasArt), nameof(HasWatermark))]
    private ImageSource? _art;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasArt), nameof(HasWatermark))]
    private bool _artIsIcon;

    public bool HasArt => Art is not null && !ArtIsIcon;
    public bool HasWatermark => Art is not null && ArtIsIcon;

    public async Task LoadArtAsync()
    {
        var art = await IconCache.GetTalentArtAsync(Id, Background);
        if (art is not null)
        {
            Art = art;
            return;
        }
        ArtIsIcon = true;
        Art = await IconCache.GetAsync(IconId, IconCache.Large);
    }
    public ObservableCollection<TalentCell> Cells { get; } = [];
    public ObservableCollection<TalentArrow> Arrows { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Header))]
    private int _points;

    [ObservableProperty] private bool _isSelected;

    public string Header => $"{Name}  {Points}";

    public int PointsBelow(int tier) => Cells.Where(c => c.Tier < tier).Sum(c => c.Rank);
}

/// <summary>Prerequisite arrow between two talents, drawn behind the squares. Lit once the prerequisite is met.</summary>
public sealed record TalentArrow(Geometry Line, Geometry Head, bool Active)
{
    // Must match the tree grid in MainWindow.xaml: 96 px per slot, 84 px squares.
    private const double Slot = 96, Half = 42, HeadSize = 7;

    public static TalentArrow Between(TalentCell from, TalentCell to, bool active)
    {
        double Cx(TalentCell c) => c.Column * Slot + Slot / 2;
        double Cy(TalentCell c) => c.Tier * Slot + Slot / 2;

        var points = new List<(double X, double Y)>();
        if (from.Tier == to.Tier)
        {
            var dir = Math.Sign(to.Column - from.Column);
            points.Add((Cx(from) + dir * Half, Cy(from)));
            points.Add((Cx(to) - dir * Half, Cy(to)));
        }
        else if (from.Column == to.Column)
        {
            points.Add((Cx(from), Cy(from) + Half));
            points.Add((Cx(to), Cy(to) - Half));
        }
        else
        {
            // Across first, then down into the target.
            var dir = Math.Sign(to.Column - from.Column);
            points.Add((Cx(from) + dir * Half, Cy(from)));
            points.Add((Cx(to), Cy(from)));
            points.Add((Cx(to), Cy(to) - Half));
        }

        var (ex, ey) = points[^1];
        var (px, py) = points[^2];
        var dx = Math.Sign(ex - px);
        var dy = Math.Sign(ey - py);
        // Triangle pointing along the last segment.
        var head = dx != 0
            ? $"M {F(ex)},{F(ey)} L {F(ex - dx * HeadSize)},{F(ey - HeadSize)} L {F(ex - dx * HeadSize)},{F(ey + HeadSize)} Z"
            : $"M {F(ex)},{F(ey)} L {F(ex - HeadSize)},{F(ey - dy * HeadSize)} L {F(ex + HeadSize)},{F(ey - dy * HeadSize)} Z";
        var line = "M " + string.Join(" L ", points.Select(p => $"{F(p.X)},{F(p.Y)}"));

        return new TalentArrow(Freeze(Geometry.Parse(line)), Freeze(Geometry.Parse(head)), active);
    }

    private static string F(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);

    private static Geometry Freeze(Geometry g)
    {
        g.Freeze();
        return g;
    }
}

/// <summary>Hover / selection panel next to the tree.</summary>
public sealed record TalentDetail(
    string Name, string RankLine, string Current, string NextLabel, string Next, IReadOnlyList<string> Requirements);
