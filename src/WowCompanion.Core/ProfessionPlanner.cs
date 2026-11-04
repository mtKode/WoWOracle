using Microsoft.Data.Sqlite;

namespace WowCompanion.Core;

/// <summary>What the planner may use and what you already have.</summary>
public sealed record PlanOptions(
    string Profession, int FromSkill, int ToSkill,
    bool VendorRecipes = true,
    bool DropRecipes = false,
    bool MakeSubParts = true,
    bool Smelt = false,
    IReadOnlySet<int>? KnownRecipes = null,
    IReadOnlyDictionary<int, int>? Owned = null);

/// <summary>One leg of the route: craft <see cref="Crafts"/> of a recipe to go from <see cref="FromSkill"/> to <see cref="ToSkill"/>.</summary>
/// <param name="Cost">Rough value of the materials, in copper (vendor prices and vendor values; no auction prices).</param>
public sealed record PlanStep(
    int FromSkill, int ToSkill, int SpellId, string Recipe, int CreatedItemId, int Crafts, int SafeCrafts,
    string ColorAtStart, int Learn, int Yellow, int Green, int Grey, string Source, IReadOnlyList<(int ItemId, int Quantity)> Reagents,
    long Cost)
{
    /// <summary>Icon of what the recipe makes (0 for enchants).</summary>
    public int IconId { get; init; }
    /// <summary>"2× Rough Stone, 1× Linen Cloth".</summary>
    public string ReagentText { get; init; } = "";
    /// <summary>Items one craft makes (ammo 200, bombs 2…); 1 for most.</summary>
    public int Makes { get; init; } = 1;
}

/// <summary>A material for the whole route: how many, how many you have, where it comes from.</summary>
public sealed record PlanMaterial(int ItemId, string Name, int Quality, int IconId, int Total, int Owned, string Source)
{
    public int ToGet => Math.Max(0, Total - Owned);
}

/// <summary>Something made along the way from its own reagents (bolts, bars, cured leather…).</summary>
public sealed record PlanSubCraft(int ItemId, string Name, int Quality, int IconId, int Count, string How);

public sealed record PlanNote(int AtSkill, string Text);

public sealed record ProfessionPlan(
    IReadOnlyList<PlanStep> Steps, IReadOnlyList<PlanMaterial> Materials, IReadOnlyList<PlanSubCraft> SubCrafts,
    IReadOnlyList<PlanNote> Notes, int ReachedSkill)
{
    public long TotalCost => Steps.Sum(s => s.Cost);
}

/// <summary>
/// Profession leveling: from your skill upward, at every point the recipe with the lowest expected material cost per
/// skill point. Skill-up chance (Classic): 100% while the recipe is orange, then (grey − skill) / (grey − yellow) through
/// yellow and green, 0 at grey; expected crafts for a point = 1 / chance. Materials are weighed by how hard they are to
/// get (no auction prices): vendor reagents at their price, the rest at their vendor value (×1.5 for green and better);
/// materials you have count less. Sub-parts of the same profession (and smelting, if chosen) are made from their own reagents.
/// </summary>
public sealed class ProfessionPlanner(string databasePath)
{
    /// <summary>Trainer ranks: (skill cap, skill to learn it, character level to learn it).</summary>
    public static readonly IReadOnlyList<(int Cap, int LearnAt, int Level, string Name)> Ranks =
    [
        (75, 1, 5, "Apprentice"), (150, 50, 10, "Journeyman"), (225, 125, 20, "Expert"), (300, 200, 35, "Artisan"),
    ];

    private sealed record Recipe(int SpellId, string Name, string Profession, int Learn, int Yellow, int Grey, int Created,
        string SkillSource, int TaughtBy, List<(int Item, int Qty)> Reagents, bool Cooldown, int Makes)
    {
        public int Green => (Yellow + Grey) / 2;
        public bool HasRange => Yellow > 0 && Grey > Yellow;
    }

