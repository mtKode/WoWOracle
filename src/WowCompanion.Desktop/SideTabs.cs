using System.Windows;

namespace WowCompanion.Desktop;

/// <summary>Corner rounding per side tab: only the top and bottom tabs of the column are rounded.</summary>
public static class SideTabs
{
    public static readonly DependencyProperty CornerProperty = DependencyProperty.RegisterAttached(
        "Corner", typeof(CornerRadius), typeof(SideTabs), new PropertyMetadata(new CornerRadius(0)));

    public static CornerRadius GetCorner(DependencyObject element) => (CornerRadius)element.GetValue(CornerProperty);
    public static void SetCorner(DependencyObject element, CornerRadius value) => element.SetValue(CornerProperty, value);
}
