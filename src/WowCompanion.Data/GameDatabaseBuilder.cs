using Microsoft.Data.Sqlite;
using WowCompanion.Core;

namespace WowCompanion.Data;

public sealed record BuildStats(int TotalRows, int KeptRows, int Recipes);

public static partial class GameDatabaseBuilder
{
    /// <summary>Every table the build needs. The updater downloads exactly these.</summary>
    public static readonly string[] RequiredTables =
    [
        "Item", "ItemSparse", "RandPropPoints", "SkillLine", "SkillLineAbility", "SpellName", "SpellEffect", "SpellReagents",
        // item numbers
        "ItemArmorQuality", "ItemArmorTotal", "ItemArmorShield",
        "ItemDamageOneHand", "ItemDamageOneHandCaster", "ItemDamageTwoHand", "ItemDamageTwoHandCaster",
        "ItemDamageAmmo", "ItemDamageThrown", "ItemDamageWand",
        "ItemEffect", "ItemSet", "ItemSetSpell",
        // spells
        "Spell", "SpellMisc", "SpellPower", "SpellCastTimes", "SpellDuration", "SpellRange", "SpellCooldowns",
        "SpellClassOptions", "SpellLevels", "SpellAuraOptions",
        // talents, classes, races
        "Talent", "TalentTab", "ChrClasses", "ChrRaces",
        // WoW Forever talents (trait trees). Required: without them the build would fall back to Classic's talents,
        // which aren't Forever's (a failed download keeps the previous game data instead).
        "TraitTree", "TraitNode", "TraitNodeEntry", "TraitNodeXTraitNodeEntry", "TraitDefinition",
    ];

    /// <summary>Used when present. A missing one turns off a detail (armor per slot, item effects, radius), nothing more.</summary>
    public static readonly string[] OptionalTables =
    [
        "ArmorLocation", "ItemXItemEffect", "SpellRadius", "Map", "AreaTable",
        // enchants (gear page): what each adds, and which spells put it on which slots
        "SpellItemEnchantment", "SpellEquippedItems",
        // zone coordinates for NPCs and quest givers ("Westfall (56, 47)")
        "UiMap", "UiMapAssignment",
        // item icons for items whose Item row has none (newer items keep it on their appearance)
        "ItemModifiedAppearance", "ItemAppearance",
        // zone map pictures (Map page): which tiles make up each map
        "UiMapXMapArt", "UiMapArt", "UiMapArtStyleLayer", "UiMapArtTile", "WorldMapOverlay", "WorldMapOverlayTile",
        // WoW Forever talents (trait trees). The Cond/Group/Currency tables are fetched for the next step: exact unlock rules.
        "TraitEdge", "TraitDefinitionEffectPoints", "CurvePoint", // per-rank talent values (Improved Seals 5/10/15%)
        "TraitCond", "TraitNodeGroup", "TraitNodeGroupXTraitNode", "TraitNodeGroupXTraitCond", "TraitNodeXTraitCond",
        "TraitNodeGroupXTraitCost", "TraitCurrency", "TraitCurrencySource", "TraitTreeXTraitCurrency", "TraitSubTree",
    ];

