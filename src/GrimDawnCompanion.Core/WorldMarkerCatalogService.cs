using System.Buffers;
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
        var scriptArchives = new[] { "resources", "gdx1/resources", "gdx2/resources", "gdx3/resources" }
            .Select(relative => Path.Combine(game.RootDirectory, relative.Replace('/', Path.DirectorySeparatorChar), "Scripts.arc"))
            .Where(File.Exists).ToArray();

        // This fingerprint also guards saved bookmarks. Preserve its existing
        // installed-map identity; enriching navigation metadata must not make
        // previously saved positions appear incompatible with unchanged maps.
        var allHashPaths = archivePaths.Concat(questArchives).ToArray();
        var fingerprintTask = Task.Run(() =>
        {
            var hashes = new string[allHashPaths.Length];
            Parallel.For(0, allHashPaths.Length, new ParallelOptions { CancellationToken = cancellationToken }, index =>
            {
                var path = allHashPaths[index];
                hashes[index] = $"{Path.GetRelativePath(game.RootDirectory, path).Replace('\\', '/')}:{Hashing.Sha256File(path)}";
            });
            return Hashing.CombineFingerprint(hashes);
        }, cancellationToken);

        var luaScriptsTask = Task.Run(() => scriptArchives.AsParallel().AsOrdered().SelectMany(path =>
            ArcReader.ReadTextEntries(path, name => name.EndsWith(".lua", StringComparison.OrdinalIgnoreCase)).Values)
            .ToArray(), cancellationToken);

        var questTask = Task.Run(async () =>
        {
            Dictionary<string, QuestNavigationIndex.Target[]>? resolvedTargets = null;
            Dictionary<string, string[]> resolvedQuestRecords;
            if (navigationRecords is not null)
            {
                var luaScripts = await luaScriptsTask;
                var relevantScripts = luaScripts.Where(script =>
                    script.Contains("TokenStateBasedObjectSwap", StringComparison.Ordinal) &&
                    script.Contains("orderedTable", StringComparison.Ordinal));
                resolvedTargets = QuestNavigationIndex.Resolve(navigationRecords, relevantScripts);
                // A whole-quest binary string reference is not proof of a current
                // objective. Use only task-qualified database associations here.
                resolvedQuestRecords = resolvedTargets.ToDictionary(pair => pair.Key,
                    pair => pair.Value.SelectMany(target => target.Bindings.Select(binding => binding.QuestPath)).Distinct().ToArray(),
                    StringComparer.OrdinalIgnoreCase);
            }
            else
            {
                resolvedQuestRecords = ReadQuestRecordAssociations(game, questArchives, databaseQuestRecords);
            }
            return (resolvedTargets, resolvedQuestRecords);
        }, cancellationToken);

        var metadataTask = Task.Run(async () =>
            await ReadArzMarkerMetadataAsync(game, luaScriptsTask, cancellationToken), cancellationToken);

        var (targets, questRecords) = await questTask;
        var markerMetadata = await metadataTask;

        var perArchiveMarkers = new List<WorldMarkerRecord>[archivePaths.Length];
        await Task.Run(() =>
        {
            var descriptors = new (ArcReader.IndexedEntryDescriptor Descriptor, List<LevelEntry> Levels)[archivePaths.Length];
            Parallel.For(0, archivePaths.Length, new ParallelOptions { CancellationToken = cancellationToken }, index =>
            {
                var descriptor = ArcReader.IndexedEntryDescriptor.OpenFirst(archivePaths[index],
                    name => name.EndsWith("world001.map", StringComparison.OrdinalIgnoreCase));
                using var stream = descriptor.CreateStream();
                descriptors[index] = (descriptor, ReadMapLevels(stream));
            });

            const int BatchSize = 64;
            var batches = new List<(int ArchiveIndex, int StartLevel, int Count)>();
            for (var archiveIndex = 0; archiveIndex < descriptors.Length; archiveIndex++)
            {
                var levelCount = descriptors[archiveIndex].Levels.Count;
                for (var start = 0; start < levelCount; start += BatchSize)
                    batches.Add((archiveIndex, start, Math.Min(BatchSize, levelCount - start)));
            }

            var batchMarkers = new List<WorldMarkerRecord>[batches.Count];
            Parallel.For(0, batches.Count, new ParallelOptions { CancellationToken = cancellationToken }, batchIndex =>
            {
                var (archiveIndex, start, count) = batches[batchIndex];
                var (descriptor, levels) = descriptors[archiveIndex];
                using var stream = descriptor.CreateStream();
                var localMarkers = new List<WorldMarkerRecord>();
                var scratchBuffer = ArrayPool<byte>.Shared.Rent(256 * 1024);
                try
                {
                    for (var i = start; i < start + count; i++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        try
                        {
                            ParseLevel(stream, levels[i], ref scratchBuffer, localMarkers,
                                questRecords, markerMetadata.RiftgateNames, markerMetadata.Shrines, markerMetadata.ChestOpenTokens);
                        }
                        catch (Exception exception) when (exception is InvalidDataException or OverflowException or DecoderFallbackException)
                        {
                            // A malformed embedded level must not prevent the remaining
                            // valid indexed levels from supplying their placements.
                        }
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(scratchBuffer);
                }
                batchMarkers[batchIndex] = localMarkers;
            });

            for (var archiveIndex = 0; archiveIndex < archivePaths.Length; archiveIndex++)
                perArchiveMarkers[archiveIndex] = new List<WorldMarkerRecord>();
            for (var batchIndex = 0; batchIndex < batches.Count; batchIndex++)
                perArchiveMarkers[batches[batchIndex].ArchiveIndex].AddRange(batchMarkers[batchIndex]);
            for (var archiveIndex = 0; archiveIndex < archivePaths.Length; archiveIndex++)
                perArchiveMarkers[archiveIndex].Sort((left, right) => string.Compare(left.Id, right.Id, StringComparison.Ordinal));
        }, cancellationToken);

        var allMarkers = new Dictionary<string, WorldMarkerRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var archiveMarkers in perArchiveMarkers)
            foreach (var marker in archiveMarkers)
                allMarkers[marker.Id] = marker;

        var markers = (targets is null ? allMarkers.Values : ExpandTargets(allMarkers.Values, targets))
            .OrderBy(marker => marker.Id, StringComparer.Ordinal).ToList();
        var fingerprint = await fingerprintTask;
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
        IReadOnlyDictionary<string, string[]>? questRecords = null,
        IReadOnlyDictionary<string, string>? riftgateNames = null)
    {
        using var stream = new MemoryStream(world, writable: false);
        return ParsePrimaryPlacements(stream, cancellationToken, questRecords, riftgateNames);
    }

    internal static List<WorldMarkerRecord> ParsePrimaryPlacements(Stream world, CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, string[]>? questRecords = null,
        IReadOnlyDictionary<string, string>? riftgateNames = null,
        IReadOnlyDictionary<string, ShrineMetadata>? shrineMetadata = null,
        IReadOnlyDictionary<string, string[]>? chestOpenTokens = null)
    {
        var levels = ReadMapLevels(world);
        var markers = new List<WorldMarkerRecord>();
        var scratchBuffer = ArrayPool<byte>.Shared.Rent(256 * 1024);
        try
        {
            foreach (var level in levels)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try { ParseLevel(world, level, ref scratchBuffer, markers, questRecords, riftgateNames, shrineMetadata, chestOpenTokens); }
                catch (Exception exception) when (exception is InvalidDataException or OverflowException or DecoderFallbackException)
                {
                    // A malformed embedded level must not prevent the remaining
                    // valid indexed levels from supplying their placements.
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scratchBuffer);
        }
        markers.Sort((left, right) => string.Compare(left.Id, right.Id, StringComparison.Ordinal));
        return markers;
    }

    private static List<LevelEntry> ReadMapLevels(Stream world)
    {
        if (!world.CanSeek || world.Length < 8) throw new InvalidDataException("Compiled world map stream is invalid.");
        Span<byte> header = stackalloc byte[8];
        ReadSpan(world, 0, header);
        if (!header[..4].SequenceEqual("MAP\t"u8))
            throw new InvalidDataException("Unsupported compiled world map header.");
        // MAP stores the byte length of the index body, excluding this
        // eight-byte header. Treating it as an absolute end truncates the
        // final index chunk by eight bytes in every shipped archive.
        var indexEnd = checked(8 + (int)ReadU32(header, 4));
        if (indexEnd < 8 || indexEnd > world.Length) throw new InvalidDataException("Compiled world map index is invalid.");
        var index = new byte[indexEnd];
        ReadSpan(world, 0, index);
        return FindLevels(index);
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
                    SkipIndexString(data, ref cursor); // map location record
                    SkipIndexString(data, ref cursor); // shrine map record
                    SkipIndexString(data, ref cursor); // optional record
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

    private static void SkipIndexString(ReadOnlySpan<byte> data, ref int cursor)
    {
        var length = ReadU32(data, cursor);
        cursor += 4;
        if (length > data.Length - cursor)
            throw new InvalidDataException("Compiled world map index string is truncated.");
        cursor += (int)length;
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

    private static void ParseLevel(Stream world, LevelEntry level, ref byte[] scratchBuffer, List<WorldMarkerRecord> output,
        IReadOnlyDictionary<string, string[]>? questRecords, IReadOnlyDictionary<string, string>? riftgateNames,
        IReadOnlyDictionary<string, ShrineMetadata>? shrineMetadata,
        IReadOnlyDictionary<string, string[]>? chestOpenTokens = null)
    {
        var offset = level.DataOffset;
        // The index scan may encounter incidental `.lvl` bytes. Reject those
        // with subtraction-based bounds checks so a hostile or corrupt offset
        // cannot overflow before validation.
        if (offset < 0 || level.DataLength < 36 || offset > world.Length - level.DataLength) return;
        var levelEnd = offset + level.DataLength;

        Span<byte> initial = stackalloc byte[36];
        ReadSpan(world, offset, initial);
        if (!initial[..3].SequenceEqual("LVL"u8) || initial[3] != 15) return;
        var chunkType = ReadU32(initial, 28);
        var chunkSize = ReadU32(initial, 32);
        var dataStart = offset + 36;
        if (chunkSize > int.MaxValue || dataStart > levelEnd - chunkSize) return;
        if (chunkType == 5)
        {
            ReadAndParsePlacementChunk(world, dataStart, (int)chunkSize, ref scratchBuffer,
                level, output, questRecords, riftgateNames, shrineMetadata, chestOpenTokens);
            return;
        }

        var chunkCursor = dataStart + chunkSize;
        Span<byte> chunkHeader = stackalloc byte[8];
        while (chunkCursor <= levelEnd - 8)
        {
            ReadSpan(world, chunkCursor, chunkHeader);
            chunkType = ReadU32(chunkHeader, 0);
            chunkSize = ReadU32(chunkHeader, 4);
            dataStart = chunkCursor + 8;
            if (chunkSize > int.MaxValue || dataStart > levelEnd - chunkSize) return;
            if (chunkType == 5)
            {
                ReadAndParsePlacementChunk(world, dataStart, (int)chunkSize, ref scratchBuffer,
                    level, output, questRecords, riftgateNames, shrineMetadata, chestOpenTokens);
                return;
            }
            chunkCursor = dataStart + chunkSize;
        }
    }

    private static void ReadAndParsePlacementChunk(Stream world, long dataStart, int dataLength, ref byte[] scratchBuffer,
        LevelEntry level, List<WorldMarkerRecord> output, IReadOnlyDictionary<string, string[]>? questRecords,
        IReadOnlyDictionary<string, string>? riftgateNames, IReadOnlyDictionary<string, ShrineMetadata>? shrineMetadata,
        IReadOnlyDictionary<string, string[]>? chestOpenTokens = null)
    {
        if (scratchBuffer.Length < dataLength)
        {
            ArrayPool<byte>.Shared.Return(scratchBuffer);
            scratchBuffer = ArrayPool<byte>.Shared.Rent(dataLength);
        }
        var chunk = scratchBuffer.AsSpan(0, dataLength);
        ReadSpan(world, dataStart, chunk);
        ParsePlacementChunk(chunk, level, output, questRecords, riftgateNames, shrineMetadata, chestOpenTokens);
    }

    private readonly record struct ResolvedStringEntry(byte State, string? RecordPath, string? Category, string[]? QuestPaths);

    private static void ParsePlacementChunk(ReadOnlySpan<byte> chunk, LevelEntry level,
        List<WorldMarkerRecord> output, IReadOnlyDictionary<string, string[]>? questRecords,
        IReadOnlyDictionary<string, string>? riftgateNames, IReadOnlyDictionary<string, ShrineMetadata>? shrineMetadata,
        IReadOnlyDictionary<string, string[]>? chestOpenTokens = null)
    {
        if (chunk.Length < 4) return;
        var stringCountRaw = ReadU32(chunk, 0);
        if (stringCountRaw > 100_000) return;
        var stringCount = (int)stringCountRaw;
        var cursor = 4;
        var stringSlices = ArrayPool<(int Offset, int Length)>.Shared.Rent(stringCount);
        var resolvedStrings = ArrayPool<ResolvedStringEntry>.Shared.Rent(stringCount);
        Array.Clear(resolvedStrings, 0, stringCount);
        try
        {
            for (var index = 0; index < stringCount; index++)
            {
                if (cursor > chunk.Length - 4) return;
                var lengthRaw = ReadU32(chunk, cursor);
                cursor += 4;
                if (lengthRaw > int.MaxValue || lengthRaw > (uint)(chunk.Length - cursor)) return;
                var length = (int)lengthRaw;
                stringSlices[index] = (cursor, length);
                cursor += length;
            }

            if (cursor > chunk.Length - 4) return;
            var placementCountRaw = ReadU32(chunk, cursor);
            cursor += 4;
            if (placementCountRaw > 1_000_000 || cursor > chunk.Length - (long)placementCountRaw * 56) return;
            var placementCount = (int)placementCountRaw;
            for (var index = 0; index < placementCount; index++)
            {
                if (cursor > chunk.Length - 56) return;
                var placement = chunk.Slice(cursor, 56);
                cursor += 56;
                // Placement flags at +52 indicate a trailing 16-byte Entity UniqueId
                // copied verbatim into Player::GetShrineUIDs() when restored.
                ReadOnlySpan<byte> uidBytes = default;
                var hasUid = ReadU32(placement, 52) != 0;
                if (hasUid)
                {
                    if (cursor > chunk.Length - 16) return;
                    uidBytes = chunk.Slice(cursor, 16);
                    cursor += 16;
                }
                var recordIndexRaw = ReadU32(placement, 0);
                if (recordIndexRaw >= (uint)stringCount) continue;
                var recIdx = (int)recordIndexRaw;

                var entry = resolvedStrings[recIdx];
                if (entry.State == 0)
                {
                    var (sOff, sLen) = stringSlices[recIdx];
                    var recordPath = Encoding.UTF8.GetString(chunk.Slice(sOff, sLen)).Replace('\\', '/');
                    var category = Classify(recordPath);
                    string[]? questPaths = null;
                    if (questRecords?.TryGetValue(recordPath, out questPaths) == true && category is null)
                        category = "Quest Objective";
                    entry = new ResolvedStringEntry(category is null ? (byte)1 : (byte)2, recordPath, category, questPaths);
                    resolvedStrings[recIdx] = entry;
                }
                if (entry.State == 1) continue;

                var x = ReadF32(placement, 40) + level.OriginX;
                var y = ReadF32(placement, 44) + level.OriginY;
                var z = ReadF32(placement, 48) + level.OriginZ;
                if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z)) continue;
                var entityUid = hasUid ? Convert.ToHexString(uidBytes) : null;
                var id = $"{level.Name}|{entry.RecordPath!}|{BitConverter.SingleToUInt32Bits(x):x8}|{BitConverter.SingleToUInt32Bits(z):x8}";
                var normalizedRecord = entry.RecordPath!.TrimStart('/');
                var displayName = entry.Category == "Riftgate" && riftgateNames is not null &&
                                  riftgateNames.TryGetValue(normalizedRecord, out var localizedRiftgate)
                    ? localizedRiftgate
                    : DisplayName(entry.RecordPath!, entry.Category!);
                ShrineMetadata? shrine = null;
                if (entry.Category == "Devotion Shrine") shrineMetadata?.TryGetValue(normalizedRecord, out shrine);
                string[]? excludedTokens = null;
                if (entry.Category == "One-Shot Chest") chestOpenTokens?.TryGetValue(normalizedRecord, out excludedTokens);
                output.Add(new WorldMarkerRecord(id, entry.Category!, entry.RecordPath!, level.Name, x, y, z, entry.QuestPaths, displayName,
                    ExcludedTokens: excludedTokens,
                    EntityUid: entityUid,
                    DisabledDifficultiesMask: shrine?.DisabledDifficultiesMask ?? 0,
                    HideMapIcon: shrine?.HideMapIcon ?? false));
            }
        }
        finally
        {
            ArrayPool<(int Offset, int Length)>.Shared.Return(stringSlices);
            ArrayPool<ResolvedStringEntry>.Shared.Return(resolvedStrings);
        }
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
        if (path.Contains("devotionshrine", StringComparison.OrdinalIgnoreCase)) return "Devotion Shrine";
        if ((path.StartsWith("records/interactive/", StringComparison.OrdinalIgnoreCase) ||
             path.StartsWith("records/ui/riftgatemap/locations/", StringComparison.OrdinalIgnoreCase)) &&
            path.Contains("riftgate", StringComparison.OrdinalIgnoreCase)) return "Riftgate";
        if (path.StartsWith("records/items/lootchests/oneshotchests/", StringComparison.OrdinalIgnoreCase)) return "One-Shot Chest";
        if (path.StartsWith("records/items/loreobjects/", StringComparison.OrdinalIgnoreCase)) return "Lore Note";
        if (path.StartsWith("records/creatures/npcs/blacksmith", StringComparison.OrdinalIgnoreCase)) return "Blacksmith";
        if (path.StartsWith("records/creatures/npcs/spiritguide", StringComparison.OrdinalIgnoreCase)) return "Spirit Guide";
        if (path.StartsWith("records/creatures/npcs/merchants/", StringComparison.OrdinalIgnoreCase)) return "Vendor";
        if ((path.Contains("/doorobjects/", StringComparison.OrdinalIgnoreCase) ||
             path.Contains("/interactive/", StringComparison.OrdinalIgnoreCase)) &&
            (path.Contains("boat", StringComparison.OrdinalIgnoreCase) ||
             path.Contains("ferry", StringComparison.OrdinalIgnoreCase))) return "Travel Utility";
        if (path.StartsWith("records/interactive/", StringComparison.OrdinalIgnoreCase) &&
            path.Contains("ascension", StringComparison.OrdinalIgnoreCase) &&
            path.Contains("altar", StringComparison.OrdinalIgnoreCase))
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

    private static async Task<ArzMarkerMetadata> ReadArzMarkerMetadataAsync(GameInstallation game,
        Task<string[]> luaScriptsTask, CancellationToken cancellationToken)
    {
        var archives = GameLocator.GetInstalledArchives(game)
            .Where(archive => archive.Id is "base" or "gdx1" or "gdx2" or "gdx3")
            .ToArray();
        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var archive in archives)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(archive.TextPath) || !File.Exists(archive.TextPath) ||
                new FileInfo(archive.TextPath).Length <= 2048)
                continue;
            try
            {
                foreach (var pair in ArcReader.ReadTags(archive.TextPath))
                    tags[pair.Key] = pair.Value;
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested) { }
        }

        var perArchive = new (Dictionary<string, string> Rifts, Dictionary<string, ShrineMetadata> Shrines,
            Dictionary<string, OneShotChestArzEntry> Chests)[archives.Length];
        Parallel.For(0, archives.Length, new ParallelOptions { CancellationToken = cancellationToken }, index =>
        {
            var localRifts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var localShrines = new Dictionary<string, ShrineMetadata>(StringComparer.OrdinalIgnoreCase);
            var localChests = new Dictionary<string, OneShotChestArzEntry>(StringComparer.OrdinalIgnoreCase);
            try { ReadMarkerMetadataFromArz(archives[index].DatabasePath, tags, localRifts, localShrines, localChests, cancellationToken); }
            catch (Exception) when (!cancellationToken.IsCancellationRequested) { }
            perArchive[index] = (localRifts, localShrines, localChests);
        });

        var riftgateNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var shrines = new Dictionary<string, ShrineMetadata>(StringComparer.OrdinalIgnoreCase);
        var chestEntries = new Dictionary<string, OneShotChestArzEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var (localRifts, localShrines, localChests) in perArchive)
        {
            foreach (var pair in localRifts) riftgateNames[pair.Key] = pair.Value;
            foreach (var pair in localShrines) shrines[pair.Key] = pair.Value;
            foreach (var pair in localChests) chestEntries[pair.Key] = pair.Value;
        }
        var chestOpenTokens = ResolveOneShotChestTokens(chestEntries, await luaScriptsTask);
        return new ArzMarkerMetadata(riftgateNames, shrines, chestOpenTokens);
    }

    private static void ReadMarkerMetadataFromArz(string arzPath, IReadOnlyDictionary<string, string> tags,
        Dictionary<string, string> riftgateNames, Dictionary<string, ShrineMetadata> shrines,
        Dictionary<string, OneShotChestArzEntry> chestEntries, CancellationToken cancellationToken)
    {
        using var stream = File.OpenRead(arzPath);
        if (stream.Length < 24) return;
        Span<byte> header = stackalloc byte[24];
        ReadSpan(stream, 0, header);
        var magic = ReadU16(header, 0);
        var version = ReadU16(header, 2);
        var recordTableOffset = (long)ReadU32(header, 4);
        var recordTableSize = (long)ReadU32(header, 8);
        var recordCount = ReadU32(header, 12);
        var stringTableOffset = (long)ReadU32(header, 16);
        var stringTableSize = (long)ReadU32(header, 20);
        if (magic is not (2 or 3) || version != 3) return;
        if (recordTableOffset < 24 || recordTableSize < 4 || recordTableOffset > stream.Length - recordTableSize) return;
        if (stringTableOffset < 24 || stringTableSize < 4 || stringTableOffset > stream.Length - stringTableSize) return;
        if (stringTableSize > 64 * 1024 * 1024 || recordTableSize > 64 * 1024 * 1024) return;

        var stringTable = ReadBytes(stream, stringTableOffset, (int)stringTableSize);
        var stringCount = ReadU32(stringTable, 0);
        if (stringCount > 2_000_000) return;
        var stringSlices = new (int Offset, int Length)[stringCount];
        var sc = 4;
        for (uint i = 0; i < stringCount; i++)
        {
            if (sc > stringTable.Length - 4) return;
            var len = ReadU32(stringTable, sc);
            sc += 4;
            if (len > stringTable.Length - sc) return;
            stringSlices[i] = (sc, (int)len);
            sc += (int)len;
        }
        string GetString(uint id)
        {
            var (off, len) = stringSlices[id];
            return Encoding.Latin1.GetString(stringTable, off, len);
        }

        var recordTable = ReadBytes(stream, recordTableOffset, (int)recordTableSize);
        var rc = 0;
        for (uint i = 0; i < recordCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (rc > recordTable.Length - 28) return;
            var nameId = ReadU32(recordTable, rc); rc += 4;
            var typeLen = ReadU32(recordTable, rc); rc += 4;
            if (typeLen > recordTable.Length - rc - 20) return;
            rc += (int)typeLen;
            var dataOffset = (long)ReadU32(recordTable, rc); rc += 4;
            var compressedSize = ReadU32(recordTable, rc); rc += 4;
            var uncompressedSize = ReadU32(recordTable, rc); rc += 4;
            rc += 8; // timestamp
            if (nameId >= stringCount) continue;
            var (nameOffset, nameLength) = stringSlices[nameId];
            if (nameLength < 12) continue;
            var rawName = stringTable.AsSpan(nameOffset, nameLength);
            if (!ContainsAsciiIgnoreCase(rawName, "riftgate"u8) &&
                !ContainsAsciiIgnoreCase(rawName, "devotionshrine"u8) &&
                !ContainsAsciiIgnoreCase(rawName, "oneshotchests"u8))
                continue;
            var recordPath = Encoding.Latin1.GetString(rawName).Replace('\\', '/').TrimStart('/');
            var category = Classify(recordPath);
            if (category is not ("Riftgate" or "Devotion Shrine" or "One-Shot Chest")) continue;
            if (category == "Riftgate" && tags.Count == 0) continue;
            if (uncompressedSize > 4 * 1024 * 1024 || compressedSize > stream.Length) continue;
            var fileOffset = 24L + dataOffset;
            if (fileOffset < 24 || fileOffset > stream.Length - compressedSize) continue;
            var compressed = ReadBytes(stream, fileOffset, (int)compressedSize);
            var raw = compressedSize == uncompressedSize
                ? compressed
                : ArcReader.DecompressBlock(compressed, (int)uncompressedSize);
            string? description = null, actorName = null, teleportTag = null, zoneTag = null, onOpen = null;
            List<string>? chestRefs = null;
            byte disabledMask = 0;
            var hideMapIcon = false;
            var fc = 0;
            while (fc <= raw.Length - 8)
            {
                var dataType = ReadU16(raw, fc);
                var valueCount = ReadU16(raw, fc + 2);
                var keyId = ReadU32(raw, fc + 4);
                fc += 8;
                var valueBytes = (long)valueCount * 4;
                if (valueBytes > raw.Length - fc) break;
                if (keyId < stringCount && valueCount >= 1)
                {
                    var key = GetString(keyId);
                    if (category == "Riftgate" && dataType == 2)
                    {
                        var valId = ReadU32(raw, fc);
                        if (valId < stringCount)
                        {
                            if (key.Equals("description", StringComparison.OrdinalIgnoreCase)) description = GetString(valId);
                            else if (key.Equals("ActorName", StringComparison.OrdinalIgnoreCase)) actorName = GetString(valId);
                            else if (key.Equals("TeleportNameTag", StringComparison.OrdinalIgnoreCase)) teleportTag = GetString(valId);
                            else if (key.Equals("ZoneNameTag", StringComparison.OrdinalIgnoreCase)) zoneTag = GetString(valId);
                        }
                    }
                    else if (category == "Devotion Shrine" && dataType is 0 or 3 && ReadU32(raw, fc) != 0)
                    {
                        if (key.Equals("normalDisabled", StringComparison.OrdinalIgnoreCase)) disabledMask |= 1;
                        else if (key.Equals("epicDisabled", StringComparison.OrdinalIgnoreCase)) disabledMask |= 2;
                        else if (key.Equals("legendaryDisabled", StringComparison.OrdinalIgnoreCase)) disabledMask |= 4;
                        else if (key.Equals("hideMapIcon", StringComparison.OrdinalIgnoreCase)) hideMapIcon = true;
                    }
                    else if (category == "One-Shot Chest" && dataType == 2)
                    {
                        if (key.Equals("onOpen", StringComparison.OrdinalIgnoreCase))
                        {
                            var valId = ReadU32(raw, fc);
                            if (valId < stringCount)
                            {
                                var val = GetString(valId);
                                if (!string.IsNullOrWhiteSpace(val)) onOpen = val;
                            }
                        }
                        else if (!key.Equals("templateName", StringComparison.OrdinalIgnoreCase))
                        {
                            for (var v = 0; v < valueCount; v++)
                            {
                                var valId = ReadU32(raw, fc + v * 4);
                                if (valId >= stringCount) continue;
                                var (vOff, vLen) = stringSlices[valId];
                                if (vLen < 42 || !ContainsAsciiIgnoreCase(stringTable.AsSpan(vOff, vLen), "oneshotchests"u8))
                                    continue;
                                var candidate = GetString(valId).Replace('\\', '/').TrimStart('/');
                                if (candidate.EndsWith(".dbr", StringComparison.OrdinalIgnoreCase) &&
                                    candidate.StartsWith("records/items/lootchests/oneshotchests/", StringComparison.OrdinalIgnoreCase))
                                {
                                    chestRefs ??= [];
                                    chestRefs.Add(candidate);
                                }
                            }
                        }
                    }
                }
                fc += (int)valueBytes;
            }
            if (category == "Riftgate")
            {
                foreach (var tag in new[] { description, actorName, teleportTag, zoneTag })
                {
                    if (string.IsNullOrWhiteSpace(tag) || !tags.TryGetValue(tag, out var localized)) continue;
                    var cleaned = Regex.Replace(localized, @"\{\^[^}]*\}", "").Trim();
                    if (cleaned.Length > 0)
                    {
                        riftgateNames[recordPath] = cleaned;
                        break;
                    }
                }
            }
            else if (category == "Devotion Shrine")
            {
                shrines[recordPath] = new ShrineMetadata(disabledMask, hideMapIcon);
            }
            else
            {
                chestEntries[recordPath] = new OneShotChestArzEntry(onOpen, chestRefs);
            }
        }
    }

    private static Dictionary<string, string[]> ResolveOneShotChestTokens(
        IReadOnlyDictionary<string, OneShotChestArzEntry> chestEntries,
        IReadOnlyList<string> luaScripts)
    {
        var result = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        if (chestEntries.Count == 0 || luaScripts.Count == 0) return result;

        var functions = new Dictionary<string, string>(StringComparer.Ordinal);
        var questEvents = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var script in luaScripts)
        {
            if (!script.Contains("GiveToken", StringComparison.Ordinal) &&
                !script.Contains("QuestGlobalEvent", StringComparison.Ordinal) &&
                !script.Contains("clientQuestTable", StringComparison.Ordinal))
                continue;
            var cleaned = Regex.Replace(script, @"/\*[\s\S]*?\*/|--\[\[[\s\S]*?\]\]|--[^\r\n]*", "");
            var definitions = Regex.Matches(cleaned, @"\bfunction\s+([\w.]+)\s*\([^)]*\)");
            for (var i = 0; i < definitions.Count; i++)
            {
                var start = definitions[i].Index + definitions[i].Length;
                var end = i + 1 < definitions.Count ? definitions[i + 1].Index : cleaned.Length;
                functions[definitions[i].Groups[1].Value] = cleaned[start..end];
            }
            foreach (Match dispatch in Regex.Matches(cleaned, @"\b(\w+)\s*=\s*(gd\.[\w.]+)"))
                questEvents[dispatch.Groups[1].Value] = dispatch.Groups[2].Value;
        }

        void CollectOnOpen(string recordPath, HashSet<string> visitedRecords, List<string> onOpens, int depth)
        {
            if (depth > 6 || !visitedRecords.Add(recordPath) || !chestEntries.TryGetValue(recordPath, out var entry)) return;
            if (!string.IsNullOrWhiteSpace(entry.OnOpen)) onOpens.Add(entry.OnOpen);
            if (entry.References is null) return;
            foreach (var reference in entry.References)
                CollectOnOpen(reference, visitedRecords, onOpens, depth + 1);
        }

        void ExtractTokens(string functionName, HashSet<string> visitedFunctions, HashSet<string> tokens, int depth)
        {
            if (depth > 2 || !visitedFunctions.Add(functionName) || !functions.TryGetValue(functionName, out var body)) return;
            foreach (Match tokenCall in Regex.Matches(body,
                         @"\b(?:GiveTokenToLocalPlayer|GiveTokenIfPlayer|GiveToken)\s*\([^\r\n]*?[""']([A-Za-z0-9_.:-]+)[""']"))
                tokens.Add(tokenCall.Groups[1].Value);
            foreach (Match globalEvent in Regex.Matches(body, @"\bQuestGlobalEvent\s*\(\s*[""'](\w+)[""']"))
                if (questEvents.TryGetValue(globalEvent.Groups[1].Value, out var targetFunction))
                    ExtractTokens(targetFunction, visitedFunctions, tokens, depth + 1);
        }

        foreach (var recordPath in chestEntries.Keys)
        {
            var onOpens = new List<string>();
            CollectOnOpen(recordPath, new HashSet<string>(StringComparer.OrdinalIgnoreCase), onOpens, 0);
            if (onOpens.Count == 0) continue;
            var tokens = new HashSet<string>(StringComparer.Ordinal);
            var visitedFunctions = new HashSet<string>(StringComparer.Ordinal);
            foreach (var onOpen in onOpens)
                ExtractTokens(onOpen, visitedFunctions, tokens, 0);
            if (tokens.Count > 0)
                result[recordPath] = tokens.OrderBy(token => token, StringComparer.Ordinal).ToArray();
        }
        return result;
    }

    private static bool ContainsAsciiIgnoreCase(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needleLower)
    {
        if (haystack.Length < needleLower.Length) return false;
        var limit = haystack.Length - needleLower.Length;
        var first = needleLower[0];
        for (var i = 0; i <= limit; i++)
        {
            if ((haystack[i] | 0x20) != first) continue;
            var match = true;
            for (var j = 1; j < needleLower.Length; j++)
            {
                if ((haystack[i + j] | 0x20) != needleLower[j]) { match = false; break; }
            }
            if (match) return true;
        }
        return false;
    }

    private static ushort ReadU16(ReadOnlySpan<byte> data, int offset)
    {
        if (offset < 0 || offset + 2 > data.Length) throw new InvalidDataException("Compiled game data is truncated.");
        return BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset, 2));
    }

    private static uint ReadU32(ReadOnlySpan<byte> data, int offset)
    {
        if (offset < 0 || offset + 4 > data.Length) throw new InvalidDataException("Compiled map data is truncated.");
        return BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4));
    }

    private static float ReadF32(ReadOnlySpan<byte> data, int offset) => BitConverter.UInt32BitsToSingle(ReadU32(data, offset));

    private static void ReadSpan(Stream stream, long offset, Span<byte> destination)
    {
        if (offset < 0 || offset > stream.Length - destination.Length)
            throw new InvalidDataException("Compiled map data is truncated.");
        stream.Position = offset;
        stream.ReadExactly(destination);
    }

    private static byte[] ReadBytes(Stream stream, long offset, int length)
    {
        if (offset < 0 || length < 0 || offset > stream.Length - length)
            throw new InvalidDataException("Compiled map data is truncated.");
        var result = new byte[length];
        stream.Position = offset;
        stream.ReadExactly(result);
        return result;
    }

    internal sealed record ShrineMetadata(byte DisabledDifficultiesMask, bool HideMapIcon);
    private sealed record OneShotChestArzEntry(string? OnOpen, List<string>? References);
    private sealed record ArzMarkerMetadata(
        Dictionary<string, string> RiftgateNames,
        Dictionary<string, ShrineMetadata> Shrines,
        Dictionary<string, string[]> ChestOpenTokens);
    private sealed record LevelEntry(string Name, long DataOffset, long DataLength, float OriginX, float OriginY, float OriginZ);
}
