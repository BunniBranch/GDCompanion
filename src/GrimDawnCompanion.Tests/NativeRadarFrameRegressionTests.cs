using System.Text;
using GrimDawnCompanion.Core;

internal static class NativeRadarFrameRegressionTests
{
    public static void Run(Action<bool, string> require)
    {
        foreach (var camera in new MinimapProjection?[] { null, new(0, 0, 0, 0, 0, .025, -.025, 0) })
        {
            var quest = new WorldMarkerRecord("rim-quest", "Quest Objective", "live://quest", "live://map", 400, 0, 0, ["$live"]);
            var poi = quest with { Id = "rim-poi", Category = "Travel Utility", RecordPath = "records/boat.dbr", QuestPaths = [] };
            var pair = NavigationOverlayProjection.Build(new(0, 0, 0), [quest, poi], [], true, true, 40, projection: camera);
            var q = pair.Single(p => p.Marker.Id == quest.Id);
            var p = pair.Single(p => p.Marker.Id == poi.Id);
            static double Radius(NavigationOverlayPoint point) => Math.Sqrt(point.HorizontalRatio * point.HorizontalRatio + point.VerticalRatio * point.VerticalRatio);
            require(q.IsDistant && p.IsDistant && Math.Abs(Radius(q) - 1.05) < .00001 && Math.Abs(Radius(p) - .91) < .00001 &&
                Math.Abs(q.HorizontalRatio * p.VerticalRatio - q.VerticalRatio * p.HorizontalRatio) < .00001,
                "external automatic/manual quest stars use an outer lane with unchanged POI bearing and rim");
            var near = NavigationOverlayProjection.Build(new(0, 0, 0), [quest with { WorldX = 38 }], [], true, false, 40, projection: camera).Single();
            require(!near.IsDistant && Math.Abs(Radius(near) - .95) < .00001,
                "visible near-edge quest remains at its actual minimap location");
        }
        var marker = new WorldMarkerRecord("quest", "Quest Objective", "test.dbr", "test.lvl", 1.25f, 0, -2.5f,
            DisplayName: "{^y}Quest\tname\n " + string.Concat(Enumerable.Repeat("🌟", 100)));
        var point = new NavigationOverlayPoint(marker, 0, 0, 0, false);
        var commands = NativeRadarFrame.Commands(9, Enumerable.Repeat(point, 112));
        require(commands.First() == "RADAR\tCANVAS\tBEGIN\t9\t112" && commands.Last() == "RADAR\tCANVAS\tCOMMIT" &&
            commands.All(command => command.Length <= 3500), "native radar batches bounded packets between an atomic begin and commit");
        var entries = commands.Skip(1).SkipLast(1).SelectMany(command => command.Split('\t').Skip(3)).ToArray();
        require(entries.Length == 112 && entries.All(entry => entry.StartsWith("1.25,-2.5,0,")),
            "native radar sends world coordinates rather than delayed screen positions");
        var bytes = Convert.FromHexString(entries[0].Split(',')[3]);
        var label = new UTF8Encoding(false, true).GetString(bytes);
        require(bytes.Length <= 192 && label.StartsWith("Questname ") && !label.Contains('{'),
            "native hover labels strip markup and controls without splitting Unicode characters");
        require(NativeRadarFrame.Commands(9, []).SequenceEqual(new[] { "RADAR\tCANVAS\tBEGIN\t9\t0", "RADAR\tCANVAS\tCOMMIT" }),
            "native empty frame explicitly clears previous targets");
        Reject(() => NativeRadarFrame.Commands(0, [point]));
        Reject(() => NativeRadarFrame.Commands(9, Enumerable.Repeat(point, 113)));
        Reject(() => NativeRadarFrame.Commands(9, [point with { Marker = marker with { WorldX = float.NaN } }]));
        require(true, "native sender rejects invalid context, overflow and nonfinite coordinates");
        const string legacy = "OK MINIMAP_FRAME 1735 43 174 174 1920 1080 100 200 0 0 .05 0 0 .05";
        require(MinimapProjection.ParseFrame(legacy)!.RenderContext == 0 &&
            MinimapProjection.ParseFrame(legacy + " 123")!.RenderContext == 123,
            "minimap frame parser supports legacy and map-scoped native render frames");
        Reject(() => MinimapProjection.ParseFrame(legacy + " 0"));
        Reject(() => MinimapProjection.ParseFrame(legacy + " -1"));
        require(NativeRadarStatus.Parse("OK RADAR_CANVAS 12 10") is { Drawn: 12, Rendering: true } &&
            !NativeRadarStatus.Parse("OK RADAR_CANVAS 12 500").Rendering,
            "native status reports actual recent drawing rather than queued targets");
        Reject(() => NativeRadarStatus.Parse("OK RADAR_CANVAS 113 10"));
        var client = new GameBridgeClient();
        try
        {
            typeof(GameBridgeClient).GetProperty(nameof(GameBridgeClient.ProtocolVersion))!.SetValue(client, 31);
            typeof(GameBridgeClient).GetProperty(nameof(GameBridgeClient.Capabilities))!.SetValue(client,
                new HashSet<string> { "MINIMAP", "MINIMAP_PROJECTION", "RADAR_CANVAS" });
            require(client.SupportsNativeRadar, "verified native radar requires projection and canvas capabilities");
            typeof(GameBridgeClient).GetProperty(nameof(GameBridgeClient.ProtocolVersion))!.SetValue(client, 30);
            require(!client.SupportsNativeRadar, "old bridge cannot activate native fullscreen rendering");
        }
        finally { client.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }
    private static void Reject(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or InvalidDataException) { return; }
        throw new InvalidOperationException("TEST FAILED: invalid native radar input accepted");
    }
}
