namespace WowCompanion.Data;

/// <summary>
/// Weapon damage and armor are not stored per item. The client works them out from item level, quality and
/// slot using lookup tables, and so do we.
///   damage: DPS table [item level][quality] x speed, spread by DmgVariance (checked: Arcanite Reaper 153-256, Sulfuras 223-372)
///   armor:  ItemArmorTotal [item level][material] x ItemArmorQuality [item level][quality] x ArmorLocation [slot][material]
///           (checked: Lionheart Helm 565). Shields come straight from ItemArmorShield.
/// Ranged weapons use the Thrown table; that one is a best guess until compared with in-game tooltips.
/// </summary>
public sealed class ItemNumbers
{
    private readonly Dictionary<string, Dictionary<int, double[]>> _damage = new();
    private readonly Dictionary<int, double[]> _armorTotal = new();   // ilvl -> cloth, leather, mail, plate
    private readonly Dictionary<int, double[]> _armorQuality = new(); // ilvl -> quality 0..6
    private readonly Dictionary<int, double[]> _shield = new();       // ilvl -> quality 0..6
    private readonly Dictionary<int, double[]> _location = new();     // inventory type -> cloth, leather, mail, plate

    public List<string> Warnings { get; } = [];

    // Share of a full armor set per slot, used only when ArmorLocation could not be downloaded.
    // Head is confirmed (Lionheart Helm); the rest follow the same client table as far as known.
    private static readonly Dictionary<int, double> FallbackLocation = new()
    {
        [1] = 0.13, [3] = 0.12, [5] = 0.16, [6] = 0.09, [7] = 0.14, [8] = 0.11,
        [9] = 0.07, [10] = 0.10, [16] = 0.08, [20] = 0.16,
    };

    public static ItemNumbers Load(Func<string, string> csv)
    {
        var n = new ItemNumbers();
        foreach (var table in new[]
                 {
                     "ItemDamageOneHand", "ItemDamageOneHandCaster", "ItemDamageTwoHand", "ItemDamageTwoHandCaster",
                     "ItemDamageAmmo", "ItemDamageThrown", "ItemDamageWand",
                 })
        {
            n._damage[table] = ReadQualityTable(csv(table), "ItemLevel", "Quality_");
        }

        n._armorQuality.Clear();
        foreach (var (key, value) in ReadQualityTable(csv("ItemArmorQuality"), "ID", "Qualitymod_"))
            n._armorQuality[key] = value;
        foreach (var (key, value) in ReadQualityTable(csv("ItemArmorShield"), "ItemLevel", "Quality_"))
            n._shield[key] = value;

        foreach (var row in CsvTable.Read(csv("ItemArmorTotal")))
            n._armorTotal[row.GetInt("ItemLevel")] =
                [row.GetDouble("Cloth"), row.GetDouble("Leather"), row.GetDouble("Mail"), row.GetDouble("Plate")];

        var locationPath = csv("ArmorLocation");
        if (File.Exists(locationPath))
        {
            var cloth = CsvTable.FindColumn(locationPath, "cloth");
            var leather = CsvTable.FindColumn(locationPath, "leather");
            var mail = CsvTable.FindColumn(locationPath, "chain") ?? CsvTable.FindColumn(locationPath, "mail");
            var plate = CsvTable.FindColumn(locationPath, "plate");
            if (cloth is null || leather is null || mail is null || plate is null)
            {
                n.Warnings.Add("ArmorLocation columns not recognised: " + string.Join(", ", CsvTable.ReadHeader(locationPath)));
            }
            else
            {
                foreach (var row in CsvTable.Read(locationPath))
                    n._location[row.GetInt("ID")] =
                        [row.GetDouble(cloth), row.GetDouble(leather), row.GetDouble(mail), row.GetDouble(plate)];
            }
        }
        else
        {
            n.Warnings.Add("ArmorLocation not available; armor uses built-in slot shares.");
        }
        return n;
    }

    /// <returns>(min, max). Ammo returns its bonus DPS as both values with no speed.</returns>
    public (double Min, double Max) WeaponDamage(
        int itemLevel, int quality, int inventoryType, int classId, int subclassId, int flags1, int delayMs, double variance)
    {
        var isWeapon = classId == 2;
        var isAmmo = classId == 6 || inventoryType == 24;
        if (!isWeapon && !isAmmo) return (0, 0);

        var caster = (flags1 & 0x200) != 0;
        var table = isAmmo
            ? "ItemDamageAmmo"
            : inventoryType switch
            {
                17 => caster ? "ItemDamageTwoHandCaster" : "ItemDamageTwoHand",
                13 or 21 or 22 => caster ? "ItemDamageOneHandCaster" : "ItemDamageOneHand",
                15 or 25 or 26 => subclassId == 19 ? "ItemDamageWand" : "ItemDamageThrown",
                _ => null,
            };
        if (table is null || !_damage[table].TryGetValue(itemLevel, out var row)) return (0, 0);

        var dps = row[Math.Clamp(quality, 0, 6)];
        if (isAmmo) return (dps, dps);
        if (delayMs <= 0) return (0, 0);

        var average = dps * delayMs / 1000.0;
        var min = Math.Floor(average * (1 - variance / 2));
        var max = Math.Floor(average * (1 + variance / 2) + 0.5);
        return (Math.Max(1, min), Math.Max(1, max));
    }

    public int Armor(int itemLevel, int quality, int inventoryType, int classId, int subclassId)
    {
        if (classId != 4) return 0;
        var q = Math.Clamp(quality, 0, 6);

        if (subclassId == 6 || inventoryType == 14)
            return _shield.TryGetValue(itemLevel, out var shield) ? (int)Math.Round(shield[q]) : 0;

        // Cloaks are cloth whatever the subclass says.
        var material = inventoryType == 16 ? 0 : subclassId - 1;
        if (material is < 0 or > 3) return 0;
        if (!_armorTotal.TryGetValue(itemLevel, out var total) || !_armorQuality.TryGetValue(itemLevel, out var mod))
            return 0;

        double share;
        if (_location.TryGetValue(inventoryType, out var location)) share = location[material];
        else if (!FallbackLocation.TryGetValue(inventoryType, out share)) return 0;

        return (int)Math.Floor(total[material] * mod[q] * share + 0.5);
    }

    private static Dictionary<int, double[]> ReadQualityTable(string path, string keyColumn, string prefix)
    {
        var result = new Dictionary<int, double[]>();
        foreach (var row in CsvTable.Read(path))
        {
            var values = new double[7];
            for (var q = 0; q < 7; q++) values[q] = row.GetDouble(prefix + q);
            result[row.GetInt(keyColumn)] = values;
        }
        return result;
    }
}
