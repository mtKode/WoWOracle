using Microsoft.Data.Sqlite;

namespace WowCompanion.Core;

/// <summary>An item a mob drops, with what one kill gives on average (chance × price).</summary>
public sealed record FarmDrop(int ItemId, string Name, int Quality, double Chance, long Price, double PerKill);

/// <summary>
/// A mob worth farming for vendor gold: what one kill gives on average (its coin, plus each drop's chance × price),
/// and how many spawns it has in its zone. Greys and Other only count steady drops (FarmService.SteadyChance or more);
/// Rare is the rest, which mostly doesn't happen in a short farm.
/// </summary>
public sealed record FarmMob(int NpcId, string Name, string Zone, double X, double Y, int MinLevel, int MaxLevel, bool Elite, int Spawns,
    long Coin, long Greys, long Other, long Rare, IReadOnlyList<FarmDrop> TopDrops)
{
    /// <summary>Steady copper per kill: coin, and greys and other items that drop often (the list's order).</summary>
    public long PerKill => Coin + Greys + Other;
    /// <summary>With the rare drops' average too.</summary>
    public long WithRare => PerKill + Rare;
    public string LevelText => MinLevel == MaxLevel ? MinLevel.ToString() : $"{MinLevel}-{MaxLevel}";
}

/// <summary>
/// Vendor-gold farming (Map page, "Farming"): mobs around a level ranked by what a kill gives on average. From the
/// Classic world data: coin (npcs.min_gold / max_gold, game data schema 38), drop chances (item_sources) and the items'
/// vendor price; WoW Forever's own mobs aren't in it.
/// </summary>
public sealed class FarmService(string databasePath)
{
    /// <summary>Drops at least this likely (percent) count as steady income; rarer ones are shown apart.</summary>
    public const double SteadyChance = 5;

    /// <param name="price">
    /// What a non-grey item is worth (item id, vendor price → copper), e.g. the auction price when it's higher; null
    /// counts the vendor price. Greys always count their vendor price.
    /// </param>
    public IReadOnlyList<FarmMob> Find(int minLevel, int maxLevel, bool elites, Func<int, long, long>? price = null,
        int minSpawns = 4, int limit = 60)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();
        if (!HasColumn(connection, "npcs", "min_gold")) return [];

        var mobs = new Dictionary<int, (string Name, string Zone, double X, double Y, int Min, int Max, bool Elite, int Spawns, long Coin)>();
        using (var command = connection.CreateCommand())
        {
            // World mobs only (instances are left out, and Alliance / Horde NPCs), with their spawns in their own zone.
            command.CommandText = """
                SELECT n.id, n.name, n.zone, n.zone_x, n.zone_y, n.min_level, n.max_level, n.rank, n.min_gold, n.max_gold,
                       (SELECT COUNT(*) FROM npc_spawns p WHERE p.npc_id = n.id AND p.zone = n.zone) AS spawns
                FROM npcs n
                LEFT JOIN maps m ON m.id = n.map
                WHERE n.max_level BETWEEN $lo AND $hi AND n.zone <> '' AND COALESCE(m.instance_type, 0) = 0
                  AND ($elites = 1 OR n.rank = 0) AND n.rank <> 3
                  AND n.side = ''   -- not town guards and other Alliance / Horde NPCs
                """;
            command.Parameters.AddWithValue("$lo", minLevel);
            command.Parameters.AddWithValue("$hi", maxLevel);
            command.Parameters.AddWithValue("$elites", elites ? 1 : 0);
            using var r = command.ExecuteReader();
            while (r.Read())
            {
                if (r.GetInt32(10) < minSpawns) continue;
                mobs[r.GetInt32(0)] = (r.GetString(1), r.GetString(2), r.GetDouble(3), r.GetDouble(4), r.GetInt32(5), r.GetInt32(6),
                    r.GetInt32(7) > 0, r.GetInt32(10), (r.GetInt64(8) + r.GetInt64(9)) / 2);
            }
        }
        if (mobs.Count == 0) return [];

        var drops = new Dictionary<int, List<FarmDrop>>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"""
                SELECT s.ref_id, i.id, i.name, i.quality, s.chance, i.sell_price, i.bonding
                FROM item_sources s JOIN items i ON i.id = s.item_id
                WHERE s.kind = 'drop' AND s.chance > 0 AND s.ref_id IN ({string.Join(',', mobs.Keys)})
                  AND i.class_id <> 12   -- quest items: they drop only while on the quest (the dump's chance is for then)
                """;
            using var r = command.ExecuteReader();
            while (r.Read())
            {
                var (quality, chance, sell) = (r.GetInt32(3), r.GetDouble(4), r.GetInt64(5));
                // Soulbound (bind on pickup 1, quest 4) can't go to the auction house: vendor price only.
                var tradable = r.GetInt32(6) is not (1 or 4);
                var worth = quality == 0 || price is null || !tradable ? sell : Math.Max(sell, price(r.GetInt32(1), sell));
                if (worth <= 0) continue;
                if (!drops.TryGetValue(r.GetInt32(0), out var list)) drops[r.GetInt32(0)] = list = [];
                list.Add(new FarmDrop(r.GetInt32(1), r.GetString(2), quality, chance, worth, chance / 100 * worth));
            }
        }

        return mobs
            .Select(m =>
            {
                var list = drops.GetValueOrDefault(m.Key) ?? [];
                var steady = list.Where(d => d.Chance >= SteadyChance).ToList();
                var greys = (long)Math.Round(steady.Where(d => d.Quality == 0).Sum(d => d.PerKill));
                var other = (long)Math.Round(steady.Where(d => d.Quality > 0).Sum(d => d.PerKill));
                var rare = (long)Math.Round(list.Where(d => d.Chance < SteadyChance).Sum(d => d.PerKill));
                var v = m.Value;
                return new FarmMob(m.Key, v.Name, v.Zone, v.X, v.Y, v.Min, v.Max, v.Elite, v.Spawns, v.Coin, greys, other, rare,
                    list.OrderByDescending(d => d.PerKill).Take(6).ToList());
            })
            .Where(m => m.PerKill > 0)
            // The same mob under several ids (level variants): the best one per name and zone.
            .GroupBy(m => (m.Name, m.Zone))
            .Select(g => g.OrderByDescending(m => m.PerKill).First() with { Spawns = g.Sum(m => m.Spawns) })
            .OrderByDescending(m => m.PerKill)
            .Take(limit)
            .ToList();
    }

    private static bool HasColumn(SqliteConnection connection, string table, string column)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT 1 FROM pragma_table_info('{table}') WHERE name = '{column}'";
        return command.ExecuteScalar() is not null;
    }
}
