using Microsoft.Data.Sqlite;

namespace WowCompanion.Data;

public static partial class GameDatabaseBuilder
{
    private const string TalentSchema = """

        CREATE TABLE classes (
            id INTEGER PRIMARY KEY,
            name TEXT NOT NULL,
            filename TEXT NOT NULL,
            spell_class_set INTEGER NOT NULL,
            display_power INTEGER NOT NULL,
            icon_file_id INTEGER NOT NULL DEFAULT 0
        );
        CREATE TABLE races (
            id INTEGER PRIMARY KEY,
            name TEXT NOT NULL,
            playable INTEGER NOT NULL,
            alliance INTEGER NOT NULL
        );
        CREATE TABLE talent_tabs (
            id INTEGER PRIMARY KEY,
            name TEXT NOT NULL,
            class_id INTEGER NOT NULL,
            order_index INTEGER NOT NULL,
            background TEXT NOT NULL,
            icon_file_id INTEGER NOT NULL
        );
        CREATE TABLE talents (
            id INTEGER PRIMARY KEY,
            tab_id INTEGER NOT NULL,
            tier INTEGER NOT NULL,
            col INTEGER NOT NULL,
            max_rank INTEGER NOT NULL,
            rank_spells TEXT NOT NULL,
            prereq_talent INTEGER NOT NULL,
            prereq_rank INTEGER NOT NULL,
            name TEXT NOT NULL,
            rank_descriptions TEXT NOT NULL DEFAULT ''
        );
        CREATE INDEX talents_tab ON talents (tab_id);
        -- Trait talents whose values come per rank from a curve instead of base points × rank.
        CREATE TABLE talent_points (
            spell_id INTEGER NOT NULL,
            effect_index INTEGER NOT NULL,
            rank INTEGER NOT NULL,
            value REAL NOT NULL,
            PRIMARY KEY (spell_id, effect_index, rank)
        );
        """;

    // Playable in Classic: Human .. Troll (PlayableRaceBit 0..7).
    private const int LastClassicRaceBit = 7;

