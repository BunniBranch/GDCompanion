using System.Windows;
using GrimDawnCompanion.Core;

namespace GrimDawnCompanion.App;

public partial class MainWindow
{
    private bool _masteryAuditBusy;
    private int? _masteryAuditProcess;
    private bool _masteryResetAttempted;
    private MasteryResetPlan? _masteryResetPlan;

    private void UpdateMasteryAuditUi()
    {
        MasteryReplaceButton.IsEnabled = !_masteryAuditBusy && !_masteryResetAttempted &&
            MasteryResetAcknowledgementBox.IsChecked == true && _bridge.IsConnected && _bridge.SupportsRepeatableMasteryReset;
        MasteryOutcomeButton.IsEnabled = !_masteryAuditBusy && _masteryResetPlan is not null &&
            _bridge.IsConnected && _bridge.SupportsExperimentalMasteryReset;
        if (!_bridge.IsConnected || _masteryAuditProcess != _bridge.ProcessId)
        {
            MasteryAuditResult.Text = "No character checked yet. Select Respec Character to inspect the loaded character and review the confirmation.";
            _masteryAuditProcess = _bridge.ProcessId;
        }
        MasteryAuditConnection.Text = !_bridge.IsConnected ? "Connect to Grim Dawn to inspect the loaded character." :
            !_bridge.SupportsMasteryAudit ? "This connection has an older bridge. Restart Grim Dawn and reconnect with this build to enable inspection." :
            !_bridge.SupportsRepeatableMasteryReset ? "Respec unavailable: disconnect and reconnect with this build and a supported game executable." :
            "Ready. Each Respec Character use starts with a read-only inspection and requires confirmation before changes.";
    }

