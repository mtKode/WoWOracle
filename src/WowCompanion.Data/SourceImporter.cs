using System.Globalization;
using System.IO.Compression;
using System.Text;
using Microsoft.Data.Sqlite;

namespace WowCompanion.Data;

/// <summary>
/// Where items come from: drops, vendors, quest rewards, containers, chests/nodes, pickpocketing, skinning and
/// fishing. The game client doesn't ship this (it's server data), so it is read from the CMaNGOS Classic world
/// database dump (github.com/cmangos/classic-db, Full_DB/*.sql.gz). That is Classic 1.12 data: WoW Forever
/// changes won't be in it. Drop chances are real percentages (loot groups and references worked out).
/// </summary>
public static class SourceImporter
{
    public const string Schema = """

        CREATE TABLE npcs (
            id INTEGER PRIMARY KEY, name TEXT NOT NULL, subname TEXT NOT NULL,
            min_level INTEGER NOT NULL, max_level INTEGER NOT NULL, rank INTEGER NOT NULL, map INTEGER NOT NULL,
            zone TEXT NOT NULL DEFAULT '', zone_x REAL NOT NULL DEFAULT 0, zone_y REAL NOT NULL DEFAULT 0
        );
        CREATE TABLE objects (
            id INTEGER PRIMARY KEY, name TEXT NOT NULL, type INTEGER NOT NULL, map INTEGER NOT NULL,
            zone TEXT NOT NULL DEFAULT '', zone_x REAL NOT NULL DEFAULT 0, zone_y REAL NOT NULL DEFAULT 0
        );
        CREATE TABLE quests (
            id INTEGER PRIMARY KEY, title TEXT NOT NULL, level INTEGER NOT NULL, min_level INTEGER NOT NULL,
            zone INTEGER NOT NULL, giver TEXT NOT NULL,
            giver_kind TEXT NOT NULL DEFAULT '', giver_id INTEGER NOT NULL DEFAULT 0,
            prev_quest INTEGER NOT NULL DEFAULT 0, next_in_chain INTEGER NOT NULL DEFAULT 0,
            races INTEGER NOT NULL DEFAULT 0, classes INTEGER NOT NULL DEFAULT 0, skill INTEGER NOT NULL DEFAULT 0,
            type INTEGER NOT NULL DEFAULT 0, details TEXT NOT NULL DEFAULT '', objectives TEXT NOT NULL DEFAULT ''
        );
        CREATE INDEX quests_zone ON quests (zone);
        CREATE TABLE maps (id INTEGER PRIMARY KEY, name TEXT NOT NULL, instance_type INTEGER NOT NULL);
        CREATE TABLE areas (id INTEGER PRIMARY KEY, name TEXT NOT NULL, map INTEGER NOT NULL DEFAULT -1);
        CREATE TABLE item_sources (item_id INTEGER NOT NULL, kind TEXT NOT NULL, ref_id INTEGER NOT NULL, chance REAL NOT NULL);
        CREATE INDEX item_sources_item ON item_sources (item_id);
        CREATE INDEX item_sources_ref ON item_sources (kind, ref_id);
        CREATE INDEX npcs_map ON npcs (map);
        CREATE TABLE gather_nodes (id INTEGER PRIMARY KEY, name TEXT NOT NULL, kind TEXT NOT NULL, skill INTEGER NOT NULL);
        CREATE TABLE gather_spawns (node_id INTEGER NOT NULL, zone TEXT NOT NULL, x REAL NOT NULL, y REAL NOT NULL);
        CREATE INDEX gather_spawns_node ON gather_spawns (node_id);
        CREATE INDEX gather_spawns_zone ON gather_spawns (zone);
        CREATE TABLE recipe_trainers (spell_id INTEGER NOT NULL, npc_id INTEGER NOT NULL, skill INTEGER NOT NULL);
        CREATE INDEX recipe_trainers_spell ON recipe_trainers (spell_id);
        """;

