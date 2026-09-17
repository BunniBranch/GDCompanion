namespace GrimDawnCompanion.Core;

public sealed partial class GameBridgeClient
{
    public bool SupportsExperimentalMasteryReset => ProtocolVersion >= 22 && Capabilities.Contains("FULL_RESPEC_TEST");
    public bool SupportsRepeatableMasteryReset => ProtocolVersion >= 25 && SupportsExperimentalMasteryReset &&
        SupportsMasteryAudit && Capabilities.Contains("FULL_RESPEC_REPEAT");

    public async Task<MasteryResetPlan> PrepareMasteryResetAsync(CancellationToken cancellationToken = default)
    {
        EnsureCapability("FULL_RESPEC_TEST");
        if (!SupportsRepeatableMasteryReset) throw new InvalidOperationException("Disconnect and reconnect with this Companion build to enable repeatable respecs.");
        return MasteryResetPlan.Parse(await SendAsync("MASTERY\tPREPARE", cancellationToken));
    }

    // A transport failure has an UNKNOWN outcome, not permission to resubmit.
    // Close the channel to prevent a late reply being read as another response.
    public async Task<string> CommitMasteryResetAsync(ulong token, CancellationToken cancellationToken = default)
    {
        EnsureCapability("FULL_RESPEC_TEST");
        if (token == 0) throw new ArgumentOutOfRangeException(nameof(token));
        try { return await SendAsync($"MASTERY\tCOMMIT\t{token}", cancellationToken); }
        catch { DisconnectLocally(); throw; }
    }

    public async Task<string> GetMasteryResetStatusAsync(ulong token, CancellationToken cancellationToken = default)
    {
        EnsureCapability("FULL_RESPEC_TEST");
        if (token == 0) throw new ArgumentOutOfRangeException(nameof(token));
        return await SendAsync($"MASTERY\tSTATUS\t{token}", cancellationToken);
    }
    public bool SupportsMasteryAudit => ProtocolVersion >= 20 && Capabilities.Contains("MASTERY_AUDIT");

    public async Task<MasteryPointAudit> ReadMasteryAuditAsync(CancellationToken cancellationToken = default)
    {
        if (!SupportsMasteryAudit)
            throw new InvalidOperationException("Live mastery inspection requires the new bridge. Restart Grim Dawn and reconnect with this Companion build.");
        EnsureCapability("MASTERY_AUDIT");
        return MasteryPointAudit.Parse(await SendAsync("MASTERY\tAUDIT", cancellationToken));
    }
}
