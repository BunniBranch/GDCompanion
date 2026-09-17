using System.Globalization;
using System.Text;

namespace GrimDawnCompanion.Core;

public sealed record CharacterLevelPreparation(ulong Token, uint Current, uint Target, uint Cap, uint CharacterId, string Name)
{
    public const uint HardLimit = 1000; // Decoder safety bound, never an override of the loaded cap.
    public static CharacterLevelPreparation Parse(string response)
    {
        var p = response.Split(' ');
        if (p.Length != 8 || p[0] != "OK" || p[1] != "LEVEL_PREPARED" ||
            !ulong.TryParse(p[2], NumberStyles.None, CultureInfo.InvariantCulture, out var token) || token == 0)
            throw new InvalidDataException(response);
        var values = new uint[4];
        for (var i = 0; i < 4; i++)
            if (!uint.TryParse(p[i + 3], NumberStyles.None, CultureInfo.InvariantCulture, out values[i])) throw new InvalidDataException(response);
        if (values[0] < 1 || values[1] <= values[0] || values[1] > values[2] || values[2] > HardLimit || values[3] == 0 ||
            p[7].Length is 0 or > 512 || p[7].Length % 4 != 0) throw new InvalidDataException(response);
        var name = new StringBuilder();
        for (var i = 0; i < p[7].Length; i += 4)
        {
            if (!ushort.TryParse(p[7].AsSpan(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var ch) || char.IsControl((char)ch))
                throw new InvalidDataException(response);
            name.Append((char)ch);
        }
        return new(token, values[0], values[1], values[2], values[3], name.ToString());
    }
}

public sealed partial class GameBridgeClient
{
    public async Task<CharacterLevelPreparation> PrepareCharacterLevelAsync(uint target, CancellationToken cancellationToken = default)
    {
        if (!SupportsCharacterLevel) throw new InvalidOperationException("Restart Grim Dawn and reconnect with the latest supported bridge to set character levels.");
        if (target is < 2 or > CharacterLevelPreparation.HardLimit) throw new ArgumentOutOfRangeException(nameof(target), "Enter a higher level within the active game or mod cap.");
        return CharacterLevelPreparation.Parse(await SendAsync($"LEVEL\tPREPARE\t{target}", cancellationToken));
    }
    public async Task<uint> CommitCharacterLevelAsync(CharacterLevelPreparation preparation, CancellationToken cancellationToken = default)
    {
        if (!SupportsCharacterLevel) throw new InvalidOperationException("Character-level changes are unavailable.");
        var response = await SendAsync($"LEVEL\tCOMMIT\t{preparation.Token}", cancellationToken);
        var parts = response.Split(' ');
        if (parts.Length != 5 || parts[0] != "OK" || parts[1] != "LEVEL_SET" ||
            !uint.TryParse(parts[2], out var level) || level != preparation.Target || level > preparation.Cap)
            throw new InvalidOperationException(response + "\nDo not retry automatically. Inspect the character's current level first.");
        return level;
    }
}
