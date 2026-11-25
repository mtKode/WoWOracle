using System.Globalization;
using System.IO;
using System.Windows;
using WowCompanion.Core;

namespace WowCompanion.Desktop;

/// <summary>
/// Where the window was and how big, kept in %LOCALAPPDATA%\WowCompanion\window.txt ("left;top;width;height;maximized")
/// and put back at the next start. A spot no longer on any screen (a monitor unplugged) is ignored.
/// </summary>
public static class WindowPlacement
{
    private static string FilePath => Path.Combine(AppPaths.Default().Root, "window.txt");

    public static void Restore(Window window)
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            var parts = File.ReadAllText(FilePath).Trim().Split(';');
            if (parts.Length < 5) return;
            var n = parts.Take(4).Select(p => double.Parse(p, CultureInfo.InvariantCulture)).ToArray();
            var (left, top, width, height) = (n[0], n[1], Math.Max(n[2], window.MinWidth), Math.Max(n[3], window.MinHeight));

            // The top edge (where you grab it) must be on the desktop, with a bit of room to drag.
            var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            if (!screen.IntersectsWith(new Rect(left + 40, top, Math.Max(1, width - 80), 40))) return;

            window.WindowStartupLocation = WindowStartupLocation.Manual;
            (window.Left, window.Top, window.Width, window.Height) = (left, top, width, height);
            if (parts[4] == "1") window.Loaded += (_, _) => window.WindowState = WindowState.Maximized;
        }
        catch (Exception)
        {
            // Unreadable file: the default spot.
        }
    }

    public static void Save(Window window)
    {
        try
        {
            // Maximized or minimized: the normal-size spot it returns to.
            var bounds = window.WindowState == WindowState.Normal
                ? new Rect(window.Left, window.Top, window.ActualWidth, window.ActualHeight)
                : window.RestoreBounds;
            if (bounds.IsEmpty || double.IsNaN(bounds.Left) || bounds.Width <= 0) return;
            var inv = CultureInfo.InvariantCulture;
            File.WriteAllText(FilePath, string.Join(';',
                bounds.Left.ToString("0", inv), bounds.Top.ToString("0", inv),
                bounds.Width.ToString("0", inv), bounds.Height.ToString("0", inv),
                window.WindowState == WindowState.Maximized ? "1" : "0"));
        }
        catch (Exception)
        {
            // Not worth bothering anyone about.
        }
    }
}
