using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;

namespace GrimDawnCompanion.Core;

/// <summary>
/// Builds a read-only placement index from the installed Levels.arc. The
/// source hash lets the desktop app rebuild automatically after a game patch.
/// </summary>
public sealed class WorldMarkerCatalogService
{
    public Task<WorldMarkerCatalog> BuildAsync(GameInstallation game, CancellationToken cancellationToken = default) =>
        BuildAsync(game, null, cancellationToken);

    public async Task<WorldMarkerCatalog> BuildAsync(GameInstallation game,
        IReadOnlyDictionary<string, string[]>? databaseQuestRecords, CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, QuestNavigationRecord>? navigationRecords = null)
    {
        var archivePaths = new[] { "resources", "gdx1/resources", "gdx2/resources", "gdx3/resources" }
            .Select(relative => Path.Combine(game.RootDirectory, relative.Replace('/', Path.DirectorySeparatorChar), "Levels.arc"))
            .Where(File.Exists)
            .ToArray();
        if (archivePaths.Length == 0) throw new FileNotFoundException("No installed Grim Dawn Levels.arc files were found.");
        var questArchives = new[] { "resources", "gdx1/resources", "gdx2/resources", "gdx3/resources" }
            .Select(relative => Path.Combine(game.RootDirectory, relative.Replace('/', Path.DirectorySeparatorChar), "Quests.arc"))
            .Where(File.Exists)
            .ToArray();
        var questRecords = await Task.Run(() => ReadQuestRecordAssociations(game, questArchives, databaseQuestRecords), cancellationToken);
        var scriptArchives = new[] { "resources", "gdx1/resources", "gdx2/resources", "gdx3/resources" }
            .Select(relative => Path.Combine(game.RootDirectory, relative.Replace('/', Path.DirectorySeparatorChar), "Scripts.arc"))
            .Where(File.Exists).ToArray();
        Dictionary<string, QuestNavigationIndex.Target[]>? targets = null;
        if (navigationRecords is not null)
        {
            targets = await Task.Run(() => QuestNavigationIndex.Resolve(navigationRecords,
                scriptArchives.SelectMany(path => ArcReader.ReadTextEntries(path, name => name.EndsWith(".lua", StringComparison.OrdinalIgnoreCase)).Values)), cancellationToken);
            // A whole-quest binary string reference is not proof of a current
            // objective. Use only task-qualified database associations here.
            questRecords = targets.ToDictionary(pair => pair.Key,
                pair => pair.Value.SelectMany(target => target.Bindings.Select(binding => binding.QuestPath)).Distinct().ToArray(),
                StringComparer.OrdinalIgnoreCase);
        }
        // This fingerprint also guards saved bookmarks. Preserve its existing
        // installed-map identity; enriching navigation metadata must not make
        // previously saved positions appear incompatible with unchanged maps.
        var hashes = await Task.Run(() => archivePaths.Concat(questArchives).Select(path =>
            $"{Path.GetRelativePath(game.RootDirectory, path).Replace('\\', '/')}:{Hashing.Sha256File(path)}").ToArray(), cancellationToken);
        var fingerprint = Hashing.CombineFingerprint(hashes);
        var allMarkers = new Dictionary<string, WorldMarkerRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var archivePath in archivePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var temporaryPath = Path.Combine(Path.GetTempPath(), $"gdc-world-{Guid.NewGuid():N}.map");
            try
            {
                await Task.Run(() =>
                {
                    using var temporary = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.ReadWrite,
                        FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
                    ArcReader.CopyFirstEntryToStream(archivePath,
                        name => name.EndsWith("world001.map", StringComparison.OrdinalIgnoreCase), temporary);
                    temporary.Position = 0;
                    foreach (var marker in ParsePrimaryPlacements(temporary, cancellationToken, questRecords)) allMarkers[marker.Id] = marker;
                }, cancellationToken);
            }
            finally { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
        }
        var markers = (targets is null ? allMarkers.Values : ExpandTargets(allMarkers.Values, targets))
            .OrderBy(marker => marker.Id, StringComparer.Ordinal).ToList();
        return new WorldMarkerCatalog
        {
            SourceFingerprint = fingerprint,
            GeneratedAt = DateTimeOffset.UtcNow,
            Markers = markers
        };
    }

