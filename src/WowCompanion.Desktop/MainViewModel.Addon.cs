using System.IO;
using WowCompanion.Core;

namespace WowCompanion.Desktop;

/// <summary>
/// What the WoWOracle addon saved in game (SavedVariables\WoWOracle.lua), read on every database load:
/// - quests: kept in user.sqlite (so the addon file can be cleared) and added to the game data when it lacks them;
/// - talent trees: a class's tree replaces the downloaded one only when the game client's build is newer;
/// - sightings: new NPCs, vendors and loot (MainViewModel.Seen.cs).
/// wago.tools and the CMaNGOS data stay the master; the addon only fills gaps.
/// </summary>
public partial class MainViewModel
{
    private readonly LearnedQuestStore _learnedQuests = new(paths.UserDbPath);

    private List<string> ReadAddonFiles()
    {
        var texts = new List<string>();
        try
        {
            foreach (var file in AddonSavedData.FindFiles(paths.Root))
            {
                try { texts.Add(File.ReadAllText(file)); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (Exception) { }
        return texts;
    }

    /// <summary>Quests from the addon into user.sqlite, their reward items into the learned items.</summary>
    /// <returns>Quests seen for the first time.</returns>
    private int LearnAddonQuests(IEnumerable<string> addonTexts)
    {
        try
        {
            var quests = addonTexts
                .SelectMany(text => AddonSavedData.Blocks(text, LearnedQuest.Marker))
                .Select(LearnedQuest.Parse).OfType<LearnedQuest>()
                .ToList();
            if (quests.Count == 0) return 0;
            var fresh = _learnedQuests.Save(quests);
            var items = quests.SelectMany(q => q.RewardItems()).DistinctBy(i => i.Id).ToList();
            if (items.Count > 0) _learnedItems.Save(items);
            return fresh;
        }
        catch (Exception)
        {
            return 0; // addon data is a bonus
        }
    }

    private QuestInjectResult InjectLearnedQuests(string gameDbPath)
    {
        try { return LearnedQuestStore.InjectInto(gameDbPath, _learnedQuests.All()); }
        catch (Exception) { return new QuestInjectResult([], []); }
    }

    /// <returns>Classes whose tree came from the client (e.g. "Mage"), with the client build.</returns>
    private (List<string> Classes, int ClientBuild) ApplyAddonTalents(string gameDbPath, string version, IEnumerable<string> addonTexts)
    {
        var applied = new List<string>();
        var clientBuild = 0;
        try
        {
            var dataBuild = TalentOverlay.BuildNumber(version);
            var newest = new Dictionary<string, TalentLayout>(StringComparer.OrdinalIgnoreCase);
            foreach (var block in addonTexts.SelectMany(text => AddonSavedData.Blocks(text, TalentLayout.Marker)))
            {
                if (TalentLayout.Parse(block) is not { } layout) continue;
                if (!newest.TryGetValue(layout.ClassFile, out var have) || layout.Build > have.Build)
                    newest[layout.ClassFile] = layout;
            }

            foreach (var layout in newest.Values.Where(l => l.Build > dataBuild))
            {
                if (TalentOverlay.Apply(gameDbPath, layout) <= 0) continue;
                applied.Add(Pretty(layout.ClassFile));
                clientBuild = Math.Max(clientBuild, layout.Build);
            }
        }
        catch (Exception)
        {
            // The downloaded talents stay as they are.
        }
        return (applied, clientBuild);

        static string Pretty(string classFile) =>
            classFile.Length == 0 ? classFile : char.ToUpperInvariant(classFile[0]) + classFile[1..].ToLowerInvariant();
    }

    /// <summary>What one pass over the addon's saved files brought in (worked out off the UI thread).</summary>
    private sealed record AddonPassResult(
        List<string> Texts, int NewQuests, int Items, QuestInjectResult Quests, int NewSightings, SeenInjectResult Sightings,
        List<string> TalentClasses, int ClientBuild, KnownIdsResult Known);

    // One pass at a time: start-up and the file watcher both write to the game database.
    private readonly SemaphoreSlim _addonPassLock = new(1, 1);

    /// <summary>
    /// Reads the addon's saved files once and fills the game database's gaps: learn (user.sqlite), then add
    /// items, quests, sightings and (at start-up) talent trees, then tell the addon what the data now knows.
    /// Touches no UI, so it runs on a worker thread.
    /// </summary>
    private AddonPassResult AddonPass(string gameDbPath, string version, bool talents)
    {
        var texts = ReadAddonFiles();
        if (gameDbPath.Length == 0)
            return new AddonPassResult(texts, 0, 0, new QuestInjectResult([], []), 0, new SeenInjectResult(0, 0, 0, 0, 0, 0), [], 0, KnownIdsResult.None);

        var newQuests = LearnAddonQuests(texts);
        var items = InjectLearnedItems(gameDbPath); // items from character imports and quest rewards
        var quests = InjectLearnedQuests(gameDbPath);
        var newSightings = LearnAddonSightings(texts);
        var sightings = InjectSightings(gameDbPath); // new NPCs, vendors, loot
        (List<string> Classes, int Build) talentTrees = talents ? ApplyAddonTalents(gameDbPath, version, texts) : (new List<string>(), 0);
        // After the gaps are filled, so the addon doesn't record them again; only rebuilt when something changed.
        var changed = items > 0 || quests.Added.Count + quests.Updated.Count > 0 || sightings.Any;
        var known = WriteKnownIds(gameDbPath, force: changed);
        return new AddonPassResult(texts, newQuests, items, quests, newSightings, sightings, talentTrees.Classes, talentTrees.Build, known);
    }

    /// <summary>Start-up: the addon pass in the background, then the planner and the file watcher.</summary>
    /// <returns>The addon's file texts, for the other start-up steps (read once).</returns>
    private async Task<List<string>> UseAddonDataAsync(string gameDbPath, string version)
    {
        AddonPassResult result;
        await _addonPassLock.WaitAsync();
        try { result = await Task.Run(() => AddonPass(gameDbPath, version, talents: true)); }
        finally { _addonPassLock.Release(); }

        ReportAddonPass(result, version);
        UsePlanner(gameDbPath, result.Texts);
        WatchAddonFiles();
        return result.Texts;
    }

    /// <summary>Chronicle lines and one toast for what a pass brought in (UI thread).</summary>
    private void ReportAddonPass(AddonPassResult r, string version)
    {
        static string Names(List<string> titles) =>
            string.Join(", ", titles.Take(8)) + (titles.Count > 8 ? $" and {titles.Count - 8} more" : "");

        if (r.NewQuests > 0)
            Log($"Addon: {Plural(r.NewQuests, "new quest")} read from the game (kept in the app's own data).", "addon");
        if (r.Quests.Added.Count > 0)
            Log($"Quests added (not in the quest data): {Names(r.Quests.Added)}.", "addon");
        if (r.Quests.Updated.Count > 0)
            Log($"Quests updated from a newer capture: {Names(r.Quests.Updated)}.", "addon");
        if (r.Items > 0)
            Log($"{Plural(r.Items, "item")} from the game client added to the item data.", "addon");
        if (r.TalentClasses.Count > 0)
            Log($"Talent trees from the game client (build {r.ClientBuild}, newer than {TalentOverlay.BuildNumber(version)}): {string.Join(", ", r.TalentClasses.Order())}.", "addon");
        var sightings = ReportSightings(r.NewSightings, r.Sightings);
        ReportKnownIds(r.Known);

        var parts = new List<string>();
        if (r.Quests.Added.Count > 0) parts.Add(Plural(r.Quests.Added.Count, "quest"));
        if (r.Items > 0) parts.Add(Plural(r.Items, "item"));
        if (r.TalentClasses.Count > 0) parts.Add($"{string.Join(", ", r.TalentClasses.Order())} talents");
        if (sightings is not null) parts.Add(sightings);
        if (parts.Count > 0) ToastOnly($"From the game client: {string.Join(" and ", parts)}.");
    }

    private static string Plural(int n, string word) => n == 1 ? $"1 {word}" : $"{n} {word}s";
}
