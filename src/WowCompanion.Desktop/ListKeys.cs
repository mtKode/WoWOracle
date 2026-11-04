using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace WowCompanion.Desktop;

/// <summary>
/// The same list keys everywhere:
/// - A filter box with <c>local:ListKeys.Target="{Binding ElementName=SomeList}"</c>: Up/Down, Tab/Shift+Tab and
///   PageUp/PageDown move the list's selection while you keep typing (the selection scrolls into view).
/// - Inside any list: arrows keep walking the entries even when the focus sits in an entry's selectable text
///   (CopyText boxes would otherwise eat the arrow keys). Registered once for every ListBox by <see cref="Register"/>.
/// </summary>
public static class ListKeys
{
    public static readonly DependencyProperty TargetProperty = DependencyProperty.RegisterAttached(
        "Target", typeof(Selector), typeof(ListKeys), new PropertyMetadata(null, OnTargetChanged));

    public static Selector? GetTarget(DependencyObject element) => (Selector?)element.GetValue(TargetProperty);
    public static void SetTarget(DependencyObject element, Selector? value) => element.SetValue(TargetProperty, value);

    private static bool _registered;

    /// <summary>Arrow keys in read-only text inside any ListBox move the list instead of the caret.</summary>
    public static void Register()
    {
        if (_registered) return;
        _registered = true;
        EventManager.RegisterClassHandler(typeof(ListBox), UIElement.PreviewKeyDownEvent, new KeyEventHandler(OnListKeyDown));
    }

    /// <summary>The step a key means for a list (0 = not a list key). Tab only counts when asked.</summary>
    public static int Step(Key key, bool withTab)
    {
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) != 0) return 0;
        return key switch
        {
            Key.Down => 1,
            Key.Up => -1,
            Key.PageDown => 10,
            Key.PageUp => -10,
            Key.Tab when withTab => shift ? -1 : 1,
            _ => 0,
        };
    }

    /// <summary>Moves the selection; from nothing selected, down starts at the top and up at the bottom.</summary>
    public static bool Move(Selector list, int step)
    {
        var count = list.Items.Count;
        if (step == 0 || count == 0) return false;
        var index = list.SelectedIndex;
        index = index < 0 ? (step > 0 ? 0 : count - 1) : Math.Clamp(index + step, 0, count - 1);
        list.SelectedIndex = index;
        if (list is ListBox box && box.SelectedItem is { } item) box.ScrollIntoView(item);
        return true;
    }

    private static void OnTargetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement box) return;
        box.PreviewKeyDown -= OnBoxKeyDown;
        if (e.NewValue is Selector) box.PreviewKeyDown += OnBoxKeyDown;
    }

    private static void OnBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not DependencyObject box || GetTarget(box) is not { } list) return;
        if (Move(list, Step(e.Key, withTab: true))) e.Handled = true;
    }

    private static void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Handled || sender is not ListBox list) return;
        // Only when an entry's read-only text has the focus; the list's own keys work as usual otherwise.
        if (e.OriginalSource is not TextBoxBase { IsReadOnly: true } text) return;
        if (ItemsControl.ContainerFromElement(list, text) is not ListBoxItem container) return;
        var step = Step(e.Key, withTab: false);
        if (step == 0) return;
        // Start from the entry the text belongs to, so clicking text and pressing Down goes to the next entry.
        var own = list.ItemContainerGenerator.ItemFromContainer(container);
        if (own != DependencyProperty.UnsetValue) list.SelectedItem = own;
        if (Move(list, step)) e.Handled = true;
    }
}