    private static readonly HashSet<string> Wanted =
    [
        "creature_template", "creature", "creature_loot_template", "reference_loot_template", "gameobject_template",
        "gameobject", "gameobject_loot_template", "npc_vendor", "npc_vendor_template", "quest_template",
        "creature_questrelation", "gameobject_questrelation", "item_loot_template", "pickpocketing_loot_template",
        "skinning_loot_template", "fishing_loot_template", "npc_trainer", "npc_trainer_template",
        "spell_template",
    ];

    /// <summary>Big tables of which only some rows matter: spell_template only for its "learn a spell" spells.</summary>
    private static readonly Dictionary<string, Func<Table, string[], bool>> KeepRow = new()
    {
        ["spell_template"] = (t, r) => LearnEffect(t, r) > 0,
    };

    /// <summary>The spell a Classic spell teaches (effect 36, LEARN_SPELL), or 0.</summary>
    private static int LearnEffect(Table t, string[] r)
    {
        for (var i = 1; i <= 3; i++)
            if (t.Int(r, $"Effect{i}") == 36 && t.Int(r, $"EffectTriggerSpell{i}") > 0) return t.Int(r, $"EffectTriggerSpell{i}");
        return 0;
    }

    private sealed class Table
    {
        public Dictionary<string, int> Columns { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string[]> Rows { get; } = [];
        public string Get(string[] row, string column) =>
            Columns.TryGetValue(column, out var i) && i < row.Length ? row[i] : "";
        public int Int(string[] row, string column) =>
            double.TryParse(Get(row, column), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? (int)v : 0;
        public double Real(string[] row, string column) =>
            double.TryParse(Get(row, column), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }

    /// <summary>Client Map and AreaTable names (optional tables), so sources can say "Molten Core" or "Silithus".</summary>
    public static void ImportNames(SqliteConnection connection, SqliteTransaction transaction, Func<string, string> csv)
    {
        var mapPath = csv("Map");
        if (File.Exists(mapPath))
        {
            var name = CsvTable.FindColumn(mapPath, "MapName") ?? "MapName_lang";
            var type = CsvTable.FindColumn(mapPath, "InstanceType") ?? "InstanceType";
            Insert(connection, transaction, "INSERT OR REPLACE INTO maps (id, name, instance_type) VALUES ($a, $b, $c)",
                CsvTable.Read(mapPath).Select(r => new object[] { r.GetInt("ID"), r.Get(name), r.GetInt(type) }));
        }
        var areaPath = csv("AreaTable");
        if (File.Exists(areaPath))
        {
            var name = CsvTable.FindColumn(areaPath, "AreaName") ?? "AreaName_lang";
            Insert(connection, transaction, "INSERT OR REPLACE INTO areas (id, name, map) VALUES ($a, $b, $c)",
                CsvTable.Read(areaPath).Select(r => new object[] { r.GetInt("ID"), r.Get(name), r.Has("ContinentID") ? r.GetInt("ContinentID") : -1 }));
        }
    }

    /// <returns>Number of item source rows written.</returns>
    /// <param name="csv">Client tables (UiMap, UiMapAssignment) to turn spawn positions into zone coordinates.</param>
    public static int Import(SqliteConnection connection, SqliteTransaction transaction, string dumpPath, Func<string, string>? csv = null)
    {
        var t = ReadDump(dumpPath);
        var locator = csv is null ? null : ZoneLocator.TryLoad(csv, classicOnly: true); // Classic 1.12 spawns
        Table T(string name) => t.TryGetValue(name, out var table) ? table : new Table();

        // ----- creatures -----
        var npcs = T("creature_template");
        var npcSpawn = MainSpawn(T("creature"), "id");
        Insert(connection, transaction,
            "INSERT OR REPLACE INTO npcs (id, name, subname, min_level, max_level, rank, map, zone, zone_x, zone_y) VALUES ($a, $b, $c, $d, $e, $f, $g, $h, $i, $j)",
            npcs.Rows.Select(r =>
            {
                var id = npcs.Int(r, "Entry");
                var (map, sx, sy) = npcSpawn.TryGetValue(id, out var spawn) ? spawn : (-1, 0.0, 0.0);
                var (zone, zx, zy) = locator is null ? ("", 0.0, 0.0) : locator.Locate(map, sx, sy);
                return new object[]
                {
                    id, npcs.Get(r, "Name"), npcs.Get(r, "SubName"), npcs.Int(r, "MinLevel"), npcs.Int(r, "MaxLevel"),
                    npcs.Int(r, "Rank"), map, zone, zx, zy,
                };
            }));

        // ----- objects (chests, herbs, ore, fishing schools) -----
        var objects = T("gameobject_template");
        var objectSpawn = MainSpawn(T("gameobject"), "id");
        Insert(connection, transaction, "INSERT OR REPLACE INTO objects (id, name, type, map, zone, zone_x, zone_y) VALUES ($a, $b, $c, $d, $e, $f, $g)",
            objects.Rows.Select(r =>
            {
                var id = objects.Int(r, "entry");
                var (map, sx, sy) = objectSpawn.TryGetValue(id, out var spawn) ? spawn : (-1, 0.0, 0.0);
                var (zone, zx, zy) = locator is null ? ("", 0.0, 0.0) : locator.Locate(map, sx, sy);
                return new object[] { id, objects.Get(r, "name"), objects.Int(r, "type"), map, zone, zx, zy };
            }));

        // ----- ore and herb nodes: every spot they can spawn (the map's gathering layer) -----
        if (csv is not null && locator is not null) ImportGatherNodes(connection, transaction, objects, T("gameobject"), csv("Lock"), locator);

        // ----- profession trainers: who teaches which recipe, at what skill -----
        ImportRecipeTrainers(connection, transaction, npcs, npcSpawn, T("npc_trainer"), T("npc_trainer_template"), T("spell_template"));

        // ----- quests, with who gives them -----
        var givers = new Dictionary<int, (string Name, string Kind, int Id)>();
        var npcNames = npcs.Rows.ToDictionary(r => npcs.Int(r, "Entry"), r => npcs.Get(r, "Name"));
        var objectNames = objects.Rows.ToDictionary(r => objects.Int(r, "entry"), r => objects.Get(r, "name"));
        var qr = T("creature_questrelation");
        foreach (var r in qr.Rows)
            if (npcNames.TryGetValue(qr.Int(r, "id"), out var n)) givers.TryAdd(qr.Int(r, "quest"), (n, "npc", qr.Int(r, "id")));
        var gqr = T("gameobject_questrelation");
        foreach (var r in gqr.Rows)
            if (objectNames.TryGetValue(gqr.Int(r, "id"), out var n)) givers.TryAdd(gqr.Int(r, "quest"), (n, "object", gqr.Int(r, "id")));

        var quests = T("quest_template");
        Insert(connection, transaction,
            """
            INSERT OR REPLACE INTO quests (id, title, level, min_level, zone, giver, giver_kind, giver_id,
                                           prev_quest, next_in_chain, races, classes, skill, type, details, objectives)
            VALUES ($a, $b, $c, $d, $e, $f, $g, $h, $i, $j, $k, $l, $m, $n, $o, $p)
            """,
            quests.Rows.Select(r =>
            {
                var id = quests.Int(r, "entry");
                var (giverName, giverKind, giverId) = givers.TryGetValue(id, out var g) ? g : ("", "", 0);
                return new object[]
                {
                    id, quests.Get(r, "Title"), quests.Int(r, "QuestLevel"), quests.Int(r, "MinLevel"),
                    quests.Int(r, "ZoneOrSort"), giverName, giverKind, giverId,
                    quests.Int(r, "PrevQuestId"), quests.Int(r, "NextQuestInChain"), quests.Int(r, "RequiredRaces"),
                    quests.Int(r, "RequiredClasses"), quests.Int(r, "RequiredSkill"), quests.Int(r, "Type"),
                    quests.Get(r, "Details"), quests.Get(r, "Objectives"),
                };
            }));

        // ----- item sources -----
        var sources = new List<object[]>();
        void Add(int item, string kind, int refId, double chance)
        {
            if (item > 0) sources.Add([item, kind, refId, Math.Abs(chance)]);
        }

        // Shared loot lists: a negative mincountOrRef points into reference_loot_template.
        // Chances become real percentages: entries with chance 0 in a loot group split what the group's explicit
        // chances leave (one of the group drops), and items reached through a reference are scaled by the chance
        // that the reference rolls (times its roll count).
        var reference = T("reference_loot_template");
        var refRows = reference.Rows.GroupBy(r => reference.Int(r, "entry")).ToDictionary(g => g.Key, g => g.ToList());
        IEnumerable<(int Item, double Chance)> Expand(Table table, IEnumerable<string[]> rows, int depth, double scale)
        {
            var list = rows.ToList();
            var groups = list.Where(r => table.Int(r, "groupid") > 0)
                .GroupBy(r => table.Int(r, "groupid"))
                .ToDictionary(g => g.Key, g =>
                {
                    var explicitSum = g.Sum(r => Math.Abs(table.Real(r, "ChanceOrQuestChance")));
                    var equal = g.Count(r => table.Real(r, "ChanceOrQuestChance") == 0);
                    return equal > 0 ? Math.Max(0, 100 - explicitSum) / equal : 0;
                });

            foreach (var r in list)
            {
                var raw = Math.Abs(table.Real(r, "ChanceOrQuestChance"));
                var group = table.Int(r, "groupid");
                var countOrRef = table.Int(r, "mincountOrRef");
                double chance = raw > 0 ? raw
                    : group > 0 ? groups.GetValueOrDefault(group)
                    : countOrRef < 0 ? 100 : 0; // ungrouped 0: unknown for items, "always" for a reference
                chance = chance * scale / 100;

                if (countOrRef < 0)
                {
                    var rolls = Math.Max(1, table.Int(r, "maxcount"));
                    if (depth < 5 && refRows.TryGetValue(-countOrRef, out var nested))
                        foreach (var x in Expand(reference, nested, depth + 1, Math.Min(100, chance * rolls))) yield return x;
                }
                else yield return (table.Int(r, "item"), chance);
            }
        }

        void FromLoot(string lootTable, string kind, Dictionary<int, List<int>> owners)
        {
            var loot = T(lootTable);
            foreach (var group in loot.Rows.GroupBy(r => loot.Int(r, "entry")))
            {
                if (!owners.TryGetValue(group.Key, out var who)) continue;
                foreach (var (item, chance) in Expand(loot, group, 0, 100))
                    foreach (var owner in who) Add(item, kind, owner, chance);
            }
        }

        Dictionary<int, List<int>> OwnersBy(string column) => npcs.Rows
            .Where(r => npcs.Int(r, column) > 0)
            .GroupBy(r => npcs.Int(r, column))
            .ToDictionary(g => g.Key, g => g.Select(r => npcs.Int(r, "Entry")).ToList());

        FromLoot("creature_loot_template", "drop", OwnersBy("LootId"));
        FromLoot("pickpocketing_loot_template", "pickpocket", OwnersBy("PickpocketLootId"));
        FromLoot("skinning_loot_template", "skinning", OwnersBy("SkinningLootId"));

        // Chests (type 3) and fishing holes (type 25) keep their loot id in data1.
        var objectOwners = objects.Rows
            .Where(r => objects.Int(r, "type") is 3 or 25 && objects.Int(r, "data1") > 0)
            .GroupBy(r => objects.Int(r, "data1"))
            .ToDictionary(g => g.Key, g => g.Select(r => objects.Int(r, "entry")).ToList());
        FromLoot("gameobject_loot_template", "object", objectOwners);

        // Containers (bags, boxes, clams): entry is the container's item id.
        var containers = T("item_loot_template");
        foreach (var group in containers.Rows.GroupBy(r => containers.Int(r, "entry")))
            foreach (var (item, chance) in Expand(containers, group, 0, 100)) Add(item, "container", group.Key, chance);

        // Fishing: entry is the zone/area id.
        var fishing = T("fishing_loot_template");
        foreach (var group in fishing.Rows.GroupBy(r => fishing.Int(r, "entry")))
            foreach (var (item, chance) in Expand(fishing, group, 0, 100)) Add(item, "fishing", group.Key, chance);

        // Vendors, directly or through a shared vendor template.
        var vendor = T("npc_vendor");
        foreach (var r in vendor.Rows)
            if (!TestVendors.Contains(vendor.Int(r, "entry"))) Add(vendor.Int(r, "item"), "vendor", vendor.Int(r, "entry"), 100);
        var vendorTemplate = T("npc_vendor_template");
        var templateItems = vendorTemplate.Rows.GroupBy(r => vendorTemplate.Int(r, "entry"))
            .ToDictionary(g => g.Key, g => g.Select(r => vendorTemplate.Int(r, "item")).ToList());
        foreach (var r in npcs.Rows)
            if (!TestVendors.Contains(npcs.Int(r, "Entry")) && templateItems.TryGetValue(npcs.Int(r, "VendorTemplateId"), out var items))
                foreach (var item in items) Add(item, "vendor", npcs.Int(r, "Entry"), 100);

        // Quest rewards: fixed rewards and "choose one of".
        foreach (var r in quests.Rows)
        {
            var id = quests.Int(r, "entry");
            for (var i = 1; i <= 4; i++) Add(quests.Int(r, $"RewItemId{i}"), "quest", id, 100);
            for (var i = 1; i <= 6; i++) Add(quests.Int(r, $"RewChoiceItemId{i}"), "quest_choice", id, 100);
        }

        Insert(connection, transaction,
            "INSERT INTO item_sources (item_id, kind, ref_id, chance) VALUES ($a, $b, $c, $d)",
            // The same item can be reached twice (directly and through a reference): keep the better chance.
            sources.GroupBy(s => ((int)s[0], (string)s[1], (int)s[2]))
                .Select(g => g.OrderByDescending(s => (double)s[3]).First()));
        return sources.Count;
    }

    /// <summary>
    /// Developer test NPCs in the Classic data that "sell" real items but never stand anywhere in the world: Jimmy
    /// McWeaksauce &lt;Cool Guy&gt; (bandages, poisons, reagents and his own McWeaksauce drinks). Other vendors without a
    /// spawn are real: summoned (Vendor-Tron 1000, Field Repair Bot) or holiday sellers.
    /// </summary>
    private static readonly HashSet<int> TestVendors = [17078];

    /// <summary>
    /// Profession trainers (npc_trainer, and npc_trainer_template through creature_template.TrainerTemplateId): the
    /// recipe each teaches and the skill it needs (reqskillvalue). The trainer's spell is the "teach" spell, whose
    /// LEARN_SPELL effect (36) names the recipe: Classic's spell_template has them all (today's client lacks most),
    /// the client's SpellEffect the rest. Only trainers that stand in the world count. Recipes whose learn skill was
    /// only estimated take the trainer's (the addon's trainer windows still overrule it).
    /// </summary>
    private static void ImportRecipeTrainers(SqliteConnection connection, SqliteTransaction transaction, Table npcs,
        Dictionary<int, (int Map, double X, double Y)> npcSpawn, Table direct, Table templates, Table spells)
    {
        var teaches = new Dictionary<int, int>();
        foreach (var r in spells.Rows) teaches[spells.Int(r, "Id")] = LearnEffect(spells, r);
        var rows = new List<(int Npc, int Spell, int Skill)>();
        foreach (var r in direct.Rows)
            if (direct.Int(r, "reqskill") > 0) rows.Add((direct.Int(r, "entry"), direct.Int(r, "spell"), direct.Int(r, "reqskillvalue")));
        var byTemplate = templates.Rows.Where(r => templates.Int(r, "reqskill") > 0)
            .GroupBy(r => templates.Int(r, "entry")).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var r in npcs.Rows)
            if (byTemplate.TryGetValue(npcs.Int(r, "TrainerTemplateId"), out var list))
                foreach (var t in list) rows.Add((npcs.Int(r, "Entry"), templates.Int(t, "spell"), templates.Int(t, "reqskillvalue")));

        Insert(connection, transaction, "INSERT INTO recipe_trainers (spell_id, npc_id, skill) VALUES ($a, $b, $c)",
            rows.Where(x => npcSpawn.ContainsKey(x.Npc)).Distinct()
                .Select(x => new object[] { teaches.GetValueOrDefault(x.Spell, x.Spell), x.Npc, Math.Max(1, x.Skill) }));

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE recipe_trainers SET spell_id = (
                SELECT MIN(e.trigger_spell) FROM spell_effects e
                WHERE e.spell_id = recipe_trainers.spell_id AND e.effect = 36 AND e.trigger_spell > 0)
            WHERE spell_id NOT IN (SELECT spell_id FROM recipes)
              AND EXISTS (SELECT 1 FROM spell_effects e
                          WHERE e.spell_id = recipe_trainers.spell_id AND e.effect = 36 AND e.trigger_spell > 0);
            DELETE FROM recipe_trainers WHERE spell_id NOT IN (SELECT spell_id FROM recipes);
            UPDATE recipes SET
                min_skill = (SELECT MIN(t.skill) FROM recipe_trainers t WHERE t.spell_id = recipes.spell_id),
                skill_source = 'trainer'
            WHERE skill_source = 'estimate' AND spell_id IN (SELECT spell_id FROM recipe_trainers);
            """;
        command.ExecuteNonQuery();
    }

    /// <summary>Lock types in the client's Lock table: 2 = a skill lock; its index 2 = Herbalism, 3 = Mining.</summary>
    private const int LockSkill = 2, LockHerbalism = 2, LockMining = 3;

    /// <summary>
    /// Ore veins and herbs: gameobjects (chests, type 3) whose lock (data0) asks for Mining or Herbalism, with the skill
    /// they need, and every spawn of them in zone coordinates. Spots can be shared by several node types (a pool
    /// spawns one of them), so a spot is listed for each.
    /// </summary>
    private static void ImportGatherNodes(SqliteConnection connection, SqliteTransaction transaction, Table objects, Table spawns,
        string lockPath, ZoneLocator locator)
    {
        if (!File.Exists(lockPath)) return;
        var locks = new Dictionary<int, (string Kind, int Skill)>();
        foreach (var r in CsvTable.Read(lockPath))
        {
            for (var i = 0; i < 8; i++)
            {
                if (r.GetInt($"Type_{i}") != LockSkill) continue;
                var index = r.Has($"_Index_{i}") ? r.GetInt($"_Index_{i}") : r.GetInt($"Index_{i}");
                var kind = index switch { LockMining => "mining", LockHerbalism => "herbalism", _ => null };
                if (kind is null) continue;
                locks[r.GetInt("ID")] = (kind, r.GetInt($"Skill_{i}"));
                break;
            }
        }

        var nodes = new Dictionary<int, (string Name, string Kind, int Skill)>();
        foreach (var r in objects.Rows)
        {
            if (objects.Int(r, "type") != 3 || !locks.TryGetValue(objects.Int(r, "data0"), out var lockInfo)) continue;
            var name = objects.Get(r, "name").Trim();
            if (name.Length > 0) nodes[objects.Int(r, "entry")] = (name, lockInfo.Kind, lockInfo.Skill);
        }
        Insert(connection, transaction, "INSERT OR REPLACE INTO gather_nodes (id, name, kind, skill) VALUES ($a, $b, $c, $d)",
            nodes.Select(n => new object[] { n.Key, n.Value.Name, n.Value.Kind, n.Value.Skill }));

        Insert(connection, transaction, "INSERT INTO gather_spawns (node_id, zone, x, y) VALUES ($a, $b, $c, $d)",
            spawns.Rows
                .Where(r => nodes.ContainsKey(spawns.Int(r, "id")))
                .Select(r =>
                {
                    var (zone, x, y) = locator.Locate(spawns.Int(r, "map"), spawns.Real(r, "position_x"), spawns.Real(r, "position_y"));
                    return (Id: spawns.Int(r, "id"), Zone: zone, X: x, Y: y);
                })
                .Where(s => s.Zone.Length > 0 && s.X > 0 && s.Y > 0)
                .Select(s => new object[] { s.Id, s.Zone, Math.Round(s.X, 1), Math.Round(s.Y, 1) }));
    }

    /// <summary>Where each creature/object mostly stands: its most common map, and the first spawn there.</summary>
    private static Dictionary<int, (int Map, double X, double Y)> MainSpawn(Table spawns, string idColumn) => spawns.Rows
        .GroupBy(r => spawns.Int(r, idColumn))
        .ToDictionary(g => g.Key, g =>
        {
            var onMap = g.GroupBy(r => spawns.Int(r, "map")).OrderByDescending(m => m.Count()).First();
            var first = onMap.First();
            return (onMap.Key, spawns.Real(first, "position_x"), spawns.Real(first, "position_y"));
        });

    private static void Insert(SqliteConnection connection, SqliteTransaction transaction, string sql, IEnumerable<object[]> rows)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        string[] names = ["$a", "$b", "$c", "$d", "$e", "$f", "$g", "$h", "$i", "$j", "$k", "$l", "$m", "$n", "$o", "$p"];
        var parameters = names.Where(sql.Contains).Select(n => command.Parameters.AddWithValue(n, DBNull.Value)).ToArray();
        foreach (var row in rows)
        {
            for (var i = 0; i < parameters.Length; i++) parameters[i].Value = i < row.Length ? row[i] : DBNull.Value;
            command.ExecuteNonQuery();
        }
    }

    // ---------------- MySQL dump reader ----------------

    /// <summary>Reads CREATE TABLE column lists and INSERT rows of the wanted tables from a .sql.gz dump.</summary>
    private static Dictionary<string, Table> ReadDump(string path)
    {
        var tables = new Dictionary<string, Table>();
        using var file = File.OpenRead(path);
        Stream stream = path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) ? new GZipStream(file, CompressionMode.Decompress) : file;
        using var reader = new StreamReader(stream, Encoding.UTF8);

        string? creating = null;
        while (reader.ReadLine() is { } line)
        {
            if (line.StartsWith("CREATE TABLE `", StringComparison.Ordinal))
            {
                var name = line.Split('`')[1];
                creating = Wanted.Contains(name) ? name : null;
                if (creating is not null) tables[creating] = new Table();
                continue;
            }
            if (creating is not null)
            {
                if (line.StartsWith("  `", StringComparison.Ordinal))
                {
                    var column = line.Split('`')[1];
                    tables[creating].Columns.TryAdd(column, tables[creating].Columns.Count);
                    continue;
                }
                if (line.StartsWith(")", StringComparison.Ordinal)) creating = null;
            }
            if (line.StartsWith("INSERT INTO `", StringComparison.Ordinal))
            {
                var name = line.Split('`')[1];
                if (!tables.TryGetValue(name, out var table)) continue;
                if (KeepRow.TryGetValue(name, out var keep))
                {
                    var parsed = new List<string[]>();
                    ParseValues(line, parsed);
                    table.Rows.AddRange(parsed.Where(r => keep(table, r)));
                }
                else ParseValues(line, table.Rows);
            }
        }
        return tables;
    }

    /// <summary>Parses "INSERT INTO `t` VALUES (1,'a\'b',NULL),(2,...);" into rows of strings.</summary>
    private static void ParseValues(string line, List<string[]> into)
    {
        var i = line.IndexOf(" VALUES ", StringComparison.Ordinal);
        if (i < 0) return;
        i += 8;
        var value = new StringBuilder();
        var fields = new List<string>();
        while (i < line.Length)
        {
            while (i < line.Length && line[i] != '(') i++;
            if (i >= line.Length) break;
            i++;
            fields.Clear();
            value.Clear();
            var quoted = false;
            while (i < line.Length)
            {
                var c = line[i];
                if (quoted)
                {
                    if (c == '\\' && i + 1 < line.Length)
                    {
                        var next = line[i + 1];
                        value.Append(next switch { 'n' => '\n', 'r' => '\r', 't' => '\t', '0' => '\0', _ => next });
                        i += 2;
                        continue;
                    }
                    if (c == '\'')
                    {
                        if (i + 1 < line.Length && line[i + 1] == '\'') { value.Append('\''); i += 2; continue; }
                        quoted = false;
                        i++;
                        continue;
                    }
                    value.Append(c);
                    i++;
                    continue;
                }
                if (c == '\'') { quoted = true; i++; continue; }
                if (c == ',') { fields.Add(value.ToString()); value.Clear(); i++; continue; }
                if (c == ')') { fields.Add(value.ToString()); i++; break; }
                value.Append(c);
                i++;
            }
            into.Add(fields.ToArray());
        }
    }
}