    /// <summary>
    /// Crafted items the client's Item table has but ItemSparse (names, stats) doesn't yet: a stand-in named after the
    /// recipe that makes it, with its real class and icon, listed in placeholder_items so real data replaces it.
    /// </summary>
    private static int InsertPlaceholderItems(SqliteConnection connection, SqliteTransaction transaction,
        Dictionary<int, (int Class, int Subclass)> classes, Dictionary<int, int> icons)
    {
        var missing = new List<(int Id, string Name)>();
        using (var query = connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = """
                SELECT r.created_item_id, MIN(r.name) FROM recipes r
                WHERE r.created_item_id > 0 AND NOT EXISTS (SELECT 1 FROM items i WHERE i.id = r.created_item_id)
                GROUP BY r.created_item_id
                """;
            using var r = query.ExecuteReader();
            while (r.Read()) missing.Add((r.GetInt32(0), r.GetString(1)));
        }

        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO items (id, name, description, quality, item_level, required_level, class_id, subclass_id,
                               inventory_type, bonding, sell_price, buy_price, stack_size, stats, stats_raw, icon_file_id)
            VALUES ($id, $name, 'Made by the recipe of the same name. Not in the downloaded game data yet: the rest fills in when the game data or the WoW Oracle addon has it.',
                    1, 0, 0, $class, $sub, 0, 0, 0, 0, 1, '', '', $icon);
            INSERT INTO placeholder_items (id) VALUES ($id);
            """;
        var added = 0;
        foreach (var (itemId, recipeName) in missing)
        {
            if (!classes.TryGetValue(itemId, out var cls) || recipeName.Trim().Length == 0) continue;
            insert.Parameters.Clear();
            insert.Parameters.AddWithValue("$id", itemId);
            insert.Parameters.AddWithValue("$name", recipeName.Trim());
            insert.Parameters.AddWithValue("$class", cls.Class);
            insert.Parameters.AddWithValue("$sub", cls.Subclass);
            insert.Parameters.AddWithValue("$icon", icons.GetValueOrDefault(itemId));
            insert.ExecuteNonQuery();
            added++;
        }
        return added;
    }

    private const string Schema = """
        CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
        CREATE TABLE items (
            id INTEGER PRIMARY KEY,
            name TEXT NOT NULL,
            description TEXT NOT NULL,
            quality INTEGER NOT NULL,
            item_level INTEGER NOT NULL,
            required_level INTEGER NOT NULL,
            class_id INTEGER NOT NULL,
            subclass_id INTEGER NOT NULL,
            inventory_type INTEGER NOT NULL,
            bonding INTEGER NOT NULL,
            sell_price INTEGER NOT NULL,
            buy_price INTEGER NOT NULL,
            stack_size INTEGER NOT NULL,
            stats TEXT NOT NULL,
            stats_raw TEXT NOT NULL,
            delay_ms INTEGER NOT NULL DEFAULT 0,
            dmg_min REAL NOT NULL DEFAULT 0,
            dmg_max REAL NOT NULL DEFAULT 0,
            dmg_school INTEGER NOT NULL DEFAULT 0,
            armor INTEGER NOT NULL DEFAULT 0,
            item_set INTEGER NOT NULL DEFAULT 0,
            allowable_class INTEGER NOT NULL DEFAULT -1,
            allowable_race INTEGER NOT NULL DEFAULT -1,
            max_count INTEGER NOT NULL DEFAULT 0,
            required_skill INTEGER NOT NULL DEFAULT 0,
            required_skill_rank INTEGER NOT NULL DEFAULT 0,
            icon_file_id INTEGER NOT NULL DEFAULT 0
        );
        CREATE TABLE item_effects (
            item_id INTEGER NOT NULL,
            slot INTEGER NOT NULL,
            trigger_type INTEGER NOT NULL,
            spell_id INTEGER NOT NULL,
            charges INTEGER NOT NULL,
            cooldown_ms INTEGER NOT NULL,
            category_cooldown_ms INTEGER NOT NULL
        );
        CREATE INDEX item_effects_item ON item_effects (item_id);
        CREATE INDEX item_effects_spell ON item_effects (spell_id);
        CREATE TABLE item_sets (
            id INTEGER PRIMARY KEY,
            name TEXT NOT NULL,
            required_skill INTEGER NOT NULL,
            required_skill_rank INTEGER NOT NULL
        );
        CREATE TABLE item_set_items (set_id INTEGER NOT NULL, item_id INTEGER NOT NULL, position INTEGER NOT NULL);
        CREATE INDEX item_set_items_set ON item_set_items (set_id);
        CREATE TABLE item_set_bonuses (set_id INTEGER NOT NULL, threshold INTEGER NOT NULL, spell_id INTEGER NOT NULL);
        CREATE INDEX item_set_bonuses_set ON item_set_bonuses (set_id);
        -- Items the client knows (Item table: class, icon) but wago.tools has no ItemSparse row for yet, added so recipes
        -- that make them show a name and an icon. The addon's learned data replaces them (LearnedItemStore).
        CREATE TABLE placeholder_items (id INTEGER PRIMARY KEY);
        CREATE VIRTUAL TABLE items_fts USING fts5(
            name, description,
            content='items', content_rowid='id',
            tokenize='unicode61 remove_diacritics 2'
        );
        CREATE TABLE recipes (
            spell_id INTEGER PRIMARY KEY,
            name TEXT NOT NULL,
            skill_line_id INTEGER NOT NULL,
            profession TEXT NOT NULL,
            min_skill INTEGER NOT NULL,
            trivial_low INTEGER NOT NULL,
            trivial_high INTEGER NOT NULL,
            created_item_id INTEGER NOT NULL,
            created_count INTEGER NOT NULL DEFAULT 1,  -- items one craft makes (ammo 200, bombs 2…)
            skill_source TEXT NOT NULL DEFAULT 'trainer',
            taught_by INTEGER NOT NULL DEFAULT 0
        );
        CREATE TABLE recipe_reagents (
            spell_id INTEGER NOT NULL,
            item_id INTEGER NOT NULL,
            quantity INTEGER NOT NULL
        );
        CREATE INDEX recipes_created ON recipes (created_item_id);
        CREATE INDEX reagents_item ON recipe_reagents (item_id);
        CREATE INDEX reagents_spell ON recipe_reagents (spell_id);
        """ + SpellSchema + TalentSchema + SourceImporter.Schema + EnchantSchema + MapSchema + HiddenSchema;

    /// <summary>Notes from the last build (e.g. an optional table that was missing).</summary>
    public static IReadOnlyList<string> ItemsMissingNumbers { get; private set; } = [];

    /// <param name="fallbackVersion">
    /// Classic Era build used to fill items WoW Forever ships without an ItemSparse row (it only carries the
    /// items it changed). Null skips the fallback.
    /// </param>
    /// <param name="sourcesDump">CMaNGOS Classic world database dump (.sql.gz) for drops/vendors/quests; null skips it.</param>
    public static BuildStats Build(string cacheDir, string version, string product, string outputPath, string? fallbackVersion = null,
        string? sourcesDump = null)
    {
        string Csv(string table) => Path.Combine(cacheDir, version, table + ".csv");
        string? Fallback(string table)
        {
            if (fallbackVersion is null) return null;
            var path = Path.Combine(cacheDir, fallbackVersion, table + ".csv");
            return File.Exists(path) ? path : null;
        }

        CsvTable.Require(Csv("Item"), "ID", "ClassID", "SubclassID");
        CsvTable.Require(Csv("ItemSparse"), "ID", "Display_lang", "Description_lang", "OverallQualityID", "ItemLevel",
            "RequiredLevel", "InventoryType", "Bonding", "SellPrice", "BuyPrice", "Stackable",
            "StatModifier_bonusStat_0", "StatPercentEditor_0");
        CsvTable.Require(Csv("RandPropPoints"), "ID", "Epic_0", "Superior_0", "Good_0");
        CsvTable.Require(Csv("SkillLine"), "ID", "DisplayName_lang", "CategoryID");
        CsvTable.Require(Csv("SkillLineAbility"), "SkillLine", "Spell", "MinSkillLineRank",
            "TrivialSkillLineRankLow", "TrivialSkillLineRankHigh");
        CsvTable.Require(Csv("SpellName"), "ID", "Name_lang");
        CsvTable.Require(Csv("SpellEffect"), "SpellID", "Effect", "EffectItemType");
        CsvTable.Require(Csv("SpellReagents"), "SpellID", "Reagent_0", "ReagentCount_0");

        var budgets = ReadBudgets(Csv("RandPropPoints"));
        var numbers = ItemNumbers.Load(Csv);

        var classes = new Dictionary<int, (int Class, int Subclass)>();
        var icons = new Dictionary<int, int>();
        foreach (var row in CsvTable.Read(Csv("Item")))
        {
            classes[row.GetInt("ID")] = (row.GetInt("ClassID"), row.GetInt("SubclassID"));
            icons[row.GetInt("ID")] = row.GetInt("IconFileDataID");
        }
        FillAppearanceIcons(icons, Csv);

        var temp = outputPath + ".tmp";
        if (File.Exists(temp)) File.Delete(temp);

        int total = 0, kept = 0, recipeCount;
        var connectionString = new SqliteConnectionStringBuilder { DataSource = temp, Pooling = false }.ToString();
        using (var connection = new SqliteConnection(connectionString))
        {
            connection.Open();
            Execute(connection, Schema);

            using var transaction = connection.BeginTransaction();
            // Forever's own rows first; Classic Era fills the gaps for items Forever didn't change.
            var sparseFiles = new List<string> { Csv("ItemSparse") };
            if (Fallback("ItemSparse") is { } eraSparse) sparseFiles.Add(eraSparse);
            HashSet<int> fromFallback;
            (total, kept, fromFallback) = InsertItems(connection, transaction, sparseFiles, classes, budgets, numbers, icons);
            var fallbackCount = fromFallback.Count;
            recipeCount = InsertRecipes(connection, transaction, Csv);

            var spells = LoadSpells(Csv);
            InsertSpells(connection, transaction, Csv, spells);
            InsertItemEffects(connection, transaction, Csv);
            if (fromFallback.Count > 0 && Fallback("ItemEffect") is not null)
            {
                // Only items that got nothing from Forever's own effect tables.
                using (var existing = connection.CreateCommand())
                {
                    existing.Transaction = transaction;
                    existing.CommandText = "SELECT DISTINCT item_id FROM item_effects";
                    using var reader = existing.ExecuteReader();
                    while (reader.Read()) fromFallback.Remove(reader.GetInt32(0));
                }
                InsertItemEffects(connection, transaction, t => Fallback(t) ?? Path.Combine(cacheDir, "missing", t + ".csv"), fromFallback);
            }
            InsertItemSets(connection, transaction, Csv);
            InsertClassesAndTalents(connection, transaction, Csv);
            var talentSource = ReplaceWithTraitTalents(connection, transaction, Csv, spells);
            FillRecipeRequirements(connection, transaction);
            var placeholders = InsertPlaceholderItems(connection, transaction, classes, icons);
            if (placeholders > 0) numbers.Warnings.Add($"{placeholders} crafted items have no item data yet (named after their recipe).");

            SourceImporter.ImportNames(connection, transaction, Csv);
            var enchantCount = 0;
            try { enchantCount = BuildEnchants(connection, transaction, Csv); }
            catch (Exception ex) { numbers.Warnings.Add("Enchants skipped: " + ex.Message); }
            var hiddenCount = 0;
            try { hiddenCount = MarkHiddenItems(connection, transaction); }
            catch (Exception ex) { numbers.Warnings.Add("Hidden items skipped: " + ex.Message); }
            var mapCount = 0;
            try { mapCount = BuildMaps(connection, transaction, Csv); }
            catch (Exception ex) { numbers.Warnings.Add("Maps skipped: " + ex.Message); }
            var sourceRows = 0;
            if (sourcesDump is not null && File.Exists(sourcesDump))
            {
                try { sourceRows = SourceImporter.Import(connection, transaction, sourcesDump, Csv); }
                catch (Exception ex) { numbers.Warnings.Add("Item sources skipped: " + ex.Message); }
            }
            ItemsMissingNumbers = numbers.Warnings;

            foreach (var (key, value) in new Dictionary<string, string>
            {
                ["build_version"] = version,
                ["product"] = product,
                ["schema_version"] = GameDb.SchemaVersion.ToString(),
                ["built_at_utc"] = DateTime.UtcNow.ToString("O"),
                ["notes"] = string.Join(" | ", numbers.Warnings),
                ["talent_source"] = talentSource,
                ["fallback_build"] = fallbackVersion ?? "",
                ["fallback_items"] = fallbackCount.ToString(),
                ["sources_dump"] = sourcesDump is null ? "" : Path.GetFileName(sourcesDump),
                ["source_rows"] = sourceRows.ToString(),
                ["enchants"] = enchantCount.ToString(),
                ["maps"] = mapCount.ToString(),
                ["hidden_items"] = hiddenCount.ToString(),
            })
            {
                using var meta = connection.CreateCommand();
                meta.Transaction = transaction;
                meta.CommandText = "INSERT INTO meta (key, value) VALUES ($k, $v)";
                meta.Parameters.AddWithValue("$k", key);
                meta.Parameters.AddWithValue("$v", value);
                meta.ExecuteNonQuery();
            }

            transaction.Commit();
            Execute(connection, "INSERT INTO items_fts(items_fts) VALUES('rebuild')");
            Execute(connection, "PRAGMA optimize");
        }

        SqliteConnection.ClearAllPools();
        File.Move(temp, outputPath, overwrite: true);
        return new BuildStats(total, kept, recipeCount);
    }

    // ---------------- items ----------------

    /// <summary>Season of Discovery items start around 200000; original Classic items are far below.</summary>
    private const int SeasonItemIdStart = 200000;

    private static (int Total, int Kept, HashSet<int> FromFallback) InsertItems(
        SqliteConnection connection, SqliteTransaction transaction, IReadOnlyList<string> sparseFiles,
        Dictionary<int, (int Class, int Subclass)> classes, Dictionary<int, int[]> budgets, ItemNumbers numbers,
        Dictionary<int, int> icons)
    {
        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO items (id, name, description, quality, item_level, required_level, class_id, subclass_id,
                               inventory_type, bonding, sell_price, buy_price, stack_size, stats, stats_raw,
                               delay_ms, dmg_min, dmg_max, dmg_school, armor, item_set, allowable_class, allowable_race,
                               max_count, required_skill, required_skill_rank, icon_file_id)
            VALUES ($id, $name, $description, $quality, $itemLevel, $requiredLevel, $classId, $subclassId,
                    $inventoryType, $bonding, $sellPrice, $buyPrice, $stackSize, $stats, $statsRaw,
                    $delay, $dmgMin, $dmgMax, $dmgSchool, $armor, $itemSet, $allowClass, $allowRace,
                    $maxCount, $reqSkill, $reqSkillRank, $icon)
            """;
        var id = insert.Parameters.Add("$id", SqliteType.Integer);
        var name = insert.Parameters.Add("$name", SqliteType.Text);
        var description = insert.Parameters.Add("$description", SqliteType.Text);
        var quality = insert.Parameters.Add("$quality", SqliteType.Integer);
        var itemLevel = insert.Parameters.Add("$itemLevel", SqliteType.Integer);
        var requiredLevel = insert.Parameters.Add("$requiredLevel", SqliteType.Integer);
        var classId = insert.Parameters.Add("$classId", SqliteType.Integer);
        var subclassId = insert.Parameters.Add("$subclassId", SqliteType.Integer);
        var inventoryType = insert.Parameters.Add("$inventoryType", SqliteType.Integer);
        var bonding = insert.Parameters.Add("$bonding", SqliteType.Integer);
        var sellPrice = insert.Parameters.Add("$sellPrice", SqliteType.Integer);
        var buyPrice = insert.Parameters.Add("$buyPrice", SqliteType.Integer);
        var stackSize = insert.Parameters.Add("$stackSize", SqliteType.Integer);
        var stats = insert.Parameters.Add("$stats", SqliteType.Text);
        var statsRaw = insert.Parameters.Add("$statsRaw", SqliteType.Text);
        var delay = insert.Parameters.Add("$delay", SqliteType.Integer);
        var dmgMin = insert.Parameters.Add("$dmgMin", SqliteType.Real);
        var dmgMax = insert.Parameters.Add("$dmgMax", SqliteType.Real);
        var dmgSchool = insert.Parameters.Add("$dmgSchool", SqliteType.Integer);
        var armor = insert.Parameters.Add("$armor", SqliteType.Integer);
        var itemSet = insert.Parameters.Add("$itemSet", SqliteType.Integer);
        var allowClass = insert.Parameters.Add("$allowClass", SqliteType.Integer);
        var allowRace = insert.Parameters.Add("$allowRace", SqliteType.Integer);
        var maxCount = insert.Parameters.Add("$maxCount", SqliteType.Integer);
        var reqSkill = insert.Parameters.Add("$reqSkill", SqliteType.Integer);
        var reqSkillRank = insert.Parameters.Add("$reqSkillRank", SqliteType.Integer);
        var icon = insert.Parameters.Add("$icon", SqliteType.Integer);

        int total = 0, kept = 0;
        var seen = new HashSet<int>();
        var fromFallback = new HashSet<int>();
        foreach (var (row, isFallback) in sparseFiles.SelectMany((file, index) => CsvTable.Read(file).Select(r => (r, index > 0))))
        {
            var itemId = row.GetInt("ID");
            // A later file only fills items the earlier ones lacked, and only items Forever still has.
            if (!seen.Add(itemId)) continue;
            if (isFallback && !classes.ContainsKey(itemId)) continue;
            // The Classic Era client also carries Season of Discovery items (ids from 200000 up). Forever's own
            // ItemSparse doesn't have them, and its Item table lists everything the shared client knows, so SoD
            // items would slip in through the fallback. Only original Classic items (below 200000) are filled in.
            if (isFallback && itemId >= SeasonItemIdStart) continue;
            total++;
            var itemName = row.Get("Display_lang").Trim();
            if (ItemFilters.IsJunkName(itemName)) continue;
            if (isFallback) fromFallback.Add(itemId);

            var (cls, sub) = classes.GetValueOrDefault(itemId);
            var level = row.GetInt("ItemLevel");
            var itemQuality = row.GetInt("OverallQualityID");
            var slot = row.GetInt("InventoryType");
            var (computed, raw) = ReadStats(row, Budget(budgets, level, itemQuality, slot, cls, sub));

            id.Value = itemId;
            name.Value = itemName;
            description.Value = GameText.Clean(row.Get("Description_lang"));
            quality.Value = itemQuality;
            itemLevel.Value = level;
            requiredLevel.Value = row.GetInt("RequiredLevel");
            classId.Value = cls;
            subclassId.Value = sub;
            inventoryType.Value = slot;
            bonding.Value = row.GetInt("Bonding");
            sellPrice.Value = row.GetInt("SellPrice");
            buyPrice.Value = row.GetInt("BuyPrice");
            stackSize.Value = row.GetInt("Stackable");
            stats.Value = computed;
            statsRaw.Value = raw;

            var itemDelay = row.GetInt("ItemDelay");
            var (low, high) = numbers.WeaponDamage(level, itemQuality, slot, cls, sub, row.GetInt("Flags_1"),
                itemDelay, row.GetDouble("DmgVariance"));
            // Classic Era rows store weapon damage directly; use it when present.
            if (row.GetDouble("MaxDamage_0") > 0) (low, high) = (row.GetDouble("MinDamage_0"), row.GetDouble("MaxDamage_0"));
            delay.Value = itemDelay;
            dmgMin.Value = low;
            dmgMax.Value = high;
            dmgSchool.Value = row.GetInt("DamageType");
            // Classic Era rows store armor directly (Resistances_0).
            armor.Value = row.GetInt("Resistances_0") > 0 ? row.GetInt("Resistances_0") : numbers.Armor(level, itemQuality, slot, cls, sub);
            itemSet.Value = row.GetInt("ItemSet");
            allowClass.Value = row.Has("AllowableClass") ? row.GetInt("AllowableClass") : -1;
            allowRace.Value = row.Has("AllowableRace_0") ? row.GetInt("AllowableRace_0") : -1;
            maxCount.Value = row.GetInt("MaxCount");
            reqSkill.Value = row.GetInt("RequiredSkill");
            reqSkillRank.Value = row.GetInt("RequiredSkillRank");
            icon.Value = icons.GetValueOrDefault(itemId);
            insert.ExecuteNonQuery();
            kept++;
        }
        return (total, kept, fromFallback);
    }