    private static IEnumerable<WorldMarkerRecord> ExpandTargets(IEnumerable<WorldMarkerRecord> placements,
        IReadOnlyDictionary<string, QuestNavigationIndex.Target[]> targets)
    {
        foreach (var placement in placements)
        {
            if (placement.Category != "Quest Objective") yield return placement;
            if (!targets.TryGetValue(placement.RecordPath, out var resolved)) continue;
            for (var i = 0; i < resolved.Length; i++)
            {
                var target = resolved[i];
                yield return placement with
                {
                    Id = placement.Id + "|objective|" + i,
                    Category = "Quest Objective",
                    RecordPath = target.RecordPath,
                    DisplayName = target.Name ?? DisplayName(target.RecordPath, "Quest Objective"),
                    QuestPaths = target.Bindings.Select(binding => binding.QuestPath).Distinct().ToArray(),
                    QuestTargets = target.Bindings,
                    RequiredTokens = target.RequiredTokens,
                    ExcludedTokens = target.ExcludedTokens,
                    IsSpawnArea = placement.RecordPath.Contains("/proxies/", StringComparison.OrdinalIgnoreCase)
                };
            }
        }
    }

    internal static List<WorldMarkerRecord> ParsePrimaryPlacements(byte[] world, CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, string[]>? questRecords = null)
    {
        using var stream = new MemoryStream(world, writable: false);
        return ParsePrimaryPlacements(stream, cancellationToken, questRecords);
    }