    private static void InsertClassesAndTalents(SqliteConnection connection, SqliteTransaction transaction, Func<string, string> csv)
    {
        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO classes (id, name, filename, spell_class_set, display_power, icon_file_id) VALUES ($id, $n, $f, $s, $p, $i)";
            var id = insert.Parameters.Add("$id", SqliteType.Integer);
            var name = insert.Parameters.Add("$n", SqliteType.Text);
            var file = insert.Parameters.Add("$f", SqliteType.Text);
            var set = insert.Parameters.Add("$s", SqliteType.Integer);
            var power = insert.Parameters.Add("$p", SqliteType.Integer);
            var icon = insert.Parameters.Add("$i", SqliteType.Integer);
            foreach (var row in CsvTable.Read(csv("ChrClasses")))
            {
                id.Value = row.GetInt("ID");
                name.Value = row.Get("Name_lang").Trim();
                file.Value = row.Get("Filename");
                set.Value = row.GetInt("SpellClassSet");
                power.Value = row.GetInt("DisplayPower");
                icon.Value = row.GetInt("IconFileDataID");
                insert.ExecuteNonQuery();
            }
        }

        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO races (id, name, playable, alliance) VALUES ($id, $n, $p, $a)";
            var id = insert.Parameters.Add("$id", SqliteType.Integer);
            var name = insert.Parameters.Add("$n", SqliteType.Text);
            var playable = insert.Parameters.Add("$p", SqliteType.Integer);
            var alliance = insert.Parameters.Add("$a", SqliteType.Integer);
            foreach (var row in CsvTable.Read(csv("ChrRaces")))
            {
                var bit = row.Has("PlayableRaceBit") ? row.GetInt("PlayableRaceBit") : -1;
                // PlayableRaceBit is blank for Human (bit 0), so a blank only counts for race 1.
                if (row.Get("PlayableRaceBit").Length == 0) bit = row.GetInt("ID") == 1 ? 0 : -1;
                id.Value = row.GetInt("ID");
                name.Value = row.Get("Name_lang").Trim();
                playable.Value = bit is >= 0 and <= LastClassicRaceBit ? 1 : 0;
                alliance.Value = row.GetInt("Alliance");
                insert.ExecuteNonQuery();
            }
        }

        var spellNames = new Dictionary<int, string>();
        foreach (var row in CsvTable.Read(csv("SpellName"))) spellNames[row.GetInt("ID")] = row.Get("Name_lang").Trim();

        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO talent_tabs (id, name, class_id, order_index, background, icon_file_id)
                VALUES ($id, $n, $c, $o, $b, $i)
                """;
            var id = insert.Parameters.Add("$id", SqliteType.Integer);
            var name = insert.Parameters.Add("$n", SqliteType.Text);
            var cls = insert.Parameters.Add("$c", SqliteType.Integer);
            var order = insert.Parameters.Add("$o", SqliteType.Integer);
            var background = insert.Parameters.Add("$b", SqliteType.Text);
            var icon = insert.Parameters.Add("$i", SqliteType.Integer);
            foreach (var row in CsvTable.Read(csv("TalentTab")))
            {
                var mask = row.GetInt("ClassMask");
                var classId = 0;
                for (var bit = 0; bit < 31; bit++)
                    if ((mask & (1 << bit)) != 0) { classId = bit + 1; break; }
                if (classId == 0) continue;

                id.Value = row.GetInt("ID");
                name.Value = row.Get("Name_lang").Trim();
                cls.Value = classId;
                order.Value = row.GetInt("OrderIndex");
                background.Value = row.Get("BackgroundFile");
                icon.Value = row.GetInt("SpellIconID");
                insert.ExecuteNonQuery();
            }
        }

        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO talents (id, tab_id, tier, col, max_rank, rank_spells, prereq_talent, prereq_rank, name)
                VALUES ($id, $tab, $tier, $col, $max, $ranks, $pre, $preRank, $n)
                """;
            var id = insert.Parameters.Add("$id", SqliteType.Integer);
            var tab = insert.Parameters.Add("$tab", SqliteType.Integer);
            var tier = insert.Parameters.Add("$tier", SqliteType.Integer);
            var col = insert.Parameters.Add("$col", SqliteType.Integer);
            var max = insert.Parameters.Add("$max", SqliteType.Integer);
            var ranks = insert.Parameters.Add("$ranks", SqliteType.Text);
            var pre = insert.Parameters.Add("$pre", SqliteType.Integer);
            var preRank = insert.Parameters.Add("$preRank", SqliteType.Integer);
            var name = insert.Parameters.Add("$n", SqliteType.Text);

            foreach (var row in CsvTable.Read(csv("Talent")))
            {
                var spells = new List<int>();
                for (var r = 0; r < 9; r++)
                {
                    var spell = row.GetInt($"SpellRank_{r}");
                    if (spell > 0) spells.Add(spell);
                }
                if (spells.Count == 0) continue;

                id.Value = row.GetInt("ID");
                tab.Value = row.GetInt("TabID");
                tier.Value = row.GetInt("TierID");
                col.Value = row.GetInt("ColumnIndex");
                max.Value = spells.Count;
                ranks.Value = string.Join(',', spells);
                pre.Value = row.GetInt("PrereqTalent_0");
                preRank.Value = row.GetInt("PrereqRank_0");
                name.Value = spellNames.GetValueOrDefault(spells[0], $"Talent {row.GetInt("ID")}");
                insert.ExecuteNonQuery();
            }
        }
    }

    // ---------------- WoW Forever trait trees ----------------

    /// <summary>Separates the per-rank descriptions stored in talents.rank_descriptions.</summary>
    public const char RankSeparator = '\u001f';

    private const int TraitGrid = 600;        // distance between talent slots in TraitNode.PosX/PosY
    private const int LegacyTraitSystem = 45; // account-wide "Legacy" trees, not class talents

    /// <summary>
    /// WoW Forever replaces the classic Talent table with one trait tree per class: the three specs sit side by
    /// side on a 600-unit grid, seven rows deep. Each spec column becomes a tab (named from TalentTab, by order),
    /// row = tier, position inside the spec = column. Nodes far off the grid are unused and skipped.
    /// Prerequisites come from TraitEdge (type 2/3, left node unlocks right node) and need the left node maxed.
    /// Unlock rule is still the classic 5 points per tier inside a spec until the TraitCond tables are read.
    /// </summary>
    /// <returns>"trait" when at least one class was switched to its trait tree, otherwise "classic".</returns>
    private static string ReplaceWithTraitTalents(
        SqliteConnection connection, SqliteTransaction transaction, Func<string, string> csv, Dictionary<int, SpellFacts> spells)
    {
        string[] needed = ["TraitTree", "TraitNode", "TraitNodeEntry", "TraitNodeXTraitNodeEntry", "TraitDefinition"];
        if (needed.Any(t => !File.Exists(csv(t)))) return "classic";

        var classBySet = new Dictionary<int, int>();
        foreach (var row in CsvTable.Read(csv("ChrClasses")))
            if (row.GetInt("SpellClassSet") > 0) classBySet.TryAdd(row.GetInt("SpellClassSet"), row.GetInt("ID"));

        var legacyTrees = CsvTable.Read(csv("TraitTree"))
            .Where(r => r.GetInt("TraitSystemID") == LegacyTraitSystem).Select(r => r.GetInt("ID")).ToHashSet();

        var firstEntry = new Dictionary<int, (int Entry, int Index)>();
        foreach (var row in CsvTable.Read(csv("TraitNodeXTraitNodeEntry")))
        {
            var node = row.GetInt("TraitNodeID");
            var index = row.GetInt("_Index");
            if (!firstEntry.TryGetValue(node, out var seen) || index < seen.Index)
                firstEntry[node] = (row.GetInt("TraitNodeEntryID"), index);
        }
        var entries = CsvTable.Read(csv("TraitNodeEntry"))
            .ToDictionary(r => r.GetInt("ID"), r => (Definition: r.GetInt("TraitDefinitionID"), MaxRanks: r.GetInt("MaxRanks")));
        var definitions = CsvTable.Read(csv("TraitDefinition")).ToDictionary(
            r => r.GetInt("ID"),
            r => (Spell: r.GetInt("SpellID"), Name: r.Get("OverrideName_lang").Trim(), Description: r.Get("OverrideDescription_lang").Trim()));

        var points = TraitPoints(csv);

        // Node -> (tree, x, y, spell, ranks, name)
        var nodes = new List<(int Id, int Tree, int X, int Y, int Spell, int Ranks, string Name, string Description, int Definition)>();
        foreach (var row in CsvTable.Read(csv("TraitNode")))
        {
            var id = row.GetInt("ID");
            var tree = row.GetInt("TraitTreeID");
            if (legacyTrees.Contains(tree)) continue;
            if (!firstEntry.TryGetValue(id, out var link) || !entries.TryGetValue(link.Entry, out var entry)) continue;
            if (!definitions.TryGetValue(entry.Definition, out var def) || def.Spell <= 0) continue;
            var name = def.Name.Length > 0 ? def.Name : spells.GetValueOrDefault(def.Spell)?.Name ?? "";
            if (name.Length == 0 || entry.MaxRanks <= 0) continue;
            nodes.Add((id, tree, row.GetInt("PosX"), row.GetInt("PosY"), def.Spell, entry.MaxRanks, name, def.Description, entry.Definition));
        }

        // Which class owns each tree: majority vote over the talents' spell class sets. Biggest tree per class wins.
        var chosen = new Dictionary<int, int>(); // class -> tree
        foreach (var group in nodes.GroupBy(n => n.Tree))
        {
            var classId = group
                .Select(n => classBySet.GetValueOrDefault(spells.GetValueOrDefault(n.Spell)?.ClassSet ?? 0))
                .Where(c => c > 0)
                .GroupBy(c => c).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();
            if (classId == 0 || group.Count() < 15) continue;
            if (!chosen.TryGetValue(classId, out var current) || nodes.Count(n => n.Tree == current) < group.Count())
                chosen[classId] = group.Key;
        }
        if (chosen.Count == 0) return "classic";

        var tabNames = new Dictionary<int, List<(int Id, string Name, string Background, int Icon)>>();
        foreach (var row in CsvTable.Read(csv("TalentTab")).OrderBy(r => r.GetInt("OrderIndex")))
        {
            var mask = row.GetInt("ClassMask");
            for (var bit = 0; bit < 31; bit++)
            {
                if ((mask & (1 << bit)) == 0) continue;
                if (!tabNames.TryGetValue(bit + 1, out var list)) tabNames[bit + 1] = list = [];
                list.Add((row.GetInt("ID"), row.Get("Name_lang").Trim(), row.Get("BackgroundFile"), row.GetInt("SpellIconID")));
                break;
            }
        }

        var edges = new Dictionary<int, int>(); // right node -> left node (its prerequisite)
        if (File.Exists(csv("TraitEdge")))
        {
            foreach (var row in CsvTable.Read(csv("TraitEdge")))
                if (row.GetInt("Type") is 2 or 3) edges.TryAdd(row.GetInt("RightTraitNodeID"), row.GetInt("LeftTraitNodeID"));
        }

        var text = new SpellText(id => spells.GetValueOrDefault(id));

        using var deleteTalents = connection.CreateCommand();
        deleteTalents.Transaction = transaction;
        deleteTalents.CommandText = "DELETE FROM talents WHERE tab_id IN (SELECT id FROM talent_tabs WHERE class_id = $c); DELETE FROM talent_tabs WHERE class_id = $c;";
        var dClass = deleteTalents.Parameters.Add("$c", SqliteType.Integer);

        using var insertTab = connection.CreateCommand();
        insertTab.Transaction = transaction;
        insertTab.CommandText = "INSERT INTO talent_tabs (id, name, class_id, order_index, background, icon_file_id) VALUES ($id, $n, $c, $o, $b, $i)";
        var tId = insertTab.Parameters.Add("$id", SqliteType.Integer);
        var tName = insertTab.Parameters.Add("$n", SqliteType.Text);
        var tClass = insertTab.Parameters.Add("$c", SqliteType.Integer);
        var tOrder = insertTab.Parameters.Add("$o", SqliteType.Integer);
        var tBackground = insertTab.Parameters.Add("$b", SqliteType.Text);
        var tIcon = insertTab.Parameters.Add("$i", SqliteType.Integer);

        using var insertTalent = connection.CreateCommand();
        insertTalent.Transaction = transaction;
        insertTalent.CommandText = """
            INSERT INTO talents (id, tab_id, tier, col, max_rank, rank_spells, prereq_talent, prereq_rank, name, rank_descriptions)
            VALUES ($id, $tab, $tier, $col, $max, $ranks, $pre, $preRank, $n, $desc)
            """;
        var id_ = insertTalent.Parameters.Add("$id", SqliteType.Integer);
        var tab_ = insertTalent.Parameters.Add("$tab", SqliteType.Integer);
        var tier_ = insertTalent.Parameters.Add("$tier", SqliteType.Integer);
        var col_ = insertTalent.Parameters.Add("$col", SqliteType.Integer);
        var max_ = insertTalent.Parameters.Add("$max", SqliteType.Integer);
        var ranks_ = insertTalent.Parameters.Add("$ranks", SqliteType.Text);
        var pre_ = insertTalent.Parameters.Add("$pre", SqliteType.Integer);
        var preRank_ = insertTalent.Parameters.Add("$preRank", SqliteType.Integer);
        var name_ = insertTalent.Parameters.Add("$n", SqliteType.Text);
        var desc_ = insertTalent.Parameters.Add("$desc", SqliteType.Text);

        using var insertPoints = connection.CreateCommand();
        insertPoints.Transaction = transaction;
        insertPoints.CommandText = "INSERT OR REPLACE INTO talent_points (spell_id, effect_index, rank, value) VALUES ($s, $e, $r, $v)";
        insertPoints.Parameters.Add("$s", SqliteType.Integer);
        insertPoints.Parameters.Add("$e", SqliteType.Integer);
        insertPoints.Parameters.Add("$r", SqliteType.Integer);
        insertPoints.Parameters.Add("$v", SqliteType.Real);

        foreach (var (classId, treeId) in chosen)
        {
            var treeNodes = nodes.Where(n => n.Tree == treeId).ToList();

            // Rows: the most used Y values set the grid; snap small offsets (2120 -> 2130), drop far-off nodes.
            var top = treeNodes.GroupBy(n => n.Y).Where(g => g.Count() >= 2).Min(g => g.Key);
            int? Row(int y)
            {
                var row = (int)Math.Round((y - top) / (double)TraitGrid);
                return row is >= 0 and <= 6 && Math.Abs(y - (top + row * TraitGrid)) <= 60 ? row : null;
            }
            var onGrid = treeNodes.Where(n => Row(n.Y) is not null).ToList();

            // Spec columns: clusters of X separated by more than two slots; tiny clusters are strays.
            var xs = onGrid.Select(n => n.X).Distinct().Order().ToList();
            var sections = new List<List<int>> { new() { xs[0] } };
            for (var i = 1; i < xs.Count; i++)
            {
                if (xs[i] - xs[i - 1] > TraitGrid * 2.5) sections.Add([]);
                sections[^1].Add(xs[i]);
            }
            sections = sections.Where(sec => onGrid.Count(n => sec.Contains(n.X)) >= 3).ToList();

            dClass.Value = classId;
            deleteTalents.ExecuteNonQuery();

            var names = tabNames.GetValueOrDefault(classId) ?? new List<(int Id, string Name, string Background, int Icon)>();
            var maxRanks = onGrid.ToDictionary(n => n.Id, n => n.Ranks);
            for (var s = 0; s < sections.Count; s++)
            {
                var tabId = s < names.Count ? names[s].Id : treeId * 10 + s;
                tId.Value = tabId;
                tName.Value = s < names.Count ? names[s].Name : $"Tree {s + 1}";
                tClass.Value = classId;
                tOrder.Value = s;
                tBackground.Value = s < names.Count ? names[s].Background : "";
                tIcon.Value = s < names.Count ? names[s].Icon : 0;
                insertTab.ExecuteNonQuery();

                var left = sections[s].Min();
                foreach (var n in onGrid.Where(x => sections[s].Contains(x.X)))
                {
                    var col = (int)Math.Round((n.X - left) / (double)TraitGrid);
                    if (col is < 0 or > 3) continue;

                    var spell = spells.GetValueOrDefault(n.Spell);
                    var rankPoints = points.GetValueOrDefault(n.Definition);
                    var descriptions = Enumerable.Range(1, n.Ranks).Select(rank =>
                        n.Description.Length > 0 ? n.Description
                        : spell is null ? ""
                        : Safe(() => text.Render(spell.Description, spell.AtRank(rank, rankPoints)), spell.Description));
                    if (rankPoints is not null)
                        foreach (var (index, curve) in rankPoints)
                            foreach (var (rank, value) in curve)
                            {
                                insertPoints.Parameters["$s"].Value = n.Spell;
                                insertPoints.Parameters["$e"].Value = index;
                                insertPoints.Parameters["$r"].Value = rank;
                                insertPoints.Parameters["$v"].Value = value;
                                insertPoints.ExecuteNonQuery();
                            }

                    var pre = edges.GetValueOrDefault(n.Id);
                    id_.Value = n.Id;
                    tab_.Value = tabId;
                    tier_.Value = Row(n.Y)!.Value;
                    col_.Value = col;
                    max_.Value = n.Ranks;
                    ranks_.Value = string.Join(',', Enumerable.Repeat(n.Spell, n.Ranks));
                    pre_.Value = maxRanks.ContainsKey(pre) ? pre : 0;
                    preRank_.Value = maxRanks.TryGetValue(pre, out var preMax) ? preMax - 1 : 0;
                    name_.Value = n.Name;
                    desc_.Value = string.Join(RankSeparator, descriptions);
                    insertTalent.ExecuteNonQuery();
                }
            }
        }
        return "trait";
    }

    /// <summary>
    /// Trait talents whose spell holds 0 and gets its value per rank from a curve (TraitDefinitionEffectPoints →
    /// CurvePoint: rank → value), e.g. Improved Seals 5/10/15%. Definition → effect index → rank → value.
    /// </summary>
    private static Dictionary<int, IReadOnlyDictionary<int, IReadOnlyDictionary<int, double>>> TraitPoints(Func<string, string> csv)
    {
        var result = new Dictionary<int, IReadOnlyDictionary<int, IReadOnlyDictionary<int, double>>>();
        if (!File.Exists(csv("TraitDefinitionEffectPoints")) || !File.Exists(csv("CurvePoint"))) return result;

        var curves = new Dictionary<int, Dictionary<int, double>>();
        foreach (var row in CsvTable.Read(csv("CurvePoint")))
        {
            var id = row.GetInt("CurveID");
            if (!curves.TryGetValue(id, out var points)) curves[id] = points = [];
            points[(int)Math.Round(row.GetDouble("Pos_0"))] = row.GetDouble("Pos_1");
        }
        var byDefinition = new Dictionary<int, Dictionary<int, IReadOnlyDictionary<int, double>>>();
        foreach (var row in CsvTable.Read(csv("TraitDefinitionEffectPoints")))
        {
            if (row.GetInt("OperationType") != 0 || !curves.TryGetValue(row.GetInt("CurveID"), out var curve)) continue; // 0 = set
            var definition = row.GetInt("TraitDefinitionID");
            if (!byDefinition.TryGetValue(definition, out var effects)) byDefinition[definition] = effects = [];
            effects[row.GetInt("EffectIndex")] = curve;
        }
        foreach (var (definition, effects) in byDefinition) result[definition] = effects;
        return result;
    }
}
