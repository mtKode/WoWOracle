using System.Windows;
using System.Windows.Controls;

namespace WowCompanion.Desktop;

/// <summary>
/// An Image that shows a game icon by file number: &lt;local:GameIcon FileId="{Binding IconId}" Size="large" /&gt;,
/// or by name: &lt;local:GameIcon IconName="inv_misc_bag_08" /&gt;.
/// Loads in the background through IconCache; stays empty if the icon can't be found.
/// </summary>
public sealed class GameIcon : Image
{
    public static readonly DependencyProperty FileIdProperty = DependencyProperty.Register(
        nameof(FileId), typeof(int), typeof(GameIcon), new PropertyMetadata(0, OnIconChanged));

    public static readonly DependencyProperty IconNameProperty = DependencyProperty.Register(
        nameof(IconName), typeof(string), typeof(GameIcon), new PropertyMetadata("", OnIconChanged));

    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
        nameof(Size), typeof(string), typeof(GameIcon), new PropertyMetadata(IconCache.Medium, OnIconChanged));

    public GameIcon()
    {
        Stretch = System.Windows.Media.Stretch.UniformToFill;
    }

    public int FileId
    {
        get => (int)GetValue(FileIdProperty);
        set => SetValue(FileIdProperty, value);
    }

    /// <summary>Icon by its game name instead of file number, e.g. "inv_misc_bag_08" (used when FileId is 0).</summary>
    public string IconName
    {
        get => (string)GetValue(IconNameProperty);
        set => SetValue(IconNameProperty, value);
    }

    /// <summary>small (18 px), medium (36 px) or large (56 px) source image.</summary>
    public string Size
    {
        get => (string)GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    private static async void OnIconChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var icon = (GameIcon)d;
        var id = icon.FileId;
        var name = icon.IconName ?? "";
        var size = icon.Size;
        icon.Source = null;
        if (id <= 0 && name.Length == 0) return;

        var source = id > 0 ? await IconCache.GetAsync(id, size) : await IconCache.GetByNameAsync(name, size);
        if (icon.FileId == id && icon.IconName == name && icon.Size == size) icon.Source = source;
    }
}
