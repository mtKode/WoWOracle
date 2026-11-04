using Microsoft.Data.Sqlite;

namespace WowCompanion.Data;

public static partial class GameDatabaseBuilder
{
    private const string SpellSchema = """

        CREATE TABLE spells (
            id INTEGER PRIMARY KEY,
            name TEXT NOT NULL,
            rank TEXT NOT NULL,
            description TEXT NOT NULL,
            aura_description TEXT NOT NULL,
            description_raw TEXT NOT NULL,
            school_mask INTEGER NOT NULL,
            cast_ms INTEGER NOT NULL,
            duration_ms INTEGER NOT NULL,
            range_max REAL NOT NULL,
            range_name TEXT NOT NULL,
            cooldown_ms INTEGER NOT NULL,
            category_cooldown_ms INTEGER NOT NULL,
            gcd_ms INTEGER NOT NULL,
            power_type INTEGER NOT NULL,
            power_cost INTEGER NOT NULL,
            power_cost_pct INTEGER NOT NULL,
            power_per_second INTEGER NOT NULL,
            base_level INTEGER NOT NULL,
            spell_level INTEGER NOT NULL,
            max_level INTEGER NOT NULL,
            proc_chance INTEGER NOT NULL,
            proc_charges INTEGER NOT NULL,
            max_stacks INTEGER NOT NULL,
            class_id INTEGER NOT NULL,
            is_class_spell INTEGER NOT NULL,
            skill_line TEXT NOT NULL,
            icon_file_id INTEGER NOT NULL,
            class_mask TEXT NOT NULL DEFAULT '0,0,0,0'
        );
        CREATE INDEX spells_class ON spells (is_class_spell, class_id);
        CREATE INDEX spells_name ON spells (name);
        CREATE TABLE spell_effects (
            spell_id INTEGER NOT NULL,
            idx INTEGER NOT NULL,
            effect INTEGER NOT NULL,
            aura INTEGER NOT NULL,
            base_points REAL NOT NULL,
            per_level REAL NOT NULL,
            variance REAL NOT NULL,
            sp_coef REAL NOT NULL,
            ap_coef REAL NOT NULL,
            period_ms INTEGER NOT NULL,
            trigger_spell INTEGER NOT NULL,
            misc0 INTEGER NOT NULL,
            misc1 INTEGER NOT NULL,
            chain_targets INTEGER NOT NULL,
            class_mask TEXT NOT NULL,
            target INTEGER NOT NULL
        );
        CREATE INDEX spell_effects_spell ON spell_effects (spell_id);
        """;

    // Class skill lines that are not really the class's own spells.
    private static readonly string[] NotClassSpellLines =
        ["Pet -", "Mounts", "Companions", "Engraving", "Runes", "Meditation", "Explorer Imp", "Beast Training"];

    // ---------------- load ----------------

