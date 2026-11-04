using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace WowCompanion.Core;

public enum NoteRepeat { None, Daily, Weekly }

/// <summary>A note, optionally with a reminder. Times are stored in UTC.</summary>
public sealed record Note(int Id, string Title, string Body, DateTime? RemindUtc, NoteRepeat Repeat, bool Done, DateTime UpdatedUtc);

/// <summary>Notes and reminders in user.sqlite.</summary>
public sealed partial class NotesService(string userDatabasePath)
{
    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = userDatabasePath,
        Pooling = false,
    }.ToString();

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public IReadOnlyList<Note> List()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, title, body, remind_utc, repeat, done, updated_utc FROM notes";
        var result = new List<Note>();
        using var r = command.ExecuteReader();
        while (r.Read())
        {
            result.Add(new Note(
                r.GetInt32(0), r.GetString(1), r.GetString(2),
                r.IsDBNull(3) ? null : ParseUtc(r.GetString(3)),
                Enum.TryParse<NoteRepeat>(r.GetString(4), true, out var rep) ? rep : NoteRepeat.None,
                r.GetInt32(5) != 0,
                ParseUtc(r.GetString(6)) ?? DateTime.UtcNow));
        }
        // Open notes first; among them the next reminder first, then the most recently edited.
        return result
            .OrderBy(n => n.Done)
            .ThenBy(n => n.RemindUtc is null)
            .ThenBy(n => n.RemindUtc)
            .ThenByDescending(n => n.UpdatedUtc)
            .ToList();
    }

    public int Create(string title)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        var now = Stamp(DateTime.UtcNow);
        command.CommandText = """
            INSERT INTO notes (title, body, created_utc, updated_utc) VALUES ($t, '', $now, $now);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$t", title);
        command.Parameters.AddWithValue("$now", now);
        return Convert.ToInt32(command.ExecuteScalar());
    }

    public void Save(Note note)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE notes SET title = $t, body = $b, remind_utc = $r, repeat = $rep, done = $d, updated_utc = $now
            WHERE id = $id
            """;
        command.Parameters.AddWithValue("$t", note.Title);
        command.Parameters.AddWithValue("$b", note.Body);
        command.Parameters.AddWithValue("$r", note.RemindUtc is { } at ? Stamp(at) : DBNull.Value);
        command.Parameters.AddWithValue("$rep", note.Repeat.ToString().ToLowerInvariant());
        command.Parameters.AddWithValue("$d", note.Done ? 1 : 0);
        command.Parameters.AddWithValue("$now", Stamp(DateTime.UtcNow));
        command.Parameters.AddWithValue("$id", note.Id);
        command.ExecuteNonQuery();
    }

    public void Delete(int id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM notes WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Reminders that are due. Each is moved on (repeating: to the next day/week after now) or cleared, so it fires once.
    /// </summary>
    public IReadOnlyList<Note> TakeDue(DateTime nowUtc)
    {
        var due = List().Where(n => !n.Done && n.RemindUtc is { } at && at <= nowUtc).ToList();
        foreach (var note in due)
        {
            DateTime? next = null;
            if (note.Repeat != NoteRepeat.None && note.RemindUtc is { } at)
            {
                var step = note.Repeat == NoteRepeat.Daily ? TimeSpan.FromDays(1) : TimeSpan.FromDays(7);
                next = at;
                while (next <= nowUtc) next += step;
            }
            Save(note with { RemindUtc = next });
        }
        return due;
    }

    // ----- reminder text -----

    /// <summary>
    /// Reads a reminder time typed by hand, in local time: "18:30" (today, or tomorrow if passed), "tomorrow 9:00",
    /// "2026-10-24 20:00", "24/10 20:00", "in 2h", "in 45m", "in 3d". Empty clears the reminder.
    /// </summary>
    public static bool TryParseWhen(string text, DateTime nowLocal, out DateTime? whenLocal)
    {
        whenLocal = null;
        text = text.Trim().ToLowerInvariant();
        if (text.Length == 0) return true;

        var rel = RelativePattern().Match(text);
        if (rel.Success)
        {
            var amount = double.Parse(rel.Groups["n"].Value, Inv);
            whenLocal = rel.Groups["u"].Value[0] switch
            {
                'm' => nowLocal.AddMinutes(amount),
                'h' => nowLocal.AddHours(amount),
                _ => nowLocal.AddDays(amount),
            };
            return true;
        }

        var day = nowLocal.Date;
        var explicitDay = false;
        if (text.StartsWith("tomorrow", StringComparison.Ordinal)) { day = day.AddDays(1); text = text[8..].Trim(); explicitDay = true; }
        else if (text.StartsWith("today", StringComparison.Ordinal)) { text = text[5..].Trim(); explicitDay = true; }

        string[] full = ["yyyy-MM-dd HH:mm", "yyyy-MM-dd H:mm", "yyyy-MM-dd", "d/M HH:mm", "d/M H:mm", "d/M", "d.M HH:mm", "d.M H:mm", "d.M"];
        if (!explicitDay && DateTime.TryParseExact(text, full, Inv, DateTimeStyles.AllowWhiteSpaces, out var dated))
        {
            if (!text.Contains(':')) dated = dated.Date.AddHours(9);
            // "d/M" without a year: the next time that date comes around.
            if (!text.Contains('-') && dated < nowLocal) dated = dated.AddYears(1);
            whenLocal = dated;
            return true;
        }

        string[] times = ["HH:mm", "H:mm", "HH", "H", "HHmm"];
        if (text.Length == 0) text = "09:00";
        if (DateTime.TryParseExact(text, times, Inv, DateTimeStyles.None, out var time))
        {
            var at = day.Add(time.TimeOfDay);
            if (!explicitDay && at <= nowLocal) at = at.AddDays(1);
            whenLocal = at;
            return true;
        }
        return false;
    }

    private static DateTime? ParseUtc(string text) =>
        DateTime.TryParse(text, Inv, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var v) ? v : null;

    private static string Stamp(DateTime utc) => utc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", Inv);

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    [GeneratedRegex(@"^in\s*(?<n>\d+(?:\.\d+)?)\s*(?<u>m|min|mins|minutes?|h|hours?|d|days?)$")]
    private static partial Regex RelativePattern();
}
