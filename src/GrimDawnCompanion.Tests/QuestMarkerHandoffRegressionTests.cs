using GrimDawnCompanion.Core;

internal static class QuestMarkerHandoffRegressionTests
{
    public static void Run(IReadOnlyCollection<WorldMarkerRecord> catalog)
    {
        const string quest = "quests/mq_helpingout.qst";
        var targets = catalog.Where(x => x.Category == "Quest Objective" &&
            x.QuestPaths?.Contains(quest, StringComparer.OrdinalIgnoreCase) == true).ToArray();
        var barnabas = targets.Single(x => x.RecordPath.EndsWith("npc_barnabas_01.dbr", StringComparison.OrdinalIgnoreCase));
        var kasparov = targets.Single(x => x.RecordPath.EndsWith("npc_kasparov_01.dbr", StringComparison.OrdinalIgnoreCase));
        // Kasparov sampled from the running game at the user's reproduction
        // point; Barnabas shares the same compiled level coordinate frame.
        var liveKasparov = new WorldMarkerRecord("live-kasparov", "Quest Objective", "live://21",
            "live://current-map", 98.32786f, 9.151892f, 27.171303f, ["$live"], "Kasparov {^y}Helping Out");
        var liveBarnabas = new WorldMarkerRecord("live-barnabas", "Quest Objective", "live://21",
            "live://current-map", barnabas.WorldX, barnabas.WorldY, barnabas.WorldZ,
            ["$live"], "Barnabas {^y}Helping Out");
        WorldMarkerRecord[][] snapshots = [[liveBarnabas, liveKasparov], [liveKasparov], []];
        WorldPosition[] positions = [new(80.8f, 0, 37.3f), new(65.445786f, 0.95679736f, -42.8442f), new(61.9f, 0, -127.3f)];
        foreach (var player in positions)
        {
            var expected = NavigationOverlayProjection.Build(player, [barnabas, kasparov], [quest], true, false, 45,
                "Levels/Region0A002.lvl");
            foreach (var snapshot in snapshots)
            {
                // Also exercises a fresh Companion start with only one live
                // target and no previously visited-object cache available.
                var actual = NavigationOverlayProjection.Build(player, catalog.Concat(snapshot), [quest], true, false, 45,
                    "Levels/Region0A002.lvl");
                Require(actual.Count == 2, "Helping Out always has exactly two marker destinations");
                foreach (var target in expected)
                {
                    var matching = actual.Single(point =>
                        Math.Abs(point.Marker.WorldX - target.Marker.WorldX) < 0.01 &&
                        Math.Abs(point.Marker.WorldZ - target.Marker.WorldZ) < 0.01);
                    Require(Math.Abs(matching.HorizontalRatio - target.HorizontalRatio) < 0.001 &&
                            Math.Abs(matching.VerticalRatio - target.VerticalRatio) < 0.001,
                        "live-to-catalog handoff must preserve the minimap bearing");
                }
            }
        }
        Console.WriteLine("PASS  installed quest bearings remain stable with two, one, or zero live objectives while traveling north");
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException("TEST FAILED: " + message);
    }
}
