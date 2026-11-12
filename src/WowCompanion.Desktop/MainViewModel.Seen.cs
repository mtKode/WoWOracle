using System.IO;
using WowCompanion.Core;

namespace WowCompanion.Desktop;

/// <summary>
/// New world data from the addon: NPCs, vendors and loot the game data lacks. The app tells the addon what it
/// already knows (Known.lua in the installed addon), the addon saves only what's new, and the app keeps that in
/// user.sqlite and fills the game database's gaps with it. wago.tools / CMaNGOS rows always win.
/// </summary>
public partial class MainViewModel
{
    private readonly SeenWorldStore _seenWorld = new(paths.UserDbPath);

    /// <returns>Subjects (NPCs, vendors, loot sources) seen for the first time.</returns>
    private int LearnAddonSightings(IEnumerable<string> addonTexts)
    {
        try
        {
            var blocks = addonTexts
                .SelectMany(text => SeenBlock.Markers.SelectMany(marker => AddonSavedData.Blocks(text, marker)))
                .Select(SeenBlock.Parse).OfType<SeenBlock>()
                .ToList();
            return blocks.Count == 0 ? 0 : _seenWorld.Save(blocks);
        }
        catch (Exception)
        {
            return 0; // addon data is a bonus
        }
    }

    private SeenInjectResult InjectSightings(string gameDbPath)
    {
        try { return SeenWorldStore.InjectInto(gameDbPath, _seenWorld.All()); }
        catch (Exception) { return new SeenInjectResult(0, 0, 0, 0, 0, 0); }
    }

    /// <summary>Which addon copies got a new Known.lua, or why none could.</summary>
    private sealed record KnownIdsResult(List<string> Written, string Error)
    {
        public static readonly KnownIdsResult None = new([], "");
    }

    // The game database Known.lua was last worked out for; it is only rebuilt when that changes or data was added.
    private string _knownIdsFor = "";

    /// <summary>
    /// Writes Known.lua into every installed copy of the addon (Forever clients only) when it changed. The list is a
    /// few heavy queries, so it's skipped when this database was already done and nothing was added since.
    /// Safe off the UI thread (no logging here; see ReportKnownIds).
    /// </summary>
    private KnownIdsResult WriteKnownIds(string gameDbPath, bool force)
    {
        if (gameDbPath.Length == 0) return KnownIdsResult.None;
        if (!force && _knownIdsFor == gameDbPath) return KnownIdsResult.None;
        try
        {
            string? content = null;
            var written = new List<string>();
            foreach (var root in WowInstall.Roots(paths.Root))
            foreach (var client in AddonInstaller.Clients(root))
            {
                if (client.InstalledVersion is null) continue; // not installed there
                content ??= KnownIds.Build(gameDbPath);
                if (KnownIds.WriteTo(client.Target, content)) written.Add(client.ClientName);
            }
            _knownIdsFor = gameDbPath;
            return new KnownIdsResult(written, "");
        }
        catch (Exception ex)
        {
            return new KnownIdsResult([], ex.Message);
        }
    }

    private void ReportKnownIds(KnownIdsResult known)
    {
        if (known.Error.Length > 0)
            Log($"Couldn't update the addon's known-data list: {known.Error}", "warn");
        else if (known.Written.Count > 0)
            Log($"Addon's known-data list updated ({string.Join(", ", known.Written)}); it skips those after /reload.", "note");
    }

    /// <summary>Short pieces for what new sightings added to the game data (NPCs, map spots, vendors, drops).</summary>
    private static IEnumerable<string> SightingParts(SeenInjectResult result)
    {
        if (result.Npcs > 0) yield return Plural(result.Npcs, "new NPC");
        if (result.Placed > 0) yield return $"{Plural(result.Placed, "NPC")} placed";
        if (result.VendorItems > 0) yield return Plural(result.VendorItems, "vendor item");
        if (result.LootItems > 0) yield return Plural(result.LootItems, "drop");
    }
}
