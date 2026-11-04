using System.Globalization;
using System.Text;
using WowCompanion.Core;

namespace WowCompanion.Data;

/// <summary>
/// Fills in the $tokens in spell descriptions ("causes $s1 Fire damage over $d") the way the game tooltip does,
/// using values at the spell's own level. Unknown tokens are dropped rather than shown raw.
/// Covered: $s $m $M $o $t $d $a $h $n $u $x $e $b $r, spell-id prefixes ($12345s1), ${expressions},
/// $/10;s1 and $*2;s1, $l and $g word choices, $?conditions[then][else] (else is used), $@spelldesc and $@spellname.
/// </summary>
public sealed class SpellText(Func<int, SpellFacts?> lookup, Func<int, double>? radius = null)
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>The tooltip text, with the client's colour codes and hard line wraps tidied (GameText.Clean).</summary>
    public string Render(string text, SpellFacts spell) => GameText.Clean(Render(text, spell, 0));

    private string Render(string text, SpellFacts spell, int depth)
    {
        if (string.IsNullOrEmpty(text) || depth > 3) return text ?? "";
        var output = new StringBuilder(text.Length + 16);
        double? lastNumber = null;
        var i = 0;

        while (i < text.Length)
        {
            var c = text[i];
            if (c != '$' || i + 1 >= text.Length)
            {
                output.Append(c);
                i++;
                continue;
            }

            i++; // past '$'
            var next = text[i];

            if (next == '{')
            {
                var end = MatchClose(text, i, '{', '}');
                var inner = text.Substring(i + 1, Math.Max(0, end - i - 1));
                i = end + 1;
                // ${...}.1 asks for one decimal; ignore the precision, format normally.
                if (i + 1 < text.Length && text[i] == '.' && char.IsDigit(text[i + 1])) i += 2;
                var expression = ReplaceForMath(inner, spell, depth);
                var value = Expression.TryEvaluate(expression);
                if (value is { } v)
                {
                    output.Append(Format(Math.Abs(v)));
                    lastNumber = Math.Abs(v);
                }
                continue;
            }

            if (next == '?')
            {
                i = SkipConditional(text, i + 1, out var chosen);
                output.Append(Render(chosen, spell, depth + 1));
                continue;
            }

            if (next == '/' || next == '*')
            {
                // $/10;s1  or  $*2;12345s1
                var semicolon = text.IndexOf(';', i);
                if (semicolon < 0) { i++; continue; }
                var factorText = text.Substring(i + 1, semicolon - i - 1);
                i = semicolon + 1;
                var token = ReadToken(text, ref i);
                if (token is null) continue;
                var raw = Number(token.Value, spell);
                if (raw is { } r && double.TryParse(factorText, NumberStyles.Float, Inv, out var factor) && factor != 0)
                {
                    var value = next == '/' ? r / factor : r * factor;
                    output.Append(Format(Math.Abs(value)));
                    lastNumber = Math.Abs(value);
                }
                continue;
            }

            if (next is 'l' or 'L' or 'g' or 'G')
            {
                // $lsingular:plural;  $gmale:female;
                var semicolon = text.IndexOf(';', i);
                if (semicolon < 0) { i++; continue; }
                var options = text.Substring(i + 1, semicolon - i - 1).Split(':');
                i = semicolon + 1;
                var pick = next is 'l' or 'L'
                    ? (lastNumber == 1 ? options[0] : options[^1])
                    : options[0];
                output.Append(pick);
                continue;
            }

            if (next == '@')
            {
                // $@spelldesc12345, $@spellname12345, $@spellaura12345
                var start = i + 1;
                var j = start;
                while (j < text.Length && char.IsLetter(text[j])) j++;
                var word = text[start..j].ToLowerInvariant();
                var idStart = j;
                while (j < text.Length && char.IsDigit(text[j])) j++;
                i = j;
                var other = int.TryParse(text[idStart..j], out var otherId) ? lookup(otherId) : null;
                if (other is null) continue;
                output.Append(word switch
                {
                    "spelldesc" => Render(other.Description, other, depth + 1),
                    "spellname" => other.Name,
                    "spellaura" => Render(other.AuraDescription, other, depth + 1),
                    _ => "",
                });
                continue;
            }

            var at = i;
            var parsed = ReadToken(text, ref i);
            if (parsed is null)
            {
                output.Append('$');
                i = at;
                continue;
            }

            var shown = Display(parsed.Value, spell, out var number);
            output.Append(shown);
            if (number is not null) lastNumber = number;
        }

        return Tidy(output.ToString());
    }

    // ---------------- tokens ----------------

    private readonly record struct Token(int SpellId, char Letter, int Index);

    /// <summary>Reads [spell id] letter [index] starting at i (just after '$').</summary>
    private static Token? ReadToken(string text, ref int i)
    {
        var j = i;
        var idStart = j;
        while (j < text.Length && char.IsDigit(text[j])) j++;
        var spellId = j > idStart ? int.Parse(text[idStart..j], Inv) : 0;
        if (j >= text.Length || !char.IsLetter(text[j])) return null;
        var letter = text[j++];
        var index = 1;
        if (j < text.Length && char.IsDigit(text[j]))
        {
            index = text[j] - '0';
            j++;
        }
        i = j;
        return new Token(spellId, letter, index);
    }

    private string Display(Token token, SpellFacts current, out double? number)
    {
        number = null;
        var spell = token.SpellId > 0 ? lookup(token.SpellId) : current;
        if (spell is null) return "";

        switch (token.Letter)
        {
            case 's':
            case 'S':
            {
                var effect = spell.Effect(token.Index - 1);
                if (effect is null) return "";
                var (min, max) = Range(effect, spell);
                number = max;
                return min == max ? Format(min) : $"{Format(min)} to {Format(max)}";
            }
            case 'd':
            case 'D':
                return spell.DurationMs > 0 ? Duration(spell.DurationMs) : "";
            default:
            {
                var value = Number(token, current);
                if (value is null) return "";
                number = value;
                return Format(value.Value);
            }
        }
    }

    /// <summary>The plain number behind a token, for maths and for most letters.</summary>
    private double? Number(Token token, SpellFacts current)
    {
        var spell = token.SpellId > 0 ? lookup(token.SpellId) : current;
        if (spell is null) return null;
        var effect = spell.Effect(token.Index - 1);

        switch (token.Letter)
        {
            case 's': case 'S': case 'm':
                return effect is null ? null : Math.Abs(Value(effect, spell));
            case 'M':
                return effect is null ? null : Range(effect, spell).Max;
            case 'o': case 'O':
                if (effect is null) return null;
                var ticks = effect.PeriodMs > 0 && spell.DurationMs > 0 ? spell.DurationMs / effect.PeriodMs : 1;
                return Math.Abs(Value(effect, spell)) * Math.Max(1, ticks);
            case 't': case 'T':
                return effect is null || effect.PeriodMs <= 0 ? null : effect.PeriodMs / 1000.0;
            case 'd': case 'D':
                return spell.DurationMs > 0 ? spell.DurationMs / 1000.0 : null;
            case 'a': case 'A':
            {
                if (effect is null || radius is null) return null;
                var yards = radius(effect.RadiusIndex);
                return yards > 0 ? yards : null;
            }
            case 'h': case 'H':
                return spell.ProcChance > 0 ? spell.ProcChance : null;
            case 'n': case 'N':
                return spell.ProcCharges > 0 ? spell.ProcCharges : null;
            case 'u': case 'U':
                return spell.MaxStacks > 0 ? spell.MaxStacks : null;
            case 'x': case 'X':
                return effect is { ChainTargets: > 0 } ? effect.ChainTargets : null;
            case 'e': case 'E':
                return effect is null ? null : effect.Amplitude;
            case 'b': case 'B':
                return effect is null ? null : Math.Abs(effect.PointsPerCombo);
            case 'r': case 'R':
                return spell.RangeMax > 0 ? spell.RangeMax : null;
            case 'q': case 'Q':
                return effect?.Misc0;
            default:
                return null;
        }
    }

    public static double Value(EffectFacts effect, SpellFacts spell) => effect.BasePoints;

    public static (double Min, double Max) Range(EffectFacts effect, SpellFacts spell)
    {
        var value = Math.Abs(Value(effect, spell));
        if (effect.Variance <= 0) return (Math.Round(value), Math.Round(value));
        var min = Math.Round(value * (1 - effect.Variance / 2), MidpointRounding.AwayFromZero);
        var max = Math.Round(value * (1 + effect.Variance / 2), MidpointRounding.AwayFromZero);
        return (min, max);
    }

    // ---------------- helpers ----------------

    /// <summary>Turns "$m1*2" inside ${...} into plain numbers so it can be evaluated.</summary>
    private string ReplaceForMath(string inner, SpellFacts spell, int depth)
    {
        var output = new StringBuilder();
        var i = 0;
        while (i < inner.Length)
        {
            if (inner[i] != '$') { output.Append(inner[i++]); continue; }
            i++;
            if (i < inner.Length && (inner[i] == '<' || inner[i] == '{'))
            {
                // Variables we don't track ($<var>): treat as zero so the rest still evaluates.
                var close = inner.IndexOf(inner[i] == '<' ? '>' : '}', i);
                i = close < 0 ? inner.Length : close + 1;
                output.Append('0');
                continue;
            }
            var token = ReadToken(inner, ref i);
            var value = token is null ? null : Number(token.Value, spell);
            output.Append((value ?? 0).ToString(Inv));
        }
        return output.ToString();
    }

    private static int MatchClose(string text, int open, char a, char b)
    {
        var level = 0;
        for (var i = open; i < text.Length; i++)
        {
            if (text[i] == a) level++;
            else if (text[i] == b && --level == 0) return i;
        }
        return text.Length - 1;
    }

    /// <summary>$?cond[a]?cond2[b][c] — returns the index after the whole thing and the last (default) branch.</summary>
    private static int SkipConditional(string text, int i, out string chosen)
    {
        chosen = "";
        while (i < text.Length)
        {
            var open = text.IndexOf('[', i);
            if (open < 0) return text.Length;
            var close = MatchClose(text, open, '[', ']');
            chosen = text.Substring(open + 1, Math.Max(0, close - open - 1));
            i = close + 1;
            if (i < text.Length && text[i] == '?') { i++; continue; }
            if (i < text.Length && text[i] == '[') continue;
            return i;
        }
        return i;
    }

    public static string Format(double value)
    {
        var rounded = Math.Round(value);
        if (Math.Abs(value - rounded) < 0.05) return rounded.ToString("0", Inv);
        return value.ToString("0.#", Inv);
    }

    public static string Duration(int ms)
    {
        if (ms < 0) return "until cancelled";
        var seconds = ms / 1000.0;
        if (seconds >= 3600 && seconds % 3600 == 0) return $"{seconds / 3600:0} hr";
        if (seconds >= 60 && seconds % 60 == 0) return $"{seconds / 60:0} min";
        return $"{Format(seconds)} sec";
    }

    private static string Tidy(string text) => text.Replace("  ", " ").Trim();

    /// <summary>Tiny + - * / ( ) evaluator for ${...}. Returns null on anything it doesn't understand.</summary>
    private static class Expression
    {
        public static double? TryEvaluate(string text)
        {
            var i = 0;
            try
            {
                var value = Sum(text, ref i);
                Skip(text, ref i);
                return i == text.Length ? value : value;
            }
            catch (FormatException) { return null; }
        }

        private static double Sum(string s, ref int i)
        {
            var value = Product(s, ref i);
            while (true)
            {
                Skip(s, ref i);
                if (i < s.Length && s[i] == '+') { i++; value += Product(s, ref i); }
                else if (i < s.Length && s[i] == '-') { i++; value -= Product(s, ref i); }
                else return value;
            }
        }

        private static double Product(string s, ref int i)
        {
            var value = Unary(s, ref i);
            while (true)
            {
                Skip(s, ref i);
                if (i < s.Length && s[i] == '*') { i++; value *= Unary(s, ref i); }
                else if (i < s.Length && s[i] == '/')
                {
                    i++;
                    var divisor = Unary(s, ref i);
                    value = divisor == 0 ? 0 : value / divisor;
                }
                else return value;
            }
        }

        private static double Unary(string s, ref int i)
        {
            Skip(s, ref i);
            if (i < s.Length && s[i] == '-') { i++; return -Unary(s, ref i); }
            if (i < s.Length && s[i] == '(')
            {
                i++;
                var value = Sum(s, ref i);
                Skip(s, ref i);
                if (i < s.Length && s[i] == ')') i++;
                return value;
            }
            var start = i;
            while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.' || s[i] == 'E' || s[i] == 'e')) i++;
            if (start == i) throw new FormatException();
            return double.Parse(s[start..i], NumberStyles.Float, Inv);
        }

        private static void Skip(string s, ref int i)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        }
    }
}
