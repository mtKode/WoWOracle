using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WowCompanion.Core;

namespace WowCompanion.Desktop;

/// <summary>
/// Notes page: things to check, optionally with a reminder ("18:30", "tomorrow 9:00", "in 2h") that can repeat
/// daily or weekly. Reminders are checked every 20 seconds while the app runs (also from the tray) and show as a
/// toast plus a tray notification. Edits save when you switch note, leave the page, close the app, or press Save.
/// </summary>
public partial class MainViewModel
{
    private readonly NotesService _notes = new(paths.UserDbPath);
    private DispatcherTimer? _reminderTimer;
    private bool _loadingNote;
    private bool _noteDirty;

    /// <summary>Raised for each reminder that comes due: (title, body). The window shows it from the tray.</summary>
    public event Action<string, string>? ReminderDue;

    public ObservableCollection<NoteRow> Notes { get; } = [];
    public IReadOnlyList<NoteRepeat> RepeatChoices { get; } = Enum.GetValues<NoteRepeat>();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNote), nameof(NoNote))]
    private NoteRow? _selectedNote;

    [ObservableProperty] private string _noteTitle = "";
    [ObservableProperty] private string _noteBody = "";
    [ObservableProperty] private string _noteWhen = "";
    [ObservableProperty] private string _noteWhenHint = "";
    [ObservableProperty] private NoteRepeat _noteRepeat;
    [ObservableProperty] private bool _noteDone;

    public bool HasNote => SelectedNote is not null;
    public bool NoNote => SelectedNote is null;

    private void StartReminders()
    {
        LoadNotes(null);
        CheckReminders();
        _reminderTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _reminderTimer.Tick += (_, _) => CheckReminders();
        _reminderTimer.Start();
    }

    private void CheckReminders()
    {
        var now = DateTime.UtcNow;
        IReadOnlyList<Note> due;
        try
        {
            if (!_notes.List().Any(n => !n.Done && n.RemindUtc is { } at && at <= now)) return;
            SaveNoteIfDirty(); // first, so an open edit can't write back a reminder that's about to be moved on
            due = _notes.TakeDue(now);
        }
        catch (Exception) { return; }
        if (due.Count == 0) return;
        foreach (var note in due)
        {
            Toast($"Reminder: {note.Title}");
            ReminderDue?.Invoke(note.Title, note.Body);
        }
        LoadNotes(SelectedNote?.Id);
    }

    private void LoadNotes(int? select)
    {
        var nowLocal = DateTime.Now;
        _loadingNote = true;
        Notes.Clear();
        foreach (var n in _notes.List()) Notes.Add(RowFor(n, nowLocal));
        _loadingNote = false;
        SelectedNote = Notes.FirstOrDefault(n => n.Id == select);
    }

    private static NoteRow RowFor(Note n, DateTime nowLocal) =>
        new(n.Id, n.Title.Length > 0 ? n.Title : "(untitled)", Meta(n, nowLocal), n.Done,
            !n.Done && n.RemindUtc is { } at && at <= DateTime.UtcNow, n);

    private static string Meta(Note n, DateTime nowLocal)
    {
        var parts = new List<string>();
        if (n.Done) parts.Add("done");
        if (n.RemindUtc is { } at) parts.Add("⏰ " + WhenText(at.ToLocalTime(), nowLocal));
        if (n.Repeat != NoteRepeat.None) parts.Add(n.Repeat == NoteRepeat.Daily ? "daily" : "weekly");
        var firstLine = n.Body.Split('\n', 2)[0].Trim();
        if (firstLine.Length > 0) parts.Add(firstLine.Length > 60 ? firstLine[..60] + "…" : firstLine);
        return string.Join(" · ", parts);
    }

    private static string WhenText(DateTime local, DateTime nowLocal)
    {
        var inv = CultureInfo.InvariantCulture;
        var time = local.ToString("HH:mm", inv);
        if (local.Date == nowLocal.Date) return $"today {time}";
        if (local.Date == nowLocal.Date.AddDays(1)) return $"tomorrow {time}";
        if (local.Date == nowLocal.Date.AddDays(-1)) return $"yesterday {time}";
        if (Math.Abs((local.Date - nowLocal.Date).TotalDays) < 7) return local.ToString("ddd HH:mm", inv);
        return local.ToString("yyyy-MM-dd HH:mm", inv);
    }

    partial void OnSelectedNoteChanged(NoteRow? oldValue, NoteRow? newValue)
    {
        if (_loadingNote) return;
        if (oldValue is not null && _noteDirty && SaveFields(oldValue.Id, reload: false) is { } saved)
        {
            // Keep the list in step with what was just saved (the old row still holds the earlier copy).
            var index = Notes.ToList().FindIndex(r => r.Id == saved.Id);
            if (index >= 0)
            {
                _loadingNote = true;
                Notes[index] = RowFor(saved, DateTime.Now);
                _loadingNote = false;
            }
        }

        _loadingNote = true;
        var n = newValue?.Note;
        NoteTitle = n?.Title ?? "";
        NoteBody = n?.Body ?? "";
        NoteWhen = n?.RemindUtc is { } at ? at.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "";
        NoteRepeat = n?.Repeat ?? NoteRepeat.None;
        NoteDone = n?.Done ?? false;
        NoteWhenHint = "";
        _loadingNote = false;
        _noteDirty = false;
    }

    partial void OnNoteTitleChanged(string value) => MarkNoteDirty();
    partial void OnNoteBodyChanged(string value) => MarkNoteDirty();
    partial void OnNoteRepeatChanged(NoteRepeat value) => MarkNoteDirty();
    partial void OnNoteDoneChanged(bool value) => MarkNoteDirty();

    partial void OnNoteWhenChanged(string value)
    {
        MarkNoteDirty();
        if (_loadingNote) return;
        // Live feedback on what the typed time means.
        NoteWhenHint = !NotesService.TryParseWhen(value, DateTime.Now, out var when)
            ? "Can't read that time. Try 18:30, tomorrow 9:00, in 2h or 24/10 20:00."
            : when is { } w ? "Reminds " + WhenText(w, DateTime.Now) : "No reminder";
    }

    private void MarkNoteDirty()
    {
        if (!_loadingNote && SelectedNote is not null) _noteDirty = true;
    }

    /// <summary>Writes the editor fields to a note. Returns the saved note, or null when saving failed.</summary>
    private Note? SaveFields(int noteId, bool reload)
    {
        var original = Notes.FirstOrDefault(n => n.Id == noteId)?.Note;
        if (original is null) return null;
        if (!NotesService.TryParseWhen(NoteWhen, DateTime.Now, out var whenLocal))
        {
            Toast("Couldn't read the reminder time, so the reminder wasn't changed.");
            whenLocal = original.RemindUtc?.ToLocalTime();
        }
        var updated = original with
        {
            Title = NoteTitle.Trim(),
            Body = NoteBody,
            RemindUtc = whenLocal?.ToUniversalTime(),
            Repeat = NoteRepeat,
            Done = NoteDone,
        };
        try { _notes.Save(updated); }
        catch (Exception) { Toast("Couldn't save the note."); return null; }
        _noteDirty = false;
        if (reload) LoadNotes(noteId);
        return updated;
    }

    public void SaveNoteIfDirty()
    {
        if (_noteDirty && SelectedNote is { } note) SaveFields(note.Id, reload: true);
    }

    [RelayCommand]
    private void NewNote()
    {
        SaveNoteIfDirty();
        var id = _notes.Create("New note");
        LoadNotes(id);
        CurrentView = AppView.Notes;
        RequestNoteTitleFocus?.Invoke();
    }

    /// <summary>The window focuses and selects the title box when a note is created.</summary>
    public event Action? RequestNoteTitleFocus;

    [RelayCommand]
    private void SaveNote()
    {
        if (CurrentView != AppView.Notes || SelectedNote is not { } note) return; // Ctrl+S is global

        if (SaveFields(note.Id, reload: true) is not null) Toast("Note saved.");
    }

    [RelayCommand]
    private void DeleteNote()
    {
        if (SelectedNote is not { } note) return;
        _notes.Delete(note.Id);
        _noteDirty = false;
        var index = Notes.IndexOf(note);
        LoadNotes(null);
        SelectedNote = Notes.ElementAtOrDefault(Math.Min(index, Notes.Count - 1));
        Toast($"Deleted \"{note.Title}\".");
    }

    [RelayCommand]
    private void ToggleNoteDone(NoteRow? row)
    {
        if (row is null) return;
        if (SelectedNote?.Id == row.Id) { NoteDone = !NoteDone; SaveFields(row.Id, reload: true); return; }
        _notes.Save(row.Note with { Done = !row.Done });
        LoadNotes(SelectedNote?.Id);
    }

    /// <summary>Quick reminder buttons: "1h", "evening" (20:00), "tomorrow" (9:00), "clear".</summary>
    [RelayCommand]
    private void QuickReminder(string preset)
    {
        if (SelectedNote is null) return;
        var now = DateTime.Now;
        NoteWhen = preset switch
        {
            "1h" => now.AddHours(1).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            "evening" => (now.Hour >= 20 ? now.Date.AddDays(1) : now.Date).AddHours(20).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            "tomorrow" => now.Date.AddDays(1).AddHours(9).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            _ => "",
        };
        SaveFields(SelectedNote.Id, reload: true);
    }
}
