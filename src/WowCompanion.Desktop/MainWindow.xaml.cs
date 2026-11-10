using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using Wpf.Ui.Controls;

namespace WowCompanion.Desktop;

public partial class MainWindow : FluentWindow
{
    private readonly MainViewModel _vm;
    private GlobalHotkey? _hotkey;

    /// <summary>
    /// The window is transparent (AllowsTransparency, WindowStyle None) so the side tabs can stick out of the frame.
    /// FluentWindow would switch WindowStyle back to SingleBorderWindow here, which a transparent window can't have.
    /// </summary>
    protected override void OnExtendsContentIntoTitleBarChanged(bool oldValue, bool newValue)
    {
    }

    /// <summary>No backdrop handling either: it would put the theme's grey background back behind the tabs.</summary>
    protected override void OnBackdropTypeChanged(WindowBackdropType oldValue, WindowBackdropType newValue)
    {
    }

    /// <summary>
    /// No window frame at all: Windows would otherwise paint its dark rounded window background behind the
    /// transparent area (visible behind the side tabs). Resizing still works through the WindowChrome border.
    /// </summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 0,
            CornerRadius = default,
            GlassFrameThickness = new Thickness(0),
            ResizeBorderThickness = new Thickness(6),
            UseAeroCaptionButtons = false,
        });

        // FluentWindow's "no backdrop" path paints the theme background (window and HWND); keep both see-through.
        SetCurrentValue(BackgroundProperty, Brushes.Transparent);
        if (PresentationSource.FromVisual(this) is HwndSource source && source.CompositionTarget is { } target)
            target.BackgroundColor = Colors.Transparent;

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        try
        {
            var margins = new DwmMargins();
            _ = DwmExtendFrameIntoClientArea(hwnd, ref margins);
            var noBorder = unchecked((int)0xFFFFFFFE); // DWMWA_COLOR_NONE
            _ = DwmSetWindowAttribute(hwnd, 34, ref noBorder, sizeof(int)); // DWMWA_BORDER_COLOR (Windows 11)
            var square = 1; // DWMWCP_DONOTROUND: the frame draws its own rounded corners
            _ = DwmSetWindowAttribute(hwnd, 33, ref square, sizeof(int)); // DWMWA_WINDOW_CORNER_PREFERENCE
            var noBackdrop = 1; // DWMSBT_NONE
            _ = DwmSetWindowAttribute(hwnd, 38, ref noBackdrop, sizeof(int)); // DWMWA_SYSTEMBACKDROP_TYPE
        }
        catch (Exception)
        {
            // Older Windows: nothing to remove.
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DwmMargins
    {
        public int Left, Right, Top, Bottom;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref DwmMargins margins);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    private bool _compareHeld;
    private int _compareItemId;
    private TrayIcon? _tray;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _vm = viewModel;
        DataContext = viewModel;

        // Use the game's own font when it can be found; otherwise the Palatino fallback in the XAML stays.
        if (GameFont.TryLoad() is { } font) Resources["Serif"] = font;

        Loaded += async (_, _) =>
        {
            SearchBox.Focus();
            await viewModel.InitializeAsync();
        };

        SourceInitialized += (_, _) =>
        {
            // Ctrl+Shift+_ (the -/_ key): rarely taken by other apps.
            _hotkey = new GlobalHotkey(this, GlobalHotkey.Ctrl | GlobalHotkey.Shift, GlobalHotkey.VkMinus);
            _hotkey.Pressed += ToggleVisibility;
            _tray = new TrayIcon(ShowFromTray, Close);
            if (!_hotkey.IsRegistered) viewModel.Toast("Ctrl+Shift+_ is used by another app.");
        };

        // The popup stays open on its own, so close it whenever focus or position could leave it stranded.
        // Switching page while hovering a talent: the square disappears without a MouseLeave, so close the card here.
        // Map page: fit the picture to the view when a new map opens, zoom in on a requested spot.
        viewModel.MapFocusRequested += pin => Dispatcher.InvokeAsync(() => FocusMap(pin), System.Windows.Threading.DispatcherPriority.Loaded);
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MainViewModel.SelectedMapZone))
            {
                _mapZoom = 1;
                Dispatcher.InvokeAsync(UpdateMapScale, System.Windows.Threading.DispatcherPriority.Loaded);
            }
            if (e.PropertyName is nameof(MainViewModel.MapWidth) or nameof(MainViewModel.MapHeight) or nameof(MainViewModel.CurrentView))
                Dispatcher.InvokeAsync(UpdateMapScale, System.Windows.Threading.DispatcherPriority.Loaded);
        };
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MainViewModel.CurrentView) or nameof(MainViewModel.SelectedTalentClass))
                TalentPopup.IsOpen = false;
            // Rows under the mouse can vanish without a MouseLeave (page switch, list refresh).
            if (e.PropertyName is nameof(MainViewModel.CurrentView) or nameof(MainViewModel.SelectedLootBoss))
                ItemPopup.IsOpen = false;
        };
        Deactivated += (_, _) => { viewModel.CloseDropdown(); FilterHelp.IsOpen = false; TalentPopup.IsOpen = false; ItemPopup.IsOpen = false; };
        LocationChanged += (_, _) => { viewModel.CloseDropdown(); FilterHelp.IsOpen = false; TalentPopup.IsOpen = false; ItemPopup.IsOpen = false; };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && FilterHelp.IsOpen) { FilterHelp.IsOpen = false; e.Handled = true; }
        };
        PreviewMouseDown += OnPreviewMouseDown;

        // Alt-compare: hold left Alt over any item. (Right Alt is AltGr on Nordic keyboards, used for @ { [ and so on.)
        PreviewKeyDown += OnCompareKeyDown;
        PreviewKeyUp += OnCompareKeyUp;
        PreviewMouseMove += (_, _) =>
        {
            if (_compareHeld) UpdateCompare();
            if (ItemPopup.IsOpen) MoveItemPopup();
        };
        Deactivated += (_, _) => EndCompare();
        PreviewTextInput += OnPreviewTextInput;

        SearchBox.PreviewKeyDown += OnSearchKeyDown;
        // Chips: keep the caret at the end after text is rewritten; Backspace at the start pops the last chip.
        viewModel.SearchTextReplaced += () => Dispatcher.InvokeAsync(() => SearchBox.CaretIndex = SearchBox.Text.Length);
        SearchBox.GotKeyboardFocus += (_, _) => viewModel.ReopenDropdown();
        // Walking the chips: the caret hides while a chip is highlighted; a click in the text or leaving the box ends it.
        SearchBox.PreviewMouseLeftButtonDown += (_, _) => viewModel.LeaveChips();
        SearchBox.LostKeyboardFocus += (_, _) => viewModel.LeaveChips();
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(MainViewModel.IsChipSelected)) return;
            if (viewModel.IsChipSelected) SearchBox.CaretBrush = Brushes.Transparent;
            else SearchBox.ClearValue(System.Windows.Controls.Primitives.TextBoxBase.CaretBrushProperty);
        };

        ResultsList.SelectionChanged += (_, _) =>
        {
            if (ResultsList.SelectedItem is { } item) ResultsList.ScrollIntoView(item);
        };
        ResultsList.PreviewMouseLeftButtonUp += OnResultClicked;

        SpellList.SelectionChanged += (_, _) =>
        {
            if (SpellList.SelectedItem is { } item) SpellList.ScrollIntoView(item);
        };

        // Loot and upgrade lists: click opens the item, Shift+click adds it to gear, Ctrl+click queues its recipe.
        LootList.PreviewMouseLeftButtonUp += OnItemListClicked;
        LootFilterBox.PreviewKeyDown += OnLootFilterKeyDown;
        LootPlacesList.SelectionChanged += (_, _) =>
        {
            if (LootPlacesList.SelectedItem is { } item) LootPlacesList.ScrollIntoView(item);
        };
        LootBossesList.SelectionChanged += (_, _) =>
        {
            if (LootBossesList.SelectedItem is { } item) LootBossesList.ScrollIntoView(item);
        };
        UpgradeList.PreviewMouseLeftButtonUp += OnItemListClicked;

        // Reminders pop from the tray too, so they reach you while the window is hidden.
        viewModel.ReminderDue += OnReminderDue;
        viewModel.RequestNoteTitleFocus += () => Dispatcher.InvokeAsync(() =>
        {
            NoteTitleBox.Focus();
            NoteTitleBox.SelectAll();
        }, System.Windows.Threading.DispatcherPriority.Input);

        Closed += (_, _) =>
        {
            viewModel.SaveNoteIfDirty();
            viewModel.FlushTalentSave();
            _hotkey?.Dispose();
            _tray?.Dispose();
        };
    }

    /// <summary>"/" jumps to the search box and empties it. Text input is layout-independent, so this works on any keyboard.</summary>
    private void OnPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (e.Text != "/" || SearchBox.IsKeyboardFocused) return;
        _vm.ClearQuery();
        SearchBox.Focus();
        e.Handled = true;
    }

    /// <summary>Chips in the search box: Left at the start of the text walks onto them (see MainViewModel.Search).</summary>
    private bool OnChipKey(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt) return false;
        var atStart = SearchBox.CaretIndex == 0 && SearchBox.SelectionLength == 0 && Keyboard.Modifiers == ModifierKeys.None;

        if (!_vm.IsChipSelected)
        {
            if (atStart && key == Key.Left) return _vm.ChipLeft();
            if (atStart && key == Key.Home && _vm.SearchChips.Count > 0) { _vm.ChipHome(); return true; }
            return false;
        }

        switch (key)
        {
            case Key.Left: _vm.ChipLeft(); return true;
            case Key.Right: _vm.ChipRight(); return true;
            case Key.Home: _vm.ChipHome(); return true;
            case Key.End:
                _vm.LeaveChips();
                SearchBox.CaretIndex = SearchBox.Text.Length;
                return true;
            case Key.Back: _vm.DeleteSelectedChip(backwards: true); return true;
            case Key.Delete: _vm.DeleteSelectedChip(backwards: false); return true;
            case Key.Enter or Key.F2: _vm.EditSelectedChip(); return true;
            case Key.Escape: _vm.LeaveChips(); return true;
            default:
                _vm.LeaveChips(); // typing, Up/Down…: back to the text, the key does its usual thing
                return false;
        }
    }

    /// <summary>Click a chip to highlight it (then ← → Del Enter work), double click to edit it.</summary>
    private void OnChipMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: FilterChip chip }) return;
        if (e.ClickCount >= 2) _vm.EditChip(chip);
        else _vm.SelectChip(chip);
        SearchBox.Focus();
        if (_vm.IsChipSelected) SearchBox.CaretIndex = 0;
        e.Handled = true;
    }

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (OnChipKey(e))
        {
            e.Handled = true;
            return;
        }
        var shift = Keyboard.Modifiers == ModifierKeys.Shift;

        // Autocomplete: Tab takes the highlighted suggestion (or the first one); Enter takes a highlighted one.
        if (_vm.IsDropdownOpen && Keyboard.Modifiers == ModifierKeys.None
            && ((e.Key == Key.Tab && _vm.AcceptSuggestion()) || (e.Key == Key.Enter && _vm.HighlightedIsSuggestion && _vm.AcceptSuggestion())))
        {
            e.Handled = true;
            return;
        }

        var forward = e.Key == Key.Down || (e.Key == Key.Tab && !shift && _vm.IsDropdownOpen);
        var backward = e.Key == Key.Up || (e.Key == Key.Tab && shift && _vm.IsDropdownOpen);

        if (e.Key == Key.Back && SearchBox.CaretIndex == 0 && SearchBox.SelectionLength == 0 && _vm.PopChip())
        {
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape)
        {
            _vm.Escape();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Shift)
        {
            // Shift+Enter: put the highlighted item into the active gear set (replaces what's in that slot).
            _vm.AddHighlightedToGear();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
        {
            // Ctrl+Enter: queue the recipe that makes the highlighted item.
            _vm.AddHighlightedToCraft();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            if (_vm.ChooseHighlighted())
            {
                SearchBox.SelectAll();
                e.Handled = true;
            }
        }
        else if ((forward || backward) && _vm.Results.Count > 0)
        {
            if (_vm.IsDropdownOpen) _vm.MoveHighlight(forward ? 1 : -1);
            else _vm.ReopenDropdown();
            e.Handled = true;
        }
    }

    private void OnResultClicked(object sender, MouseButtonEventArgs e)
    {
        var container = ItemsControl.ContainerFromElement(ResultsList, e.OriginalSource as DependencyObject) as ListBoxItem;
        if (container?.DataContext is not ItemRow row) return;

        // Shift+click: add to gear set. Ctrl+click: add its recipe to the craft list. The list stays open.
        if (Keyboard.Modifiers == ModifierKeys.Shift) { _vm.AddRowToGear(row); e.Handled = true; return; }
        if (Keyboard.Modifiers == ModifierKeys.Control) { _vm.AddRowToCraft(row); e.Handled = true; return; }
        if (row.IsSuggestion)
        {
            _vm.AcceptSuggestion(row);
            SearchBox.Focus();
            e.Handled = true;
            return;
        }

        _vm.Choose(row);
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    /// <summary>Hovering a talent shows its ranks in the side panel.</summary>
    private void OnTalentMouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TalentCell cell } slot) return;
        _vm.HoverTalent(cell);
        TalentPopup.IsOpen = false; // re-open so it moves to the new square
        // Next to the icon itself (the slot is wider than the icon, so the card would float off to the side).
        TalentPopup.PlacementTarget = FindNamed(slot, "Cell") ?? slot;
        TalentPopup.IsOpen = true;
    }

    /// <summary>The first element with this x:Name inside a template's visual tree.</summary>
    private static FrameworkElement? FindNamed(DependencyObject root, string name)
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement { Name: var n } element && n == name) return element;
            if (FindNamed(child, name) is { } found) return found;
        }
        return null;
    }

    /// <summary>Gear page "Enchant": open the picker under the button, filter box focused.</summary>
    private void OnEnchantClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: GearSlotRow row } button) return;
        ItemPopup.IsOpen = false;
        EnchantPopup.PlacementTarget = button;
        if (!_vm.OpenEnchantPicker(row)) return;
        Dispatcher.InvokeAsync(() => EnchantFilterBox.Focus(), System.Windows.Threading.DispatcherPriority.Input);
    }

    /// <summary>Item rows (Gear, Loot, upgrades): show the item's card next to the row.</summary>
    private void OnItemMouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: { } context } row) return;
        var id = context switch
        {
            GearSlotRow gear => gear.ItemId,
            ItemRow item => item.Id,
            UpgradeRow up => up.ItemId,
            QuestRewardRow reward => reward.ItemId,
            PlanMaterialRow material => material.ItemId,
            _ => 0,
        };
        ItemPopup.IsOpen = false;
        if (id <= 0 || !_vm.ShowHoverCard(id)) return;
        ItemPopup.PlacementTarget = this;
        MoveItemPopup();
        ItemPopup.IsOpen = true;
    }

    /// <summary>Keeps the item card just below-right of the mouse pointer, like a game tooltip.</summary>
    private void MoveItemPopup()
    {
        var p = Mouse.GetPosition(this);
        ItemPopup.HorizontalOffset = p.X + 18;
        ItemPopup.VerticalOffset = p.Y + 16;
    }

    private void OnItemMouseLeave(object sender, MouseEventArgs e) => ItemPopup.IsOpen = false;

    /// <summary>The card goes away as soon as the mouse leaves the square, wherever it goes next.</summary>
    private void OnTalentMouseLeave(object sender, MouseEventArgs e) => TalentPopup.IsOpen = false;

    /// <summary>
    /// Every press counts, however fast: left adds a rank, right removes one. (A MouseBinding "LeftClick" skips the
    /// second press of a double click, which made fast clicking into a 5-rank talent stall.)
    /// </summary>
    private void OnTalentMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TalentCell cell }) return;
        if (e.ChangedButton == MouseButton.Left) _vm.AddRankCommand.Execute(cell);
        else if (e.ChangedButton == MouseButton.Right) _vm.RemoveRankCommand.Execute(cell);
        else return;
        e.Handled = true;
    }

    /// <summary>The ? in the search box: shows or hides the filter reference.</summary>
    private void OnHelpClick(object sender, RoutedEventArgs e)
    {
        FilterHelp.IsOpen = !FilterHelp.IsOpen;
        if (FilterHelp.IsOpen) _vm.CloseDropdown();
    }

    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        // Mouse back / forward buttons walk the page history, like a browser.
        if (e.ChangedButton is MouseButton.XButton1 or MouseButton.XButton2)
        {
            ItemPopup.IsOpen = false;
            if (e.ChangedButton == MouseButton.XButton1) _vm.NavigateBack();
            else _vm.NavigateForward();
            e.Handled = true;
            return;
        }

        // Any click outside the chronicle closes it (its own button toggles it).
        if (_vm.IsLogOpen && !IsInside(e.OriginalSource as DependencyObject, LogPanel)
            && !IsInside(e.OriginalSource as DependencyObject, LogButton)) _vm.IsLogOpen = false;

        // Any click outside the ? button closes the filter reference (clicks inside the card stay in its own popup).
        if (FilterHelp.IsOpen && !IsInside(e.OriginalSource as DependencyObject, HelpButton)) FilterHelp.IsOpen = false;

        if (!_vm.IsDropdownOpen) return;
        if (IsInside(e.OriginalSource as DependencyObject, SearchFrame)
            || IsInside(e.OriginalSource as DependencyObject, ResultsList)) return;
        _vm.CloseDropdown();
    }

    private static bool IsInside(DependencyObject? node, DependencyObject container)
    {
        while (node is not null)
        {
            if (ReferenceEquals(node, container)) return true;
            node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        }
        return false;
    }

    /// <summary>
    /// Ctrl+Shift+_: tuck the window away into the tray, or bring it back. The X button still exits the app.
    /// </summary>
    private void ToggleVisibility()
    {
        if (IsVisible && IsActive && WindowState != WindowState.Minimized)
        {
            HideToTray();
            return;
        }
        ShowFromTray();
    }

    private void HideToTray()
    {
        TalentPopup.IsOpen = false;
        FilterHelp.IsOpen = false;
        _vm.CloseDropdown();
        _vm.FlushTalentSave();
        Hide();
        _tray?.HintOnce();
    }

    private void ShowFromTray()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        // Topmost blip: Windows only lets a background app take focus this way.
        if (!Topmost) { Topmost = true; Topmost = _vm.AlwaysOnTop; }
        SearchBox.Focus();
        SearchBox.SelectAll();
        ShowMissedReminders();
    }

    // ----- Reminders -----

    /// <summary>Reminders that came due while the window was hidden in the tray, oldest first.</summary>
    private readonly List<string> _missedReminders = [];

    /// <summary>
    /// A reminder came due: the tray notification, plus the taskbar button flashing until the window is in front.
    /// Hidden in the tray there is no taskbar button, so it waits for the window to come back.
    /// </summary>
    private void OnReminderDue(string title, string body)
    {
        _tray?.ShowReminder(title, body);
        if (!IsVisible) _missedReminders.Add(title.Length > 0 ? title : "Reminder");
        else if (!IsActive || WindowState == WindowState.Minimized) TaskbarFlash.UntilForeground(this);
    }

    /// <summary>Back from the tray: flash, and say which reminders came while it was away.</summary>
    private void ShowMissedReminders()
    {
        if (_missedReminders.Count == 0) return;
        _vm.ToastOnly(_missedReminders.Count == 1
            ? $"Missed reminder: {_missedReminders[0]}"
            : $"{_missedReminders.Count} missed reminders: see Notes");
        _missedReminders.Clear();
        TaskbarFlash.Times(this, 5);
    }

    // ----- Alt-compare -----

    private static bool IsLeftAlt(KeyEventArgs e) => (e.Key == Key.System ? e.SystemKey : e.Key) == Key.LeftAlt;

    private void OnCompareKeyDown(object sender, KeyEventArgs e)
    {
        if (!IsLeftAlt(e) || (Keyboard.Modifiers & ModifierKeys.Control) != 0) return;
        e.Handled = true; // keeps Windows from moving focus to the (hidden) system menu
        if (_compareHeld) return;
        _compareHeld = true;
        _compareItemId = -1;
        UpdateCompare();
    }

    private void OnCompareKeyUp(object sender, KeyEventArgs e)
    {
        if (!IsLeftAlt(e)) return;
        if (_compareHeld) e.Handled = true; // plain Alt release would otherwise open the system menu
        EndCompare();
    }

    private void EndCompare()
    {
        _compareHeld = false;
        _compareItemId = 0;
        ComparePopup.IsOpen = false;
        _vm.HideCompare();
    }

    /// <summary>Finds the item under the mouse (or the keyboard's item) and reopens the card at the mouse when it changes.</summary>
    private void UpdateCompare()
    {
        var itemId = ItemUnderMouse();
        if (itemId == 0) itemId = _vm.FallbackCompareItem();
        if (itemId == _compareItemId) return;
        _compareItemId = itemId;

        ComparePopup.IsOpen = false;
        if (itemId == 0) { _vm.HideCompare(); return; }
        _vm.ShowCompare(itemId);
        ComparePopup.IsOpen = _vm.CompareCard is not null;
    }

    private static int ItemUnderMouse()
    {
        var node = Mouse.DirectlyOver as DependencyObject;
        while (node is not null)
        {
            if (node is FrameworkElement { DataContext: { } context })
            {
                var id = context switch
                {
                    ItemRow row => row.Id,
                    GearSlotRow gear => gear.ItemId,
                    RecipeRow recipe => recipe.CreatedItemId,
                    UpgradeRow upgrade => upgrade.ItemId,
                    QuestRewardRow reward => reward.ItemId,
                    PlanMaterialRow material => material.ItemId,
                    ItemView card => card.Id,
                    _ => 0,
                };
                if (id > 0) return id;
            }
            node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        }
        return 0;
    }

    private void OnItemListClicked(object sender, MouseButtonEventArgs e)
    {
        // Buttons in the row (Equip) do their own thing.
        for (var node = e.OriginalSource as DependencyObject; node is not null && !ReferenceEquals(node, sender);
             node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
        {
            if (node is System.Windows.Controls.Primitives.ButtonBase) return;
            if (node is not FrameworkElement { DataContext: { } context }) continue;
            var (id, name) = context switch
            {
                ItemRow row => (row.Id, row.Name),
                UpgradeRow up => (up.ItemId, up.Name),
                _ => (0, ""),
            };
            if (id == 0) continue;

            if (Keyboard.Modifiers == ModifierKeys.Shift) _vm.AddItemIdToGear(id, name);
            else if (Keyboard.Modifiers == ModifierKeys.Control) _vm.AddRowToCraft(new ItemRow(id, name, "", CompareColors.Neutral));
            else _vm.OpenItem(id);
            e.Handled = true;
            return;
        }
    }

    /// <summary>Loot search box: arrows / Tab walk the places, Ctrl+arrows the bosses; typing stays in the box.</summary>
    private void OnLootFilterKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        var delta = e.Key switch
        {
            Key.Down => 1,
            Key.Up => -1,
            Key.Tab => shift ? -1 : 1,
            _ => 0,
        };
        if (delta == 0) return;
        if (ctrl && e.Key != Key.Tab) _vm.MoveLootBoss(delta);
        else _vm.MoveLootPlace(delta);
        e.Handled = true;
    }

    // ----- Map page: zoom with the wheel (around the mouse), drag to move, coordinates under the mouse -----

    private double _mapZoom = 1;
    private Point? _mapDragFrom;
    private double _mapDragH, _mapDragV;

    /// <summary>Scale that fits the whole map in the view.</summary>
    private double MapFit()
    {
        var w = Math.Max(1, _vm.MapWidth);
        var h = Math.Max(1, _vm.MapHeight);
        var viewW = MapScroller.ActualWidth - 4;
        var viewH = MapScroller.ActualHeight - 4;
        return viewW <= 0 || viewH <= 0 ? 1 : Math.Min(viewW / w, viewH / h);
    }

    private void UpdateMapScale()
    {
        var scale = MapFit() * _mapZoom;
        MapScale.ScaleX = scale;
        MapScale.ScaleY = scale;
    }

    private void OnMapViewportChanged(object sender, SizeChangedEventArgs e) => UpdateMapScale();

    private void OnMapWheel(object sender, MouseWheelEventArgs e)
    {
        var before = MapScale.ScaleX;
        var mouse = e.GetPosition(MapScroller);
        var spot = e.GetPosition(MapSurface); // map pixels
        _mapZoom = Math.Clamp(_mapZoom * (e.Delta > 0 ? 1.25 : 0.8), 1, 6);
        UpdateMapScale();
        if (Math.Abs(MapScale.ScaleX - before) > 0.0001)
        {
            MapScroller.UpdateLayout();
            MapScroller.ScrollToHorizontalOffset(spot.X * MapScale.ScaleX - mouse.X);
            MapScroller.ScrollToVerticalOffset(spot.Y * MapScale.ScaleY - mouse.Y);
        }
        e.Handled = true;
    }

    private void FocusMap(MapPin pin)
    {
        _mapZoom = 2;
        UpdateMapScale();
        MapScroller.UpdateLayout();
        MapScroller.ScrollToHorizontalOffset(pin.Left * MapScale.ScaleX - MapScroller.ViewportWidth / 2);
        MapScroller.ScrollToVerticalOffset(pin.Top * MapScale.ScaleY - MapScroller.ViewportHeight / 2);
    }

    private void OnMapDragStart(object sender, MouseButtonEventArgs e)
    {
        _mapDragFrom = e.GetPosition(MapScroller);
        _mapDragH = MapScroller.HorizontalOffset;
        _mapDragV = MapScroller.VerticalOffset;
        MapSurface.CaptureMouse();
    }

    private void OnMapDragEnd(object sender, MouseButtonEventArgs e)
    {
        _mapDragFrom = null;
        MapSurface.ReleaseMouseCapture();
    }

    private void OnMapMouseMove(object sender, MouseEventArgs e)
    {
        if (_mapDragFrom is { } from && e.LeftButton == MouseButtonState.Pressed)
        {
            var now = e.GetPosition(MapScroller);
            MapScroller.ScrollToHorizontalOffset(_mapDragH - (now.X - from.X));
            MapScroller.ScrollToVerticalOffset(_mapDragV - (now.Y - from.Y));
        }
        var p = e.GetPosition(MapSurface);
        _vm.SetMapCursor(p.X / Math.Max(1, _vm.MapWidth) * 100, p.Y / Math.Max(1, _vm.MapHeight) * 100);
    }

    private void OnMapMouseLeave(object sender, MouseEventArgs e)
    {
        if (_mapDragFrom is null) _vm.SetMapCursor(-1, -1);
    }
}
