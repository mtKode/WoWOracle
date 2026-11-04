using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WowCompanion.Core;

/// <summary>One talent in the community calculators' layout (Icy Veins / Wowhead use the same order).</summary>
public sealed record ExternalTalent(int Index, int Tree, string Name, int MaxRank)
{
    public char UrlId => TalentCode.UrlChars[Index];
}

/// <summary>A class's talent layout as Icy Veins publishes it. Wowhead's Forever calculator lists talents in the same order.</summary>
public sealed record ExternalLayout(string Slug, IReadOnlyList<string> TreeNames, IReadOnlyList<ExternalTalent> Talents)
{
    /// <summary>
    /// Reads static.icy-veins.com/json/forever-talent-calculator/{class}.json. Each tree is a 4-wide grid with
    /// empty cells as null; url ids count only the real talents, tree after tree.
    /// </summary>
    public static ExternalLayout Parse(string slug, string json)
    {
        using var doc = JsonDocument.Parse(json);
        var trees = new List<string>();
        var talents = new List<ExternalTalent>();
        var tree = 0;
        foreach (var group in doc.RootElement.GetProperty("talentGroups").EnumerateArray())
        {
            trees.Add(group.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "");
            foreach (var t in group.GetProperty("talents").EnumerateArray())
            {
                if (t.ValueKind != JsonValueKind.Object) continue;
                var name = t.TryGetProperty("name", out var nm) ? nm.GetString() ?? "" : "";
                var max = t.TryGetProperty("maxRank", out var mr) && mr.TryGetInt32(out var m) ? m : 1;
                talents.Add(new ExternalTalent(talents.Count, tree, name, max));
            }
            tree++;
        }
        if (talents.Count > TalentCode.UrlChars.Length) throw new FormatException("Layout has more talents than url ids.");
        return new ExternalLayout(slug, trees, talents);
    }
}

public enum TalentLinkSite { IcyVeins, Wowhead }

/// <summary>A pasted link, split up but not yet matched to any layout.</summary>
public sealed record TalentLink(TalentLinkSite Site, string ClassSlug, string Payload);

