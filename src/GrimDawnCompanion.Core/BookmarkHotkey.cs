namespace GrimDawnCompanion.Core;

// Windows virtual-key codes, independent of WPF. Modifier bits: Alt=1, Ctrl=2, Shift=4.
public sealed record BookmarkHotkey(int Key, int Modifiers)
{
    public void Validate()
    {
        if (Modifiers is not (3 or 7) || !(Key is >= 0x41 and <= 0x5A or >= 0x30 and <= 0x39 or >= 0x70 and <= 0x7B))
            throw new InvalidDataException("Use Ctrl+Alt (optionally Shift) with a letter, number or F1–F12.");
    }
    public override string ToString() => "Ctrl+Alt+" + ((Modifiers & 4) != 0 ? "Shift+" : "") +
        (Key >= 0x70 ? $"F{Key - 0x6F}" : ((char)Key).ToString());
}

// Ineligible presses are consumed too, so held keys cannot travel on focus return.
public sealed class BookmarkHotkeyEdge
{
    private bool _down;
    public bool Update(bool down, bool eligible)
    {
        var activate = down && !_down && eligible;
        _down = down;
        return activate;
    }
}