    // ---------------- stat budget ----------------
    // StatPercentEditor_n is a share of the item's stat budget in hundredths of a percent (7500 = 75%).
    // The budget comes from RandPropPoints by item level, quality group and slot group.
    // Same method TrinityCore uses; unverified for this client until compared with in-game tooltips.

    private static Dictionary<int, int[]> ReadBudgets(string path)
    {
        string[] groups = ["Epic", "Superior", "Good"];
        var result = new Dictionary<int, int[]>();
        foreach (var row in CsvTable.Read(path))
        {
            var values = new int[15];
            for (var g = 0; g < 3; g++)
                for (var s = 0; s < 5; s++)
                    values[g * 5 + s] = row.GetInt($"{groups[g]}_{s}");
            result[row.GetInt("ID")] = values;
        }
        return result;
    }

    private static int Budget(Dictionary<int, int[]> budgets, int itemLevel, int quality, int inventoryType, int classId, int subclassId)
    {
        var group = quality switch
        {
            2 => 2,           // uncommon uses the Good column
            3 or 7 => 1,      // rare and heirloom use Superior
            4 or 5 or 6 => 0, // epic and above use Epic
            _ => -1,
        };

        var slot = inventoryType switch
        {
            1 or 4 or 5 or 7 or 15 or 17 or 20 or 25 => 0,
            26 => classId == 2 && subclassId == 19 ? 3 : 0,
            13 or 21 or 22 => 3,
            3 or 6 or 8 or 10 or 12 => 1,
            2 or 9 or 11 or 14 or 16 or 23 => 2,
            28 => 4,
            _ => -1,
        };

        if (group < 0 || slot < 0 || !budgets.TryGetValue(itemLevel, out var row)) return 0;
        return row[group * 5 + slot];
    }

