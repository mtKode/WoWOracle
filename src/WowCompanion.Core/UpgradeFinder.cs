using Microsoft.Data.Sqlite;
using WowCompanion.Core.Calc;

namespace WowCompanion.Core;

/// <summary>Where an item mostly comes from, boiled down to one line and a difficulty bucket.</summary>
public enum SourceKind { Unknown, Quest, Vendor, Crafted, Dungeon, Raid, WorldDrop, Other }

public sealed record SourceSummary(SourceKind Kind, string Text);

public sealed record UpgradeOption(
    int ItemId, string Name, int Quality, int IconId, int ItemLevel, int RequiredLevel,
    double Score, double CurrentScore, SourceSummary Source, string StatLine)
{
    /// <summary>Change vs what's worn, in percent; null when the slot is empty.</summary>
    public double? Gain => CurrentScore > 0 ? (Score / CurrentScore - 1) * 100 : null;
}

public sealed record UpgradeRequest(
    int Slot, int ClassId, int Level, IReadOnlyList<GearItem> SetItems, GearItem? Current,
    bool IncludeRaid = true, bool IncludeDungeon = true, bool IncludeWorld = true, int Limit = 25);

/// <summary>A quest reward that beats what the set wears in <see cref="Slot"/>.</summary>
public sealed record RewardUpgrade(UpgradeOption Option, int Slot, int QuestId, string QuestTitle);

public sealed record UpgradeResult(IReadOnlyList<UpgradeOption> Options, IReadOnlyList<string> WeightedBy, string Note);

