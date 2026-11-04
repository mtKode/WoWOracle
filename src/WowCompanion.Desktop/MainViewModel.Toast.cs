using CommunityToolkit.Mvvm.ComponentModel;

namespace WowCompanion.Desktop;

/// <summary>
/// Short confirmations ("Added X to Set 1") as a toast that drops in above the search bar and fades out.
/// Progress messages about game data stay in the status line at the bottom left.
/// </summary>
public partial class MainViewModel
{
    private CancellationTokenSource? _toastCts;

    [ObservableProperty] private string _toastText = "";
    [ObservableProperty] private bool _toastVisible;
    [ObservableProperty] private bool _toastIsWarning;

    private static readonly string[] WarningMarks =
        ["Couldn't", "can't", "Nothing crafts", "depend on", "no longer exists", "keeps at least"];

    private bool _toastSkipLog;

    /// <summary>A toast for something already written to the log in more detail.</summary>
    private void ToastOnly(string text)
    {
        _toastSkipLog = true;
        try { Toast(text); }
        finally { _toastSkipLog = false; }
    }

    public async void Toast(string text)
    {
        _toastCts?.Cancel();
        var cts = _toastCts = new CancellationTokenSource();

        // Off then on, so the drop-in animation replays even when a toast is already showing.
        ToastVisible = false;
        ToastText = text;
        ToastIsWarning = WarningMarks.Any(mark => text.Contains(mark, StringComparison.OrdinalIgnoreCase));
        if (!_toastSkipLog) Log(text, ToastIsWarning ? "warn" : "note");
        ToastVisible = true;

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2.2), cts.Token);
            ToastVisible = false;
        }
        catch (TaskCanceledException)
        {
            // A newer toast took over.
        }
    }
}
