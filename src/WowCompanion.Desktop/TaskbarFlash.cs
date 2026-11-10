using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace WowCompanion.Desktop;

/// <summary>Flashes the window's taskbar button (FlashWindowEx), the way Windows apps ask for attention.</summary>
public static class TaskbarFlash
{
    private const uint FlashAll = 0x3;            // FLASHW_ALL: caption and taskbar button
    private const uint FlashUntilForeground = 0xC; // FLASHW_TIMERNOFG: keep going until the window comes to the front

    /// <summary>Flashes until the window is brought to the front (it stays lit on the taskbar till then).</summary>
    public static void UntilForeground(Window window) => Flash(window, FlashAll | FlashUntilForeground, 0);

    /// <summary>Flashes a few times, also while the window is already in front.</summary>
    public static void Times(Window window, uint count) => Flash(window, FlashAll, count);

    private static void Flash(Window window, uint flags, uint count)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == 0) return;
        var info = new FlashInfo
        {
            Size = (uint)Marshal.SizeOf<FlashInfo>(),
            Window = handle,
            Flags = flags,
            Count = count,
            Timeout = 0, // the cursor blink rate
        };
        FlashWindowEx(ref info);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashInfo
    {
        public uint Size;
        public nint Window;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }

    [DllImport("user32.dll")]
    private static extern bool FlashWindowEx(ref FlashInfo info);
}