    private sealed record ItemInfo(string Name, int Quality, int Icon, int ClassId, bool Vendor, int BuyPrice, int SellPrice);

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    /// <summary>Professions with enough craftable recipes to level.</summary>
    public IReadOnlyList<string> Professions()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT profession FROM recipes WHERE trivial_high > trivial_low GROUP BY profession HAVING COUNT(*) >= 10 ORDER BY profession
            """;
        var list = new List<string>();
        using var r = command.ExecuteReader();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    /// <summary>
    /// Which options matter for a profession: sub-parts (it uses reagents it makes itself: bolts, cured leather…) and
    /// smelting (bars are a main material: at least 15% of its recipes use one, so Blacksmithing and Engineering, not
    /// Alchemy or Tailoring with a few transmutes and threads).
    /// </summary>
    public (bool SubParts, bool Bars) OptionsFor(string profession)
    {
        using var connection = Open();
        double Share(string maker)
        {
            using var c = connection.CreateCommand();
            c.CommandText = $"""
                SELECT CAST(COUNT(DISTINCT CASE WHEN rr.item_id IN
                                  (SELECT created_item_id FROM recipes WHERE created_item_id > 0 AND profession = {maker})
                             THEN r.spell_id END) AS REAL) / MAX(COUNT(DISTINCT r.spell_id), 1)
                FROM recipes r LEFT JOIN recipe_reagents rr ON rr.spell_id = r.spell_id
                WHERE r.profession = $p
                """;
            c.Parameters.AddWithValue("$p", profession);
            return c.ExecuteScalar() is double d ? d : 0;
        }
        return (Share("$p") > 0, profession != "Mining" && Share("'Mining'") >= 0.15);
    }

    public ProfessionPlan Plan(PlanOptions options)
    {
        using var connection = Open();
        var hidden = ItemVisibility.HiddenIds(connection); // SoD / test recipes and items stay out
        var recipes = LoadRecipes(connection).Where(r => !hidden.Contains(r.Created) && !hidden.Contains(r.TaughtBy)).ToList();
        var items = LoadItems(connection, recipes);
        var vendorRecipeItems = VendorItems(connection, recipes.Where(r => r.TaughtBy > 0).Select(r => r.TaughtBy).Distinct().ToList());
        var owned = options.Owned ?? new Dictionary<int, int>();

        // Who makes what: the same profession's own recipes, and smelting when chosen.
        var makers = recipes
            .Where(r => r.Created > 0 && (r.Profession == options.Profession && options.MakeSubParts || r.Profession == "Mining" && options.Smelt))
            .GroupBy(r => r.Created)
            .ToDictionary(g => g.Key, g => g.OrderBy(r => r.Learn).First());

        var costMemo = new Dictionary<int, double>();
        double Cost(int item, int depth = 0)
        {
            if (costMemo.TryGetValue(item, out var known)) return known;
            // Copper. Vendor reagents cost what the vendor asks; for the rest the item's vendor value stands in for
            // what it's worth (no auction prices): it follows the game's own idea of how rare it is.
            var info = items.GetValueOrDefault(item);
            double cost;
            if (info is { Vendor: true }) cost = Math.Max(1, info.BuyPrice);
            else if (depth < 4 && makers.TryGetValue(item, out var maker) && maker.Reagents.Count > 0)
                cost = maker.Reagents.Sum(x => x.Qty * Cost(x.Item, depth + 1)) / maker.Makes; // per item made
            else if (info is null) cost = 500;
            else cost = Math.Max(1, info.BuyPrice > 0 ? info.BuyPrice : info.SellPrice > 0 ? info.SellPrice * 4 : 100)
                        * (info.Quality >= 2 ? 1.5 : 1);
            if (owned.GetValueOrDefault(item) > 0) cost *= 0.4; // use what you have
            costMemo[item] = cost;
            return cost;
        }

        bool Allowed(Recipe r)
        {
            if (r.Profession != options.Profession || !r.HasRange || r.Reagents.Count == 0 || r.Cooldown) return false;
            if (options.KnownRecipes?.Contains(r.SpellId) == true) return true;
            if (r.SkillSource != "recipe") return true; // trainer
            if (options.VendorRecipes && vendorRecipeItems.Contains(r.TaughtBy)) return true;
            return options.DropRecipes;
        }

        var usable = recipes.Where(Allowed).ToList();
        var notes = new List<PlanNote>();
        var steps = new List<(Recipe Recipe, int From, int To, double Crafts, string Color)>();
        Recipe? current = null;
        var skill = Math.Max(1, options.FromSkill);
        var target = Math.Clamp(options.ToSkill, skill, 300);

        static double Chance(Recipe r, int s) =>
            s < r.Yellow ? 1 : s >= r.Grey ? 0 : (double)(r.Grey - s) / (r.Grey - r.Yellow);
        static string Color(Recipe r, int s) => s < r.Yellow ? "orange" : s < r.Green ? "yellow" : s < r.Grey ? "green" : "grey";

        while (skill < target)
        {
            var options1 = usable.Where(r => r.Learn <= skill && r.Grey > skill).ToList();
            if (options1.Count == 0)
            {
                notes.Add(new PlanNote(skill, $"No usable recipe at {skill}: allow vendor or drop recipes, or check the trainer."));
                break;
            }
            double PerPoint(Recipe r) => r.Reagents.Sum(x => x.Qty * Cost(x.Item)) / Math.Max(0.01, Chance(r, skill));
            var best = options1.MinBy(PerPoint)!;
            // Stay with the current recipe unless another is clearly cheaper: fewer switches.
            if (current is not null && options1.Contains(current) && PerPoint(current) <= PerPoint(best) * 1.15) best = current;

            var crafts = 1 / Math.Max(0.01, Chance(best, skill));
            if (steps.Count > 0 && ReferenceEquals(steps[^1].Recipe, best))
            {
                var last = steps[^1];
                steps[^1] = (last.Recipe, last.From, skill + 1, last.Crafts + crafts, last.Color);
            }
            else
            {
                steps.Add((best, skill, skill + 1, crafts, Color(best, skill)));
            }
            current = best;
            skill++;
        }

        // Trainer checkpoints the route passes.
        for (var i = 0; i + 1 < Ranks.Count; i++)
        {
            var (cap, _, _, _) = Ranks[i];
            var next = Ranks[i + 1];
            if (cap >= options.FromSkill && cap < skill)
                notes.Add(new PlanNote(cap, $"Before {cap}: train {next.Name} (from skill {next.LearnAt}, character level {next.Level}) to go higher."));
        }

        // Steps with whole crafts, and the materials they need.
        var planSteps = new List<PlanStep>();
        var totals = new Dictionary<int, int>();
        foreach (var (recipe, from, to, crafts, color) in steps)
        {
            var count = (int)Math.Ceiling(crafts - 0.05);
            var safe = (int)Math.Ceiling(crafts * 1.2);
            planSteps.Add(new PlanStep(from, to, recipe.SpellId, recipe.Name, recipe.Created, count, safe, color, recipe.Learn,
                recipe.Yellow, recipe.Green, recipe.Grey, SourceText(recipe, items, vendorRecipeItems),
                recipe.Reagents.Select(x => (x.Item, x.Qty)).ToList(),
                (long)(count * recipe.Reagents.Sum(x => x.Qty * Cost(x.Item))))
            {
                IconId = RecipeIcons.Or(items.GetValueOrDefault(recipe.Created)?.Icon ?? 0, recipe.Learn),
                Makes = recipe.Makes,
                ReagentText = string.Join(", ", recipe.Reagents.Select(x => $"{x.Qty}× {items.GetValueOrDefault(x.Item)?.Name ?? "item " + x.Item}")),
            });
            foreach (var (item, qty) in recipe.Reagents)
                totals[item] = totals.GetValueOrDefault(item) + qty * count;
        }

        // Break sub-parts down into their reagents (what you have of a sub-part is used first).
        var subCrafts = new Dictionary<int, int>();
        for (var pass = 0; pass < 4; pass++)
        {
            var expand = totals.Keys.Where(item => makers.ContainsKey(item) && items.GetValueOrDefault(item) is not { Vendor: true }).ToList();
            if (expand.Count == 0) break;
            foreach (var item in expand)
            {
                var need = Math.Max(0, totals[item] - owned.GetValueOrDefault(item));
                totals.Remove(item);
                if (need == 0) continue;
                var crafts = (int)Math.Ceiling(need / (double)makers[item].Makes); // a craft can make several
                subCrafts[item] = subCrafts.GetValueOrDefault(item) + crafts;
                foreach (var (part, qty) in makers[item].Reagents)
                    totals[part] = totals.GetValueOrDefault(part) + qty * crafts;
            }
        }

        var materials = totals
            .Select(t =>
            {
                var info = items.GetValueOrDefault(t.Key) ?? new ItemInfo($"Item {t.Key}", 1, 0, 0, false, 0, 0);
                var source = info.Vendor ? "Vendor" : info.ClassId == 7 && info.Quality < 2 ? "Gathered / trade goods" : "Drop or other";
                return new PlanMaterial(t.Key, info.Name, info.Quality, info.Icon, t.Value, owned.GetValueOrDefault(t.Key), source);
            })
            .OrderBy(m => m.Source == "Vendor" ? 1 : 0).ThenByDescending(m => m.ToGet).ThenBy(m => m.Name)
            .ToList();
        var subs = subCrafts
            .Select(s =>
            {
                var info = items.GetValueOrDefault(s.Key) ?? new ItemInfo($"Item {s.Key}", 1, 0, 0, false, 0, 0);
                var maker = makers[s.Key];
                return new PlanSubCraft(s.Key, info.Name, info.Quality, info.Icon, s.Value,
                    (maker.Profession == "Mining" ? $"Smelt ({maker.Name})" : maker.Name) + (maker.Makes > 1 ? $", makes {maker.Makes} each" : ""));
            })
            .OrderBy(s => s.Name).ToList();

        return new ProfessionPlan(planSteps, materials, subs, notes.OrderBy(n => n.AtSkill).ToList(), skill);
    }

    private static string SourceText(Recipe recipe, Dictionary<int, ItemInfo> items, HashSet<int> vendorRecipes)
    {
        if (recipe.SkillSource != "recipe") return "Trainer";
        var name = items.GetValueOrDefault(recipe.TaughtBy)?.Name ?? "a recipe";
        return vendorRecipes.Contains(recipe.TaughtBy) ? $"Vendor: {name}" : $"Found: {name}";
    }

    private static List<Recipe> LoadRecipes(SqliteConnection connection)
    {
        var reagents = new Dictionary<int, List<(int, int)>>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT spell_id, item_id, quantity FROM recipe_reagents";
            using var r = command.ExecuteReader();
            while (r.Read())
            {
                if (!reagents.TryGetValue(r.GetInt32(0), out var list)) reagents[r.GetInt32(0)] = list = [];
                list.Add((r.GetInt32(1), Math.Max(1, r.GetInt32(2))));
            }
        }
        var recipes = new List<Recipe>();
        using (var command = connection.CreateCommand())
        {
            // Recipes on a long cooldown (Mooncloth, transmutes) can't carry a skill-up route.
            command.CommandText = """
                SELECT r.spell_id, r.name, r.profession, r.min_skill, r.trivial_low, r.trivial_high, r.created_item_id, r.skill_source,
                       r.taught_by, MAX(COALESCE(s.cooldown_ms, 0), COALESCE(s.category_cooldown_ms, 0)), r.created_count
                FROM recipes r LEFT JOIN spells s ON s.id = r.spell_id
                """;
            using var r = command.ExecuteReader();
            while (r.Read())
                recipes.Add(new Recipe(r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetInt32(3), r.GetInt32(4), r.GetInt32(5),
                    r.GetInt32(6), r.GetString(7), r.GetInt32(8), reagents.GetValueOrDefault(r.GetInt32(0)) ?? [], r.GetInt64(9) > 60000,
                    Math.Max(1, r.GetInt32(10))));
        }
        return recipes;
    }

    private static Dictionary<int, ItemInfo> LoadItems(SqliteConnection connection, List<Recipe> recipes)
    {
        var ids = recipes.SelectMany(r => r.Reagents.Select(x => x.Item))
            .Concat(recipes.Select(r => r.Created)).Concat(recipes.Select(r => r.TaughtBy))
            .Where(id => id > 0).Distinct().ToList();
        var vendor = VendorItems(connection, ids);
        var result = new Dictionary<int, ItemInfo>();
        foreach (var chunk in ids.Chunk(500))
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT id, name, quality, icon_file_id, class_id, buy_price, sell_price FROM items WHERE id IN ({string.Join(',', chunk)})";
            using var r = command.ExecuteReader();
            while (r.Read())
                result[r.GetInt32(0)] = new ItemInfo(r.GetString(1), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4), vendor.Contains(r.GetInt32(0)),
                    r.GetInt32(5), r.GetInt32(6));
        }
        return result;
    }

    private static HashSet<int> VendorItems(SqliteConnection connection, IReadOnlyCollection<int> ids)
    {
        var set = new HashSet<int>();
        foreach (var chunk in ids.Chunk(500))
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT DISTINCT item_id FROM item_sources WHERE kind = 'vendor' AND item_id IN ({string.Join(',', chunk)})";
            try
            {
                using var r = command.ExecuteReader();
                while (r.Read()) set.Add(r.GetInt32(0));
            }
            catch (SqliteException)
            {
                // No source data: nothing counts as vendor-sold.
            }
        }
        return set;
    }
}
