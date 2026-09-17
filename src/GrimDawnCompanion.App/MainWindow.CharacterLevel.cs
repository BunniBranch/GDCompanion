using System.Windows;
using GrimDawnCompanion.Core;

namespace GrimDawnCompanion.App;

public partial class MainWindow
{
    private bool _characterLevelBusy;
    private async void SetCharacterLevel(object sender, RoutedEventArgs e)
    {
        if (_characterLevelBusy || _characterResourceBusy || _viewModel.IsBusy) return;
        if (!uint.TryParse(CharacterLevelBox.Text, out var target) || target is < 2 or > CharacterLevelPreparation.HardLimit)
        { MessageBox.Show("Enter a whole target level higher than the current level and within the game's supported level cap.", "Invalid level"); return; }
        _characterLevelBusy = true;
        SetCharacterLevelButton.IsEnabled = false;
        try
        {
            var limits=await RefreshResourceLimitsAsync();
            if(target>limits.LevelCap || target<=limits.Level) throw new InvalidOperationException($"Choose a level above {limits.Level} and no higher than this game's cap of {limits.LevelCap}.");
            var prepared = await _bridge.PrepareCharacterLevelAsync(target, _lifetime.Token);
            CharacterLevelStatusText.Text = $"{prepared.Name} • Level {prepared.Current} → {prepared.Target} • Available cap {prepared.Cap}";
            if (MessageBox.Show($"Raise {prepared.Name} from level {prepared.Current} to {prepared.Target}?\n\n" +
                $"Current game or mod level cap: {prepared.Cap}\n\n" +
                "This uses the game's level-up process, including XP and point rewards, and may unlock level-related achievements. Companion cannot undo this change.\n\n" +
                "Back up the character first. Keep a separate, restorable copy; Cloud saving is not a backup.",
                "Confirm Character Level", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            var level = await _bridge.CommitCharacterLevelAsync(prepared, _lifetime.Token);
            CharacterLevelStatusText.Text = $"{prepared.Name} is now level {level}.";
            _viewModel.Resources = await _bridge.GetResourcesAsync(_lifetime.Token);
            await RefreshResourceLimitsAsync();
            SetStatus("Character level updated", CharacterLevelStatusText.Text, 100);
        }
        catch (Exception ex)
        {
            var explanation = ex.Message.Contains("LEVEL_REMOVE_XP_BONUSES")
                ? "Remove equipment and let buffs granting bonus experience expire before setting a level. The tool will not remove them for you."
                : ex.Message.Contains("LEVEL_EXCEEDS_GAME_CAP")
                    ? "That target exceeds this character's active game or mod level cap. The tool does not override it."
                    : ex.Message.Contains("LEVEL_REWARDS_EXCEED_POINT_BUDGET")
                        ? "Level-up rewards would exceed the endgame point budget. Reduce unspent skill/attribute points with the Set controls first."
                        : ex.Message;
            CharacterLevelStatusText.Text = explanation;
            MessageBox.Show(explanation, "Character level not completed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { _characterLevelBusy = false; SetCharacterLevelButton.IsEnabled = true; }
    }
}
