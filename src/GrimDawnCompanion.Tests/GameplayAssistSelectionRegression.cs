using GrimDawnCompanion.Core;

internal static class GameplayAssistSelectionRegression
{
    public static void Run(Action<bool, string> require)
    {
        for (uint mask = 0; mask <= 15; mask++)
        foreach (uint flag in new uint[] { 1, 2, 4, 8 })
        foreach (bool enabled in new[] { false, true })
        {
            var current = new GameplayAssistStatus(7, 42, mask, (mask & 8) == 0 ? 100u : 150u);
            var change = GameplayAssistSelection.Toggle(current, flag, enabled, 175);
            uint expected = enabled ? mask | flag : mask & ~flag;
            require(change.Mask == expected && (change.Mask & ~flag) == (mask & ~flag),
                $"checkbox {flag}={enabled} changes only its own bit from mask {mask}");
            require(change.SpeedPercent == ((expected & 8) == 0 ? 100u : flag == 8 ? 175u : 150u),
                "other checkboxes preserve confirmed movement speed; disabling movement restores 100%");
            require(change.MatchesContext(current, current with { }) &&
                !change.MatchesContext(current, null) &&
                !change.MatchesContext(current, current with { Revision = 8 }) &&
                !change.MatchesContext(current, current with { PlayerId = 43 }) &&
                !change.MatchesContext(current, current with { Mask = mask ^ 1 }),
                "toggle is valid only for the unchanged confirmed character and revision");
        }
        var off = new GameplayAssistStatus(1, 0, 0, 100);
        require(!GameplayAssistSelection.Toggle(off, 1, true, 100).MatchesContext(off, off),
            "no character cannot authorize an assist");
        foreach (var input in new (uint Flag, uint Speed)[] { (0, 100), (3, 100), (16, 100), (1, 99), (8, 201) })
        {
            bool rejected = false;
            try { GameplayAssistSelection.Toggle(off, input.Flag, true, input.Speed); }
            catch (ArgumentOutOfRangeException) { rejected = true; }
            require(rejected, "invalid checkbox flag or speed is rejected");
        }
    }
}
