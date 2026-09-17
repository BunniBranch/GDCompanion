using GrimDawnCompanion.Core;

internal static class MinimapGeometryRegressionTests
{
    public static void Run()
    {
        using (var bridge = new AsyncBridgeScope())
        {
            var client = bridge.Client;
            typeof(GameBridgeClient).GetProperty(nameof(GameBridgeClient.Capabilities))!.SetValue(client,
                new HashSet<string> { "MINIMAP", "MINIMAP_PROJECTION" });
            typeof(GameBridgeClient).GetProperty(nameof(GameBridgeClient.ProtocolVersion))!.SetValue(client, 22);
            Require(!client.SupportsMinimapProjection, "old protocol cannot enable automatic zoom");
            typeof(GameBridgeClient).GetProperty(nameof(GameBridgeClient.ProtocolVersion))!.SetValue(client, 23);
            Require(client.SupportsMinimapProjection, "new protocol with both capabilities supports camera projection");
            typeof(GameBridgeClient).GetProperty(nameof(GameBridgeClient.Capabilities))!.SetValue(client, new HashSet<string> { "MINIMAP" });
            Require(!client.SupportsMinimapProjection, "bounds alone cannot enable automatic zoom");
        }
        Require(MinimapProjection.ParseFrame("OK MINIMAP_FRAME HIDDEN") is null, "missing camera frame cannot reuse a stale transform");
        var cameraFrame = MinimapProjection.ParseFrame("OK MINIMAP_FRAME 1735 43 174 174 1920 1080 100 200 0 0 0.05 0 0 0.05")!;
        Require(Near(cameraFrame.Projection!.DisplayRange, 20), "automatic zoom is not clamped to the old 25-unit manual minimum");
        foreach (var range in new[] { 10d, 20, 30, 45, 120 })
        foreach (var angle in new[] { 0d, Math.PI / 4, Math.PI / 2, Math.PI })
        {
            var c = Math.Cos(angle) / range; var s = Math.Sin(angle) / range;
            var transform = new MinimapProjection(100, 200, 0, 0, c, s, -s, c);
            var marker = new WorldMarkerRecord("static", "Vendor", "live://static", "live://current-map", 104, 0, 200, ["$live"]);
            var points = NavigationOverlayProjection.Build(new WorldPosition(100, 0, 200), [marker], [], false, true, 45, projection: transform);
            Require(points.Count == 1 && Near(points[0].HorizontalRatio, 4 * c) && Near(points[0].VerticalRatio, 4 * s),
                "zoom and camera rotation determine marker placement");
            var moved = transform with { OriginX = 102, OriginZ = 201 };
            var point = NavigationOverlayProjection.Build(new WorldPosition(100, 0, 200), [marker], [], false, true, 99, projection: moved).Single();
            Require(Near(point.HorizontalRatio, 2 * c + s) && Near(point.VerticalRatio, 2 * s - c),
                "rendered camera origin controls motion even when player polling is older");
            var far = marker with { WorldX = 10000, Category = "Quest Objective" };
            var edge = NavigationOverlayProjection.Build(new WorldPosition(100, 0, 200), [far], [], true, false, 45, projection: transform).Single();
            Require(edge.IsDistant && Near(Math.Sqrt(edge.HorizontalRatio * edge.HorizontalRatio + edge.VerticalRatio * edge.VerticalRatio), 1.05),
                "automatic projection preserves distant quest bearings on the rim");
        }
        foreach (var invalid in new[] {
            "OK MINIMAP_FRAME 1735 43 174 174 1920 1080 100 200 0 0 0 0 0 0",
            "OK MINIMAP_FRAME 1735 43 174 174 1920 1080 100 200 0 0 NaN 0 0 .05",
            "OK MINIMAP_FRAME 1735 43 174 174 1920 1080 100 200 0 0 .05 0 .05 0",
            "OK MINIMAP_FRAME 1735 43 174 174 1920 1080 100 200 0 0 .05 0 0 .1",
            "OK MINIMAP_FRAME 1735 43 174 174 1920 1080 100 200 4 0 .05 0 0 .05",
            "OK MINIMAP_FRAME 1735 43 174 174 1920 1080" })
        {
            try { MinimapProjection.ParseFrame(invalid); }
            catch (InvalidDataException) { continue; }
            throw new InvalidOperationException("TEST FAILED: invalid camera projection accepted");
        }
        Console.WriteLine("PASS  automatic zoom handles movement, rotation, range changes, distant bearings, missing frames and invalid transforms");
        var current = MinimapGeometry.Parse("OK MINIMAP 1642 43 260 260 1920 1080")!;
        var native = current.ToClient(1920, 1080);
        Require(native == new RadarRectangle(1642, 43, 260, 260), "native render pixels align without calibration offsets");
        foreach (var dpi in new[] { 1d, 1.25, 1.5, 2d })
        {
            var dip = current.ToClient(1920 / dpi, 1080 / dpi);
            Require(Near(dip.Left * dpi, 1642) && Near(dip.Top * dpi, 43) && Near(dip.Width * dpi, 260),
                "DPI conversion preserves physical minimap coordinates");
        }
        foreach (var resolution in new[] { (1280, 720), (1920, 1080), (2560, 1440), (3440, 1440), (3840, 2160) })
        {
            // Minimap dimensions come from each native frame, not a reference
            // screen-height multiplier. Simulate independently varying UI scale.
            foreach (var diameter in new[] { 174d, 260, 350 })
            {
                var frame = new MinimapGeometry(resolution.Item1 - diameter - 17, 39, diameter, diameter,
                    resolution.Item1, resolution.Item2);
                var result = frame.ToClient(resolution.Item1 / 1.5, resolution.Item2 / 1.5);
                Require(Near(result.Width * 1.5, diameter) && Near(result.Left * 1.5, frame.Left),
                    "resolution/aspect ratio and UI size are independent");
            }
        }
        Require(MinimapGeometry.Parse("OK MINIMAP HIDDEN") is null, "hidden minimaps cannot reuse stale bounds");
        foreach (var invalid in new[] {
            "OK MINIMAP NaN 43 260 260 1920 1080", "OK MINIMAP -1 43 260 260 1920 1080",
            "OK MINIMAP 1800 43 260 260 1920 1080", "OK MINIMAP 10 10 0 0 1920 1080",
            "OK MINIMAP 10 10 260 180 1920 1080", "OK MINIMAP 10 10 260 260 0 1080",
            "ERROR MINIMAP_UNAVAILABLE", "OK MINIMAP 1 2 3" })
        {
            try { MinimapGeometry.Parse(invalid); }
            catch (InvalidDataException) { continue; }
            throw new InvalidOperationException("TEST FAILED: malformed minimap geometry accepted");
        }
        Console.WriteLine("PASS  automatic minimap geometry covers resolution, aspect ratio, DPI, hidden state, and malformed frames");
    }

    private static bool Near(double left, double right) => Math.Abs(left - right) < .0001;
    private sealed class AsyncBridgeScope : IDisposable
    {
        public GameBridgeClient Client { get; } = new();
        public void Dispose() => Client.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException("TEST FAILED: " + message);
    }
}
