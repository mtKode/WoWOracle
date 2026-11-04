using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace WowCompanion.Desktop;

/// <summary>System-wide hotkey for one window. Windows only.</summary>
public sealed class GlobalHotkey : IDisposable
{
    public const uint Alt = 0x0001;
    public const uint Ctrl = 0x0002;
    public const uint Shift = 0x0004;
    public const uint VkSpace = 0x20;
    /// <summary>The -/_ key next to 0 (VK_OEM_MINUS); with Shift it types "_" on US and Nordic layouts.</summary>
    public const uint VkMinus = 0xBD;

    private const uint NoRepeat = 0x4000;
    private const int WmHotkey = 0x0312;
    private const int HotkeyId = 0x5743;

    private readonly nint _handle;
    private readonly HwndSource _source;

    public GlobalHotkey(Window window, uint modifiers, uint virtualKey)
    {
        _handle = new WindowInteropHelper(window).Handle;
        _source = HwndSource.FromHwnd(_handle);
        _source.AddHook(WndProc);
        IsRegistered = RegisterHotKey(_handle, HotkeyId, modifiers | NoRepeat, virtualKey);
    }

    public bool IsRegistered { get; }

    public event Action? Pressed;

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam == HotkeyId)
        {
            Pressed?.Invoke();
            handled = true;
        }
        return nint.Zero;
    }

    public void Dispose()
    {
        if (IsRegistered) UnregisterHotKey(_handle, HotkeyId);
        _source.RemoveHook(WndProc);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(nint hWnd, int id);
}
