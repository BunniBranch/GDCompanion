using GrimDawnCompanion.Core;

internal static class MasteryAuditRegression
{
    public static void Run(Action<bool, string> require)
    {
        const string wire = "OK MASTERY_AUDIT 1 42 50 7 80 50 2 2 0 0041006E00610020D83DDE00";
        var before = MasteryPointAudit.Parse(wire);
        require(before.CharacterName == "Ana 😀" && before.PlayerId == 42 && before.SkillSet == 0,
            "mastery audit decodes character identity including Unicode and spaces");
        require(before.AllocatedPoints == 130 && before.AccountedPoints == 137,
            "mastery audit separates unspent points from both invested point categories");
        var after = before with { UnspentPoints = 137, RegularSkillPoints = 0, MasteryPoints = 0, ActiveMasteries = 0 };
        require(after.HasConservedFullyUnassignedPointsComparedWith(before), "numeric refund check accepts an exact full refund");
        foreach (var mismatch in new[]
        {
            after with { UnspentPoints = 136 }, after with { UnspentPoints = 138 },
            after with { UnspentPoints = 267 }, after with { RegularSkillPoints = 1 },
            after with { MasteryPoints = 1 }, after with { ActiveMasteries = 1 },
            after with { PlayerId = 43 }, after with { CharacterName = "Other" },
            after with { Level = 51 }, after with { SkillSet = 1 }, after with { AllowedMasteries = 1 }
        })
            require(!mismatch.HasConservedFullyUnassignedPointsComparedWith(before),
                "numeric refund check rejects missing/duplicate refunds, leftover ranks, or changed character context");
        foreach (var invalid in new[]
        {
            "ERROR NO_LOCAL_PLAYER", wire + " trailing", wire.Replace("AUDIT 1", "AUDIT 2"),
            wire.Replace("42 50", "0 50"), wire.Replace("7 80", "-1 80"),
            wire.Replace("7 80", "4294967295 80"), wire.Replace("2 2 0", "3 2 0"),
            wire.Replace("2 2 0", "2 2 invalid"), wire[..wire.LastIndexOf(' ')],
            wire[..(wire.LastIndexOf(' ') + 1)] + "004", wire[..(wire.LastIndexOf(' ') + 1)] + "ZZZZ",
            wire[..(wire.LastIndexOf(' ') + 1)] + "000A", wire[..(wire.LastIndexOf(' ') + 1)] + "D800"
        })
        {
            var rejected = false;
            try { MasteryPointAudit.Parse(invalid); }
            catch (FormatException) { rejected = true; }
            require(rejected, "mastery audit rejects unsupported, truncated, malformed, or implausible replies");
        }
        using var cancellation = new CancellationTokenSource();
        var bridge = new GameBridgeClient();
        require(!bridge.SupportsMasteryAudit, "disconnected/legacy bridges cannot advertise mastery inspection");
        typeof(GameBridgeClient).GetProperty(nameof(GameBridgeClient.ProtocolVersion))!.SetValue(bridge, 20);
        require(!bridge.SupportsMasteryAudit, "new protocol without independent mastery capability remains blocked");
        typeof(GameBridgeClient).GetProperty(nameof(GameBridgeClient.Capabilities))!.SetValue(bridge,
            new HashSet<string> { "MASTERY_AUDIT" });
        require(bridge.SupportsMasteryAudit, "mastery inspection requires both protocol and independent capability");
        typeof(GameBridgeClient).GetProperty(nameof(GameBridgeClient.ProtocolVersion))!.SetValue(bridge, 19);
        require(!bridge.SupportsMasteryAudit, "old protocol cannot enable mastery inspection with a capability string alone");
        typeof(GameBridgeClient).GetProperty(nameof(GameBridgeClient.Capabilities))!.SetValue(bridge,
            new HashSet<string> { "FULL_RESPEC_TEST", "MASTERY_TEST" });
        typeof(GameBridgeClient).GetProperty(nameof(GameBridgeClient.ProtocolVersion))!.SetValue(bridge, 21);
        require(!bridge.SupportsExperimentalMasteryReset, "legacy mastery bridge cannot enable the full-respec workflow");
        typeof(GameBridgeClient).GetProperty(nameof(GameBridgeClient.ProtocolVersion))!.SetValue(bridge, 22);
        require(bridge.SupportsExperimentalMasteryReset, "full respec requires protocol 22 and its independent capability");
        require(!bridge.SupportsRepeatableMasteryReset, "single-use bridge cannot advertise repeatable respecs");
        typeof(GameBridgeClient).GetProperty(nameof(GameBridgeClient.Capabilities))!.SetValue(bridge,
            new HashSet<string> { "FULL_RESPEC_TEST", "MASTERY_AUDIT", "FULL_RESPEC_REPEAT" });
        typeof(GameBridgeClient).GetProperty(nameof(GameBridgeClient.ProtocolVersion))!.SetValue(bridge, 24);
        require(!bridge.SupportsRepeatableMasteryReset, "repeatable respec requires protocol 25 even with a capability string");
        typeof(GameBridgeClient).GetProperty(nameof(GameBridgeClient.ProtocolVersion))!.SetValue(bridge, 25);
        require(bridge.SupportsRepeatableMasteryReset, "repeatable respec requires protocol 25 plus audit and both reset capabilities");
        foreach (var missing in new[] { "FULL_RESPEC_TEST", "MASTERY_AUDIT", "FULL_RESPEC_REPEAT" })
        {
            typeof(GameBridgeClient).GetProperty(nameof(GameBridgeClient.Capabilities))!.SetValue(bridge,
                new HashSet<string>(new[] { "FULL_RESPEC_TEST", "MASTERY_AUDIT", "FULL_RESPEC_REPEAT" }.Where(cap => cap != missing)));
            require(!bridge.SupportsRepeatableMasteryReset, "missing repeatable-respec dependency keeps the workflow unavailable");
        }
        foreach (var invalid in new[]
        {
            "OK MASTERY_PREPARED 100 42 51 4 2", "OK FULL_RESPEC_PREPARED 0 42 51 4 2 2 3 2",
            "OK FULL_RESPEC_PREPARED 100 0 51 4 2 2 3 2", "OK FULL_RESPEC_PREPARED 100 42 51 4 2 2 3 1",
            "OK FULL_RESPEC_PREPARED 100 42 51 4 2 100 1 2", "OK FULL_RESPEC_PREPARED 100 42 51 0 0 0 0 0",
            "OK FULL_RESPEC_PREPARED 100 42 51 4 2 -1 3 2", "OK FULL_RESPEC_PREPARED 100 42 51 4 2 2 3 2 extra"
        })
        {
            var rejected = false;
            try { MasteryResetPlan.Parse(invalid); }
            catch (Exception ex) when (ex is InvalidOperationException or FormatException) { rejected = true; }
            require(rejected, "full-respec plan rejects legacy, malformed and inconsistent budgets/class slots");
        }
        require(MasteryResetPlan.Parse("OK FULL_RESPEC_PREPARED 100 42 55 0 0 2 3 0").ExpectedDevotionPoints == 5,
            "devotion-only reset is supported without inventing a mastery refund");
        require(MasteryResetPlan.Parse("OK FULL_RESPEC_PREPARED 100 42 55 0 0 0 0 2").SelectedClasses == 2,
            "zero-investment class selections can be cleared without adding points");
    }
}
