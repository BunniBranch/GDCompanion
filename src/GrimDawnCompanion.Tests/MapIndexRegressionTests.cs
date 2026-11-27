using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using GrimDawnCompanion.Core;

internal static class MapIndexRegressionTests
{
    public static void Run()
    {
        var (world, countOffset, firstStringOffset) = CreateWorld();
        var markers = Parse(world);
        Require(markers.Count == 3, "compiled map index reads every level and ignores incidental paths in other chunks");
        var expected = new[] { (110f, 2f, -180f), (-390f, 4f, 520f), (710f, 2f, -780f) };
        for (var index = 0; index < expected.Length; index++)
        {
            var marker = markers.Single(x => x.LevelPath == $"Levels/Region0A{index + 1:000}.lvl");
            var (x, y, z) = expected[index];
            Require(marker.WorldX == x && marker.WorldY == y && marker.WorldZ == z,
                $"compiled map level {index + 1} uses its own preceding origin, including the final entry");
        }

        var badString = (byte[])world.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(badString.AsSpan(firstStringOffset, 4), uint.MaxValue);
        RequireInvalid(badString, "compiled map index rejects an overflowing metadata string length");
        var badCount = (byte[])world.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(badCount.AsSpan(countOffset, 4), uint.MaxValue);
        RequireInvalid(badCount, "compiled map index rejects a level count larger than its payload");
        var badChunk = (byte[])world.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(badChunk.AsSpan(countOffset - 4, 4), uint.MaxValue);
        RequireInvalid(badChunk, "compiled map index rejects a chunk extending beyond the index");

        var shrineUid = new byte[] { 0x9D, 0xAD, 0x78, 0x65, 0x06, 0x45, 0xD9, 0x9B, 0x7D, 0xE6, 0xED, 0xB1, 0xC3, 0x2E, 0x5F, 0x80 };
        var uidMarkers = Parse(CreateWorldWithTrailingUid(shrineUid));
        var shrine = uidMarkers.Single(x => x.Category == "Devotion Shrine");
        var riftgate = uidMarkers.Single(x => x.Category == "Riftgate");
        Require(shrine.EntityUid == Convert.ToHexString(shrineUid) && riftgate.EntityUid is null &&
                riftgate.WorldX == 30f && riftgate.WorldZ == 40f,
            "compiled map chunk decodes trailing 16-byte shrine UniqueIds and keeps following placements aligned");
    }

    private static List<WorldMarkerRecord> Parse(byte[] world)
    {
        var parser = typeof(WorldMarkerCatalogService).GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .Single(x => x.Name == "ParsePrimaryPlacements" && x.GetParameters()[0].ParameterType == typeof(byte[]));
        try { return (List<WorldMarkerRecord>)parser.Invoke(null, [world, CancellationToken.None, null, null])!; }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }

