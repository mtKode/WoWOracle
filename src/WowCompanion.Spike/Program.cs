using WowCompanion.Core;
using WowCompanion.Data;

// Data check before any UI work.
//   dotnet run --project src/WowCompanion.Spike -- builds
//   dotnet run --project src/WowCompanion.Spike -- fetch  [--product=<code>] [--prefix=1.60.]
//   dotnet run --project src/WowCompanion.Spike -- report [--build=<full version>] [--categories=9,11]

var command = args.FirstOrDefault() ?? "help";
var options = new DataSourceOptions
{
    Product = Arg("product") ?? new DataSourceOptions().Product,
    VersionPrefix = Arg("prefix"),
};
var paths = AppPaths.Default();
paths.EnsureCreated();

using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
http.DefaultRequestHeaders.UserAgent.ParseAdd("WowCompanion-Spike/0.1 (personal tool)");
var wago = new WagoClient(http, options);

string[] tables = ["Item", "ItemSparse", "SkillLine", "SkillLineAbility", "SpellName", "SpellEffect", "SpellReagents"];

// Candidate tables for items, spells, talents and character stats. Missing ones are reported, not fatal.
string[] exploreTables =
[
    // items
    "ItemArmorQuality", "ItemArmorTotal", "ItemArmorShield", "ItemDamageOneHand", "ItemDamageOneHandCaster",
    "ItemDamageTwoHand", "ItemDamageTwoHandCaster", "ItemDamageAmmo", "ItemDamageThrown", "ItemDamageWand",
    "ItemEffect", "ItemXItemEffect", "ArmorLocation", "SpellRadius", "ItemSet", "ItemSetSpell", "ItemClass", "ItemSubClass", "ItemNameDescription",
    // spells
    "Spell", "SpellMisc", "SpellPower", "SpellCastTimes", "SpellDuration", "SpellRange", "SpellCooldowns",
    "SpellCategories", "SpellCategory", "SpellClassOptions", "SpellLevels", "SpellAuraOptions", "SpellScaling",
    "SpellProcsPerMinute", "SpellEquippedItems", "SpellShapeshift",
    // talents (classic style, then modern style)
    "Talent", "TalentTab", "TalentTree", "TalentRankInfo", "TraitTree", "TraitNode", "TraitNodeEntry",
    "TraitNodeXTraitNodeEntry", "TraitDefinition", "TraitEdge", "TraitCost", "TraitTreeLoadout",
    "ChrSpecialization", "SpecializationSpells",
    // classes, races, stats
    "ChrClasses", "ChrRaces", "ChrClassRaceSex", "ChrClassesXPowerTypes", "PowerType",
    "ExpectedStat", "ExpectedStatMod", "ContentTuning", "Curve", "CurvePoint", "ChrBaseStats", "CombatRatings",
];

switch (command)
{
    case "builds": await ListBuilds(); break;
    case "fetch": await Fetch(); break;
    case "report": Report(); break;
    case "explore": await Explore(); break;
    default:
        Console.WriteLine("Commands: builds | fetch | report");
        Console.WriteLine("Options:  --product=<code>  --prefix=<1.60.>  --build=<full version>  --categories=9,11");
        break;
}
return;

string? Arg(string name) => args
    .Select(a => a.TrimStart('-'))
    .Where(a => a.StartsWith(name + "=", StringComparison.Ordinal))
    .Select(a => a[(name.Length + 1)..])
    .FirstOrDefault();

async Task ListBuilds()
{
    var all = await wago.GetBuildsAsync();
    foreach (var group in all.GroupBy(b => b.Product).OrderBy(g => g.Key))
    {
        var newest = group.OrderByDescending(b => WagoClient.ParseVersion(b.Version)).First();
        Console.WriteLine($"{group.Key,-30} {group.Count(),4} builds   newest {newest.Version}");
    }
}

