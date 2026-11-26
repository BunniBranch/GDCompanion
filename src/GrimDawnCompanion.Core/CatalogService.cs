using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GrimDawnCompanion.Core;

public sealed partial class CatalogService
{
    public const int CurrentSchema = 6;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _cacheDirectory;

    private static readonly Dictionary<string, int> StatIndexByField = AffixLabels.StatLabels
        .Select((stat, index) => (stat.Field, index))
        .ToDictionary(x => x.Field, x => x.index, StringComparer.Ordinal);

    private static readonly string[] ExcludedSubstrings =
    [
        "/lootaffixes/", "/loottables/", "/lootchests/", "/dropfx/", "/itemskills/", "/controllers/",
        "/merchants/", "/randomizers/", "/transmuters/", "/dynamictables/", "/ui/", "/lootsets/",
        "/skills/", "records/skills/", "records/fx/", "/proxies/", "/loottable", "/lorechest"
    ];

    public CatalogService(string? cacheDirectory = null, string? workDirectory = null)
    {
        _cacheDirectory = cacheDirectory ?? CompanionDataPaths.DirectoryPath;
        _ = workDirectory;
    }

    public string CachePath => Path.Combine(_cacheDirectory, "catalog.json");

    public async Task<CatalogDocument?> LoadAsync(string? bundledPath = null, CancellationToken cancellationToken = default)
    {
        foreach (var path in new[] { CachePath, bundledPath })
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) continue;
            try
            {
                await using var stream = File.OpenRead(path);
                var result = await JsonSerializer.DeserializeAsync<CatalogDocument>(stream, JsonOptions, cancellationToken);
                if (result?.SchemaVersion == CurrentSchema && result.Items.Count > 0)
                {
                    InternalGearLabels.ApplyTo(result);
                    return result;
                }
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested) { }
        }
        return null;
    }

    public static async Task<string> ComputeSourceFingerprintAsync(GameInstallation game, CancellationToken cancellationToken = default)
    {
        var archives = GameLocator.GetInstalledArchives(game);
        var sources = new CatalogSource[archives.Count];
        await Task.Run(() => Parallel.For(0, archives.Count, new ParallelOptions { CancellationToken = cancellationToken }, i =>
        {
            var archive = archives[i];
            var databaseHash = Hashing.Sha256File(archive.DatabasePath);
            string? textHash = null;
            if (archive.TextPath is { } text && File.Exists(text) && new FileInfo(text).Length > 2048)
                textHash = Hashing.Sha256File(text);
            sources[i] = new(archive.Id, archive.DisplayName, databaseHash, textHash);
        }), cancellationToken);
        return SourceFingerprint(sources);
    }

    public async Task<CatalogDocument> RebuildAsync(
        GameInstallation game,
        string? outputPath = null,
        IProgress<CatalogProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var archives = GameLocator.GetInstalledArchives(game);
        if (archives.Count == 0)
            throw new FileNotFoundException("No installed Grim Dawn databases were found.", Path.Combine(game.RootDirectory, "database", "database.arz"));

        progress?.Report(new("Reading game databases", 0, archives.Count, $"Loading {archives.Count} installed archives"));

        var loaded = new LoadedArchive[archives.Count];
        var loadedCount = 0;
        await Task.Run(() => Parallel.For(0, archives.Count, new ParallelOptions { CancellationToken = cancellationToken }, i =>
        {
            var archive = archives[i];
            var rawBytes = File.ReadAllBytes(archive.DatabasePath);
            var databaseHash = Convert.ToHexString(SHA256.HashData(rawBytes));

            string? textHash = null;
            Dictionary<string, string>? archiveTags = null;
            if (archive.TextPath is { } textPath && File.Exists(textPath) && new FileInfo(textPath).Length > 2048)
            {
                textHash = Hashing.Sha256File(textPath);
                archiveTags = ArcReader.ReadTags(textPath);
            }

            var reader = ArzReader.Parse(rawBytes, archive.DisplayName);
            var keyMetas = ClassifyStringTable(reader);
            loaded[i] = new LoadedArchive(archive, databaseHash, textHash, archiveTags, reader, keyMetas);

            var done = Interlocked.Increment(ref loadedCount);
            progress?.Report(new("Reading game databases", done, archives.Count, archive.DisplayName));
        }), cancellationToken);

        var sources = new List<CatalogSource>(loaded.Length);
        var tags = new Dictionary<string, string>(65536, StringComparer.OrdinalIgnoreCase);
        var totalRecords = 0;
        for (var i = 0; i < loaded.Length; i++)
        {
            var item = loaded[i];
            sources.Add(new(item.Archive.Id, item.Archive.DisplayName, item.DatabaseSha256, item.TextSha256));
            if (item.Tags is not null)
            {
                foreach (var pair in item.Tags)
                    tags[pair.Key] = pair.Value;
            }
            totalRecords += item.Reader.Records.Length;
        }

        if (totalRecords == 0)
            throw new InvalidOperationException("Installed Grim Dawn databases contained no DBR records.");

        progress?.Report(new("Indexing items", 0, totalRecords, $"Scanning {totalRecords:N0} database records"));

        const int ChunkSize = 256;
        var workItems = new List<(int ArchiveIndex, int Start, int End)>(totalRecords / ChunkSize + loaded.Length);
        for (var a = 0; a < loaded.Length; a++)
        {
            var count = loaded[a].Reader.Records.Length;
            for (var start = 0; start < count; start += ChunkSize)
                workItems.Add((a, start, Math.Min(start + ChunkSize, count)));
        }

        var perArchiveOutputs = new ConcurrentBag<ArchiveParseOutput>[loaded.Length];
        for (var i = 0; i < loaded.Length; i++) perArchiveOutputs[i] = [];

        var processedRecords = 0;
        await Task.Run(() => Parallel.ForEach(
            workItems,
            new ParallelOptions { CancellationToken = cancellationToken },
            () => (LocalOutputs: new ArchiveParseOutput?[loaded.Length], Scratch: new byte[65536]),
            (work, _, state) =>
            {
                var archive = loaded[work.ArchiveIndex];
                var output = state.LocalOutputs[work.ArchiveIndex];
                if (output is null)
                {
                    output = new ArchiveParseOutput();
                    state.LocalOutputs[work.ArchiveIndex] = output;
                }

                for (var r = work.Start; r < work.End; r++)
                    ProcessRecord(archive, r, tags, ref state.Scratch, output);

                var current = Interlocked.Add(ref processedRecords, work.End - work.Start);
                if (current == totalRecords || (current / 2048) != ((current - (work.End - work.Start)) / 2048))
                    progress?.Report(new("Indexing items", current, totalRecords, archive.Archive.DisplayName));
                return state;
            },
            state =>
            {
                for (var i = 0; i < loaded.Length; i++)
                {
                    if (state.LocalOutputs[i] is { } local)
                        perArchiveOutputs[i].Add(local);
                }
            }), cancellationToken);

        var records = new Dictionary<string, ItemRecord>(16384, StringComparer.OrdinalIgnoreCase);
        var affixRecords = new Dictionary<string, RawAffixRecord>(16384, StringComparer.OrdinalIgnoreCase);
        var skillNames = new Dictionary<string, string>(8192, StringComparer.OrdinalIgnoreCase);
        var questAssociations = new Dictionary<string, HashSet<string>>(2048, StringComparer.OrdinalIgnoreCase);
        var navigationRecords = new Dictionary<string, QuestNavigationRecord>(8192, StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < loaded.Length; i++)
        {
            foreach (var part in perArchiveOutputs[i])
            {
                foreach (var item in part.Items) records[item.RecordPath] = item;
                foreach (var affix in part.Affixes) affixRecords[affix.RecordPath] = affix;
                foreach (var skill in part.SkillNames) skillNames[skill.Key] = skill.Value;
                foreach (var nav in part.NavigationRecords) navigationRecords[nav.RecordPath] = nav;
                foreach (var (recordPath, questPath) in part.QuestAssociations)
                {
                    if (!questAssociations.TryGetValue(recordPath, out var paths))
                        questAssociations[recordPath] = paths = new(StringComparer.OrdinalIgnoreCase);
                    paths.Add(questPath);
                }
            }
        }

        var ordered = records.Values
            .OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(x => x.RecordPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (ordered.Count < 1_000)
            throw new InvalidOperationException($"Catalog validation failed: only {ordered.Count:N0} items were identified.");

        var document = new CatalogDocument
        {
            SourceFingerprint = SourceFingerprint(sources),
            GeneratedAt = DateTimeOffset.UtcNow,
            Sources = sources,
            Items = ordered,
            AffixRecords = affixRecords.Values
                .Select(r => ResolveAffix(r, skillNames))
                .OrderBy(r => r.RecordPath, StringComparer.Ordinal)
                .ToList(),
            QuestNavigationRecords = navigationRecords,
            QuestRecordAssociations = questAssociations.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray(),
                StringComparer.OrdinalIgnoreCase),
        };

        var destination = outputPath ?? CachePath;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        var temporary = destination + ".new";
        await using (var stream = File.Create(temporary))
            await JsonSerializer.SerializeAsync(stream, document, JsonOptions, cancellationToken);
        File.Move(temporary, destination, true);
        progress?.Report(new("Catalog ready", ordered.Count, ordered.Count, $"{ordered.Count:N0} searchable items"));
        return document;
    }

    private static KeyMeta[] ClassifyStringTable(ArzReader reader)
    {
        var metas = new KeyMeta[reader.StringCount];
        for (uint i = 0; i < reader.StringCount; i++)
            metas[i] = ClassifyKey(reader.GetStringBytes(i));
        return metas;
    }

    private static KeyMeta ClassifyKey(ReadOnlySpan<byte> keyBytes)
    {
        if (keyBytes.Length == 0 || keyBytes.Length > 64) return default;
        if (keyBytes.IndexOfAny("/\\. "u8) >= 0) return default;

        Span<char> charBuf = stackalloc char[keyBytes.Length];
        for (var i = 0; i < keyBytes.Length; i++) charBuf[i] = (char)keyBytes[i];
        ReadOnlySpan<char> chars = charBuf;

        if (chars.Equals("Class", StringComparison.Ordinal)) return new(KeyKind.Class, 0, 0);
        if (chars.Equals("itemNameTag", StringComparison.Ordinal)) return new(KeyKind.ItemNameTag, 0, 0);
        if (chars.Equals("description", StringComparison.OrdinalIgnoreCase))
            return new(KeyKind.Description, (byte)(chars.Equals("description", StringComparison.Ordinal) ? 1 : 0), 0);
        if (chars.Equals("AreaDescription", StringComparison.OrdinalIgnoreCase)) return new(KeyKind.AreaDescription, 0, 0);
        if (chars.Equals("itemClassification", StringComparison.Ordinal)) return new(KeyKind.ItemClassification, 0, 0);
        if (chars.Equals("itemLevel", StringComparison.Ordinal)) return new(KeyKind.ItemLevel, 0, 0);
        if (chars.Equals("templateName", StringComparison.OrdinalIgnoreCase))
            return new(KeyKind.TemplateName, (byte)(chars.Equals("templateName", StringComparison.Ordinal) ? 1 : 0), 0);
        if (chars.Equals("skillDisplayName", StringComparison.Ordinal)) return new(KeyKind.SkillDisplayName, 0, 0);
        if (chars.Equals("buffSkillName", StringComparison.Ordinal)) return new(KeyKind.BuffSkillName, 0, 0);
        if (chars.Equals("onAddToWorld", StringComparison.OrdinalIgnoreCase)) return new(KeyKind.OnAddToWorld, 0, 0);
        if (chars.Equals("lootRandomizerName", StringComparison.Ordinal)) return new(KeyKind.LootRandomizerName, 0, 0);
        if (chars.Equals("levelRequirement", StringComparison.Ordinal)) return new(KeyKind.LevelRequirement, 0, 0);
        if (chars.Equals("itemSkillName", StringComparison.Ordinal) || chars.Equals("itemSkillAutoController", StringComparison.Ordinal))
            return new(KeyKind.ItemSkillName, 0, 0);

        if (chars.StartsWith("questFile", StringComparison.OrdinalIgnoreCase))
            return new(KeyKind.QuestFile, 0, 0);
        if (chars.StartsWith("taskUID", StringComparison.OrdinalIgnoreCase))
            return new(KeyKind.TaskUid, 0, 0);

        if (TryMatchAffixKey(chars, out var isWeight, out var group, out var index))
            return new(isWeight ? KeyKind.AffixWeight : KeyKind.AffixLink, group, index);

        if (chars.StartsWith("augmentSkillName", StringComparison.Ordinal) && TryParseDigits(chars[16..], out var augIdx))
            return new(KeyKind.AugmentSkillName, 0, augIdx);
        if (chars.StartsWith("augmentSkillLevel", StringComparison.Ordinal) && TryParseDigits(chars[17..], out var augLvlIdx))
            return new(KeyKind.AugmentSkillLevel, 0, augLvlIdx);

        if ((chars.StartsWith("modifiedSkillName", StringComparison.Ordinal) && AllDigits(chars[17..])) ||
            (chars.StartsWith("skillModifierName", StringComparison.Ordinal) && AllDigits(chars[17..])))
            return new(KeyKind.ModifiedSkillName, 0, 0);

        if (IsPoolChild(chars)) return new(KeyKind.PoolChild, 0, 0);
        if (IsPoolNameChild(chars)) return new(KeyKind.PoolNameChild, 0, 0);

        if (chars[0] is 'c' or 'd' or 'o')
        {
            if (StatIndexByField.TryGetValue(new string(chars), out var statIndex))
                return new(KeyKind.StatBonus, 0, (ushort)statIndex);
        }

        return default;
    }

    private static void ProcessRecord(
        LoadedArchive archive,
        int recordIndex,
        IReadOnlyDictionary<string, string> tags,
        ref byte[] scratchBuffer,
        ArchiveParseOutput output)
    {
        var reader = archive.Reader;
        ref readonly var header = ref reader.Records[recordIndex];
        if (header.NameId >= reader.StringCount) return;

        var relativePath = NormalizePath(reader.GetStringBytes(header.NameId));
        var checkItem = IsItemRecordLocation(relativePath) && !IsExcluded(relativePath);
        var rawData = reader.ReadRecordData(in header, ref scratchBuffer);

        string className = "", nameTag = "", descriptionTag = "", areaDescriptionTag = "", classification = "", template = "";
        string? descriptionExact = null;
        string? templateExact = null;
        var itemLevel = 0;

        string? skillResolved = null;
        string? skillChild = null;
        string? onAddToWorld = null;
        string? lootRandomizerNameTag = null;
        var levelRequirement = 0;

        List<(string Suffix, string Value)>? questFiles = null;
        Dictionary<string, uint>? taskUids = null;
        List<string>? poolChildren = null;
        List<string>? poolNameChildren = null;

        List<(string Key, byte Group, ushort Index, string Value)>? affixLinks = null;
        Dictionary<int, double>? affixWeights = null;
        List<(string Field, int Index, string Path)>? augmentSkillNames = null;
        Dictionary<int, double>? augmentSkillLevels = null;
        List<(int StatIndex, double Value)>? statBonuses = null;
        List<(bool IsGrants, string Path)>? skillRefs = null;

        var keyMetas = archive.KeyMetas;
        var stringCount = reader.StringCount;

        var fc = 0;
        var limit = rawData.Length - 8;
        while (fc <= limit)
        {
            var dataType = BinaryPrimitives.ReadUInt16LittleEndian(rawData.Slice(fc, 2));
            var valueCount = BinaryPrimitives.ReadUInt16LittleEndian(rawData.Slice(fc + 2, 2));
            var keyId = BinaryPrimitives.ReadUInt32LittleEndian(rawData.Slice(fc + 4, 4));
            fc += 8;
            var valueBytes = valueCount * 4;
            if (fc + valueBytes > rawData.Length) break;

            if (keyId < stringCount && valueCount > 0)
            {
                var meta = keyMetas[keyId];
                if (meta.Kind != KeyKind.None)
                {
                    switch (meta.Kind)
                    {
                        case KeyKind.Class:
                            if (dataType == 2) className = ReadStringValue(reader, rawData, fc, valueCount);
                            break;
                        case KeyKind.ItemNameTag:
                            if (dataType == 2) nameTag = ReadStringValue(reader, rawData, fc, valueCount);
                            break;
                        case KeyKind.Description:
                            if (dataType == 2)
                            {
                                var value = ReadStringValue(reader, rawData, fc, valueCount);
                                descriptionTag = value;
                                if (meta.Group == 1) descriptionExact = value;
                            }
                            break;
                        case KeyKind.AreaDescription:
                            if (dataType == 2) areaDescriptionTag = ReadStringValue(reader, rawData, fc, valueCount);
                            break;
                        case KeyKind.ItemClassification:
                            if (dataType == 2) classification = ReadStringValue(reader, rawData, fc, valueCount);
                            break;
                        case KeyKind.ItemLevel:
                            itemLevel = ReadIntValue(rawData, fc, dataType);
                            break;
                        case KeyKind.TemplateName:
                            if (dataType == 2)
                            {
                                var value = ReadStringValue(reader, rawData, fc, valueCount);
                                template = value;
                                if (meta.Group == 1) templateExact = value;
                            }
                            break;
                        case KeyKind.SkillDisplayName:
                            if (dataType == 2)
                            {
                                var tag = ReadStringValue(reader, rawData, fc, valueCount).Trim();
                                skillResolved = tags.TryGetValue(tag, out var name) ? AffixLabels.Clean(name) : "";
                            }
                            break;
                        case KeyKind.BuffSkillName:
                            if (dataType == 2)
                                skillChild = AffixLabels.Normalize(ReadStringValue(reader, rawData, fc, valueCount).Trim());
                            break;
                        case KeyKind.QuestFile:
                            if (dataType == 2)
                            {
                                var value = ReadStringValue(reader, rawData, fc, valueCount)
                                    .Replace('\\', '/').Trim().TrimStart('/').ToLowerInvariant();
                                if (value.EndsWith(".qst", StringComparison.Ordinal))
                                {
                                    output.QuestAssociations.Add((relativePath, value));
                                    questFiles ??= [];
                                    questFiles.Add((reader.GetString(keyId)[9..], value));
                                }
                            }
                            break;
                        case KeyKind.TaskUid:
                            {
                                long uid = dataType switch
                                {
                                    0 or 3 => BinaryPrimitives.ReadInt32LittleEndian(rawData.Slice(fc, 4)),
                                    1 => (long)BitConverter.UInt32BitsToSingle(BinaryPrimitives.ReadUInt32LittleEndian(rawData.Slice(fc, 4))),
                                    2 => long.TryParse(ReadStringValue(reader, rawData, fc, valueCount).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0,
                                    _ => 0
                                };
                                if (uid != 0)
                                {
                                    taskUids ??= new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
                                    taskUids[reader.GetString(keyId)[7..]] = unchecked((uint)uid);
                                }
                            }
                            break;
                        case KeyKind.PoolChild:
                            if (dataType == 2)
                            {
                                var value = ReadStringValue(reader, rawData, fc, valueCount).Trim();
                                if (value.EndsWith(".dbr", StringComparison.OrdinalIgnoreCase))
                                {
                                    poolChildren ??= [];
                                    poolChildren.Add(QuestNavigationIndex.Normalize(value));
                                }
                            }
                            break;
                        case KeyKind.PoolNameChild:
                            if (dataType == 2)
                            {
                                var value = ReadStringValue(reader, rawData, fc, valueCount).Trim();
                                if (value.EndsWith(".dbr", StringComparison.OrdinalIgnoreCase))
                                {
                                    poolNameChildren ??= [];
                                    poolNameChildren.Add(QuestNavigationIndex.Normalize(value));
                                }
                            }
                            break;
                        case KeyKind.OnAddToWorld:
                            if (dataType == 2)
                                onAddToWorld = ReadStringValue(reader, rawData, fc, valueCount).Trim();
                            break;
                        case KeyKind.LootRandomizerName:
                            if (dataType == 2)
                                lootRandomizerNameTag = ReadStringValue(reader, rawData, fc, valueCount).Trim();
                            break;
                        case KeyKind.LevelRequirement:
                            levelRequirement = ReadIntValue(rawData, fc, dataType);
                            break;
                        case KeyKind.AffixLink:
                            if (dataType == 2)
                            {
                                var value = ReadStringValue(reader, rawData, fc, valueCount).Trim();
                                if (value.EndsWith(".dbr", StringComparison.OrdinalIgnoreCase))
                                {
                                    affixLinks ??= [];
                                    affixLinks.Add((reader.GetString(keyId), meta.Group, meta.Index, value.Replace('\\', '/').ToLowerInvariant()));
                                }
                            }
                            break;
                        case KeyKind.AffixWeight:
                            {
                                var weight = ReadDoubleValue(rawData, fc, dataType);
                                if (weight > 0)
                                {
                                    affixWeights ??= [];
                                    affixWeights[(meta.Group << 16) | meta.Index] = weight;
                                }
                            }
                            break;
                        case KeyKind.AugmentSkillName:
                            if (dataType == 2)
                            {
                                augmentSkillNames ??= [];
                                augmentSkillNames.Add((reader.GetString(keyId), meta.Index, ReadStringValue(reader, rawData, fc, valueCount).Trim()));
                            }
                            break;
                        case KeyKind.AugmentSkillLevel:
                            {
                                var level = ReadDoubleValue(rawData, fc, dataType);
                                if (double.IsFinite(level) && level != 0)
                                {
                                    augmentSkillLevels ??= [];
                                    augmentSkillLevels[meta.Index] = level;
                                }
                            }
                            break;
                        case KeyKind.ItemSkillName:
                            if (dataType == 2)
                            {
                                var value = ReadStringValue(reader, rawData, fc, valueCount).Trim();
                                if (value.EndsWith(".dbr", StringComparison.OrdinalIgnoreCase))
                                {
                                    skillRefs ??= [];
                                    skillRefs.Add((true, value));
                                }
                            }
                            break;
                        case KeyKind.ModifiedSkillName:
                            if (dataType == 2)
                            {
                                var value = ReadStringValue(reader, rawData, fc, valueCount).Trim();
                                if (value.EndsWith(".dbr", StringComparison.OrdinalIgnoreCase))
                                {
                                    skillRefs ??= [];
                                    skillRefs.Add((false, value));
                                }
                            }
                            break;
                        case KeyKind.StatBonus:
                            {
                                var value = ReadDoubleValue(rawData, fc, dataType);
                                if (double.IsFinite(value) && value != 0)
                                {
                                    statBonuses ??= [];
                                    statBonuses.Add((meta.Index, value));
                                }
                            }
                            break;
                    }
                }
            }
            fc += valueBytes;
        }

        if (checkItem)
        {
            var tmpl = templateExact ?? "";
            if (relativePath.StartsWith("records/items/", StringComparison.Ordinal) || LooksSpawnable(className, tmpl))
            {
                var desc = descriptionExact ?? "";
                var displayTag = !string.IsNullOrWhiteSpace(nameTag) ? nameTag : desc;
                var hasName = tags.TryGetValue(displayTag, out var localized) && !string.IsNullOrWhiteSpace(CleanMarkup(localized));
                var name = hasName ? CleanMarkup(localized!) : Humanize(Path.GetFileNameWithoutExtension(relativePath));
                if (string.IsNullOrWhiteSpace(name)) name = relativePath;
                output.Items.Add(InternalGearLabels.Apply(
                    new(name, relativePath, archive.Archive.DisplayName, CategoryFor(relativePath, className),
                        classification, itemLevel, displayTag, className)
                    { NameUnavailable = !hasName }));
            }
        }

        if (!string.IsNullOrWhiteSpace(skillResolved))
            output.SkillNames.Add(new(relativePath, skillResolved));
        else if (skillChild?.EndsWith(".dbr", StringComparison.Ordinal) == true)
            output.SkillNames.Add(new(relativePath, skillChild));
        else if (skillResolved is not null)
            output.SkillNames.Add(new(relativePath, ""));

        if (className == "LootRandomizer" || className == "LootRandomizerTable" || className.StartsWith("LootItemTable", StringComparison.Ordinal))
        {
            var links = new Dictionary<string, string>(StringComparer.Ordinal);
            if (affixLinks is not null && affixWeights is not null)
            {
                foreach (var (key, group, idx, val) in affixLinks)
                {
                    if (affixWeights.ContainsKey((group << 16) | idx))
                        links[key] = val;
                }
            }
            var rawName = lootRandomizerNameTag is not null && tags.TryGetValue(lootRandomizerNameTag, out var localizedAffix) ? localizedAffix : "";
            rawName = MarkupRegex().Replace(rawName, "");
            output.Affixes.Add(new RawAffixRecord
            {
                RecordPath = relativePath,
                Class = className,
                Name = rawName,
                Level = levelRequirement,
                Classification = classification,
                Links = links,
                AugmentSkillNames = className == "LootRandomizer" ? augmentSkillNames : null,
                AugmentSkillLevels = className == "LootRandomizer" ? augmentSkillLevels : null,
                StatBonuses = className == "LootRandomizer" ? statBonuses : null,
                SkillRefs = className == "LootRandomizer" ? skillRefs : null,
            });
        }

        List<QuestTargetBinding>? bindings = null;
        if (questFiles is not null && taskUids is not null)
        {
            foreach (var (suffix, questPath) in questFiles)
            {
                if (taskUids.TryGetValue(suffix, out var uid) && uid != 0)
                {
                    bindings ??= [];
                    bindings.Add(new(questPath, uid));
                }
            }
        }
        var kind = className;
        if (kind.Length == 0 && QuestNavigationIndex.Normalize(template).EndsWith("/proxypool.tpl", StringComparison.Ordinal))
            kind = "ProxyPool";
        List<string>? children = null;
        if (kind.Equals("Proxy", StringComparison.OrdinalIgnoreCase)) children = poolChildren;
        else if (kind.Equals("ProxyPool", StringComparison.OrdinalIgnoreCase)) children = poolNameChildren;
        var onAdd = kind.Equals("ScriptEntity", StringComparison.OrdinalIgnoreCase) ? onAddToWorld : null;
        if (bindings is { Count: > 0 } || children is { Count: > 0 } || !string.IsNullOrWhiteSpace(onAdd))
        {
            var navTag = descriptionTag.Length > 0 ? descriptionTag : areaDescriptionTag;
            output.NavigationRecords.Add(new QuestNavigationRecord(
                relativePath,
                tags.GetValueOrDefault(navTag),
                bindings is not null ? bindings.Distinct().ToArray() : [],
                children is not null ? children.Distinct(StringComparer.OrdinalIgnoreCase).ToArray() : [],
                onAdd));
        }
    }

    private static AffixCatalogRecord ResolveAffix(RawAffixRecord record, IReadOnlyDictionary<string, string> skills)
    {
        if (record.Class != "LootRandomizer")
            return new(record.RecordPath, record.Class, record.Name, record.Level, record.Classification, record.Links);

        var effects = new List<string>();
        if (record.AugmentSkillNames is not null && record.AugmentSkillLevels is not null)
        {
            foreach (var (_, idx, path) in record.AugmentSkillNames.OrderBy(x => x.Field, StringComparer.Ordinal))
            {
                if (record.AugmentSkillLevels.TryGetValue(idx, out var n) && n != 0)
                    effects.Add($"{Signed(n)} to {Skill(path)}");
            }
        }
        if (record.StatBonuses is not null)
        {
            var statValues = new double[AffixLabels.StatLabels.Length];
            foreach (var (statIndex, value) in record.StatBonuses)
                statValues[statIndex] = value;
            for (var i = 0; i < AffixLabels.StatLabels.Length; i++)
            {
                if (statValues[i] != 0)
                    effects.Add($"{Signed(statValues[i])}{(AffixLabels.StatLabels[i].Percent ? "%" : "")} {AffixLabels.StatLabels[i].Label}");
            }
        }
        if (record.SkillRefs is not null)
        {
            foreach (var (isGrants, path) in record.SkillRefs)
                effects.Add((isGrants ? "Grants " : "Modifies ") + Skill(path));
        }

        var description = string.Join("; ", effects.Distinct());
        var name = AffixLabels.Clean(record.Name);
        if (string.IsNullOrWhiteSpace(name))
            name = effects.Count > 0 ? string.Join("; ", effects.Distinct().Take(2)) : "Special equipment bonus";
        return new(record.RecordPath, record.Class, name, record.Level, record.Classification, record.Links) { Description = description };

        string Skill(string path)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var depth = 0; depth < 12; depth++)
            {
                path = AffixLabels.Normalize(path);
                if (!seen.Add(path) || !skills.TryGetValue(path, out var resolved) || string.IsNullOrWhiteSpace(resolved)) break;
                if (!resolved.EndsWith(".dbr", StringComparison.OrdinalIgnoreCase)) return resolved;
                path = resolved;
            }
            return "an unnamed skill";
        }
    }

    private static string ReadStringValue(ArzReader reader, ReadOnlySpan<byte> rawData, int offset, int valueCount)
    {
        if (valueCount == 1)
        {
            var id = BinaryPrimitives.ReadUInt32LittleEndian(rawData.Slice(offset, 4));
            return id < reader.StringCount ? reader.GetString(id) : "";
        }
        var builder = new StringBuilder();
        for (var i = 0; i < valueCount; i++)
        {
            if (i > 0) builder.Append(';');
            var id = BinaryPrimitives.ReadUInt32LittleEndian(rawData.Slice(offset + i * 4, 4));
            if (id < reader.StringCount) builder.Append(reader.GetString(id));
        }
        return builder.ToString();
    }

    private static int ReadIntValue(ReadOnlySpan<byte> rawData, int offset, ushort dataType) => dataType switch
    {
        0 or 3 => BinaryPrimitives.ReadInt32LittleEndian(rawData.Slice(offset, 4)),
        1 => (int)BitConverter.UInt32BitsToSingle(BinaryPrimitives.ReadUInt32LittleEndian(rawData.Slice(offset, 4))),
        _ => 0
    };

    private static double ReadDoubleValue(ReadOnlySpan<byte> rawData, int offset, ushort dataType) => dataType switch
    {
        0 or 3 => BinaryPrimitives.ReadInt32LittleEndian(rawData.Slice(offset, 4)),
        1 => BitConverter.UInt32BitsToSingle(BinaryPrimitives.ReadUInt32LittleEndian(rawData.Slice(offset, 4))),
        _ => 0
    };

    private static string NormalizePath(ReadOnlySpan<byte> raw)
    {
        Span<char> buffer = raw.Length <= 512 ? stackalloc char[raw.Length] : new char[raw.Length];
        for (var i = 0; i < raw.Length; i++)
        {
            var b = raw[i];
            if (b == (byte)'\\') buffer[i] = '/';
            else if (b is >= (byte)'A' and <= (byte)'Z') buffer[i] = (char)(b + 32);
            else buffer[i] = (char)b;
        }
        return new string(buffer);
    }

    private static bool TryParseDigits(ReadOnlySpan<char> value, out ushort result)
    {
        result = 0;
        if (value.Length == 0 || value.Length > 4) return false;
        for (var i = 0; i < value.Length; i++)
            if (value[i] is < '0' or > '9') return false;
        return ushort.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out result);
    }

    private static bool AllDigits(ReadOnlySpan<char> value)
    {
        for (var i = 0; i < value.Length; i++)
            if (value[i] is < '0' or > '9') return false;
        return true;
    }

    private static bool TryMatchAffixKey(ReadOnlySpan<char> chars, out bool isWeight, out byte group, out ushort index)
    {
        isWeight = false; group = 0; index = 0;
        ReadOnlySpan<char> rest;
        if (chars.StartsWith("lootName", StringComparison.Ordinal)) { group = 0; rest = chars[8..]; }
        else if (chars.StartsWith("lootWeight", StringComparison.Ordinal)) { isWeight = true; group = 0; rest = chars[10..]; }
        else if (chars.StartsWith("randomizerName", StringComparison.Ordinal)) { group = 1; rest = chars[14..]; }
        else if (chars.StartsWith("randomizerWeight", StringComparison.Ordinal)) { isWeight = true; group = 1; rest = chars[16..]; }
        else if (chars.StartsWith("prefixTableName", StringComparison.Ordinal)) { group = 2; rest = chars[15..]; }
        else if (chars.StartsWith("prefixTableWeight", StringComparison.Ordinal)) { isWeight = true; group = 2; rest = chars[17..]; }
        else if (chars.StartsWith("suffixTableName", StringComparison.Ordinal)) { group = 3; rest = chars[15..]; }
        else if (chars.StartsWith("suffixTableWeight", StringComparison.Ordinal)) { isWeight = true; group = 3; rest = chars[17..]; }
        else if (chars.StartsWith("rarePrefixTableName", StringComparison.Ordinal)) { group = 4; rest = chars[19..]; }
        else if (chars.StartsWith("rarePrefixTableWeight", StringComparison.Ordinal)) { isWeight = true; group = 4; rest = chars[21..]; }
        else if (chars.StartsWith("rareSuffixTableName", StringComparison.Ordinal)) { group = 5; rest = chars[19..]; }
        else if (chars.StartsWith("rareSuffixTableWeight", StringComparison.Ordinal)) { isWeight = true; group = 5; rest = chars[21..]; }
        else return false;

        if (rest.Length is < 1 or > 4 || rest[0] == '0') return false;
        return TryParseDigits(rest, out index);
    }

    private static bool IsPoolChild(ReadOnlySpan<char> chars)
    {
        if (!chars.StartsWith("pool", StringComparison.OrdinalIgnoreCase)) return false;
        var rest = chars[4..];
        if (rest.StartsWith("Epic", StringComparison.OrdinalIgnoreCase)) rest = rest[4..];
        else if (rest.StartsWith("Legendary", StringComparison.OrdinalIgnoreCase)) rest = rest[9..];
        return rest.Length > 0 && AllDigits(rest);
    }

    private static bool IsPoolNameChild(ReadOnlySpan<char> chars)
    {
        if (!chars.StartsWith("name", StringComparison.OrdinalIgnoreCase)) return false;
        var rest = chars[4..];
        if (rest.StartsWith("Champion", StringComparison.OrdinalIgnoreCase)) rest = rest[8..];
        return rest.Length > 0 && AllDigits(rest);
    }

    private static bool LooksSpawnable(string className, string template)
    {
        if (className.StartsWith("Skill", StringComparison.OrdinalIgnoreCase) ||
            template.Contains("/skill_", StringComparison.OrdinalIgnoreCase)) return false;
        var value = (className + " " + template).ToLowerInvariant();
        return className.StartsWith("Item", StringComparison.OrdinalIgnoreCase) ||
               className.StartsWith("OneShot", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("weapon") || value.Contains("armor") || value.Contains("accessory") ||
               value.Contains("consumable") || value.Contains("questitem") || value.Contains("component") ||
               value.Contains("relic") || value.Contains("blueprint") || value.Contains("artifact") ||
               value.Contains("oneshot") || value.Contains("formula") || value.Contains("enchant") ||
               value.Contains("transmute") || value.Contains("itemfaction") || value.Contains("item_misc") ||
               value.Contains("item/misc") || value.Contains("item_usable");
    }

    private static bool IsItemRecordLocation(string path) =>
        path.StartsWith("records/items/", StringComparison.Ordinal) ||
        path.Contains("/items/", StringComparison.Ordinal) ||
        path.Contains("/npcgear/", StringComparison.Ordinal) ||
        path.Contains("/defaultgear/", StringComparison.Ordinal) ||
        path.StartsWith("records/sandbox/", StringComparison.Ordinal) ||
        path.StartsWith("records/endlessdungeon/", StringComparison.Ordinal) ||
        path.StartsWith("records/storyelements", StringComparison.Ordinal);

    private static bool IsExcluded(string path)
    {
        foreach (var excluded in ExcludedSubstrings)
            if (path.Contains(excluded, StringComparison.Ordinal)) return true;
        return false;
    }

    private static string CategoryFor(string path, string className)
    {
        var text = (path + " " + className).ToLowerInvariant();
        if (text.Contains("blueprint") || text.Contains("formula")) return "Blueprints";
        if (text.Contains("component")) return "Components";
        if (text.Contains("consumable") || text.Contains("potion") || text.Contains("booster")) return "Consumables";
        if (text.Contains("quest")) return "Quest Items";
        if (text.Contains("weapon") || text.Contains("sword") || text.Contains("axe") || text.Contains("mace") || text.Contains("gun") || text.Contains("shield") || text.Contains("offhand")) return "Weapons & Off-Hands";
        if (text.Contains("accessor") || text.Contains("ring") || text.Contains("amulet") || text.Contains("medal")) return "Accessories";
        if (text.Contains("armor") || text.Contains("gearhead") || text.Contains("geartorso") || text.Contains("gearfeet") || text.Contains("gearhands") || text.Contains("gearlegs") || text.Contains("shoulder") || text.Contains("waist")) return "Armor";
        return "Other Items";
    }

    private static string Humanize(string value)
    {
        value = HumanizePrefixRegex().Replace(value, "");
        value = value.Replace('_', ' ').Replace('-', ' ').Trim();
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(value.ToLowerInvariant());
    }

    private static string Signed(double value) => (value > 0 ? "+" : "") + value.ToString("0.##", CultureInfo.InvariantCulture);

    private static string SourceFingerprint(IEnumerable<CatalogSource> sources) =>
        Hashing.CombineFingerprint(sources.SelectMany(x => new[] { x.Id, x.DatabaseSha256, x.TextSha256 ?? "" }));

    private static string CleanMarkup(string value) => ColorMarkupRegex().Replace(MarkupRegex().Replace(value, ""), "").Trim();

    private enum KeyKind : byte
    {
        None = 0,
        Class,
        ItemNameTag,
        Description,
        AreaDescription,
        ItemClassification,
        ItemLevel,
        TemplateName,
        SkillDisplayName,
        BuffSkillName,
        QuestFile,
        TaskUid,
        PoolChild,
        PoolNameChild,
        OnAddToWorld,
        LootRandomizerName,
        LevelRequirement,
        AffixLink,
        AffixWeight,
        AugmentSkillName,
        AugmentSkillLevel,
        ItemSkillName,
        ModifiedSkillName,
        StatBonus,
    }

    private readonly record struct KeyMeta(KeyKind Kind, byte Group, ushort Index);

    private sealed record LoadedArchive(
        GameArchive Archive,
        string DatabaseSha256,
        string? TextSha256,
        Dictionary<string, string>? Tags,
        ArzReader Reader,
        KeyMeta[] KeyMetas);

    private sealed class ArchiveParseOutput
    {
        public List<ItemRecord> Items { get; } = [];
        public List<RawAffixRecord> Affixes { get; } = [];
        public List<KeyValuePair<string, string>> SkillNames { get; } = [];
        public List<(string RecordPath, string QuestPath)> QuestAssociations { get; } = [];
        public List<QuestNavigationRecord> NavigationRecords { get; } = [];
    }

    private sealed class RawAffixRecord
    {
        public required string RecordPath { get; init; }
        public required string Class { get; init; }
        public required string Name { get; init; }
        public required int Level { get; init; }
        public required string Classification { get; init; }
        public required Dictionary<string, string> Links { get; init; }
        public List<(string Field, int Index, string Path)>? AugmentSkillNames { get; init; }
        public Dictionary<int, double>? AugmentSkillLevels { get; init; }
        public List<(int StatIndex, double Value)>? StatBonuses { get; init; }
        public List<(bool IsGrants, string Path)>? SkillRefs { get; init; }
    }

    [GeneratedRegex("^[a-z]+[0-9]*_", RegexOptions.IgnoreCase)]
    private static partial Regex HumanizePrefixRegex();
    [GeneratedRegex("\\{[^}]+\\}")]
    private static partial Regex MarkupRegex();
    [GeneratedRegex("\\^[A-Za-z0-9]")]
    private static partial Regex ColorMarkupRegex();
}
