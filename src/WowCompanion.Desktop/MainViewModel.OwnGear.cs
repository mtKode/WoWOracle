using System.Security.Cryptography;
using System.Text;
using WowCompanion.Core;

namespace WowCompanion.Desktop;

/// <summary>
/// Your own gear and talents from the addon (!WOWORACLE-SELF blocks, saved by the game on login, gear or talent
/// changes and logout): kept in a gear set and a talent set named exactly after the character. Only that exact name
/// is updated, so copies under other names ("Thrall Alt SP") stay as you left them.
/// </summary>
public partial class MainViewModel
{
    private const string SelfMarker = "!WOWORACLE-SELF";

    private void SyncOwnGear(IEnumerable<string> addonTexts)
    {
        if (_calcData is null || _talentService is null) return;
        try
        {
            // Newest export per character (several WoW folders or accounts may hold one).
            var exports = new Dictionary<string, (CharacterExport Export, string Text, long Time)>(StringComparer.Ordinal);
            foreach (var block in addonTexts.SelectMany(t => AddonSavedData.Blocks(t, SelfMarker)))
            {
                var text = block[(block.IndexOf('\n') + 1)..];
                if (!CharacterImport.TryParse(text, out var export, out _) || export is null || export.FromInspect) continue;
                // No items and no health: saved while logging out (addon 0.10.0 and older). Keep the set as it is.
                if (export.Items.Count == 0 && export.Stats.GetValueOrDefault("hp") is null or "0") continue;
                var time = TimeOf(text);
                if (!exports.TryGetValue(export.Name, out var have) || time > have.Time) exports[export.Name] = (export, text, time);
            }

            var updated = new List<string>();
            int? reselect = null;
            foreach (var (name, (export, text, _)) in exports)
            {
                var key = $"autogear:{name}";
                var fingerprint = Fingerprint(text);
                if (_gearSets.GetSetting(key) == fingerprint) continue; // nothing changed since the last sync

                var cls = TalentClasses.FirstOrDefault(c => c.Id == export.ClassId);
                if (cls is null) continue;
                var race = Races.FirstOrDefault(r => r.Id == export.RaceId)
                           ?? Races.FirstOrDefault(r => string.Equals(r.Name, export.RaceName, StringComparison.OrdinalIgnoreCase));
                var notes = new List<string>();
                var (setId, placed, talentNote) = ApplyExport(export, cls, race, name, replaceExact: true, notes);
                if (setId == 0) continue;
                _gearSets.SetSetting(key, fingerprint);
                updated.Add($"{name} ({placed} items, {talentNote})");
                if (SelectedGearSet?.Id == setId || SelectedGearSet is null) reselect = setId;
            }

            if (updated.Count == 0) return;
            ReloadGearSets(reselect ?? SelectedGearSet?.Id);
            if (SelectedTalentClass is not null) LoadTalentSets(SelectedTalentSet?.Id);
            Log($"Gear and talents from the game: {string.Join(", ", updated)}.", "addon");
        }
        catch (Exception ex)
        {
            Log("Couldn't update your gear from the addon: " + ex.Message, "warn");
        }
    }

    private static long TimeOf(string exportText)
    {
        foreach (var line in exportText.Split('\n'))
            if (line.StartsWith("time=", StringComparison.Ordinal) && long.TryParse(line[5..].Trim(), out var t)) return t;
        return 0;
    }

    /// <summary>The export without its save time: the same gear saved again isn't a change.</summary>
    private static string Fingerprint(string exportText)
    {
        var content = string.Join("\n", exportText.Split('\n').Where(l => !l.StartsWith("time=", StringComparison.Ordinal)).Select(l => l.TrimEnd('\r')));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)))[..20];
    }
}
