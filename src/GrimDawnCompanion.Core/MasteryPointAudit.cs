using System.Globalization;

namespace GrimDawnCompanion.Core;

/// <summary>
/// Read-only, game-reported totals at one instant, not a respec authorization.
/// These totals do not verify individual ranks, class state, equipment,
/// devotion bindings, hotbar references, or save/reload persistence.
/// </summary>
public sealed record MasteryPointAudit(
    uint PlayerId, string CharacterName, uint Level, uint UnspentPoints,
    uint RegularSkillPoints, uint MasteryPoints, uint ActiveMasteries,
    uint AllowedMasteries, int SkillSet)
{
    public ulong AllocatedPoints => (ulong)RegularSkillPoints + MasteryPoints;
    public ulong AccountedPoints => UnspentPoints + AllocatedPoints;

    public static MasteryPointAudit Parse(string response)
    {
        var fields = response.Split(' ', StringSplitOptions.None);
        if (fields.Length != 12 || fields[0] != "OK" || fields[1] != "MASTERY_AUDIT" || fields[2] != "1")
            throw new FormatException("The bridge did not return a supported mastery point audit: " + response);
        uint Read(int index) => uint.TryParse(fields[index], NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value : throw new FormatException("Invalid mastery audit numeric field.");
        if (!int.TryParse(fields[10], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var skillSet))
            throw new FormatException("Invalid mastery audit skill set.");
        var encoded = fields[11];
        if (encoded.Length is 0 or > 508 || encoded.Length % 4 != 0)
            throw new FormatException("Invalid mastery audit character name.");
        var chars = new char[encoded.Length / 4];
        for (var i = 0; i < chars.Length; i++)
        {
            if (!ushort.TryParse(encoded.AsSpan(i * 4, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var code) || char.IsControl((char)code))
                throw new FormatException("Invalid mastery audit character name encoding.");
            chars[i] = (char)code;
        }
        var name = new string(chars);
        // Validate surrogate pairs rather than accepting malformed UTF-16.
        try { _ = new System.Text.UnicodeEncoding(false, false, true).GetBytes(name); }
        catch (System.Text.EncoderFallbackException ex) { throw new FormatException("Invalid character name Unicode.", ex); }
        var result = new MasteryPointAudit(Read(3), name, Read(4), Read(5), Read(6), Read(7), Read(8), Read(9), skillSet);
        if (result.PlayerId == 0 || result.Level is 0 or > 10_000 || result.AccountedPoints > 1_000_000 ||
            result.ActiveMasteries > result.AllowedMasteries || result.AllowedMasteries > 128 || skillSet is < -1 or > 128)
            throw new FormatException("The game returned implausible mastery audit values; no changes are permitted.");
        return result;
    }

    /// <summary>
    /// Necessary numeric checks for a future refund workflow, NOT sufficient
    /// proof that a mastery reset is safe. Caller must also verify the same
    /// bridge session, full rank lists, class state, and dependent references.
    /// </summary>
    public bool HasConservedFullyUnassignedPointsComparedWith(MasteryPointAudit before) =>
        PlayerId == before.PlayerId && CharacterName == before.CharacterName && Level == before.Level &&
        SkillSet == before.SkillSet && AllowedMasteries == before.AllowedMasteries &&
        RegularSkillPoints == 0 && MasteryPoints == 0 && ActiveMasteries == 0 &&
        (ulong)UnspentPoints == before.AccountedPoints;
}
