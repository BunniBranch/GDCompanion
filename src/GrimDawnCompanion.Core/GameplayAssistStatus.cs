using System.Globalization;

namespace GrimDawnCompanion.Core;

public sealed record GameplayAssistStatus(ulong Revision, uint PlayerId, uint Mask, uint SpeedPercent)
{
    public bool Active => Mask != 0;
    public static GameplayAssistStatus Parse(string response)
    {
        var parts = response.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 6 || parts[0] != "OK" || parts[1] != "ASSISTS" ||
            !ulong.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var revision) || revision == 0 ||
            !uint.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var player) ||
            !uint.TryParse(parts[4], NumberStyles.None, CultureInfo.InvariantCulture, out var mask) || mask > 15 ||
            !uint.TryParse(parts[5], NumberStyles.None, CultureInfo.InvariantCulture, out var speed) || speed is < 100 or > 200 ||
            ((mask & 8) == 0 && speed != 100) || (player == 0 && mask != 0))
            throw new InvalidOperationException(response.StartsWith("ERROR", StringComparison.Ordinal) ? response : "Invalid gameplay-assist status.");
        return new(revision, player, mask, speed);
    }
}

public sealed partial class GameBridgeClient
{
    public bool SupportsGameplayAssists => ProtocolVersion >= 24 && Capabilities.Contains("GAMEPLAY_ASSISTS");
    private async Task<GameplayAssistStatus> AssistCommandAsync(string command, CancellationToken cancellationToken)
    {
        if (!IsConnected || !SupportsGameplayAssists)
            throw new InvalidOperationException("Gameplay Assists require the supported game build and the new Companion bridge. Disconnect and reconnect after updating.");
        return GameplayAssistStatus.Parse(await SendAsync("ASSISTS\t" + command, cancellationToken));
    }
    public Task<GameplayAssistStatus> GetGameplayAssistsAsync(CancellationToken cancellationToken = default) =>
        AssistCommandAsync("STATUS", cancellationToken);
    public Task<GameplayAssistStatus> StopGameplayAssistsAsync(CancellationToken cancellationToken = default) =>
        AssistCommandAsync("OFF", cancellationToken);
    public Task<GameplayAssistStatus> PulseGameplayAssistsAsync(ulong revision, CancellationToken cancellationToken = default) =>
        AssistCommandAsync(FormattableString.Invariant($"PULSE\t{revision}"), cancellationToken);
    public Task<GameplayAssistStatus> SetGameplayAssistsAsync(ulong revision, uint mask, uint speedPercent, CancellationToken cancellationToken = default)
    {
        if (revision == 0 || mask > 15 || speedPercent is < 100 or > 200 || ((mask & 8) == 0 && speedPercent != 100))
            throw new ArgumentOutOfRangeException(nameof(mask), "Invalid assist selection or movement speed.");
        return AssistCommandAsync(FormattableString.Invariant($"SET\t{revision}\t{mask}\t{speedPercent}"), cancellationToken);
    }
}
