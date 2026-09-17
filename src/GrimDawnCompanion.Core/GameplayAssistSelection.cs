namespace GrimDawnCompanion.Core;

/// <summary>A single checkbox change based on the last confirmed game state.</summary>
public sealed record GameplayAssistSelection(uint Mask, uint SpeedPercent)
{
    public static GameplayAssistSelection Toggle(GameplayAssistStatus current, uint flag, bool enabled, uint speedPercent)
    {
        if (flag is not (1 or 2 or 4 or 8) || speedPercent is < 100 or > 200)
            throw new ArgumentOutOfRangeException(nameof(flag), "Invalid assist selection.");
        uint mask = enabled ? current.Mask | flag : current.Mask & ~flag;
        uint speed = (mask & 8) == 0 ? 100 : flag == 8 ? speedPercent : current.SpeedPercent;
        return new(mask, speed);
    }

    public bool MatchesContext(GameplayAssistStatus inspected, GameplayAssistStatus? latest) =>
        latest is not null && latest.PlayerId != 0 && latest == inspected;
}