    private static (string Computed, string Raw) ReadStats(CsvRow row, int budget)
    {
        var computed = new List<string>();
        var raw = new List<string>();
        for (var i = 0; i < 10; i++)
        {
            var type = row.GetInt($"StatModifier_bonusStat_{i}");
            var percent = row.GetInt($"StatPercentEditor_{i}");
            if (type < 0) continue;

            // Classic Era rows carry the finished amount instead of a budget share.
            var amount = row.GetInt($"StatModifier_bonusAmount_{i}");
            if (percent == 0 && amount != 0)
            {
                computed.Add($"{type}:{amount}");
                raw.Add($"{type}:={amount}");
                continue;
            }
            if (percent == 0) continue;

            raw.Add($"{type}:{percent}");
            if (budget <= 0) continue;
            var value = (int)Math.Floor(percent * (double)budget * 0.0001 + 0.5);
            if (value != 0) computed.Add($"{type}:{value}");
        }

        // Classic Era resistances: Resistances_1..6 = holy, fire, nature, frost, shadow, arcane -> item stat types.
        int[] resistStat = [0, 53, 51, 55, 52, 54, 56];
        for (var school = 1; school <= 6; school++)
        {
            var value = row.GetInt($"Resistances_{school}");
            if (value != 0) computed.Add($"{resistStat[school]}:{value}");
        }
        return (string.Join(',', computed), string.Join(',', raw));
    }

