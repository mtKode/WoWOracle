using System.Text;
using System.Text.RegularExpressions;

namespace WowCompanion.Core;

/// <summary>
/// Tidies text from the game client for showing in the app. Tooltips carry the UI's escape codes, which the game
/// draws but plain text shows raw: |cFFFFFFFF…|r or |cnEPIC_PURPLE_COLOR:…|r (colour), |n (new line), |T…|t (an inline
/// icon), |H…|h…|h (a link).
/// Some descriptions are also wrapped by hand to fit the in-game tooltip ("Your successful\r\n  melee attacks…");
/// those breaks are joined again so the text wraps to the app's width. Blank lines (paragraphs) stay.
/// </summary>
public static partial class GameText
{
    // |cFFRRGGBB and named colours (|cnEPIC_PURPLE_COLOR:), the colour end |r, inline icons |T…|t.
    [GeneratedRegex(@"\|c[0-9a-fA-F]{8}|\|cn[A-Z0-9_]+:|\|r|\|T[^|]*\|t", RegexOptions.IgnoreCase)]
    private static partial Regex Codes();

    [GeneratedRegex(@"\|H[^|]*\|h(.*?)\|h", RegexOptions.IgnoreCase)]
    private static partial Regex Links();

    [GeneratedRegex(@"\n[ \t]*\n\s*")]
    private static partial Regex Paragraphs();

    [GeneratedRegex(@"[ \t]{2,}")]
    private static partial Regex Spaces();

    public static string Clean(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        if (text.IndexOf('|') >= 0)
        {
            text = Links().Replace(text, "$1");
            text = Codes().Replace(text, "");
            text = text.Replace("|n", "\n", StringComparison.OrdinalIgnoreCase);
        }
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        if (text.IndexOf('\n') < 0) return text.Trim();

        // A line break inside a paragraph is a hand wrap when the next line carries on the sentence (starts with a
        // small letter or a number); others ("Requires Cat Form" / "Increases…") are real lines and stay.
        var paragraphs = Paragraphs().Split(text);
        var output = new StringBuilder(text.Length);
        foreach (var paragraph in paragraphs)
        {
            var lines = paragraph.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            if (lines.Count == 0) continue;
            if (output.Length > 0) output.Append("\n\n");
            output.Append(lines[0]);
            for (var i = 1; i < lines.Count; i++)
            {
                var next = lines[i];
                var continues = char.IsLower(next[0]) || char.IsDigit(next[0]);
                output.Append(continues ? ' ' : '\n').Append(next);
            }
        }
        return Spaces().Replace(output.ToString(), " ").Trim();
    }
}
