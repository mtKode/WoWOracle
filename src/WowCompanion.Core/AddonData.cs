using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace WowCompanion.Core;

/// <summary>Finds the WoWOracle addon's saved file(s) and the text blocks it stores in them.</summary>
public static partial class AddonSavedData
{
    /// <summary>
    /// Every WTF\Account\*\SavedVariables\WoWOracle.lua in the WoW folders WowInstall finds (all clients:
    /// _classic_beta_, the Forever client…).
    /// </summary>
    public static List<string> FindFiles(string appRoot)
    {
        var files = new List<string>();
        foreach (var root in WowInstall.Roots(appRoot))
        {
            try
            {
                foreach (var client in WowInstall.ForeverClients(root)) // never another game's saved files
                {
                    var accounts = Path.Combine(client, "WTF", "Account");
                    if (!Directory.Exists(accounts)) continue;
                    foreach (var account in Directory.EnumerateDirectories(accounts))
                    {
                        var file = Path.Combine(account, "SavedVariables", "WoWOracle.lua");
                        if (File.Exists(file)) files.Add(file);
                    }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return files;
    }

    /// <summary>All string values in a SavedVariables file that start with the given marker, unescaped.</summary>
    public static IEnumerable<string> Blocks(string luaText, string marker)
    {
        foreach (Match m in LuaString().Matches(luaText))
        {
            var raw = m.Groups[1].Value;
            if (!raw.StartsWith(marker, StringComparison.Ordinal)) continue;
            yield return Unescape(raw);
        }
    }

    /// <summary>Lua string escapes as WoW writes them: \n, \", \\, \r, \t, \ddd, and a backslash before a real newline.</summary>
    private static string Unescape(string s)
    {
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c != '\\' || i + 1 >= s.Length) { sb.Append(c); continue; }
            var n = s[++i];
            switch (n)
            {
                case 'n': sb.Append('\n'); break;
                case 'r': break;
                case 't': sb.Append('\t'); break;
                case '\n': sb.Append('\n'); break;
                case '\r': if (i + 1 < s.Length && s[i + 1] == '\n') i++; sb.Append('\n'); break;
                case >= '0' and <= '9':
                    var digits = n.ToString();
                    while (digits.Length < 3 && i + 1 < s.Length && char.IsDigit(s[i + 1])) digits += s[++i];
                    sb.Append((char)int.Parse(digits, CultureInfo.InvariantCulture));
                    break;
                default: sb.Append(n); break; // \" \\ \'
            }
        }
        return sb.ToString();
    }

    [GeneratedRegex("\"((?:[^\"\\\\]|\\\\[\\s\\S])*)\"")]
    private static partial Regex LuaString();
}

/// <summary>One talent node as the game client showed it.</summary>
public sealed record LayoutNode(int Id, int X, int Y, int MaxRanks, int SpellId, int IconId, IReadOnlyList<int> Unlocks, string Name, string Description);

/// <summary>A class's talent tree captured in game by the addon (/oracle talents, or automatically at login).</summary>
public sealed record TalentLayout(string ClassFile, int ClassId, int Build, IReadOnlyList<LayoutNode> Nodes)
{
    public const string Marker = "!WOWORACLE-TALENTS";

    public static TalentLayout? Parse(string text)
    {
        if (!text.StartsWith(Marker, StringComparison.Ordinal)) return null;
        string classFile = "";
        int classId = 0, build = 0;
        var nodes = new List<LayoutNode>();
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            var eq = raw.IndexOf('=');
            if (eq <= 0) continue;
            var key = raw[..eq];
            var value = raw[(eq + 1)..];
            var p = value.Split(';');
            int I(int i) => i < p.Length && int.TryParse(p[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
            switch (key)
            {
                case "class":
                    classFile = p[0];
                    classId = I(1);
                    break;
                case "build":
                    build = I(0);
                    break;
                case "node" when p.Length >= 8:
                    var unlocks = p[6].Split(',', StringSplitOptions.RemoveEmptyEntries)
                        .Select(x => int.TryParse(x, out var v) ? v : 0).Where(v => v > 0).ToList();
                    nodes.Add(new LayoutNode(I(0), I(1), I(2), Math.Max(1, I(3)), I(4), I(5), unlocks, p[7].Trim(),
                        p.Length > 8 ? string.Join(";", p[8..]).Trim() : ""));
                    break;
            }
        }
        return classId > 0 && build > 0 && nodes.Count > 0 ? new TalentLayout(classFile, classId, build, nodes) : null;
    }
}

/// <summary>
/// Puts a captured talent layout into the game database for its class, the same way the data build lays out
/// trait trees (600-unit grid, seven rows, one tab per spec column). Only used when the game client is a newer
/// build than the downloaded data; once wago.tools catches up, the rebuilt data wins again.
/// </summary>
public static class TalentOverlay
{
    private const int Grid = 600;