    private static Dictionary<int, SpellFacts> LoadSpells(Func<string, string> csv)
    {
        var spells = new Dictionary<int, SpellFacts>();
        foreach (var row in CsvTable.Read(csv("SpellName")))
        {
            var name = row.Get("Name_lang").Trim();
            if (name.Length == 0) continue;
            var id = row.GetInt("ID");
            spells[id] = new SpellFacts { Id = id, Name = name };
        }

        foreach (var row in CsvTable.Read(csv("Spell")))
        {
            if (!spells.TryGetValue(row.GetInt("ID"), out var s)) continue;
            s.Rank = row.Get("NameSubtext_lang").Trim();
            s.Description = row.Get("Description_lang").Trim();
            s.AuraDescription = row.Get("AuraDescription_lang").Trim();
        }

        var castTimes = CsvTable.Read(csv("SpellCastTimes")).ToDictionary(r => r.GetInt("ID"), r => r.GetInt("Base"));
        var durations = CsvTable.Read(csv("SpellDuration")).ToDictionary(r => r.GetInt("ID"), r => r.GetInt("Duration"));
        var ranges = CsvTable.Read(csv("SpellRange")).ToDictionary(
            r => r.GetInt("ID"),
            r => (Max: Math.Max(r.GetDouble("RangeMax_0"), r.GetDouble("RangeMax_1")), Name: r.Get("DisplayName_lang")));

        // Per-difficulty tables: difficulty 0 is the normal world version; take it over anything else.
        foreach (var row in BaseDifficulty(csv("SpellMisc")))
        {
            if (!spells.TryGetValue(row.GetInt("SpellID"), out var s)) continue;
            s.CastMs = castTimes.GetValueOrDefault(row.GetInt("CastingTimeIndex"));
            s.DurationMs = durations.GetValueOrDefault(row.GetInt("DurationIndex"));
            s.RangeIndex = row.GetInt("RangeIndex");
            if (ranges.TryGetValue(s.RangeIndex, out var range)) (s.RangeMax, s.RangeName) = (range.Max, range.Name);
            s.SchoolMask = row.GetInt("SchoolMask");
            s.IconFileId = row.GetInt("SpellIconFileDataID");
        }

        var powerOrder = new Dictionary<int, int>();
        foreach (var row in CsvTable.Read(csv("SpellPower")))
        {
            var spellId = row.GetInt("SpellID");
            if (!spells.TryGetValue(spellId, out var s)) continue;
            var order = row.GetInt("OrderIndex");
            if (powerOrder.TryGetValue(spellId, out var seen) && seen <= order) continue;
            powerOrder[spellId] = order;
            s.PowerType = row.GetInt("PowerType");
            s.PowerCost = row.GetInt("ManaCost");
            s.PowerCostPct = (int)Math.Round(row.GetDouble("PowerCostPct"));
            s.PowerPerSecond = row.GetInt("ManaPerSecond");
        }

        foreach (var row in BaseDifficulty(csv("SpellCooldowns")))
        {
            if (!spells.TryGetValue(row.GetInt("SpellID"), out var s)) continue;
            s.CooldownMs = row.GetInt("RecoveryTime");
            s.CategoryCooldownMs = row.GetInt("CategoryRecoveryTime");
            s.GcdMs = row.GetInt("StartRecoveryTime");
        }

        foreach (var row in BaseDifficulty(csv("SpellLevels")))
        {
            if (!spells.TryGetValue(row.GetInt("SpellID"), out var s)) continue;
            s.BaseLevel = row.GetInt("BaseLevel");
            s.SpellLevel = row.GetInt("SpellLevel");
            s.MaxLevel = row.GetInt("MaxLevel");
        }

        foreach (var row in BaseDifficulty(csv("SpellAuraOptions")))
        {
            if (!spells.TryGetValue(row.GetInt("SpellID"), out var s)) continue;
            s.ProcChance = row.GetInt("ProcChance");
            s.ProcCharges = row.GetInt("ProcCharges");
            s.MaxStacks = row.GetInt("CumulativeAura");
        }

        foreach (var row in CsvTable.Read(csv("SpellClassOptions")))
        {
            if (!spells.TryGetValue(row.GetInt("SpellID"), out var s)) continue;
            s.ClassSet = row.GetInt("SpellClassSet");
            s.ClassMask = $"{row.GetInt("SpellClassMask_0")},{row.GetInt("SpellClassMask_1")},{row.GetInt("SpellClassMask_2")},{row.GetInt("SpellClassMask_3")}";
        }

        foreach (var row in BaseDifficulty(csv("SpellEffect")))
        {
            if (!spells.TryGetValue(row.GetInt("SpellID"), out var s)) continue;
            var index = row.GetInt("EffectIndex");
            if (s.Effect(index) is not null) continue;
            s.Effects.Add(new EffectFacts(
                index,
                row.GetInt("Effect"),
                row.GetInt("EffectAura"),
                row.GetDouble("EffectBasePointsF"),
                row.GetDouble("EffectRealPointsPerLevel"),
                row.GetDouble("Variance"),
                row.GetDouble("EffectBonusCoefficient"),
                row.GetDouble("BonusCoefficientFromAP"),
                row.GetInt("EffectAuraPeriod"),
                row.GetInt("EffectTriggerSpell"),
                row.GetInt("EffectMiscValue_0"),
                row.GetInt("EffectMiscValue_1"),
                row.GetInt("EffectChainTargets"),
                row.GetInt("EffectRadiusIndex_0"),
                row.GetDouble("EffectAmplitude"),
                row.GetDouble("EffectPointsPerResource"),
                row.GetInt("EffectSpellClassMask_0"),
                row.GetInt("EffectSpellClassMask_1"),
                row.GetInt("EffectSpellClassMask_2"),
                row.GetInt("EffectSpellClassMask_3"),
                row.GetInt("ImplicitTarget_0")));
        }
        foreach (var s in spells.Values) s.Effects.Sort((a, b) => a.Index.CompareTo(b.Index));

        return spells;
    }

