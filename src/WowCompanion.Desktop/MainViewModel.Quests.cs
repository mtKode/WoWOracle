using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace WowCompanion.Desktop;

/// <summary>Quests in the big search: a quick card (who, where, chain, rewards) on the Items page.</summary>
public partial class MainViewModel
{
    /// <summary>The quest card's full quest text is folded away until asked for.</summary>
    [ObservableProperty] private bool _questTextOpen;

    /// <returns>False when the quest isn't in the data.</returns>
    private bool ShowQuest(int questId)
    {
        var card = _questService?.Card(questId);
        if (card is null) return false;
        Selected = null;
        QuestTextOpen = false;
        SelectedQuest = QuestView.From(card);
        return true;
    }

    /// <summary>Another quest of the chain.</summary>
    [RelayCommand]
    private void OpenQuest(QuestStepRow? step)
    {
        if (step is null || step.IsThis) return;
        if (ShowQuest(step.QuestId)) CurrentView = AppView.Items;
    }

    /// <summary>Loot tab, dungeon quests: the quest's heading opens its card on the Items page.</summary>
    [RelayCommand]
    private void OpenLootQuest(LootSection? section)
    {
        if (section is null || !section.IsQuest) return;
        if (ShowQuest(section.QuestId)) CurrentView = AppView.Items;
    }

    [RelayCommand]
    private void ToggleQuestText() => QuestTextOpen = !QuestTextOpen;

    [RelayCommand]
    private void OpenQuestReward(QuestRewardRow? reward)
    {
        if (reward is not null) OpenItem(reward.ItemId);
    }
}
