using GrimDawnCompanion.Core;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace GrimDawnCompanion.App;

public partial class MainWindow
{
    private DispatcherTimer? _assistTimer, _assistSpeedTimer;
    private readonly SemaphoreSlim _assistGate = new(1, 1);
    private GameplayAssistStatus? _assistStatus;
    private bool _assistChanging, _assistRendering, _assistSuspended;
    private int _assistOperation;

    private void StartGameplayAssistUi()
    {
        ++_assistOperation;
        _assistSuspended = false;
        _assistStatus = null;
        ResetAssistSelection();
        AssistOptionsPanel.IsEnabled = false;
        _assistTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(750) };
        _assistTimer.Tick -= PollGameplayAssists;
        _assistTimer.Tick += PollGameplayAssists;
        if (!_bridge.SupportsGameplayAssists)
        {
            AssistStatusText.Text = "Unavailable: reconnect with the new bridge and a supported game build.";
            return;
        }
        _assistTimer.Start();
        PollGameplayAssists(this, EventArgs.Empty);
    }

    private void ResetAssistSelection()
    {
        _assistSpeedTimer?.Stop();
        _assistRendering = true;
        try
        {
            AssistInvincibleBox.IsChecked = AssistEnergyBox.IsChecked = AssistCooldownBox.IsChecked = AssistSpeedBox.IsChecked = false;
            AssistSpeedSlider.Value = 100;
        }
        finally { _assistRendering = false; }
        SetAssistIndicator(false);
    }

    private void SetAssistIndicator(bool active, bool stopping = false)
    {
        AssistIndicatorText.Text = stopping ? "Assists Stopping" : active ? "Assists Active" : "Assists Inactive";
        AssistStatusBadge.Background = (Brush)FindResource(active || stopping ? "AccentDark" : "PanelRaised");
        AssistStatusDot.Fill = (Brush)FindResource(active || stopping ? "Accent" : "SecondaryText");
    }

    private void RenderAssistStatus(GameplayAssistStatus state, bool forceSelection = false)
    {
        if (forceSelection || (!_assistChanging && (_assistStatus is null || state.PlayerId != _assistStatus.PlayerId || state.Mask != _assistStatus.Mask || state.SpeedPercent != _assistStatus.SpeedPercent)))
        {
            _assistSpeedTimer?.Stop();
            _assistRendering = true;
            try
            {
                AssistInvincibleBox.IsChecked = (state.Mask & 1) != 0;
                AssistEnergyBox.IsChecked = (state.Mask & 2) != 0;
                AssistCooldownBox.IsChecked = (state.Mask & 4) != 0;
                AssistSpeedBox.IsChecked = (state.Mask & 8) != 0;
                AssistSpeedSlider.Value = state.SpeedPercent;
            }
            finally { _assistRendering = false; }
        }
        _assistStatus = state;
        SetAssistIndicator(state.Active);
        AssistStatusText.Text = state.PlayerId == 0 ? "Load a single-player character to use assists." :
            state.Active ? "Assists active for the current character. Uncheck an assist to turn it off." : "All assists are off.";
        AssistOptionsPanel.IsEnabled = state.PlayerId != 0 && !_assistSuspended && !_assistChanging;
    }

    private void FailGameplayAssistUi(string message, Exception ex)
    {
        // Never retry an enable or renew a stale lease after uncertainty.
        var mayBeActive = _assistStatus?.Active == true || _assistChanging;
        _assistStatus = null;
        _assistSuspended = true;
        ResetAssistSelection();
        if (mayBeActive) SetAssistIndicator(false, stopping: true);
        AssistOptionsPanel.IsEnabled = false;
        AssistStatusText.Text = message + " Connection renewal has stopped; overrides expire within four seconds. Resume the game to finish movement cleanup, then disconnect and reconnect to recheck. " + ex.Message;
    }

    private async void PollGameplayAssists(object? sender, EventArgs e)
    {
        if (_assistSuspended || _shutdownStarted || !_assistGate.Wait(0)) return;
        var operation = _assistOperation;
        try
        {
            var state = _assistStatus?.Active == true
                ? await _bridge.PulseGameplayAssistsAsync(_assistStatus.Revision, _lifetime.Token)
                : await _bridge.GetGameplayAssistsAsync(_lifetime.Token);
            if (operation == _assistOperation && !_assistSuspended) RenderAssistStatus(state);
        }
        catch (Exception ex)
        {
            if (operation == _assistOperation && !_assistSuspended)
                FailGameplayAssistUi("Assists stopped or connection check lost.", ex);
        }
        finally { _assistGate.Release(); }
    }

    private async void ToggleGameplayAssist(object sender, RoutedEventArgs e)
    {
        // Click is user-only: rendering checked states must never issue commands.
        if (_assistChanging || _assistSuspended || _assistStatus is null || _assistStatus.PlayerId == 0) return;
        var box = (CheckBox)sender;
        uint flag = box == AssistInvincibleBox ? 1u : box == AssistEnergyBox ? 2u : box == AssistCooldownBox ? 4u : 8u;
        var change = GameplayAssistSelection.Toggle(_assistStatus, flag, box.IsChecked == true, (uint)AssistSpeedSlider.Value);
        await ChangeGameplayAssistsAsync(_assistStatus, change);
    }

    private void GameplayAssistSpeedChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_assistRendering || _assistChanging || _assistSuspended || _assistStatus is null || (_assistStatus.Mask & 8) == 0) return;
        _assistSpeedTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _assistSpeedTimer.Tick -= CommitGameplayAssistSpeed;
        _assistSpeedTimer.Tick += CommitGameplayAssistSpeed;
        _assistSpeedTimer.Stop();
        _assistSpeedTimer.Start();
    }

    private async void CommitGameplayAssistSpeed(object? sender, EventArgs e)
    {
        _assistSpeedTimer?.Stop();
        if (_assistChanging || _assistSuspended || _assistStatus is null || (_assistStatus.Mask & 8) == 0) return;
        await ChangeGameplayAssistsAsync(_assistStatus,
            GameplayAssistSelection.Toggle(_assistStatus, 8, true, (uint)AssistSpeedSlider.Value));
    }

    private async Task ChangeGameplayAssistsAsync(GameplayAssistStatus inspected, GameplayAssistSelection change)
    {
        _assistSpeedTimer?.Stop();
        _assistChanging = true;
        AssistOptionsPanel.IsEnabled = false;
        var operation = _assistOperation;
        var gateHeld = false;
        try
        {
            // Queue behind an in-flight status check instead of silently dropping a click.
            await _assistGate.WaitAsync(_lifetime.Token);
            gateHeld = true;
            if (operation != _assistOperation || _assistSuspended || _shutdownStarted || !change.MatchesContext(inspected, _assistStatus)) return;
            if (change.Mask == inspected.Mask && change.SpeedPercent == inspected.SpeedPercent) return;
            AssistStatusText.Text = "Updating gameplay assist…";
            var result = change.Mask == 0
                ? await _bridge.StopGameplayAssistsAsync(_lifetime.Token)
                : await _bridge.SetGameplayAssistsAsync(inspected.Revision, change.Mask, change.SpeedPercent, _lifetime.Token);
            if (operation == _assistOperation && !_assistSuspended) RenderAssistStatus(result, true);
        }
        catch (Exception ex)
        {
            if (operation == _assistOperation && !_assistSuspended)
                FailGameplayAssistUi("The assist change was not confirmed.", ex);
        }
        finally
        {
            if (gateHeld) _assistGate.Release();
            _assistChanging = false;
            // A stale context restores the latest confirmed state.
            if (!_assistSuspended && _assistStatus is not null) RenderAssistStatus(_assistStatus, true);
        }
    }

    private void SuspendGameplayAssistUi()
    {
        var mayBeActive = _assistStatus?.Active == true || _assistChanging;
        ++_assistOperation;
        _assistSuspended = true;
        _assistTimer?.Stop();
        _assistStatus = null;
        ResetAssistSelection();
        if (mayBeActive) SetAssistIndicator(false, stopping: true);
        AssistOptionsPanel.IsEnabled = false;
        AssistStatusText.Text = "Gameplay assists are off or stopping. Reconnect to check availability.";
    }
}
