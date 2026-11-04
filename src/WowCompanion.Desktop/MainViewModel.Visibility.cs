using CommunityToolkit.Mvvm.ComponentModel;
using WowCompanion.Core;

namespace WowCompanion.Desktop;

/// <summary>"Hide Season of Discovery leftovers and test items" (search filters popup), on by default.</summary>
public partial class MainViewModel
{
    private bool _visibilityLoaded;

    [ObservableProperty] private bool _hideSodItems = true;

    private void LoadVisibilitySetting()
    {
        if (_visibilityLoaded) return;
        _visibilityLoaded = true;
        HideSodItems = _talentBuilds.GetSetting("hide_sod") != "0";
        ItemVisibility.HideSod = HideSodItems;
    }

    partial void OnHideSodItemsChanged(bool value)
    {
        ItemVisibility.HideSod = value;
        if (!_visibilityLoaded) return;
        _talentBuilds.SetSetting("hide_sod", value ? "1" : "0");
        if (EffectiveQuery.Length > 0) _ = RunSearchAsync(EffectiveQuery);
        RefreshRecipes();
        QueuePlan();
    }

    /// <summary>Items Forever is seen to use come off the hidden list: learned from the client, worn in a gear set, quest rewards captured in game.</summary>
    private void UnhideSeenItems(string gameDbPath)
    {
        try
        {
            var ids = new List<int>();
            ids.AddRange(_learnedItems.All().Select(i => i.Id));
            foreach (var set in _gearSets.List()) ids.AddRange(_gearSets.GetItems(set.Id).Values);
            foreach (var quest in _learnedQuests.All()) { ids.AddRange(quest.Rewards); ids.AddRange(quest.Choices); }
            ItemVisibility.Unhide(gameDbPath, ids);
        }
        catch (Exception)
        {
            // Best effort.
        }
    }
}