/// <summary>
/// Alternatives for one gear slot. Items are scored by the stats the gear set already leans on (each stat priced by
/// roughly what it costs on an item, so 1% crit counts like 14 Strength). Several options are returned, not just the
/// best, each with where it comes from, because the best piece is often a raid drop.
/// </summary>
public sealed class UpgradeFinder(string databasePath, CalcDataService calcData)
{
    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = databasePath,
        Mode = SqliteOpenMode.ReadOnly,
        Pooling = false,
    }.ToString();

    /// <summary>Rough item-budget price per point of each stat.</summary>
    private static readonly Dictionary<Stat, double> Budget = new()
    {
        [Stat.Strength] = 1, [Stat.Agility] = 1, [Stat.Stamina] = 1, [Stat.Intellect] = 1, [Stat.Spirit] = 1,
        [Stat.AttackPower] = 0.5, [Stat.RangedAttackPower] = 0.4,
        [Stat.SpellPower] = 0.86, [Stat.Healing] = 0.45,
        [Stat.FirePower] = 0.7, [Stat.FrostPower] = 0.7, [Stat.ShadowPower] = 0.7, [Stat.ArcanePower] = 0.7,
        [Stat.NaturePower] = 0.7, [Stat.HolyPower] = 0.7,
        [Stat.MeleeCritPct] = 14, [Stat.SpellCritPct] = 14, [Stat.MeleeHitPct] = 10, [Stat.SpellHitPct] = 8,
        [Stat.CritRating] = 1, [Stat.HitRating] = 1, [Stat.HasteRating] = 1,
        [Stat.DefenseRating] = 1, [Stat.DodgeRating] = 1, [Stat.ParryRating] = 1, [Stat.BlockRating] = 1, [Stat.BlockValue] = 0.65,
        [Stat.Mp5] = 2.5, [Stat.Hp5] = 2.5, [Stat.SpellPenetration] = 0.8,
        [Stat.Health] = 0.1, [Stat.Mana] = 0.1, [Stat.Armor] = 0.02,
        [Stat.FireResist] = 1, [Stat.FrostResist] = 1, [Stat.NatureResist] = 1, [Stat.ShadowResist] = 1, [Stat.ArcaneResist] = 1,
    };

    private static readonly HashSet<Stat> Physical =
        [Stat.Strength, Stat.Agility, Stat.AttackPower, Stat.RangedAttackPower, Stat.MeleeCritPct, Stat.MeleeHitPct];

    // Classic proficiencies at 60 (class id -> highest armor subclass, weapon subclasses).
    private static readonly Dictionary<int, int> ArmorCap = new()
    {
        [1] = 4, [2] = 4, [3] = 3, [4] = 2, [5] = 1, [7] = 3, [8] = 1, [9] = 1, [11] = 2,
    };

    private static readonly Dictionary<int, HashSet<int>> Weapons = new()
    {
        [1] = [0, 1, 2, 3, 4, 5, 6, 7, 8, 10, 13, 15, 16, 18],
        [2] = [0, 1, 4, 5, 6, 7, 8],
        [3] = [0, 1, 2, 3, 6, 7, 8, 10, 13, 15, 16, 18],
        [4] = [2, 3, 4, 7, 13, 15, 16, 18],
        [5] = [4, 10, 15, 19],
        [7] = [0, 1, 4, 5, 10, 13, 15],
        [8] = [7, 10, 15, 19],
        [9] = [7, 10, 15, 19],
        [11] = [4, 5, 10, 13, 15],
    };

    private static readonly HashSet<int> ShieldClasses = [1, 2, 7];

    public static IReadOnlyList<int> InventoryTypesFor(int slot) => slot switch
    {
        1 => [1], 2 => [2], 3 => [3], 4 => [4], 5 => [5, 20], 6 => [6], 7 => [7], 8 => [8], 9 => [9], 10 => [10],
        11 or 12 => [11], 13 or 14 => [12], 15 => [16], 19 => [19],
        GearSlots.MainHand => [13, 17, 21],
        GearSlots.OffHand => [13, 14, 22, 23],
        18 => [15, 25, 26, 28],
        _ => [],
    };

    public UpgradeResult Find(UpgradeRequest request)
    {
        var types = InventoryTypesFor(request.Slot);
        if (types.Count == 0) return new UpgradeResult([], [], "Nothing to look for in this slot.");

        // ----- candidates, scored by what the set already leans on -----
        var exclude = request.SetItems.Select(i => i.Id).ToHashSet();
        var items = calcData.GetGearItems(Candidates(types, request)).Where(i => !exclude.Contains(i.Id)).ToList();
        var scorer = MakeScorer(request.ClassId, request.SetItems, items);
        var effects = scorer.Effects;
        var basedOnSet = scorer.BasedOnSet;
        var weightedBy = scorer.WeightedBy;
        double Score(GearItem item) => scorer.Score(item);

        // ----- rank -----
        var current = request.Current;
        if (items.Count == 0) return new UpgradeResult([], weightedBy, "No items found for this slot at this level.");

        var currentScore = current is null ? 0 : Score(current);
        var scored = items.Select(i => (Item: i, Score: Score(i))).Where(x => x.Score > 0).OrderByDescending(x => x.Score).ToList();
        var sources = Sources(scored.Select(x => x.Item.Id).ToList());

        var meta = Meta(scored.Select(x => x.Item.Id).ToList());
        var options = new List<UpgradeOption>();
        foreach (var (item, score) in scored)
        {
            var source = sources.GetValueOrDefault(item.Id) ?? new SourceSummary(SourceKind.Unknown, "Source unknown");
            if (!request.IncludeRaid && source.Kind == SourceKind.Raid) continue;
            if (!request.IncludeDungeon && source.Kind == SourceKind.Dungeon) continue;
            if (!request.IncludeWorld && source.Kind == SourceKind.WorldDrop) continue;
            var (itemLevel, requiredLevel) = meta.GetValueOrDefault(item.Id);
            options.Add(new UpgradeOption(item.Id, item.Name, item.Quality, item.IconId, itemLevel, requiredLevel,
                score, currentScore, source, StatText(request.ClassId, item, effects)));
            if (options.Count >= request.Limit) break;
        }

        var note = basedOnSet ? "" : "The set is empty, so items are ranked by total stats.";
        return new UpgradeResult(options, weightedBy, note);
    }

    // ----- scoring -----

    private sealed class Scorer(
        IReadOnlyDictionary<int, List<EffectRow>> effects, bool basedOnSet, List<string> weightedBy, Func<GearItem, double> score)
    {
        public IReadOnlyDictionary<int, List<EffectRow>> Effects { get; } = effects;
        public bool BasedOnSet { get; } = basedOnSet;
        public List<string> WeightedBy { get; } = weightedBy;
        public double Score(GearItem item) => score(item);
    }

    /// <summary>Weights from the stats the set already has (each priced by its item budget), and a scorer using them.</summary>
    private Scorer MakeScorer(int classId, IReadOnlyList<GearItem> setItems, IReadOnlyList<GearItem> candidates)
    {
        var effects = calcData.GetEffects(candidates.Concat(setItems).SelectMany(i => i.EquipSpells).Distinct().ToList());

        var setBag = Bag(classId, setItems, effects);
        var weights = new Dictionary<Stat, double>();
        foreach (var (stat, value) in setBag.NonZero)
            if (Budget.TryGetValue(stat, out var price) && stat != Stat.Armor && value > 0) weights[stat] = value * price;
        var total = weights.Values.Sum();
        var basedOnSet = total > 0;
        if (basedOnSet) foreach (var key in weights.Keys.ToList()) weights[key] /= total;
        var physicalShare = basedOnSet ? weights.Where(w => Physical.Contains(w.Key)).Sum(w => w.Value) : 0.5;
        // What one "typical" stat point is worth to this set (the weight a random point of its gear gets).
        var pointWeight = basedOnSet ? weights.Values.Sum(w => w * w) : 1.0 / 5;
        var weightedBy = basedOnSet
            ? weights.OrderByDescending(w => w.Value).Take(5).Select(w => Short(w.Key)).ToList()
            : new List<string> { "total stats (set is empty)" };

        double Score(GearItem item)
        {
            var bag = Bag(classId, [item], effects);
            double score = 0;
            foreach (var (stat, value) in bag.NonZero)
            {
                if (!Budget.TryGetValue(stat, out var price)) continue;
                var weight = basedOnSet ? weights.GetValueOrDefault(stat) : 1.0 / 5;
                // Armor always counts a little, so equal-stat items prefer the sturdier one.
                if (stat == Stat.Armor) weight = Math.Max(weight, 0.02);
                score += value * price * weight;
            }
            // Weapon damage for physical sets: 1 DPS is worth about 14 attack power, so 7 stat points.
            if (item.IsWeapon && item.DelayMs > 0)
                score += (item.DamageMin + item.DamageMax) / 2 / (item.DelayMs / 1000.0) * 7 * physicalShare * pointWeight;
            return score;
        }

        return new Scorer(effects, basedOnSet, weightedBy, Score);
    }

    // ----- quest rewards that beat what's worn (Journal "gear hint") -----

    /// <summary>
    /// The quest rewards among <paramref name="questIds"/> that beat what the set wears in their slot, best gain first.
    /// Only items the class can use and whose required level is at most <paramref name="maxRequiredLevel"/>.
    /// </summary>
    public IReadOnlyList<RewardUpgrade> QuestRewardUpgrades(
        int classId, int maxRequiredLevel, IReadOnlyDictionary<int, GearItem> worn, IReadOnlyCollection<int> questIds, int limit = 2)
    {
        if (questIds.Count == 0) return [];
        var questOf = new Dictionary<int, (int QuestId, string Title)>();
        var inventory = new Dictionary<int, int>();
        using (var connection = Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"""
                SELECT i.id, i.class_id, i.subclass_id, i.inventory_type, i.allowable_class, q.id, q.title
                FROM item_sources s
                JOIN items i ON i.id = s.item_id
                JOIN quests q ON q.id = s.ref_id
                WHERE s.kind IN ('quest', 'quest_choice') AND s.ref_id IN {In(command, questIds)}
                  AND i.quality >= 2 AND i.required_level <= $level AND i.inventory_type > 0
                """;
            command.Parameters.AddWithValue("$level", maxRequiredLevel);
            using var r = command.ExecuteReader();
            while (r.Read())
            {
                var (id, cls, sub, inv, allow) = (r.GetInt32(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4));
                if (allow > 0 && allow != -1 && (allow & (1 << (classId - 1))) == 0) continue;
                if (!CanUse(classId, cls, sub, inv)) continue;
                questOf.TryAdd(id, (r.GetInt32(5), r.GetString(6)));
                inventory[id] = inv;
            }
        }
        if (questOf.Count == 0) return [];

        var wornIds = worn.Values.Select(i => i.Id).ToHashSet();
        var items = calcData.GetGearItems(questOf.Keys.Where(id => !wornIds.Contains(id)).ToList());
        var scorer = MakeScorer(classId, worn.Values.ToList(), items);
        var meta = Meta(items.Select(i => i.Id).ToList());

        var found = new List<(RewardUpgrade Upgrade, double Gain)>();
        foreach (var item in items)
        {
            var inv = inventory.GetValueOrDefault(item.Id);
            var slots = new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, GearSlots.MainHand, GearSlots.OffHand, 18, 19 }
                .Where(slot => InventoryTypesFor(slot).Contains(inv))
                .Where(slot => !(slot == GearSlots.OffHand && inv == 13 && classId is not (1 or 3 or 4)))
                .ToList();
            if (slots.Count == 0) continue;
            var score = scorer.Score(item);
            if (score <= 0) continue;

            // Rings and trinkets: compare with the weaker of the two.
            var slot = slots.OrderBy(s => worn.TryGetValue(s, out var w) ? scorer.Score(w) : 0).First();
            var currentScore = worn.TryGetValue(slot, out var current) ? scorer.Score(current) : 0;
            if (currentScore > 0 && score < currentScore * 1.03) continue; // at least 3% better
            var gain = currentScore > 0 ? score / currentScore - 1 : 10 + score; // an empty slot is the clearest gain

            var (itemLevel, requiredLevel) = meta.GetValueOrDefault(item.Id);
            var (questId, title) = questOf[item.Id];
            var option = new UpgradeOption(item.Id, item.Name, item.Quality, item.IconId, itemLevel, requiredLevel,
                score, currentScore, new SourceSummary(SourceKind.Quest, $"Quest: {title}"), StatText(classId, item, scorer.Effects));
            found.Add((new RewardUpgrade(option, slot, questId, title), gain));
        }
        return found.OrderByDescending(f => f.Gain).Select(f => f.Upgrade).Take(limit).ToList();
    }

    // ----- helpers -----

    private static StatBag Bag(int classId, IReadOnlyList<GearItem> items, IReadOnlyDictionary<int, List<EffectRow>> effects) =>
        items.Count == 0
            ? new StatBag()
            : StatEngine.Compute(new CharacterInput { ClassId = classId, RaceId = 1, Gear = items, Effects = effects }).GearOnly;

    private List<int> Candidates(IReadOnlyList<int> types, UpgradeRequest request)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        var typeList = string.Join(',', types);
        command.CommandText = $"""
            SELECT id, class_id, subclass_id, inventory_type, allowable_class FROM items
            WHERE inventory_type IN ({typeList}) AND required_level <= $level AND quality >= 2 AND {ItemVisibility.Clause("id")}
            ORDER BY item_level DESC LIMIT 1500
            """;
        command.Parameters.AddWithValue("$level", request.Level);
        var ids = new List<int>();
        using var r = command.ExecuteReader();
        while (r.Read())
        {
            var (id, cls, sub, inv, allow) = (r.GetInt32(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4));
            if (allow > 0 && allow != -1 && (allow & (1 << (request.ClassId - 1))) == 0) continue;
            if (!CanUse(request.ClassId, cls, sub, inv)) continue;
            // Off hand: one-handers only for classes that dual wield in Classic (warrior, hunter, rogue).
            if (request.Slot == GearSlots.OffHand && inv == 13 && request.ClassId is not (1 or 3 or 4)) continue;
            ids.Add(id);
        }
        return ids.Take(400).ToList();
    }

    private static bool CanUse(int classId, int itemClass, int subclass, int inventoryType)
    {
        if (itemClass == 4) // armor
        {
            if (subclass == 6) return ShieldClasses.Contains(classId);
            if (subclass is >= 1 and <= 4 && inventoryType != 16) return subclass <= ArmorCap.GetValueOrDefault(classId, 4);
            if (subclass is 7 or 8 or 9 or 10) return true; // librams, idols, totems: the slot itself decides
            return true;
        }
        if (itemClass == 2) return Weapons.TryGetValue(classId, out var allowed) ? allowed.Contains(subclass) : true;
        return true;
    }

    private Dictionary<int, (int ItemLevel, int RequiredLevel)> Meta(IReadOnlyList<int> ids)
    {
        var result = new Dictionary<int, (int, int)>();
        if (ids.Count == 0) return result;
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT id, item_level, required_level FROM items WHERE id IN {In(command, ids)}";
        using var r = command.ExecuteReader();
        while (r.Read()) result[r.GetInt32(0)] = (r.GetInt32(1), r.GetInt32(2));
        return result;
    }

    /// <summary>One line per item about where it comes from, easiest kind first.</summary>
    public Dictionary<int, SourceSummary> Sources(IReadOnlyList<int> itemIds)
    {
        var result = new Dictionary<int, SourceSummary>();
        if (itemIds.Count == 0) return result;
        using var connection = Open();
        if (!TableExists(connection, "item_sources")) return result;

        var rows = new List<(int Item, string Kind, string Name, string Place, int InstanceType, double Chance, string Giver, string GiverPlace)>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"""
                SELECT s.item_id, s.kind, COALESCE(n.name, q.title, o.name, ''), COALESCE(m.name, qa.name, ''),
                       COALESCE(m.instance_type, 0), s.chance, COALESCE(q.giver, ''),
                       COALESCE(n.zone, o.zone, ''), COALESCE(n.zone_x, o.zone_x, 0), COALESCE(n.zone_y, o.zone_y, 0),
                       COALESCE(gn.zone, go.zone, ''), COALESCE(gn.zone_x, go.zone_x, 0), COALESCE(gn.zone_y, go.zone_y, 0)
                FROM item_sources s
                LEFT JOIN npcs n ON s.kind IN ('drop', 'vendor', 'pickpocket', 'skinning') AND n.id = s.ref_id
                LEFT JOIN quests q ON s.kind IN ('quest', 'quest_choice') AND q.id = s.ref_id
                LEFT JOIN objects o ON s.kind = 'object' AND o.id = s.ref_id
                LEFT JOIN maps m ON m.id = COALESCE(n.map, o.map)
                LEFT JOIN areas qa ON q.id IS NOT NULL AND qa.id = q.zone
                LEFT JOIN npcs gn ON q.giver_kind = 'npc' AND gn.id = q.giver_id
                LEFT JOIN objects go ON q.giver_kind = 'object' AND go.id = q.giver_id
                WHERE s.item_id IN {In(command, itemIds)}
                """;
            using var r = command.ExecuteReader();
            while (r.Read())
                rows.Add((r.GetInt32(0), r.GetString(1), r.GetString(2),
                    ItemSearchService.Location(r.GetString(7), r.GetDouble(8), r.GetDouble(9), r.GetString(3)),
                    r.GetInt32(4), r.GetDouble(5), r.GetString(6),
                    ItemSearchService.Location(r.GetString(10), r.GetDouble(11), r.GetDouble(12), r.GetString(3))));
        }

        foreach (var group in rows.GroupBy(x => x.Item))
        {
            var list = group.ToList();
            var quest = list.FirstOrDefault(x => x.Kind is "quest" or "quest_choice");
            var vendor = list.FirstOrDefault(x => x.Kind == "vendor");
            var drops = list.Where(x => x.Kind == "drop").ToList();
            SourceSummary summary;
            if (quest.Name is { Length: > 0 })
            {
                // "Quest: The Defias Brotherhood · Gryan Stoutmantle, Westfall"
                var where = string.Join(", ", new[] { quest.Giver, quest.GiverPlace }.Where(x => x is { Length: > 0 }));
                summary = new(SourceKind.Quest, $"Quest: {quest.Name}" + (where.Length > 0 ? $" · {where}" : ""));
            }
            else if (vendor.Name is { Length: > 0 })
                summary = new(SourceKind.Vendor, $"Vendor: {vendor.Name}" + (vendor.Place.Length > 0 ? $" · {vendor.Place}" : ""));
            else if (drops.Count > 6) summary = new(SourceKind.WorldDrop, $"World drop ({drops.Count} sources)");
            else if (drops.Count > 0)
            {
                var best = drops.OrderByDescending(d => d.Chance).First();
                var kind = best.InstanceType == 2 ? SourceKind.Raid : best.InstanceType == 1 ? SourceKind.Dungeon : SourceKind.Other;
                var chance = best.Chance > 0 ? $" {best.Chance.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)}%" : "";
                var place = best.Place.Length > 0 ? $" · {best.Place}" : "";
                summary = new(kind, $"Drop: {best.Name}{place}{chance}");
            }
            else
            {
                var other = list[0];
                summary = new(SourceKind.Other, other.Kind switch
                {
                    "object" => $"Chest: {other.Name}{(other.Place.Length > 0 ? " · " + other.Place : "")}",
                    "container" => "From a container",
                    _ => char.ToUpperInvariant(other.Kind[0]) + other.Kind[1..],
                });
            }
            result[group.Key] = summary;
        }

        // Crafted items: anything a recipe makes and nothing else gives.
        using (var command = connection.CreateCommand())
        {
            var missing = itemIds.Where(id => !result.ContainsKey(id)).ToList();
            if (missing.Count > 0 && TableExists(connection, "recipes"))
            {
                command.CommandText = $"SELECT created_item_id, profession FROM recipes WHERE created_item_id IN {In(command, missing)}";
                try
                {
                    using var r = command.ExecuteReader();
                    while (r.Read()) result.TryAdd(r.GetInt32(0), new SourceSummary(SourceKind.Crafted, $"Crafted: {r.GetString(1)}"));
                }
                catch (SqliteException) { /* older schema: no crafted info */ }
            }
        }
        return result;
    }

    private static string StatText(int classId, GearItem item, IReadOnlyDictionary<int, List<EffectRow>> effects)
    {
        var bag = Bag(classId, [item], effects);
        var parts = bag.NonZero.Where(v => v.Key != Stat.Armor && Budget.ContainsKey(v.Key))
            .OrderByDescending(v => v.Value * Budget[v.Key])
            .Take(5)
            .Select(v => $"+{v.Value.ToString(Math.Abs(v.Value % 1) < 0.05 ? "0" : "0.0", System.Globalization.CultureInfo.InvariantCulture)} {Short(v.Key)}");
        var text = string.Join("  ", parts);
        if (item.IsWeapon && item.DelayMs > 0)
        {
            var dps = (item.DamageMin + item.DamageMax) / 2 / (item.DelayMs / 1000.0);
            text = $"{dps.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)} dps  " + text;
        }
        return text.Trim();
    }

    public static string Short(Stat stat) => stat switch
    {
        Stat.Strength => "Str", Stat.Agility => "Agi", Stat.Stamina => "Sta", Stat.Intellect => "Int", Stat.Spirit => "Spi",
        Stat.AttackPower => "AP", Stat.RangedAttackPower => "RAP", Stat.SpellPower => "SP", Stat.Healing => "Heal",
        Stat.MeleeCritPct => "Crit%", Stat.SpellCritPct => "Spell crit%", Stat.MeleeHitPct => "Hit%", Stat.SpellHitPct => "Spell hit%",
        Stat.CritRating => "Crit", Stat.HitRating => "Hit", Stat.HasteRating => "Haste", Stat.Mp5 => "MP5", Stat.Hp5 => "HP5",
        Stat.DefenseRating => "Def", Stat.DodgeRating => "Dodge", Stat.ParryRating => "Parry", Stat.BlockRating => "Block",
        Stat.BlockValue => "Block value", Stat.FirePower => "Fire SP", Stat.FrostPower => "Frost SP", Stat.ShadowPower => "Shadow SP",
        Stat.ArcanePower => "Arcane SP", Stat.NaturePower => "Nature SP", Stat.HolyPower => "Holy SP",
        Stat.SpellPenetration => "Spell pen", Stat.Health => "HP", Stat.Mana => "Mana",
        Stat.FireResist => "FR", Stat.FrostResist => "FrR", Stat.NatureResist => "NR", Stat.ShadowResist => "SR", Stat.ArcaneResist => "AR",
        _ => stat.ToString(),
    };

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private static bool TableExists(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $t";
        command.Parameters.AddWithValue("$t", table);
        return command.ExecuteScalar() is not null;
    }

    private static string In(SqliteCommand command, IEnumerable<int> ids)
    {
        var names = new List<string>();
        foreach (var id in ids)
        {
            var name = "$p" + names.Count;
            command.Parameters.AddWithValue(name, id);
            names.Add(name);
        }
        return "(" + (names.Count == 0 ? "NULL" : string.Join(',', names)) + ")";
    }
}
