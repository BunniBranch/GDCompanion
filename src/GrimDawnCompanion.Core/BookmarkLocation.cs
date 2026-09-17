using System.Globalization;
using System.Text;

namespace GrimDawnCompanion.Core;

public sealed record BookmarkLocation(string CharacterId, string CharacterName, uint Difficulty, bool Hardcore,
    string World, string Region, float X, float Y, float Z)
{
    private static readonly UnicodeEncoding Utf16 = new(false, false, true);
    private static readonly UTF8Encoding Utf8 = new(false, true);
    public void Validate()
    {
        // CharacterId is legacy metadata only. New shared bookmarks store zeros.
        if (CharacterId is null || CharacterId.Length != 32 ||
            CharacterId.Any(c => !char.IsAsciiHexDigitUpper(c) && !char.IsAsciiDigit(c)) ||
            string.IsNullOrWhiteSpace(CharacterName) || CharacterName.Length > 127 || CharacterName.Any(char.IsControl) || Difficulty > 2 ||
            !ValidPath(World) || !ValidPath(Region) ||
            !float.IsFinite(X) || !float.IsFinite(Y) || !float.IsFinite(Z) || Math.Abs(X) >= 1000000 || Math.Abs(Y) >= 1000000 || Math.Abs(Z) >= 1000000)
            throw new InvalidDataException("The bookmark contains invalid character or location data.");
        _ = Utf16.GetBytes(CharacterName);
    }
    private static bool ValidPath(string? value) => !string.IsNullOrWhiteSpace(value) && Utf8.GetByteCount(value) <= 512 &&
        !value.Any(char.IsControl) && !value.Contains("..", StringComparison.Ordinal) && !value.Contains('\\') && value == value.ToLowerInvariant();
    public string ToWire()
    {
        Validate();
        return FormattableString.Invariant($"{CharacterId} {Convert.ToHexString(Utf16.GetBytes(CharacterName))} {Difficulty} {(Hardcore ? 1 : 0)} {Convert.ToHexString(Utf8.GetBytes(World))} {Convert.ToHexString(Utf8.GetBytes(Region))} {X:R} {Y:R} {Z:R}");
    }
    public static BookmarkLocation ParseWire(string wire)
    {
        var parts = wire.Split(' ');
        if (parts.Length != 9 || parts[3] is not ("0" or "1")) throw new InvalidDataException("Invalid bookmark snapshot.");
        var result = new BookmarkLocation(parts[0], Utf16.GetString(Convert.FromHexString(parts[1])),
            uint.Parse(parts[2], CultureInfo.InvariantCulture), parts[3] == "1", Utf8.GetString(Convert.FromHexString(parts[4])),
            Utf8.GetString(Convert.FromHexString(parts[5])), float.Parse(parts[6], CultureInfo.InvariantCulture),
            float.Parse(parts[7], CultureInfo.InvariantCulture), float.Parse(parts[8], CultureInfo.InvariantCulture));
        result.Validate(); return result;
    }
    // Difficulty/hardcore are saved metadata, not map identity. The caller also
    // checks the installed-map fingerprint; native return resolves the exact region.
    public bool MatchesContext(BookmarkLocation other) => World == other.World;
}

// The live object ID guards this return request, never saved to disk.
public sealed record BookmarkCapture(uint PlayerId, BookmarkLocation Location)
{
    public static BookmarkCapture ParseResponse(string response)
    {
        const string prefix = "OK BOOKMARK 2 ";
        if (!response.StartsWith(prefix, StringComparison.Ordinal)) throw new InvalidOperationException(GameBridgeClient.BookmarkError(response));
        var body = response[prefix.Length..];
        var separator = body.IndexOf(' ');
        if (separator < 1 || !uint.TryParse(body[..separator], NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id == 0)
            throw new InvalidDataException("Invalid bookmark player context.");
        return new(id, BookmarkLocation.ParseWire(body[(separator + 1)..]));
    }
}