/// <summary>Outcome of reading a talent link. <see cref="Ranks"/> is what could be applied safely.</summary>
public sealed record TalentCodeResult(
    bool Ok,
    string Error,
    IReadOnlyDictionary<int, int> Ranks,
    int Points,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Talent links in the community calculators' formats, matched to our talents by name so they survive layout changes:
/// <list type="bullet">
/// <item>Icy Veins: <c>icy-veins.com/wow-forever/mage-talent-calculator#tc-1112233…</c>: one character per point, in the
/// order points were spent; the character is the talent's position in the class layout (0-9, a-z, A-Z, then -._~[]()).</item>
/// <item>Wowhead: <c>wowhead.com/forever/talent-calc/mage/v2{tree}-{tree}-{tree}_…</c>: one digit per talent per tree.</item>
/// </list>
/// Both can be exported and imported; each opens in a browser too.
/// </summary>
public static partial class TalentCode
{
    public const string UrlChars = "0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ-._~[]()";

    public static string Slug(ClassInfo c) => new(c.Name.ToLowerInvariant().Where(char.IsLetter).ToArray());

    public static bool TryParse(string text, out TalentLink? link)
    {
        link = null;
        text = text.Trim();
        var iv = IcyVeinsPattern().Match(text);
        if (iv.Success)
        {
            link = new TalentLink(TalentLinkSite.IcyVeins, iv.Groups["cls"].Value.ToLowerInvariant(), iv.Groups["pts"].Value);
            return true;
        }
        var wh = WowheadPattern().Match(text);
        if (wh.Success)
        {
            link = new TalentLink(TalentLinkSite.Wowhead, wh.Groups["cls"].Value.ToLowerInvariant(), wh.Groups["trees"].Value);
            return true;
        }
        return false;
    }

    /// <summary>Our ranks as ranks per layout talent (by name). Talents the site doesn't have are reported.</summary>
    public static (Dictionary<int, int> Ranks, List<string> Warnings) ToExternal(
        IReadOnlyList<TalentTree> trees, IReadOnlyDictionary<int, int> ranks, ExternalLayout layout, string site)
    {
        var warnings = new List<string>();
        var byName = layout.Talents.GroupBy(t => Key(t.Name)).ToDictionary(g => g.Key, g => g.First());
        var external = new Dictionary<int, int>(); // layout index -> rank

        foreach (var talent in trees.SelectMany(t => t.Talents))
        {
            var rank = ranks.GetValueOrDefault(talent.Id);
            if (rank <= 0) continue;
            if (!byName.TryGetValue(Key(talent.Name), out var ext))
            {
                warnings.Add($"{talent.Name} isn't on the {site} calculator, so the link leaves it out.");
                continue;
            }
            if (rank > ext.MaxRank)
            {
                warnings.Add($"{talent.Name}: {site} allows {ext.MaxRank} ranks, the build has {rank}; the link uses {ext.MaxRank}.");
                rank = ext.MaxRank;
            }
            external[ext.Index] = rank;
        }
        return (external, warnings);
    }

    /// <summary>Icy Veins link. Points go tree by tree, top row first, so the spend order is always a legal one.</summary>
    public static string IcyVeinsLink(ExternalLayout layout, IReadOnlyDictionary<int, int> external)
    {
        var sb = new StringBuilder();
        foreach (var ext in layout.Talents.OrderBy(t => t.Tree).ThenBy(t => t.Index))
            if (external.TryGetValue(ext.Index, out var r)) sb.Append(ext.UrlId, r);
        return $"https://www.icy-veins.com/wow-forever/{layout.Slug}-talent-calculator#tc-{sb}";
    }

    /// <summary>Wowhead link: "v2", then one digit per talent for each tree, trailing zeros and empty trees left off.</summary>
    public static string WowheadLink(ExternalLayout layout, IReadOnlyDictionary<int, int> external)
    {
        var parts = Enumerable.Range(0, layout.TreeNames.Count).Select(tree =>
        {
            var sb = new StringBuilder();
            foreach (var ext in layout.Talents.Where(t => t.Tree == tree).OrderBy(t => t.Index))
                sb.Append((char)('0' + Math.Clamp(external.GetValueOrDefault(ext.Index), 0, 9)));
            return sb.ToString().TrimEnd('0');
        }).ToList();
        while (parts.Count > 0 && parts[^1].Length == 0) parts.RemoveAt(parts.Count - 1);
        return $"https://www.wowhead.com/forever/talent-calc/{layout.Slug}/v2{string.Join('-', parts)}";
    }

    /// <summary>Reads a link into our talents. Anything we can't place is reported, never guessed.</summary>
    public static TalentCodeResult Decode(TalentLink link, IReadOnlyList<TalentTree> trees, ExternalLayout layout, int maxPoints = 51)
    {
        var warnings = new List<string>();
        var none = new Dictionary<int, int>();
        TalentCodeResult Fail(string error) => new(false, error, none, 0, warnings);

        if (trees.Count == 0) return Fail("No talent data for this class in the current game build.");

        // 1. Link -> ranks per layout talent.
        var external = new Dictionary<int, int>();
        if (link.Site == TalentLinkSite.IcyVeins)
        {
            foreach (var c in link.Payload)
            {
                var index = UrlChars.IndexOf(c);
                if (index < 0 || index >= layout.Talents.Count)
                    return Fail($"The link has a point ('{c}') for a talent that doesn't exist on the calculator. Is it cut off or from another game?");
                external[index] = external.GetValueOrDefault(index) + 1;
            }
        }
        else
        {
            var parts = link.Payload.Length == 0 ? Array.Empty<string>() : link.Payload.Split('-');
            if (parts.Length > layout.TreeNames.Count)
                return Fail($"The link has {parts.Length} trees, the class has {layout.TreeNames.Count}.");
            for (var tree = 0; tree < parts.Length; tree++)
            {
                var inTree = layout.Talents.Where(t => t.Tree == tree).OrderBy(t => t.Index).ToList();
                var digits = parts[tree];
                if (digits.Length > inTree.Count)
                    return Fail($"The {layout.TreeNames[tree]} part has {digits.Length} talents, the tree has {inTree.Count}. The calculator has probably changed since the link was made.");
                for (var j = 0; j < digits.Length; j++)
                {
                    if (!char.IsDigit(digits[j])) return Fail($"Unexpected character '{digits[j]}' in the {layout.TreeNames[tree]} part.");
                    var rank = digits[j] - '0';
                    if (rank > 0) external[inTree[j].Index] = rank;
                }
            }
        }

        // 2. Layout talents -> ours, by name.
        var ours = trees.SelectMany(t => t.Talents).GroupBy(t => Key(t.Name)).ToDictionary(g => g.Key, g => g.First());
        var ranks = new Dictionary<int, int>();
        foreach (var (index, count) in external.OrderBy(e => e.Key))
        {
            var ext = layout.Talents[index];
            if (!ours.TryGetValue(Key(ext.Name), out var talent))
            {
                warnings.Add($"{ext.Name} ({count} point{(count == 1 ? "" : "s")}) isn't in this game build yet. It arrives when the data does.");
                continue;
            }
            var rank = count;
            if (rank > talent.MaxRank)
            {
                warnings.Add($"{talent.Name}: {rank} ranks in the link, max is {talent.MaxRank} here.");
                rank = talent.MaxRank;
            }
            ranks[talent.Id] = rank;
        }

        // 3. Our rules: rows unlocked and prerequisites met, or the talent goes.
        Repair(trees, ranks, warnings);

        var total = ranks.Values.Sum();
        if (total > maxPoints) warnings.Add($"Uses {total} points, more than the {maxPoints} you get at level 60.");
        return new TalentCodeResult(true, "", ranks, total, warnings);
    }

    /// <summary>
    /// Talents given by name (from the game addon) placed into our trees. Kept as the game had them.
    /// </summary>
    public static Dictionary<int, int> MapByName(
        IReadOnlyList<TalentTree> trees, IEnumerable<(string Name, int Rank)> picks, List<string> warnings)
    {
        var ours = trees.SelectMany(t => t.Talents).GroupBy(t => Key(t.Name)).ToDictionary(g => g.Key, g => g.First());
        var ranks = new Dictionary<int, int>();
        foreach (var (name, count) in picks)
        {
            if (!ours.TryGetValue(Key(name), out var talent))
            {
                warnings.Add($"{name} ({count}) isn't in this game build's talent data.");
                continue;
            }
            var rank = Math.Min(count, talent.MaxRank);
            if (count > talent.MaxRank) warnings.Add($"{talent.Name}: {count} ranks in game, max is {talent.MaxRank} here.");
            ranks[talent.Id] = Math.Max(ranks.GetValueOrDefault(talent.Id), rank);
        }
        // No rule repair here: the game itself allowed this build, and our row rule is only an approximation.
        return ranks;
    }

    /// <summary>
    /// Drops talents whose row isn't unlocked or whose prerequisite isn't met, highest rows first, until the build
    /// follows the rules. Moved or missing talents can break either rule.
    /// </summary>
    private static void Repair(IReadOnlyList<TalentTree> trees, Dictionary<int, int> ranks, List<string> warnings)
    {
        var byId = trees.SelectMany(t => t.Talents).ToDictionary(t => t.Id);
        for (var pass = 0; pass < 200; pass++)
        {
            TalentInfo? bad = null;
            foreach (var tree in trees)
            {
                foreach (var t in tree.Talents.Where(t => ranks.ContainsKey(t.Id)).OrderByDescending(t => t.Tier))
                {
                    var below = tree.Talents.Where(x => x.Tier < t.Tier).Sum(x => ranks.GetValueOrDefault(x.Id));
                    var preOk = t.PrereqTalent <= 0 || !byId.ContainsKey(t.PrereqTalent)
                                || ranks.GetValueOrDefault(t.PrereqTalent) >= t.PrereqRank + 1;
                    if (below < t.Tier * 5 || !preOk) { bad = t; break; }
                }
                if (bad is not null) break;
            }
            if (bad is null) return;
            ranks.Remove(bad.Id);
            warnings.Add($"Removed {bad.Name}: its row or prerequisite isn't unlocked in this game build.");
        }
    }

    /// <summary>Name match key: letters and digits only, lower case ("Arcane Mind" == "arcane-mind").</summary>
    private static string Key(string name) => new(name.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    // icy-veins.com/wow-forever/mage-talent-calculator#tc-xxxx (also a bare "mage-talent-calculator#tc-xxxx")
    [GeneratedRegex(@"(?<cls>[A-Za-z]+)-talent-calculator/?#tc-(?<pts>[0-9A-Za-z\-._~\[\]()]*)")]
    private static partial Regex IcyVeinsPattern();

    // wowhead.com/forever/talent-calc[/embed]/mage/v2<digits>-<digits>-<digits>[_t0_r95][/...]
    [GeneratedRegex(@"talent-calc/(?:embed/)?(?<cls>[A-Za-z]+)/v\d(?<trees>[0-9]*(?:-[0-9]*)*)")]
    private static partial Regex WowheadPattern();
}