    internal static List<WorldMarkerRecord> ParsePrimaryPlacements(Stream world, CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, string[]>? questRecords = null)
    {
        if (!world.CanSeek || world.Length < 8) throw new InvalidDataException("Compiled world map stream is invalid.");
        var header = ReadBytes(world, 0, 8);
        if (!header.AsSpan(0, 4).SequenceEqual("MAP\t"u8))
            throw new InvalidDataException("Unsupported compiled world map header.");
        // MAP stores the byte length of the index body, excluding this
        // eight-byte header. Treating it as an absolute end truncates the
        // final index chunk by eight bytes in every shipped archive.
        var indexEnd = checked(8 + (int)ReadU32(header, 4));
        if (indexEnd is < 8 || indexEnd > world.Length) throw new InvalidDataException("Compiled world map index is invalid.");
        var index = ReadBytes(world, 0, indexEnd);

        var levels = FindLevels(index);
        var markers = new List<WorldMarkerRecord>();
        foreach (var level in levels)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { ParseLevel(world, level, markers, questRecords); }
            catch (Exception exception) when (exception is InvalidDataException or OverflowException or DecoderFallbackException)
            {
                // A malformed embedded level must not prevent the remaining
                // valid indexed levels from supplying their placements.
            }
        }
        markers.Sort((left, right) => string.Compare(left.Id, right.Id, StringComparison.Ordinal));
        return markers;
    }

    private static List<LevelEntry> FindLevels(ReadOnlySpan<byte> index)
    {
        var result = new List<LevelEntry>();
        var seenOffsets = new HashSet<long>();
        var chunkCursor = 8;
        while (chunkCursor <= index.Length - 8)
        {
            var chunkType = ReadU32(index, chunkCursor);
            var chunkSize = ReadU32(index, chunkCursor + 4);
            var dataStart = chunkCursor + 8;
            if (chunkSize > index.Length - dataStart)
                throw new InvalidDataException("Compiled world map index chunk is truncated.");
            var data = index.Slice(dataStart, (int)chunkSize);
            if (chunkType == 1)
            {
                var count = ReadU32(data, 0);
                // Six bounds, origin, entity ID, four string lengths, offset,
                // and size require at least 76 bytes per level entry.
                if (count > (data.Length - 4) / 76)
                    throw new InvalidDataException("Compiled world map level count is invalid.");
                var cursor = 4;
                for (var entry = 0; entry < count; entry++)
                {
                    if (cursor > data.Length - 52)
                        throw new InvalidDataException("Compiled world map level metadata is truncated.");
                    // The origin belongs to the metadata BEFORE this level's
                    // four strings. Only offset and size follow the .lvl path;
                    // the next bytes describe the NEXT level. Reading an
                    // origin after the path shifts every catalog marker into
                    // its neighboring level when the live marker unloads.
                    var originX = unchecked((int)ReadU32(data, cursor + 24));
                    var originY = unchecked((int)ReadU32(data, cursor + 28));
                    var originZ = unchecked((int)ReadU32(data, cursor + 32));
                    cursor += 52; // bounds, origin, 16-byte level ID
                    _ = ReadIndexString(data, ref cursor); // map location record
                    _ = ReadIndexString(data, ref cursor); // shrine map record
                    _ = ReadIndexString(data, ref cursor); // optional record
                    var name = ReadIndexString(data, ref cursor).Replace('\\', '/');
                    var dataOffset = (long)ReadU32(data, cursor);
                    var dataLength = (long)ReadU32(data, cursor + 4);
                    cursor += 8;
                    if (!name.EndsWith(".lvl", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Compiled world map level path is invalid.");
                    if (seenOffsets.Add(dataOffset))
                        result.Add(new LevelEntry(name, dataOffset, dataLength, originX, originY, originZ));
                }
                if (cursor != data.Length)
                    throw new InvalidDataException("Compiled world map level table has unexpected trailing data.");
            }
            chunkCursor = dataStart + (int)chunkSize;
        }
        if (chunkCursor != index.Length)
            throw new InvalidDataException("Compiled world map index is truncated.");
        return result;
    }

    private static string ReadIndexString(ReadOnlySpan<byte> data, ref int cursor)
    {
        var length = ReadU32(data, cursor);
        cursor += 4;
        if (length > data.Length - cursor)
            throw new InvalidDataException("Compiled world map index string is truncated.");
        var value = Encoding.UTF8.GetString(data.Slice(cursor, (int)length));
        cursor += (int)length;
        return value;
    }

    private static void ParseLevel(Stream world, LevelEntry level, List<WorldMarkerRecord> output,
        IReadOnlyDictionary<string, string[]>? questRecords)
    {
        var offset = level.DataOffset;
        // The index scan may encounter incidental `.lvl` bytes. Reject those
        // with subtraction-based bounds checks so a hostile or corrupt offset
        // cannot overflow before validation.
        if (offset < 0 || level.DataLength < 28 || offset > world.Length - level.DataLength) return;
        var levelHeader = ReadBytes(world, offset, 28);
        if (!levelHeader.AsSpan(0, 3).SequenceEqual("LVL"u8) || levelHeader[3] != 15) return;
        var levelEnd = offset + level.DataLength;
        var chunkCursor = offset + 28;
        while (chunkCursor <= levelEnd - 8)
        {
            var chunkHeader = ReadBytes(world, chunkCursor, 8);
            var chunkType = ReadU32(chunkHeader, 0);
            var chunkSize = ReadU32(chunkHeader, 4);
            var dataStart = chunkCursor + 8;
            if (chunkSize > int.MaxValue || dataStart > levelEnd - chunkSize) return;
            if (chunkType == 5)
                ParsePlacementChunk(world, dataStart, (int)chunkSize, level, output, questRecords);
            chunkCursor = dataStart + chunkSize;
        }
    }

    private static void ParsePlacementChunk(Stream world, long dataStart, int dataLength, LevelEntry level,
        List<WorldMarkerRecord> output, IReadOnlyDictionary<string, string[]>? questRecords)
    {
        var dataEnd = dataStart + dataLength;
        var stringCountRaw = ReadU32(ReadBytes(world, dataStart, 4), 0);
        if (stringCountRaw > 100_000) return;
        var stringCount = (int)stringCountRaw;
        long cursor = dataStart + 4;
        var strings = new string[stringCount];
        for (var index = 0; index < stringCount; index++)
        {
            var lengthRaw = ReadU32(ReadBytes(world, cursor, 4), 0);
            cursor += 4;
            if (lengthRaw > int.MaxValue) return;
            var length = (int)lengthRaw;
            strings[index] = Encoding.UTF8.GetString(ReadBytes(world, cursor, length));
            cursor += length;
        }

        var placementCountRaw = ReadU32(ReadBytes(world, cursor, 4), 0);
        cursor += 4;
        if (placementCountRaw > 1_000_000 || cursor > dataEnd - (long)placementCountRaw * 56) return;
        var placementCount = (int)placementCountRaw;
        for (var index = 0; index < placementCount; index++)
        {
            // Instance-group members carry their 16-byte entity ID directly
            // before the ordinary placement record. The first four ID bytes
            // cannot be a record-table index; consume the prefix so later
            // placements stay aligned. Quest NPCs such as Barnabas and
            // Kasparov use this compiled-level layout.
            var candidate = ReadU32(ReadBytes(world, cursor, 4), 0);
            if (candidate >= (uint)strings.Length)
            {
                if (cursor > dataEnd - 72) return;
                cursor += 16;
            }
            else if (cursor > dataEnd - 56) return;

            var placement = ReadBytes(world, cursor, 56);
            var recordIndexRaw = ReadU32(placement, 0);
            AddPlacement(strings, recordIndexRaw, placement, 40, level, output, questRecords);
            cursor += 56;
        }
    }

    private static void AddPlacement(string[] strings, uint recordIndexRaw, ReadOnlySpan<byte> placement,
        int coordinateOffset, LevelEntry level, List<WorldMarkerRecord> output,
        IReadOnlyDictionary<string, string[]>? questRecords)
    {
        if (recordIndexRaw >= (uint)strings.Length) return;
        var recordPath = strings[(int)recordIndexRaw].Replace('\\', '/');
        var category = Classify(recordPath);
        string[]? questPaths = null;
        if (questRecords?.TryGetValue(recordPath, out questPaths) == true && category is null)
            category = "Quest Objective";
        if (category is null) return;
        var x = ReadF32(placement, coordinateOffset) + level.OriginX;
        var y = ReadF32(placement, coordinateOffset + 4) + level.OriginY;
        var z = ReadF32(placement, coordinateOffset + 8) + level.OriginZ;
        if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z)) return;
        var id = $"{level.Name}|{recordPath}|{BitConverter.SingleToUInt32Bits(x):x8}|{BitConverter.SingleToUInt32Bits(z):x8}";
        output.Add(new WorldMarkerRecord(id, category, recordPath, level.Name, x, y, z, questPaths,
            DisplayName(recordPath, category)));
    }

    private static Dictionary<string, string[]> ReadQuestRecordAssociations(GameInstallation game, IEnumerable<string> archives,
        IReadOnlyDictionary<string, string[]>? databaseQuestRecords)
    {
        var associations = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        if (databaseQuestRecords is not null)
        {
            foreach (var (record, questPaths) in databaseQuestRecords)
            {
                var normalizedRecord = record.Replace('\\', '/').TrimStart('/');
                if (!associations.TryGetValue(normalizedRecord, out var paths))
                    associations[normalizedRecord] = paths = new(StringComparer.OrdinalIgnoreCase);
                paths.UnionWith(questPaths.Select(path => path.Replace('\\', '/').TrimStart('/')));
            }
        }
        foreach (var archive in archives)
        {
            var relativeArchive = Path.GetRelativePath(game.RootDirectory, archive).Replace('\\', '/');
            var expansion = relativeArchive.Contains('/') ? relativeArchive[..relativeArchive.IndexOf('/')] : "";
            foreach (var (entryName, bytes) in ArcReader.ReadEntries(archive,
                         name => name.EndsWith(".qst", StringComparison.OrdinalIgnoreCase)))
            {
                var normalizedEntry = entryName.Replace('\\', '/').TrimStart('/');
                var questPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    normalizedEntry,
                    "quests/" + normalizedEntry
                };
                if (expansion.Length > 0) questPaths.Add(expansion + "/quests/" + normalizedEntry);
                var binaryText = Encoding.Latin1.GetString(bytes);
                foreach (Match match in Regex.Matches(binaryText, @"[A-Za-z0-9_./\\-]*quests[/\\][A-Za-z0-9_./\\-]+\.qst",
                             RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                    questPaths.Add(match.Value.Replace('\\', '/').TrimStart('/'));

                foreach (Match match in Regex.Matches(binaryText, @"records[/\\][A-Za-z0-9_&@+./\\-]+\.dbr",
                             RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                {
                    var record = match.Value.Replace('\\', '/');
                    if (!associations.TryGetValue(record, out var paths)) associations[record] = paths = new(StringComparer.OrdinalIgnoreCase);
                    paths.UnionWith(questPaths);
                }
            }
        }
        return associations.ToDictionary(pair => pair.Key,
            pair => pair.Value.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray(),
            StringComparer.OrdinalIgnoreCase);
    }

    private static string? Classify(string path)
    {
        var record = path.ToLowerInvariant();
        if (record.Contains("devotionshrine")) return "Devotion Shrine";
        if (record.StartsWith("records/interactive/") && record.Contains("riftgate")) return "Riftgate";
        if (record.StartsWith("records/items/lootchests/oneshotchests/")) return "One-Shot Chest";
        if (record.StartsWith("records/items/loreobjects/")) return "Lore Note";
        if (record.StartsWith("records/creatures/npcs/blacksmith")) return "Blacksmith";
        if (record.StartsWith("records/creatures/npcs/spiritguide")) return "Spirit Guide";
        if (record.StartsWith("records/creatures/npcs/merchants/")) return "Vendor";
        if ((record.Contains("/doorobjects/") || record.Contains("/interactive/")) &&
            (record.Contains("boat") || record.Contains("ferry"))) return "Travel Utility";
        if (record.StartsWith("records/interactive/") && record.Contains("ascension") && record.Contains("altar"))
            return "Ascension Altar";
        return null;
    }

    private static string? DisplayName(string recordPath, string category)
    {
        if (category == "Travel Utility" && recordPath.Contains("boat", StringComparison.OrdinalIgnoreCase))
            return "Row Boat";
        if (category != "Quest Objective") return null;
        var name = Path.GetFileNameWithoutExtension(recordPath);
        name = Regex.Replace(name, "^npc_", "", RegexOptions.IgnoreCase);
        name = Regex.Replace(name, "_[0-9]+$", "");
        name = name.Replace('_', ' ').Replace('-', ' ').Trim();
        return name.Length == 0 ? "Quest objective" :
            System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(name.ToLowerInvariant());
    }

    private static uint ReadU32(ReadOnlySpan<byte> data, int offset)
    {
        if (offset < 0 || offset + 4 > data.Length) throw new InvalidDataException("Compiled map data is truncated.");
        return BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4));
    }

    private static float ReadF32(ReadOnlySpan<byte> data, int offset) => BitConverter.UInt32BitsToSingle(ReadU32(data, offset));

    private static byte[] ReadBytes(Stream stream, long offset, int length)
    {
        if (offset < 0 || length < 0 || offset > stream.Length - length)
            throw new InvalidDataException("Compiled map data is truncated.");
        var result = new byte[length];
        stream.Position = offset;
        stream.ReadExactly(result);
        return result;
    }

    private sealed record LevelEntry(string Name, long DataOffset, long DataLength, float OriginX, float OriginY, float OriginZ);
}
