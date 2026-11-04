using System.Windows.Media;

namespace WowCompanion.Desktop;

/// <summary>One stat difference: "+12 Stamina" in green, "-4 Spirit" in red.</summary>
public sealed record CompareRow(string Value, string Label, Brush Brush);

/// <summary>The candidate measured against one equipped item (rings and trinkets get two).</summary>
public sealed record CompareSection(string Heading, Brush HeadingBrush, IReadOnlyList<CompareRow> Rows)
{
    public bool IsEmpty => Rows.Count == 0;
}

/// <summary>What the Alt-compare card shows.</summary>
public sealed record CompareCard(string Title, Brush TitleBrush, string Subtitle, IReadOnlyList<CompareSection> Sections, string Message)
{
    public bool HasMessage => Message.Length > 0;
}

public static class CompareColors
{
    public static readonly Brush Up = Make("#1EFF00");
    public static readonly Brush Down = Make("#FF4040");
    public static readonly Brush Neutral = Make("#A8977A");

    private static Brush Make(string hex)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        brush.Freeze();
        return brush;
    }
}