    private void MasteryResetAcknowledgementChanged(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) UpdateMasteryAuditUi();
    }

    private async void PrepareMasteryReset(object sender, RoutedEventArgs e)
    {
        if (_masteryAuditBusy || _masteryResetAttempted || MasteryResetAcknowledgementBox.IsChecked != true || !_bridge.IsConnected || !_bridge.SupportsRepeatableMasteryReset) return;
        _masteryAuditBusy = true; UpdateMasteryAuditUi();
        var process = _bridge.ProcessId;
        try
        {
            var audit = await InspectLoadedCharacterAsync();
            var plan = await _bridge.PrepareMasteryResetAsync(_lifetime.Token);
            if (!_bridge.IsConnected || _bridge.ProcessId != process)
                throw new InvalidOperationException("Connection changed during preparation. No respec was submitted.");
            if (plan.PlayerId != audit.PlayerId || plan.Unspent != audit.UnspentPoints || plan.Refund != audit.AllocatedPoints || plan.Masteries != audit.ActiveMasteries)
                throw new InvalidOperationException("Character changed during inspection. No reset was submitted.");
            if (MessageBox.Show(this, $"Respec both masteries for {audit.CharacterName} (level {audit.Level})?\n\n" +
                $"Devotion refund: {plan.DevotionRefund}; unspent points {plan.DevotionUnspent} → {plan.ExpectedDevotionPoints}.\n" +
                $"Skill and mastery-bar refund: {plan.Refund}; unspent points {plan.Unspent} → {plan.ExpectedPoints}.\n" +
                $"Remove devotion bindings and clear {plan.SelectedClasses} selected mastery slots.\n" +
                "Celestial-power experience is retained.\n\n" +
                "Before you respec:\n" +
                "• Back up the character. Keep a separate, restorable copy; cloud saving is not a backup.\n" +
                "• Remove equipped items that grant skills.\n" +
                "• Open the Skills page once for the loaded character.\n\n" +
                "Each confirmation runs one respec. You may respec again after a fully verified success. A partial or unknown result blocks further attempts.\n\nChoose new masteries in the game's skill window.",
                "Confirm Mastery Respec", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            { MasteryResetResult.Text = "Cancelled. No reset submitted."; return; }
            if (!_bridge.IsConnected || _bridge.ProcessId != process)
                throw new InvalidOperationException("Connection changed before confirmation completed. No respec was submitted.");
            _masteryResetPlan = plan;
            _masteryResetAttempted = true;
            MasteryResetResult.Text = $"Submitting transaction {plan.Token}. Do not change character, skills, or equipment.";
            var result = await _bridge.CommitMasteryResetAsync(plan.Token, _lifetime.Token);
            if (result == "ERROR MASTERY_PLAN_EXPIRED_NO_CHANGES" || result == "ERROR MASTERY_CHARACTER_CHANGED_NO_CHANGES")
            {
                _masteryResetAttempted = false;
                _masteryResetPlan = null;
                MasteryResetResult.Text = "No changes made: the plan expired or the character changed. Prepare a fresh plan and confirm again.";
                return;
            }
            if (plan.IsVerifiedResponse(result))
            {
                _masteryResetAttempted = false;
                MasteryResetResult.Text = $"Full respec verified: {plan.ExpectedPoints} unspent skill points, {plan.ExpectedDevotionPoints} unspent devotion points, zero investment and no selected masteries. Celestial-power experience retained.\n" +
                    "Open the game's skill window to choose new masteries. You can respec again in this session; every use performs a fresh inspection and requires confirmation.\n" + result;
            }
            else MasteryResetResult.Text = "STOP — reset was rejected, partial, or unverified. Do not retry or allocate points.\n" + result;
        }
        catch (Exception ex)
        {
            MasteryResetResult.Text = _masteryResetAttempted
                ? $"UNKNOWN OUTCOME — do not retry or allocate points. Reconnect and check transaction {_masteryResetPlan?.Token}.\n{ex.Message}"
                : "Preflight failed. No reset submitted.\n" + ex.Message;
        }
        finally { _masteryAuditBusy = false; UpdateMasteryAuditUi(); }
    }

    private async void CheckMasteryResetOutcome(object sender, RoutedEventArgs e)
    {
        if (_masteryAuditBusy || _masteryResetPlan is not { } plan) return;
        _masteryAuditBusy = true; UpdateMasteryAuditUi();
        try
        {
            var result = await _bridge.GetMasteryResetStatusAsync(plan.Token, _lifetime.Token);
            if (plan.IsVerifiedResponse(result)) _masteryResetAttempted = false;
            MasteryResetResult.Text = "Recorded transaction outcome (not a fresh character audit):\n" + result;
        }
        catch (Exception ex) { MasteryResetResult.Text = "Outcome remains unknown. Do not retry.\n" + ex.Message; }
        finally { _masteryAuditBusy = false; UpdateMasteryAuditUi(); }
    }

    private async Task<MasteryPointAudit> InspectLoadedCharacterAsync()
    {
        MasteryAuditResult.Text = "Reading the loaded character on the game thread…";
        var process = _bridge.ProcessId;
        try
        {
            var audit = await _bridge.ReadMasteryAuditAsync(_lifetime.Token);
            if (!_bridge.IsConnected || _bridge.ProcessId != process)
                throw new InvalidOperationException("Connection changed during inspection. Inspect again.");
            MasteryAuditResult.Text = $"{audit.CharacterName} — level {audit.Level:N0}\n" +
                $"Read-only snapshot at {DateTimeOffset.Now:HH:mm:ss zzz} • player ID {audit.PlayerId} • skill set {audit.SkillSet}\n\n" +
                $"Unspent skill points: {audit.UnspentPoints:N0}\n" +
                $"Invested in regular skills: {audit.RegularSkillPoints:N0}\n" +
                $"Invested in mastery bars: {audit.MasteryPoints:N0}\n" +
                $"Active masteries: {audit.ActiveMasteries:N0} / {audit.AllowedMasteries:N0}\n\n" +
                $"Accounted total: {audit.AccountedPoints:N0} points. A full refund would need to return {audit.AllocatedPoints:N0} allocated points.\n" +
                "This inspection did not change or refund points. These totals alone do not establish that a mastery reset is safe. Inspect again after changing characters or skills.";
            SetStatus("Mastery points inspected", "Read-only snapshot; no character or save changes were made.", 100);
            return audit;
        }
        catch (Exception ex)
        {
            MasteryAuditResult.Text = "Inspection failed. No mastery changes were attempted.\n" + ex.Message;
            throw;
        }
    }
}