    /// <summary>Rows sorted so difficulty 0 comes first; callers keep the first row per spell.</summary>
    private static IEnumerable<CsvRow> BaseDifficulty(string path)
    {
        var seen = new HashSet<(int Spell, int Index)>();
        var hasIndex = CsvTable.ReadHeader(path).Contains("EffectIndex", StringComparer.OrdinalIgnoreCase);
        var hasDifficulty = CsvTable.ReadHeader(path).Contains("DifficultyID", StringComparer.OrdinalIgnoreCase);
        var rows = CsvTable.Read(path);
        if (hasDifficulty) rows = rows.OrderBy(r => r.GetInt("DifficultyID") == 0 ? 0 : 1);
        foreach (var row in rows)
        {
            var key = (row.GetInt("SpellID"), hasIndex ? row.GetInt("EffectIndex") : 0);
            if (seen.Add(key)) yield return row;
        }
    }

    // ---------------- write ----------------

    private static void InsertSpells(
        SqliteConnection connection, SqliteTransaction transaction, Func<string, string> csv, Dictionary<int, SpellFacts> spells)
    {
        // Class from SpellClassSet (via ChrClasses), class spells from the class skill lines.
        var classBySet = new Dictionary<int, int>();
        foreach (var row in CsvTable.Read(csv("ChrClasses")))
        {
            var set = row.GetInt("SpellClassSet");
            if (set > 0) classBySet.TryAdd(set, row.GetInt("ID"));
        }

        var lines = new Dictionary<int, string>();
        foreach (var row in CsvTable.Read(csv("SkillLine")))
        {
            if (row.GetInt("CategoryID") != 7) continue;
            var name = row.Get("DisplayName_lang");
            if (NotClassSpellLines.Any(x => name.StartsWith(x, StringComparison.OrdinalIgnoreCase))) continue;
            lines[row.GetInt("ID")] = name;
        }

        var classSpells = new Dictionary<int, (string Line, int ClassFromMask)>();
        foreach (var row in CsvTable.Read(csv("SkillLineAbility")))
        {
            if (!lines.TryGetValue(row.GetInt("SkillLine"), out var line)) continue;
            var mask = row.GetInt("ClassMask");
            var fromMask = 0;
            for (var bit = 0; bit < 31 && mask != 0; bit++)
                if ((mask & (1 << bit)) != 0) { fromMask = bit + 1; break; }
            classSpells.TryAdd(row.GetInt("Spell"), (line, fromMask));
        }

        Func<int, double>? radius = null;
        var radiusPath = csv("SpellRadius");
        if (File.Exists(radiusPath))
        {
            var radii = CsvTable.Read(radiusPath).ToDictionary(r => r.GetInt("ID"), r => r.GetDouble("Radius"));
            radius = index => radii.GetValueOrDefault(index);
        }
        var text = new SpellText(id => spells.GetValueOrDefault(id), radius);

        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO spells (id, name, rank, description, aura_description, description_raw, school_mask, cast_ms,
                duration_ms, range_max, range_name, cooldown_ms, category_cooldown_ms, gcd_ms, power_type, power_cost,
                power_cost_pct, power_per_second, base_level, spell_level, max_level, proc_chance, proc_charges,
                max_stacks, class_id, is_class_spell, skill_line, icon_file_id, class_mask)
            VALUES ($id, $name, $rank, $desc, $aura, $raw, $school, $cast, $duration, $rangeMax, $rangeName, $cd, $ccd,
                $gcd, $powerType, $cost, $costPct, $perSecond, $baseLevel, $spellLevel, $maxLevel, $procChance,
                $procCharges, $stacks, $classId, $isClass, $line, $icon, $mask)
            """;
        string[] names =
        [
            "$id", "$name", "$rank", "$desc", "$aura", "$raw", "$school", "$cast", "$duration", "$rangeMax", "$rangeName",
            "$cd", "$ccd", "$gcd", "$powerType", "$cost", "$costPct", "$perSecond", "$baseLevel", "$spellLevel",
            "$maxLevel", "$procChance", "$procCharges", "$stacks", "$classId", "$isClass", "$line", "$icon", "$mask",
        ];
        // No fixed type: each value binds as what it is, so decimals never pass through the PC's culture.
        var p = names.ToDictionary(n => n, n => insert.Parameters.AddWithValue(n, DBNull.Value));

        using var effectInsert = connection.CreateCommand();
        effectInsert.Transaction = transaction;
        effectInsert.CommandText = """
            INSERT INTO spell_effects (spell_id, idx, effect, aura, base_points, per_level, variance, sp_coef, ap_coef,
                period_ms, trigger_spell, misc0, misc1, chain_targets, class_mask, target)
            VALUES ($s, $i, $e, $a, $bp, $pl, $v, $sp, $ap, $per, $trig, $m0, $m1, $chain, $mask, $t)
            """;
        string[] effectNames = ["$s", "$i", "$e", "$a", "$bp", "$pl", "$v", "$sp", "$ap", "$per", "$trig", "$m0", "$m1", "$chain", "$mask", "$t"];
        var e = effectNames.ToDictionary(n => n, n => effectInsert.Parameters.AddWithValue(n, DBNull.Value));

        foreach (var s in spells.Values)
        {
            classSpells.TryGetValue(s.Id, out var classInfo);
            var isClass = classInfo.Line is not null;
            var classId = classBySet.GetValueOrDefault(s.ClassSet);
            if (classId == 0 && isClass) classId = classInfo.ClassFromMask;

            p["$id"].Value = s.Id;
            p["$name"].Value = s.Name;
            p["$rank"].Value = s.Rank;
            p["$desc"].Value = Safe(() => text.Render(s.Description, s), s.Description);
            p["$aura"].Value = Safe(() => text.Render(s.AuraDescription, s), s.AuraDescription);
            p["$raw"].Value = s.Description;
            p["$school"].Value = s.SchoolMask;
            p["$cast"].Value = s.CastMs;
            p["$duration"].Value = s.DurationMs;
            p["$rangeMax"].Value = s.RangeMax;
            p["$rangeName"].Value = s.RangeName;
            p["$cd"].Value = s.CooldownMs;
            p["$ccd"].Value = s.CategoryCooldownMs;
            p["$gcd"].Value = s.GcdMs;
            p["$powerType"].Value = s.PowerType;
            p["$cost"].Value = s.PowerCost;
            p["$costPct"].Value = s.PowerCostPct;
            p["$perSecond"].Value = s.PowerPerSecond;
            p["$baseLevel"].Value = s.BaseLevel;
            p["$spellLevel"].Value = s.SpellLevel;
            p["$maxLevel"].Value = s.MaxLevel;
            p["$procChance"].Value = s.ProcChance;
            p["$procCharges"].Value = s.ProcCharges;
            p["$stacks"].Value = s.MaxStacks;
            p["$classId"].Value = classId;
            p["$isClass"].Value = isClass ? 1 : 0;
            p["$line"].Value = classInfo.Line ?? "";
            p["$icon"].Value = s.IconFileId;
            p["$mask"].Value = s.ClassMask;
            insert.ExecuteNonQuery();

            foreach (var x in s.Effects)
            {
                e["$s"].Value = s.Id;
                e["$i"].Value = x.Index;
                e["$e"].Value = x.Effect;
                e["$a"].Value = x.Aura;
                e["$bp"].Value = x.BasePoints;
                e["$pl"].Value = x.PerLevel;
                e["$v"].Value = x.Variance;
                e["$sp"].Value = x.SpellCoefficient;
                e["$ap"].Value = x.ApCoefficient;
                e["$per"].Value = x.PeriodMs;
                e["$trig"].Value = x.TriggerSpell;
                e["$m0"].Value = x.Misc0;
                e["$m1"].Value = x.Misc1;
                e["$chain"].Value = x.ChainTargets;
                e["$mask"].Value = $"{x.Mask0},{x.Mask1},{x.Mask2},{x.Mask3}";
                e["$t"].Value = x.Target0;
                effectInsert.ExecuteNonQuery();
            }
        }
    }

    /// <summary>A description that trips the renderer keeps its raw text instead of failing the whole build.</summary>
    private static string Safe(Func<string> render, string fallback)
    {
        try { return render(); }
        catch (Exception) { return fallback; }
    }

    // ---------------- item effects and sets ----------------

    /// <param name="onlyItems">When set, only effects of these items are added (the Classic Era fallback pass).</param>
    private static void InsertItemEffects(
        SqliteConnection connection, SqliteTransaction transaction, Func<string, string> csv, HashSet<int>? onlyItems = null)
    {
        // Which item owns an effect lives in ItemXItemEffect on newer clients, or in ItemEffect.ParentItemID on older ones.
        var owner = new Dictionary<int, int>();
        var linkPath = csv("ItemXItemEffect");
        var effectPath = csv("ItemEffect");
        if (File.Exists(linkPath))
        {
            var effectColumn = CsvTable.FindColumn(linkPath, "ItemEffect") ?? "ItemEffectID";
            var itemColumn = CsvTable.ReadHeader(linkPath).FirstOrDefault(h => h.Equals("ItemID", StringComparison.OrdinalIgnoreCase)) ?? "ItemID";
            foreach (var row in CsvTable.Read(linkPath)) owner.TryAdd(row.GetInt(effectColumn), row.GetInt(itemColumn));
        }
        var parentColumn = CsvTable.FindColumn(effectPath, "Parent");

        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO item_effects (item_id, slot, trigger_type, spell_id, charges, cooldown_ms, category_cooldown_ms)
            VALUES ($item, $slot, $trigger, $spell, $charges, $cd, $ccd)
            """;
        var pItem = insert.Parameters.Add("$item", SqliteType.Integer);
        var pSlot = insert.Parameters.Add("$slot", SqliteType.Integer);
        var pTrigger = insert.Parameters.Add("$trigger", SqliteType.Integer);
        var pSpell = insert.Parameters.Add("$spell", SqliteType.Integer);
        var pCharges = insert.Parameters.Add("$charges", SqliteType.Integer);
        var pCd = insert.Parameters.Add("$cd", SqliteType.Integer);
        var pCcd = insert.Parameters.Add("$ccd", SqliteType.Integer);

        foreach (var row in CsvTable.Read(effectPath))
        {
            var item = parentColumn is not null ? row.GetInt(parentColumn) : owner.GetValueOrDefault(row.GetInt("ID"));
            var spell = row.GetInt("SpellID");
            if (item <= 0 || spell <= 0) continue;
            if (onlyItems is not null && !onlyItems.Contains(item)) continue;
            pItem.Value = item;
            pSlot.Value = row.GetInt("LegacySlotIndex");
            pTrigger.Value = row.GetInt("TriggerType");
            pSpell.Value = spell;
            pCharges.Value = row.GetInt("Charges");
            pCd.Value = row.GetInt("CoolDownMSec");
            pCcd.Value = row.GetInt("CategoryCoolDownMSec");
            insert.ExecuteNonQuery();
        }
    }

