using Microsoft.Data.Sqlite;

namespace WowCompanion.Data;

/// <summary>
/// Enchants: what each one adds (SpellItemEnchantment) and which gear slots it can go on. The slots come from the
/// spells that apply it: enchanting recipes, armor kits, oils, sharpening stones and poisons (spell effects 53/54/92,
/// misc value = enchant id), with the item class / inventory types in SpellEquippedItems.
/// </summary>
public static partial class GameDatabaseBuilder
{
    private const string EnchantSchema = """

        CREATE TABLE enchants (
            id INTEGER PRIMARY KEY,
            name TEXT NOT NULL,
            stats TEXT NOT NULL,          -- "type:value,…" item stat types (resistances as 50-56)
            damage INTEGER NOT NULL,      -- flat weapon damage
            proc_spells TEXT NOT NULL,    -- chance-on-hit spells ("Fiery Weapon")
            equip_spells TEXT NOT NULL    -- passive auras
        );
        CREATE TABLE enchant_options (
            enchant_id INTEGER NOT NULL,
            slot INTEGER NOT NULL,        -- gear slot (1-19)
            spell_id INTEGER NOT NULL,
            label TEXT NOT NULL,          -- "Enchant Boots - Minor Agility", "Brilliant Wizard Oil"
            temporary INTEGER NOT NULL,   -- oils, stones, poisons
            PRIMARY KEY (enchant_id, slot, spell_id)
        );
        CREATE INDEX enchant_options_slot ON enchant_options (slot);
        """;

    // SpellItemEnchantment effect types.
    private const int EnchantProc = 1, EnchantDamage = 2, EnchantEquip = 3, EnchantResist = 4, EnchantStat = 5;

    /// <returns>Number of enchants stored (0 when the table wasn't downloaded).</returns>
    private static int BuildEnchants(SqliteConnection connection, SqliteTransaction transaction, Func<string, string> csv)
    {
        var enchantPath = csv("SpellItemEnchantment");
        if (!File.Exists(enchantPath)) return 0;

        // Spell effects: enchant amounts referenced by spell ("$1220743M2") and the spells that apply enchants.
        var effectPoints = new Dictionary<(int Spell, int Index), double>();
        var applyRows = new List<(int Effect, int EnchantId, int SpellId)>();
        foreach (var e in CsvTable.Read(csv("SpellEffect")))
        {
            var spell = e.GetInt("SpellID");
            effectPoints[(spell, e.GetInt("EffectIndex"))] = e.GetDouble("EffectBasePointsF");
            var effect = e.GetInt("Effect");
            if (effect is 53 or 54 or 92) applyRows.Add((effect, e.GetInt("EffectMiscValue_0"), spell));
        }

        // ----- the enchants themselves -----
        var stored = new HashSet<int>();
        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = "INSERT OR REPLACE INTO enchants (id, name, stats, damage, proc_spells, equip_spells) VALUES ($id, $n, $s, $d, $p, $e)";
            var pId = insert.Parameters.Add("$id", SqliteType.Integer);
            var pName = insert.Parameters.Add("$n", SqliteType.Text);
            var pStats = insert.Parameters.Add("$s", SqliteType.Text);
            var pDamage = insert.Parameters.Add("$d", SqliteType.Integer);
            var pProc = insert.Parameters.Add("$p", SqliteType.Text);
            var pEquip = insert.Parameters.Add("$e", SqliteType.Text);

            foreach (var row in CsvTable.Read(enchantPath))
            {
                var id = row.GetInt("ID");
                var name = row.Get("Name_lang").Trim();
                if (id <= 0 || name.Length == 0) continue;
                name = FillEnchantName(name, row, effectPoints);
                var stats = new List<string>();
                var procs = new List<int>();
                var equips = new List<int>();
                var damage = 0;
                for (var i = 0; i < 3; i++)
                {
                    var type = row.GetInt($"Effect_{i}");
                    var points = row.Has($"EffectPointsMin_{i}") ? row.GetInt($"EffectPointsMin_{i}") : 0;
                    var arg = row.GetInt($"EffectArg_{i}");
                    switch (type)
                    {
                        case EnchantStat when points != 0:
                            stats.Add($"{arg}:{points}");
                            break;
                        case EnchantResist when points != 0:
                            // school -> item stat type: armor 50, holy 53, fire 51, nature 55, frost 52, shadow 54, arcane 56
                            var statType = arg switch { 0 => 50, 1 => 53, 2 => 51, 3 => 55, 4 => 52, 5 => 54, 6 => 56, _ => 0 };
                            if (statType > 0) stats.Add($"{statType}:{points}");
                            break;
                        case EnchantDamage:
                            damage += points;
                            break;
                        case EnchantProc when arg > 0:
                            procs.Add(arg);
                            break;
                        case EnchantEquip when arg > 0:
                            equips.Add(arg);
                            break;
                    }
                }
                pId.Value = id;
                pName.Value = name;
                pStats.Value = string.Join(",", stats);
                pDamage.Value = damage;
                pProc.Value = string.Join(",", procs);
                pEquip.Value = string.Join(",", equips);
                insert.ExecuteNonQuery();
                stored.Add(id);
            }
        }

