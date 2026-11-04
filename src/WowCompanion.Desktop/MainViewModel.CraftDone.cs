using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace WowCompanion.Desktop;

/// <summary>Craft list while crafting: tick recipes off one by one, or all at once, and clear the done ones.</summary>
public partial class MainViewModel
{
    [ObservableProperty] private bool _hasDoneCrafts;

    [RelayCommand]
    private void ToggleCraftDone(CraftRow? row)
    {
        if (row is null) return;
        _craft.SetDone(row.SpellId, !row.Done);
        RefreshCraftList();
    }

    [RelayCommand]
    private void CompleteAllCraft()
    {
        if (CraftList.Count == 0) return;
        _craft.CompleteAll();
        RefreshCraftList();
        Toast("Everything on the craft list is done.");
    }

    [RelayCommand]
    private void ClearCompletedCraft()
    {
        _craft.ClearCompleted();
        RefreshCraftList();
    }
}