    // ---------------- recipes ----------------

    private static int InsertRecipes(SqliteConnection connection, SqliteTransaction transaction, Func<string, string> csv)
    {
        // Profession-type skill lines only: 11 = primary professions, 9 = secondary skills (Cooking, First Aid, ...).
        var lines = new Dictionary<int, (string Name, int Category)>();
        foreach (var row in CsvTable.Read(csv("SkillLine")))
            lines[row.GetInt("ID")] = (row.Get("DisplayName_lang"), row.GetInt("CategoryID"));

        var abilities = new Dictionary<int, (int Skill, int Min, int Low, int High)>();
        foreach (var row in CsvTable.Read(csv("SkillLineAbility")))
        {
            var skill = row.GetInt("SkillLine");
            if (!lines.TryGetValue(skill, out var line)) continue;
            if (line.Category is not (9 or 11)) continue;
            if (line.Name.Contains("[DNT]", StringComparison.OrdinalIgnoreCase)) continue;
            abilities.TryAdd(row.GetInt("Spell"), (skill, row.GetInt("MinSkillLineRank"),
                row.GetInt("TrivialSkillLineRankLow"), row.GetInt("TrivialSkillLineRankHigh")));
        }

        // Effect 24 = "create item".
        var created = new Dictionary<int, int>();
        var createdCount = new Dictionary<int, int>();
        foreach (var row in CsvTable.Read(csv("SpellEffect")))
        {
            var spell = row.GetInt("SpellID");
            if (row.GetInt("Effect") != 24 || !abilities.ContainsKey(spell)) continue;
            var item = row.GetInt("EffectItemType");
            if (item > 0 && created.TryAdd(spell, item))
                createdCount[spell] = Math.Max(1, (int)Math.Round(row.GetDouble("EffectBasePointsF")));
        }

        var reagents = new Dictionary<int, List<(int Item, int Quantity)>>();
        foreach (var row in CsvTable.Read(csv("SpellReagents")))
        {
            var spell = row.GetInt("SpellID");
            if (!abilities.ContainsKey(spell)) continue;
            for (var i = 0; i < 8; i++)
            {
                var item = row.GetInt($"Reagent_{i}");
                var quantity = row.GetInt($"ReagentCount_{i}");
                if (item <= 0 || quantity <= 0) continue;
                if (!reagents.TryGetValue(spell, out var list)) reagents[spell] = list = [];
                list.Add((item, quantity));
            }
        }

        var names = new Dictionary<int, string>();
        foreach (var row in CsvTable.Read(csv("SpellName")))
        {
            var spell = row.GetInt("ID");
            if (abilities.ContainsKey(spell)) names[spell] = row.Get("Name_lang").Trim();
        }

        using var insertRecipe = connection.CreateCommand();
        insertRecipe.Transaction = transaction;
        insertRecipe.CommandText = """
            INSERT INTO recipes (spell_id, name, skill_line_id, profession, min_skill, trivial_low, trivial_high, created_item_id, created_count)
            VALUES ($spell, $name, $skill, $profession, $min, $low, $high, $item, $count)
            """;
        var pSpell = insertRecipe.Parameters.Add("$spell", SqliteType.Integer);
        var pName = insertRecipe.Parameters.Add("$name", SqliteType.Text);
        var pSkill = insertRecipe.Parameters.Add("$skill", SqliteType.Integer);
        var pProfession = insertRecipe.Parameters.Add("$profession", SqliteType.Text);
        var pMin = insertRecipe.Parameters.Add("$min", SqliteType.Integer);
        var pLow = insertRecipe.Parameters.Add("$low", SqliteType.Integer);
        var pHigh = insertRecipe.Parameters.Add("$high", SqliteType.Integer);
        var pItem = insertRecipe.Parameters.Add("$item", SqliteType.Integer);
        var pCount = insertRecipe.Parameters.Add("$count", SqliteType.Integer);

        using var insertReagent = connection.CreateCommand();
        insertReagent.Transaction = transaction;
        insertReagent.CommandText = "INSERT INTO recipe_reagents (spell_id, item_id, quantity) VALUES ($spell, $item, $quantity)";
        var rSpell = insertReagent.Parameters.Add("$spell", SqliteType.Integer);
        var rItem = insertReagent.Parameters.Add("$item", SqliteType.Integer);
        var rQuantity = insertReagent.Parameters.Add("$quantity", SqliteType.Integer);

        // A recipe is a profession spell that creates an item or uses reagents. That drops the profession's own
        // base spells, racial abilities, Riding, Fishing and the like, and keeps enchants (reagents, no item).
        var count = 0;
        foreach (var (spell, ability) in abilities)
        {
            created.TryGetValue(spell, out var createdItem);
            reagents.TryGetValue(spell, out var list);
            if (createdItem == 0 && list is null) continue;
            if (!names.TryGetValue(spell, out var name) || name.Length == 0) continue;

            pSpell.Value = spell;
            pName.Value = name;
            pSkill.Value = ability.Skill;
            pProfession.Value = lines[ability.Skill].Name;
            pMin.Value = ability.Min;
            pLow.Value = ability.Low;
            pHigh.Value = ability.High;
            pItem.Value = createdItem;
            pCount.Value = createdCount.GetValueOrDefault(spell, 1);
            insertRecipe.ExecuteNonQuery();
            count++;

            foreach (var (item, quantity) in list ?? Enumerable.Empty<(int Item, int Quantity)>())
            {
                rSpell.Value = spell;
                rItem.Value = item;
                rQuantity.Value = quantity;
                insertReagent.ExecuteNonQuery();
            }
        }
        return count;
    }