async Task Fetch()
{
    var latest = await wago.GetLatestBuildAsync()
        ?? throw new InvalidOperationException($"No build for product '{options.Product}'. Run 'builds' to see the product codes.");
    Console.WriteLine($"Latest {options.Product} build: {latest.Version}");
    foreach (var table in tables)
    {
        var path = await wago.DownloadTableAsync(table, latest.Version, paths.CacheDir);
        Console.WriteLine($"  {table,-18} {new FileInfo(path).Length / 1024,8:N0} KB");
    }
}

string CachedDir()
{
    var requested = Arg("build");
    if (requested is not null) return Path.Combine(paths.CacheDir, requested);

    var newest = Directory.EnumerateDirectories(paths.CacheDir)
        .Select(Path.GetFileName)
        .OfType<string>()
        .OrderByDescending(WagoClient.ParseVersion)
        .FirstOrDefault()
        ?? throw new InvalidOperationException("Nothing cached yet. Run 'fetch' first.");
    return Path.Combine(paths.CacheDir, newest);
}

void Report()
{
    var dir = CachedDir();
    Console.WriteLine($"Report for build {Path.GetFileName(dir)}\n");
    foreach (var table in tables) DescribeTable(dir, table);
    ReportItems(dir);
    ReportProfessions(dir);
}

void DescribeTable(string dir, string table)
{
    var path = Path.Combine(dir, table + ".csv");
    if (!File.Exists(path)) { Console.WriteLine($"{table}: missing (run fetch)"); return; }
    Console.WriteLine($"{table}: {CsvTable.Read(path).Count():N0} rows");
    Console.WriteLine($"  columns: {string.Join(", ", CsvTable.ReadHeader(path))}\n");
}

void ReportItems(string dir)
{
    var path = Path.Combine(dir, "ItemSparse.csv");
    if (!File.Exists(path)) return;

    int total = 0, kept = 0;
    var removed = new List<string>();
    foreach (var row in CsvTable.Read(path))
    {
        total++;
        var name = row.Get("Display_lang").Trim();
        if (ItemFilters.IsJunkName(name))
        {
            if (name.Length > 0 && removed.Count < 20) removed.Add($"{row.Get("ID")}: {name}");
        }
        else kept++;
    }
    Console.WriteLine($"Items: {total:N0} rows, {kept:N0} kept after the name filter, {total - kept:N0} removed");
    Console.WriteLine("Sample of removed names (check nothing real is in here):");
    foreach (var line in removed) Console.WriteLine($"  {line}");
    Console.WriteLine();
}