    private static (byte[] World, int CountOffset, int FirstStringOffset) CreateWorld()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write("MAP\t"u8);
        writer.Write(0); // completed index body length (excludes MAP header)
        var incidental = Encoding.UTF8.GetBytes("Levels/NotAnIndexedLevel.lvl");
        writer.Write(99u);
        writer.Write(incidental.Length);
        writer.Write(incidental);
        writer.Write(1u); // level index chunk
        var chunkSizeOffset = (int)stream.Position;
        writer.Write(0);
        var countOffset = (int)stream.Position;
        writer.Write(3u);
        var offsetFields = new List<int>();
        var firstStringOffset = 0;
        var origins = new[] { (100, 0, -200), (-400, 2, 500), (700, 0, -800) };
        for (var index = 0; index < origins.Length; index++)
        {
            for (var field = 0; field < 6; field++) writer.Write(64);
            var (x, y, z) = origins[index];
            writer.Write(x); writer.Write(y); writer.Write(z);
            writer.Write(new byte[16]);
            if (index == 0) firstStringOffset = (int)stream.Position;
            WriteString(writer, $"records/ui/map/location_{index}.dbr");
            WriteString(writer, index == 1 ? "records/ui/map/a_shrine.dbr" : "");
            WriteString(writer, index == 2 ? "a different optional record" : "");
            WriteString(writer, $"Levels/Region0A{index + 1:000}.lvl");
            offsetFields.Add((int)stream.Position);
            writer.Write(0); writer.Write(0);
        }
        var levelTableEnd = (int)stream.Position;
        // Shipped maps finish with a separate metadata chunk. Its last eight
        // bytes must be included by the header's body-length interpretation.
        writer.Write(16u);
        writer.Write(20);
        writer.Write(new byte[20]);
        var indexEnd = (int)stream.Position;
        var level = CreateLevel();
        foreach (var offsetField in offsetFields)
        {
            var position = (int)stream.Position;
            writer.Write(level);
            var nextPosition = stream.Position;
            stream.Position = offsetField;
            writer.Write(position); writer.Write(level.Length);
            stream.Position = nextPosition;
        }
        stream.Position = 4;
        writer.Write(indexEnd - 8);
        stream.Position = chunkSizeOffset;
        writer.Write(levelTableEnd - countOffset);
        return (stream.ToArray(), countOffset, firstStringOffset);
    }

    private static byte[] CreateLevel()
    {
        using var placements = new MemoryStream();
        using var placementWriter = new BinaryWriter(placements, Encoding.UTF8, leaveOpen: true);
        placementWriter.Write(1u);
        WriteString(placementWriter, "records/interactive/devotionshrine_test.dbr");
        placementWriter.Write(1u);
        placementWriter.Write(0u);
        for (var index = 0; index < 9; index++) placementWriter.Write(index % 4 == 0 ? 1f : 0f);
        placementWriter.Write(10f); placementWriter.Write(2f); placementWriter.Write(20f);
        placementWriter.Write(0u);
        using var level = new MemoryStream();
        using var writer = new BinaryWriter(level, Encoding.UTF8, leaveOpen: true);
        writer.Write(new byte[] { (byte)'L', (byte)'V', (byte)'L', 15 });
        writer.Write(new byte[24]);
        writer.Write(5u);
        writer.Write((int)placements.Length);
        writer.Write(placements.ToArray());
        return level.ToArray();
    }

    private static byte[] CreateWorldWithTrailingUid(byte[] uid)
    {
        using var placements = new MemoryStream();
        using var pw = new BinaryWriter(placements, Encoding.UTF8, leaveOpen: true);
        pw.Write(2u);
        WriteString(pw, "records/interactive/devotionshrine_test.dbr");
        WriteString(pw, "records/interactive/riftgate_test.dbr");
        pw.Write(2u);
        // Shrine placement with hasUniqueId = 1 followed by 16-byte UniqueId.
        pw.Write(0u);
        for (var i = 0; i < 9; i++) pw.Write(i % 4 == 0 ? 1f : 0f);
        pw.Write(10f); pw.Write(2f); pw.Write(20f);
        pw.Write(1u);
        pw.Write(uid);
        // Second placement immediately following must stay aligned.
        pw.Write(1u);
        for (var i = 0; i < 9; i++) pw.Write(i % 4 == 0 ? 1f : 0f);
        pw.Write(30f); pw.Write(4f); pw.Write(40f);
        pw.Write(0u);

        using var level = new MemoryStream();
        using var lw = new BinaryWriter(level, Encoding.UTF8, leaveOpen: true);
        lw.Write(new byte[] { (byte)'L', (byte)'V', (byte)'L', 15 });
        lw.Write(new byte[24]);
        lw.Write(5u);
        lw.Write((int)placements.Length);
        lw.Write(placements.ToArray());

        using var world = new MemoryStream();
        using var ww = new BinaryWriter(world, Encoding.UTF8, leaveOpen: true);
        ww.Write("MAP\t"u8);
        ww.Write(0);
        ww.Write(1u);
        var chunkSizeOffset = (int)world.Position;
        ww.Write(0);
        var countOffset = (int)world.Position;
        ww.Write(1u);
        for (var f = 0; f < 9; f++) ww.Write(0);
        ww.Write(new byte[16]);
        WriteString(ww, ""); WriteString(ww, ""); WriteString(ww, "");
        WriteString(ww, "Levels/Region0A001.lvl");
        var offsetField = (int)world.Position;
        ww.Write(0); ww.Write(0);
        var indexEnd = (int)world.Position;
        var levelBytes = level.ToArray();
        var levelPos = (int)world.Position;
        ww.Write(levelBytes);
        world.Position = 4; ww.Write(indexEnd - 8);
        world.Position = chunkSizeOffset; ww.Write(indexEnd - countOffset);
        world.Position = offsetField; ww.Write(levelPos); ww.Write(levelBytes.Length);
        return world.ToArray();
    }

    private static void WriteString(BinaryWriter writer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    private static void RequireInvalid(byte[] world, string message)
    {
        try { Parse(world); }
        catch (InvalidDataException) { Console.WriteLine("PASS  " + message); return; }
        throw new InvalidOperationException("TEST FAILED: " + message);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("TEST FAILED: " + message);
        Console.WriteLine("PASS  " + message);
    }
}
