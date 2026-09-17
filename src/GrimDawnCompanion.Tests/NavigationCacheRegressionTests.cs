using GrimDawnCompanion.Core;

internal static class NavigationCacheRegressionTests
{
    public static void Run()
    {
        const string region = "levels/region0a";
        string[] quests = ["quests/mq_helpingout.qst"];
        var barnabas = new LiveMapMarkerRecord(21, new WorldPosition(64, 0, -128),
            "Help Barnabas with the Water Pump");
        var kasparov = new LiveMapMarkerRecord(21, new WorldPosition(92, 0, -112),
            "Help Kasparov with his research");
        var cache = new RegionalQuestMarkerCache();

        var markers = cache.Update(region, quests, [barnabas, kasparov],
            new WorldPosition(70, 0, -120), 120);
        RequireCoordinates(markers, barnabas, kasparov);

        // A partial or empty distant map pass preserves observed objectives
        // whose game objects may have unloaded as the player travels away.
        var distantPlayer = new WorldPosition(5000, 0, 5000);
        markers = cache.Update(region, quests, [barnabas], distantPlayer, 120);
        RequireCoordinates(markers, barnabas, kasparov);
        markers = cache.Update(region, quests, [], distantPlayer, 120);
        RequireCoordinates(markers, barnabas, kasparov);
        Console.WriteLine("PASS  regional quest cache preserves distant coordinates through partial and empty map passes");

        // Inside the existing invalidation radius, omitted live entries expire.
        // The catalog projection separately supplies corrected static targets.
        var northOfCrossing = new WorldPosition(65.4f, 0, -42.8f);
        markers = cache.Update(region, quests, [barnabas], northOfCrossing, 120);
        RequireCoordinates(markers, barnabas);
        markers = cache.Update(region, quests, [], northOfCrossing, 120);
        RequireCoordinates(markers);
        Console.WriteLine("PASS  regional quest cache expires omitted nearby entries without retaining them indefinitely");

        // Reappearing objectives restore their verified coordinates; subsequent
        // observations at those coordinates refresh metadata without duplicates.
        markers = cache.Update(region, quests, [barnabas, kasparov], northOfCrossing, 120);
        RequireCoordinates(markers, barnabas, kasparov);
        var refreshedKasparov = kasparov with { Label = "Kasparov" };
        markers = cache.Update(region, quests, [refreshedKasparov, barnabas],
            new WorldPosition(70, 0, -120), 120);
        RequireCoordinates(markers, barnabas, kasparov);
        Require(markers.Single(marker => marker.Position == kasparov.Position).Label == "Kasparov",
            "reappearing quest marker refreshes its metadata");
        Console.WriteLine("PASS  regional quest cache reuses reappearing coordinates without duplicate stars");

        markers = cache.Update("levels/region0b", quests, [], northOfCrossing, 120);
        Require(markers.Count == 0, "another region cannot inherit the previous region's stars");
        var otherRegionObjective = new LiveMapMarkerRecord(21, new WorldPosition(500, 0, 500), "Another objective");
        markers = cache.Update("levels/region0b", quests, [otherRegionObjective], northOfCrossing, 120);
        RequireCoordinates(markers, otherRegionObjective);
        markers = cache.Update(region, ["QUESTS\\MQ_HELPINGOUT.QST"], [], distantPlayer, 120);
        RequireCoordinates(markers, barnabas, kasparov);
        Console.WriteLine("PASS  regional quest cache isolates regions and retains observations when returning with the same normalized quest set");

        markers = cache.Update(region, ["quests/another.qst"], [barnabas], northOfCrossing, 120);
        RequireCoordinates(markers, barnabas);
        markers = cache.Update("levels/region0b", ["quests/another.qst"], [], distantPlayer, 120);
        Require(markers.Count == 0, "quest-set changes invalidate every region's old observations");
        cache.Clear();
        markers = cache.Update(region, ["quests/another.qst"], [], distantPlayer, 120);
        Require(markers.Count == 0, "explicit clearing invalidates retained quest markers");
        Console.WriteLine("PASS  regional quest cache invalidates observations on quest-set changes and explicit clearing");
    }

    private static void RequireCoordinates(IReadOnlyList<LiveMapMarkerRecord> actual,
        params LiveMapMarkerRecord[] expected)
    {
        Require(actual.Count == expected.Length && expected.All(marker =>
                actual.Count(candidate => candidate.Type == marker.Type && candidate.Position == marker.Position) == 1),
            "quest-marker count and verified world coordinates remain unchanged");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("TEST FAILED: " + message);
    }
}