    private static void InsertItemSets(SqliteConnection connection, SqliteTransaction transaction, Func<string, string> csv)
    {
        using var set = connection.CreateCommand();
        set.Transaction = transaction;
        set.CommandText = "INSERT INTO item_sets (id, name, required_skill, required_skill_rank) VALUES ($id, $name, $skill, $rank)";
        var sId = set.Parameters.Add("$id", SqliteType.Integer);
        var sName = set.Parameters.Add("$name", SqliteType.Text);
        var sSkill = set.Parameters.Add("$skill", SqliteType.Integer);
        var sRank = set.Parameters.Add("$rank", SqliteType.Integer);

        using var piece = connection.CreateCommand();
        piece.Transaction = transaction;
        piece.CommandText = "INSERT INTO item_set_items (set_id, item_id, position) VALUES ($set, $item, $pos)";
        var pSet = piece.Parameters.Add("$set", SqliteType.Integer);
        var pItem = piece.Parameters.Add("$item", SqliteType.Integer);
        var pPos = piece.Parameters.Add("$pos", SqliteType.Integer);

        foreach (var row in CsvTable.Read(csv("ItemSet")))
        {
            var id = row.GetInt("ID");
            sId.Value = id;
            sName.Value = row.Get("Name_lang").Trim();
            sSkill.Value = row.GetInt("RequiredSkill");
            sRank.Value = row.GetInt("RequiredSkillRank");
            set.ExecuteNonQuery();

            for (var i = 0; i < 17; i++)
            {
                var item = row.GetInt($"ItemID_{i}");
                if (item <= 0) continue;
                pSet.Value = id;
                pItem.Value = item;
                pPos.Value = i;
                piece.ExecuteNonQuery();
            }
        }

        using var bonus = connection.CreateCommand();
        bonus.Transaction = transaction;
        bonus.CommandText = "INSERT INTO item_set_bonuses (set_id, threshold, spell_id) VALUES ($set, $threshold, $spell)";
        var bSet = bonus.Parameters.Add("$set", SqliteType.Integer);
        var bThreshold = bonus.Parameters.Add("$threshold", SqliteType.Integer);
        var bSpell = bonus.Parameters.Add("$spell", SqliteType.Integer);
        foreach (var row in CsvTable.Read(csv("ItemSetSpell")))
        {
            bSet.Value = row.GetInt("ItemSetID");
            bThreshold.Value = row.GetInt("Threshold");
            bSpell.Value = row.GetInt("SpellID");
            bonus.ExecuteNonQuery();
        }
    }
}