    /// <summary>The build number at the end of "1.60.1.70245".</summary>
    public static int BuildNumber(string version) =>
        int.TryParse(version.Split('.')[^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var b) ? b : 0;

    /// <summary>Build plus a hash of the nodes, stored in meta so an unchanged tree isn't rewritten.</summary>
    public static string Signature(TalentLayout layout)
    {
        var text = string.Join("\n", layout.Nodes.OrderBy(n => n.Id).Select(n =>
            $"{n.Id};{n.X};{n.Y};{n.MaxRanks};{n.SpellId};{n.IconId};{string.Join(",", n.Unlocks)};{n.Name};{n.Description}"));
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];
        return $"{layout.Build}:{hash}";
    }

    /// <returns>Talents written; 0 when the layout didn't fit the grid; -1 when this exact tree is already in place.</returns>
    public static int Apply(string gameDatabasePath, TalentLayout layout)
    {
        var nodes = layout.Nodes.Where(n => n.MaxRanks > 0 && n.Name.Length > 0).ToList();
        if (nodes.Count < 15) return 0;

        // Rows: the most used Y values set the grid; small offsets snap, far-off nodes are dropped.
        var top = nodes.GroupBy(n => n.Y).Where(g => g.Count() >= 2).Select(g => g.Key).DefaultIfEmpty(nodes.Min(n => n.Y)).Min();
        int? Row(int y)
        {
            var row = (int)Math.Round((y - top) / (double)Grid);
            return row is >= 0 and <= 6 && Math.Abs(y - (top + row * Grid)) <= 60 ? row : null;
        }
        var onGrid = nodes.Where(n => Row(n.Y) is not null).ToList();
        if (onGrid.Count == 0) return 0;

        // Spec columns: clusters of X separated by more than two slots.
        var xs = onGrid.Select(n => n.X).Distinct().Order().ToList();
        var sections = new List<List<int>> { new() { xs[0] } };
        for (var i = 1; i < xs.Count; i++)
        {
            if (xs[i] - xs[i - 1] > Grid * 2.5) sections.Add([]);
            sections[^1].Add(xs[i]);
        }
        sections = sections.Where(sec => onGrid.Count(n => sec.Contains(n.X)) >= 3).ToList();

        var prereq = new Dictionary<int, int>(); // node -> node that unlocks it
        foreach (var n in onGrid)
            foreach (var target in n.Unlocks)
                prereq.TryAdd(target, n.Id);
        var maxRanks = onGrid.ToDictionary(n => n.Id, n => n.MaxRanks);

        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = gameDatabasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        connection.Open();
        var signature = Signature(layout);
        using (var check = connection.CreateCommand())
        {
            check.CommandText = "SELECT value FROM meta WHERE key = $k";
            check.Parameters.AddWithValue("$k", $"talent_overlay_{layout.ClassId}");
            if (check.ExecuteScalar() as string == signature) return -1;
        }
        using var transaction = connection.BeginTransaction();

        SqliteCommand Command(string sql)
        {
            var c = connection.CreateCommand();
            c.Transaction = transaction;
            c.CommandText = sql;
            return c;
        }

        // Keep the class's tabs (names, art) in order; add more only if the tree grew a column.
        var tabs = new List<int>();
        using (var read = Command("SELECT id FROM talent_tabs WHERE class_id = $c ORDER BY order_index"))
        {
            read.Parameters.AddWithValue("$c", layout.ClassId);
            using var r = read.ExecuteReader();
            while (r.Read()) tabs.Add(r.GetInt32(0));
        }
        using (var delete = Command("DELETE FROM talents WHERE tab_id IN (SELECT id FROM talent_tabs WHERE class_id = $c)"))
        {
            delete.Parameters.AddWithValue("$c", layout.ClassId);
            delete.ExecuteNonQuery();
        }
        for (var s = tabs.Count; s < sections.Count; s++)
        {
            var id = layout.ClassId * 1000 + s;
            using var add = Command("INSERT OR REPLACE INTO talent_tabs (id, name, class_id, order_index, background, icon_file_id) VALUES ($id, $n, $c, $o, '', 0)");
            add.Parameters.AddWithValue("$id", id);
            add.Parameters.AddWithValue("$n", $"Tree {s + 1}");
            add.Parameters.AddWithValue("$c", layout.ClassId);
            add.Parameters.AddWithValue("$o", s);
            add.ExecuteNonQuery();
            tabs.Add(id);
        }

        using var insert = Command("""
            INSERT OR REPLACE INTO talents (id, tab_id, tier, col, max_rank, rank_spells, prereq_talent, prereq_rank, name, rank_descriptions)
            VALUES ($id, $tab, $tier, $col, $max, $ranks, $pre, $preRank, $n, $desc)
            """);
        // Spells the downloaded data doesn't know (new talents): name, text and icon, so the talent shows properly.
        using var spell = Command("""
            INSERT OR IGNORE INTO spells (id, name, rank, description, aura_description, description_raw, school_mask, cast_ms,
                duration_ms, range_max, range_name, cooldown_ms, category_cooldown_ms, gcd_ms, power_type, power_cost,
                power_cost_pct, power_per_second, base_level, spell_level, max_level, proc_chance, proc_charges, max_stacks,
                class_id, is_class_spell, skill_line, icon_file_id)
            VALUES ($id, $n, '', $d, '', $d, 0, 0, 0, 0, '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, $c, 0, '', $icon)
            """);

        var written = 0;
        for (var s = 0; s < sections.Count; s++)
        {
            var left = sections[s].Min();
            foreach (var n in onGrid.Where(x => sections[s].Contains(x.X)))
            {
                var col = (int)Math.Round((n.X - left) / (double)Grid);
                if (col is < 0 or > 3) continue;
                var pre = prereq.GetValueOrDefault(n.Id);

                insert.Parameters.Clear();
                insert.Parameters.AddWithValue("$id", n.Id);
                insert.Parameters.AddWithValue("$tab", tabs[s]);
                insert.Parameters.AddWithValue("$tier", Row(n.Y)!.Value);
                insert.Parameters.AddWithValue("$col", col);
                insert.Parameters.AddWithValue("$max", n.MaxRanks);
                insert.Parameters.AddWithValue("$ranks", string.Join(',', Enumerable.Repeat(n.SpellId, n.MaxRanks)));
                insert.Parameters.AddWithValue("$pre", maxRanks.ContainsKey(pre) ? pre : 0);
                insert.Parameters.AddWithValue("$preRank", maxRanks.TryGetValue(pre, out var preMax) ? preMax - 1 : 0);
                insert.Parameters.AddWithValue("$n", n.Name);
                insert.Parameters.AddWithValue("$desc", string.Join('\u001f', Enumerable.Repeat(n.Description, n.MaxRanks)));
                insert.ExecuteNonQuery();

                spell.Parameters.Clear();
                spell.Parameters.AddWithValue("$id", n.SpellId);
                spell.Parameters.AddWithValue("$n", n.Name);
                spell.Parameters.AddWithValue("$d", n.Description);
                spell.Parameters.AddWithValue("$c", layout.ClassId);
                spell.Parameters.AddWithValue("$icon", n.IconId);
                spell.ExecuteNonQuery();
                written++;
            }
        }

        using (var meta = Command("INSERT OR REPLACE INTO meta (key, value) VALUES ($k, $v)"))
        {
            meta.Parameters.AddWithValue("$k", $"talent_overlay_{layout.ClassId}");
            meta.Parameters.AddWithValue("$v", signature);
            meta.ExecuteNonQuery();
        }
        transaction.Commit();
        return written;
    }
}
