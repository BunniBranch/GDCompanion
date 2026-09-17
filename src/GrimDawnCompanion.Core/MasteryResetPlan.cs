using System.Globalization;

namespace GrimDawnCompanion.Core;

public sealed record MasteryResetPlan(ulong Token, uint PlayerId, uint Unspent, uint Refund, uint Masteries,
    uint DevotionUnspent, uint DevotionRefund, uint SelectedClasses)
{
    public ulong ExpectedPoints => (ulong)Unspent + Refund;
    public ulong ExpectedDevotionPoints => (ulong)DevotionUnspent + DevotionRefund;
    public static MasteryResetPlan Parse(string response)
    {
        var fields = response.Split(' ');
        if (fields.Length != 10 || fields[0] != "OK" || fields[1] != "FULL_RESPEC_PREPARED" ||
            !ulong.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var token) || token == 0)
            throw new InvalidOperationException("Reset preflight rejected: " + response);
        uint Read(int i) => uint.TryParse(fields[i], NumberStyles.None, CultureInfo.InvariantCulture, out var n)
            ? n : throw new FormatException("Invalid reset plan.");
        var plan = new MasteryResetPlan(token, Read(3), Read(4), Read(5), Read(6), Read(7), Read(8), Read(9));
        if (plan.PlayerId == 0 || plan.Unspent > 10000 || plan.Refund > 1100 || plan.Masteries > 2 ||
            plan.SelectedClasses > 2 || plan.SelectedClasses < plan.Masteries || plan.ExpectedDevotionPoints > 100 ||
            (plan.Refund == 0 && plan.DevotionRefund == 0 && plan.SelectedClasses == 0))
            throw new FormatException("Invalid reset plan limits.");
        return plan;
    }
    public bool IsVerifiedResponse(string response) =>
        response == FormattableString.Invariant($"OK FULL_RESPEC_RESET {Token} {PlayerId} {ExpectedPoints} {ExpectedDevotionPoints} 0");
}