void ReportProfessions(string dir)
{
    var skillLinePath = Path.Combine(dir, "SkillLine.csv");
    var abilityPath = Path.Combine(dir, "SkillLineAbility.csv");
    if (!File.Exists(skillLinePath) || !File.Exists(abilityPath)) return;

    var categories = (Arg("categories") ?? "9,11").Split(',').Select(int.Parse).ToHashSet();

    var lines = new Dictionary<int, (string Name, int Category)>();
    foreach (var row in CsvTable.Read(skillLinePath))
        lines[row.GetInt("ID")] = (row.Get("DisplayName_lang"), row.GetInt("CategoryID"));

    var spellsBySkill = new Dictionary<int, HashSet<int>>();
    foreach (var row in CsvTable.Read(abilityPath))
    {
        var skill = row.GetInt("SkillLine");
        if (!spellsBySkill.TryGetValue(skill, out var set)) spellsBySkill[skill] = set = [];
        set.Add(row.GetInt("Spell"));
    }

    Console.WriteLine($"Skill lines in categories {string.Join(",", categories)}:");
    var allSpells = new HashSet<int>();
    foreach (var (id, line) in lines.OrderBy(l => l.Key))
    {
        if (!categories.Contains(line.Category) || !spellsBySkill.TryGetValue(id, out var spells)) continue;
        Console.WriteLine($"  {id,5}  {line.Name,-26} category {line.Category,2}  {spells.Count,5} spells");
        allSpells.UnionWith(spells);
    }
    Console.WriteLine($"Distinct spells across those skill lines: {allSpells.Count:N0}\n");

    // Chain check: skill -> recipe spell -> created item -> reagents, for the biggest profession.
    var biggest = spellsBySkill
        .Where(kv => lines.TryGetValue(kv.Key, out var l) && categories.Contains(l.Category))
        .OrderByDescending(kv => kv.Value.Count)
        .FirstOrDefault();
    if (biggest.Value is null) return;

    var needed = new[] { "SpellName", "SpellEffect", "SpellReagents", "ItemSparse" }
        .Select(t => Path.Combine(dir, t + ".csv")).ToArray();
    if (needed.Any(p => !File.Exists(p))) { Console.WriteLine("Chain check skipped: missing tables."); return; }

    var sample = biggest.Value.Take(5).ToHashSet();
    var spellNames = ReadNames(needed[0], "ID", "Name_lang");
    var itemNames = ReadNames(needed[3], "ID", "Display_lang");

    var created = new Dictionary<int, int>();
    foreach (var row in CsvTable.Read(needed[1]))
        if (row.GetInt("Effect") == 24 && sample.Contains(row.GetInt("SpellID")))
            created.TryAdd(row.GetInt("SpellID"), row.GetInt("EffectItemType"));

    var reagents = new Dictionary<int, List<(int Item, int Count)>>();
    foreach (var row in CsvTable.Read(needed[2]))
    {
        var spell = row.GetInt("SpellID");
        if (!sample.Contains(spell)) continue;
        for (var i = 0; i < 8; i++)
        {
            var item = row.GetInt($"Reagent_{i}");
            if (item <= 0) continue;
            if (!reagents.TryGetValue(spell, out var list)) reagents[spell] = list = [];
            list.Add((item, row.GetInt($"ReagentCount_{i}")));
        }
    }

    Console.WriteLine($"Chain check for skill line {biggest.Key} ({lines[biggest.Key].Name}):");
    foreach (var spell in sample)
    {
        var made = created.TryGetValue(spell, out var itemId)
            ? itemNames.GetValueOrDefault(itemId, $"item {itemId}") : "(no create-item effect)";
        var needs = reagents.TryGetValue(spell, out var list)
            ? string.Join(", ", list.Select(r => $"{r.Count}x {itemNames.GetValueOrDefault(r.Item, $"item {r.Item}")}"))
            : "(no reagents listed)";
        Console.WriteLine($"  {spellNames.GetValueOrDefault(spell, $"spell {spell}")} -> {made}; needs {needs}");
    }
}

Dictionary<int, string> ReadNames(string path, string idColumn, string nameColumn)
{
    var map = new Dictionary<int, string>();
    foreach (var row in CsvTable.Read(path)) map[row.GetInt(idColumn)] = row.Get(nameColumn);
    return map;
}

// Downloads the candidate tables and prints columns, row counts and two sample rows each.
// Redirect to a file and upload it: dotnet run --project src\WowCompanion.Spike -- explore > explore.txt
async Task Explore()
{
    var latest = await wago.GetLatestBuildAsync()
        ?? throw new InvalidOperationException($"No build for product '{options.Product}'.");
    Console.WriteLine($"Build {latest.Version}\n");

    var wanted = (Arg("tables") ?? string.Join(',', exploreTables))
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    foreach (var table in wanted)
    {
        string path;
        try
        {
            path = await wago.DownloadTableAsync(table, latest.Version, paths.CacheDir);
        }
        catch (HttpRequestException ex)
        {
            Console.WriteLine($"## {table}: not available ({ex.StatusCode})\n");
            continue;
        }

        var header = CsvTable.ReadHeader(path);
        var samples = CsvTable.Read(path).Take(2).ToList();
        Console.WriteLine($"## {table}: {CsvTable.Read(path).Count():N0} rows");
        Console.WriteLine($"columns: {string.Join(", ", header)}");
        foreach (var row in samples)
        {
            var filled = header
                .Select(h => (Name: h, Value: row.Get(h)))
                .Where(x => x.Value.Length > 0 && x.Value != "0")
                .Select(x => $"{x.Name}={(x.Value.Length > 80 ? x.Value[..80] + "..." : x.Value)}");
            Console.WriteLine($"sample: {string.Join(", ", filled)}");
        }
        Console.WriteLine();
    }
}
