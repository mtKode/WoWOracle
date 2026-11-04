using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace WowCompanion.Desktop;

/// <summary>One line in the log: time, text, and kind ("data", "addon", "warn" or "note") for its colour.</summary>
public sealed record LogEntry(string Time, string Text, string Kind);

/// <summary>
/// "Oracle's Chronicle": a small log at the bottom left of what changed this session (game data updates, what the
/// addon brought in) plus every toast, so nothing is missed when a toast fades too fast. Opens by itself after
/// start-up when there's news; the scroll button in the status bar opens it again.
/// </summary>
public partial class MainViewModel
{
    private const int MaxLogEntries = 200;
    private bool _startupNews;

    public ObservableCollection<LogEntry> LogEntries { get; } = [];

    [ObservableProperty] private bool _isLogOpen;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasLogNews))] private int _logNewCount;
    [ObservableProperty] private bool _logIsEmpty = true;

    public bool HasLogNews => LogNewCount > 0;

    /// <param name="kind">"data" (game data), "addon" (from the game client), "warn" or "note" (toasts).</param>
    private void Log(string text, string kind = "note")
    {
        LogEntries.Insert(0, new LogEntry(DateTime.Now.ToString("HH:mm"), text, kind));
        while (LogEntries.Count > MaxLogEntries) LogEntries.RemoveAt(LogEntries.Count - 1);
        LogIsEmpty = false;
        if (kind != "note" && !IsLogOpen)
        {
            LogNewCount++;
            _startupNews = true;
        }
    }

    partial void OnIsLogOpenChanged(bool value)
    {
        if (value) LogNewCount = 0;
    }

    [RelayCommand]
    private void ToggleLog() => IsLogOpen = !IsLogOpen;

    /// <summary>After start-up: show the log when the data or the addon brought something new.</summary>
    private void OpenLogIfNews()
    {
        if (_startupNews) IsLogOpen = true;
        _startupNews = false;
    }
}