    /// <summary>
    /// The client doesn't store the skill needed to learn a recipe (MinSkillLineRank is 1 for everything).
    /// Recipes taught by an item (Pattern:, Plans:, Formula: ...) get the item's RequiredSkillRank. Trainer
    /// recipes get an estimate of 20 below the yellow point, marked as such. TrivialSkillLineRankLow is where
    /// the recipe turns yellow and TrivialSkillLineRankHigh where it turns grey; green is halfway.
    /// </summary>
    private static void FillRecipeRequirements(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE recipes SET
                taught_by = COALESCE((
                    SELECT MIN(e.item_id) FROM item_effects e JOIN items i ON i.id = e.item_id
                    WHERE e.spell_id = recipes.spell_id AND e.trigger_type = 6 AND i.required_skill_rank > 0), 0);
            UPDATE recipes SET
                min_skill = (SELECT MIN(i.required_skill_rank) FROM item_effects e JOIN items i ON i.id = e.item_id
                             WHERE e.spell_id = recipes.spell_id AND e.trigger_type = 6 AND i.required_skill_rank > 0),
                skill_source = 'recipe'
            WHERE taught_by > 0;
            -- Trainer recipes: about 20 below yellow. When yellow and grey are the same number (smelting, curing,
            -- some engineering parts) that number is the skill the recipe is learned at.
            UPDATE recipes SET
                min_skill = CASE WHEN trivial_low = trivial_high THEN trivial_low ELSE MAX(1, trivial_low - 20) END,
                skill_source = 'estimate'
            WHERE taught_by = 0 AND trivial_low > 0;
            """;
        command.ExecuteNonQuery();
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Newer items (many of Forever's own) leave Item.IconFileDataID at 0; the client shows the icon of their default
    /// appearance instead: ItemModifiedAppearance (item → appearance) → ItemAppearance.DefaultIconFileDataID.
    /// </summary>
    private static void FillAppearanceIcons(Dictionary<int, int> icons, Func<string, string> csv)
    {
        if (!File.Exists(csv("ItemModifiedAppearance")) || !File.Exists(csv("ItemAppearance"))) return;
        var appearanceIcon = new Dictionary<int, int>();
        foreach (var row in CsvTable.Read(csv("ItemAppearance")))
            if (row.GetInt("DefaultIconFileDataID") > 0) appearanceIcon[row.GetInt("ID")] = row.GetInt("DefaultIconFileDataID");

        // The plain version first (modifier 0, lowest order).
        foreach (var row in CsvTable.Read(csv("ItemModifiedAppearance"))
                     .OrderBy(r => r.GetInt("ItemAppearanceModifierID")).ThenBy(r => r.GetInt("OrderIndex")))
        {
            var item = row.GetInt("ItemID");
            if (icons.TryGetValue(item, out var current) && current > 0) continue;
            if (appearanceIcon.TryGetValue(row.GetInt("ItemAppearanceID"), out var icon)) icons[item] = icon;
        }
    }
}