        // ----- which spells apply which enchant, and to what -----
        var spellNames = CsvTable.Read(csv("SpellName")).ToDictionary(r => r.GetInt("ID"), r => r.Get("Name_lang"));
        var equipped = new Dictionary<int, (int Class, int InvTypes, int Subclass)>();
        var equippedPath = csv("SpellEquippedItems");
        if (File.Exists(equippedPath))
            foreach (var r in CsvTable.Read(equippedPath))
                equipped[r.GetInt("SpellID")] = (r.GetInt("EquippedItemClass"), r.GetInt("EquippedItemInvTypes"), r.GetInt("EquippedItemSubclass"));

        using var option = connection.CreateCommand();
        option.Transaction = transaction;
        option.CommandText = "INSERT OR IGNORE INTO enchant_options (enchant_id, slot, spell_id, label, temporary) VALUES ($e, $slot, $s, $l, $t)";
        var oEnchant = option.Parameters.Add("$e", SqliteType.Integer);
        var oSlot = option.Parameters.Add("$slot", SqliteType.Integer);
        var oSpell = option.Parameters.Add("$s", SqliteType.Integer);
        var oLabel = option.Parameters.Add("$l", SqliteType.Text);
        var oTemp = option.Parameters.Add("$t", SqliteType.Integer);

        foreach (var (effect, enchantId, spellId) in applyRows)
        {
            if (!stored.Contains(enchantId) || !equipped.TryGetValue(spellId, out var eq)) continue;
            var label = spellNames.GetValueOrDefault(spellId, "").Trim();
            if (label.Length == 0) continue;

            foreach (var slot in SlotsFor(eq.Class, eq.InvTypes, eq.Subclass))
            {
                oEnchant.Value = enchantId;
                oSlot.Value = slot;
                oSpell.Value = spellId;
                oLabel.Value = label;
                oTemp.Value = effect == 54 ? 1 : 0;
                option.ExecuteNonQuery();
            }
        }
        return stored.Count;
    }

    /// <summary>
    /// Fills the amount placeholders in enchant names: "$k2" (this enchant's 2nd amount), "$1220743M2" / "$1220754s3"
    /// (an effect of another spell), "$i" (scales with the item: shown as "X").
    /// </summary>
    private static string FillEnchantName(string name, CsvRow row, Dictionary<(int Spell, int Index), double> effectPoints)
    {
        if (!name.Contains('$')) return name;
        name = System.Text.RegularExpressions.Regex.Replace(name, @"\$[kK]([1-3])", m =>
        {
            var column = $"EffectPointsMin_{m.Groups[1].Value[0] - '1'}";
            return row.Has(column) ? Math.Abs(row.GetInt(column)).ToString() : m.Value;
        });
        name = System.Text.RegularExpressions.Regex.Replace(name, @"\$(\d+)[mMsS]([1-9])", m =>
        {
            var key = (int.Parse(m.Groups[1].Value), m.Groups[2].Value[0] - '1');
            return effectPoints.TryGetValue(key, out var v)
                ? Math.Abs(v).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)
                : m.Value;
        });
        return name.Replace("$i", "X");
    }

    /// <summary>Gear slots an enchanting spell can target.</summary>
    private static IEnumerable<int> SlotsFor(int itemClass, int invTypes, int subclassMask)
    {
        var slots = new HashSet<int>();
        // Explicit inventory types (boots, gloves, chest, cloak…): bit n = inventory type n.
        for (var inv = 1; inv < 32; inv++)
        {
            if ((invTypes & (1 << inv)) == 0) continue;
            foreach (var s in InventorySlots(inv)) slots.Add(s);
        }
        if (slots.Count == 0)
        {
            if (itemClass == 2) // weapons; ranged subclasses (bow 2, gun 3, crossbow 18, wand 19, thrown 16) -> ranged slot
            {
                const int rangedBits = (1 << 2) | (1 << 3) | (1 << 18);
                if (subclassMask == 0 || (subclassMask & ~rangedBits) != 0) { slots.Add(16); slots.Add(17); }
                if (subclassMask != 0 && (subclassMask & rangedBits) != 0) slots.Add(18);
            }
            else if (itemClass == 4 && (subclassMask & (1 << 6)) != 0)
            {
                slots.Add(17); // shields
            }
        }
        return slots;
    }

    private static int[] InventorySlots(int inventoryType) => inventoryType switch
    {
        1 => [1], 2 => [2], 3 => [3], 4 => [4], 5 or 20 => [5], 6 => [6], 7 => [7], 8 => [8], 9 => [9], 10 => [10],
        11 => [11, 12], 12 => [13, 14], 13 => [16, 17], 14 or 22 or 23 => [17], 15 or 25 or 26 => [18], 16 => [15],
        17 or 21 => [16], 19 => [19],
        _ => [],
    };
}
