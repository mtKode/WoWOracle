using System.Windows.Media;

namespace WowCompanion.Desktop;

public static class ItemQuality
{
    // Poor, common, uncommon, rare, epic, legendary, artifact
    private static readonly Brush[] Brushes = new[]
        { "#9d9d9d", "#ffffff", "#1eff00", "#0070dd", "#a335ee", "#ff8000", "#e6cc80" }
        .Select(hex =>
        {
            var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
            brush.Freeze();
            return (Brush)brush;
        })
        .ToArray();

    public static Brush BrushFor(int quality) => Brushes[Math.Clamp(quality, 0, Brushes.Length - 1)];
}
