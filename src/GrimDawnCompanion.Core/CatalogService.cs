using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GrimDawnCompanion.Core;

public sealed partial class CatalogService
{
    public const int CurrentSchema = 6;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _cacheDirectory;
    private readonly string? _workDirectory;

    public CatalogService(string? cacheDirectory = null, string? workDirectory = null)
    {
        _cacheDirectory = cacheDirectory ?? CompanionDataPaths.DirectoryPath;
        _workDirectory = workDirectory;
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
        var sources = new List<CatalogSource>();
        foreach (var archive in GameLocator.GetInstalledArchives(game))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var databaseHash = await Task.Run(() => Hashing.Sha256File(archive.DatabasePath), cancellationToken);
            string? textHash = null;
            if (archive.TextPath is { } text && File.Exists(text) && new FileInfo(text).Length > 2048)
                textHash = await Task.Run(() => Hashing.Sha256File(text), cancellationToken);
            sources.Add(new(archive.Id, archive.DisplayName, databaseHash, textHash));
        }
        return SourceFingerprint(sources);
    }

    public async Task<CatalogDocument> RebuildAsync(
        GameInstallation game,
        string? outputPath = null,
        IProgress<CatalogProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(game.ArchiveToolPath)) throw new FileNotFoundException("Grim Dawn ArchiveTool.exe was not found.", game.ArchiveToolPath);
        var archives = GameLocator.GetInstalledArchives(game);
        if (archives.Count == 0) throw new InvalidOperationException("No installed Grim Dawn databases were found.");

        var sources = new List<CatalogSource>();
        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        progress?.Report(new("Reading localized item names", 0, archives.Count, "Opening Text_EN archives"));
        for (var i = 0; i < archives.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var archive = archives[i];
            var databaseHash = await Task.Run(() => Hashing.Sha256File(archive.DatabasePath), cancellationToken);
            string? textHash = null;
            if (archive.TextPath is { } textPath && File.Exists(textPath) && new FileInfo(textPath).Length > 2048)
            {
                textHash = await Task.Run(() => Hashing.Sha256File(textPath), cancellationToken);
                var loaded = await Task.Run(() => ArcReader.ReadTags(textPath), cancellationToken);
                foreach (var pair in loaded) tags[pair.Key] = pair.Value;
            }
            sources.Add(new(archive.Id, archive.DisplayName, databaseHash, textHash));
            progress?.Report(new("Reading localized item names", i + 1, archives.Count, archive.DisplayName));
        }

        var workRoot = GetArchiveToolSafeWorkDirectory(_workDirectory);
        Directory.CreateDirectory(workRoot);
        var records = new Dictionary<string, ItemRecord>(StringComparer.OrdinalIgnoreCase);
        var affixRecords = new Dictionary<string, AffixCatalogRecord>(StringComparer.OrdinalIgnoreCase);
        var skillNames = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        var questAssociations = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var navigationRecords = new Dictionary<string, QuestNavigationRecord>(StringComparer.OrdinalIgnoreCase);
        try
        {
            for (var archiveIndex = 0; archiveIndex < archives.Count; archiveIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var archive = archives[archiveIndex];
                var extractDirectory = Path.Combine(workRoot, "Source" + archiveIndex);
                SafeDeleteDirectory(extractDirectory, workRoot);
                Directory.CreateDirectory(extractDirectory);
                progress?.Report(new("Extracting game database", archiveIndex, archives.Count, archive.DisplayName));
                await ExtractDatabaseAsync(game, archive, extractDirectory, archiveIndex, archives.Count, progress, cancellationToken);

                var files = Directory.EnumerateFiles(extractDirectory, "*.dbr", SearchOption.AllDirectories).ToArray();
                if (files.Length == 0)
                    throw new InvalidOperationException($"ArchiveTool produced no DBR records for {archive.DisplayName}. Its output path may be incompatible.");

                var parsed = new ConcurrentBag<ItemRecord>();
                var parsedAffixes = new ConcurrentBag<AffixCatalogRecord>();
                var parsedSkillNames = new ConcurrentBag<KeyValuePair<string,string>>();
                var parsedQuestAssociations = new ConcurrentBag<(string RecordPath, string QuestPath)>();
                var parsedNavigation = new ConcurrentBag<QuestNavigationRecord>();
                var done = 0;
                await Task.Run(() => Parallel.ForEach(files, new ParallelOptions { CancellationToken = cancellationToken }, file =>
                {
                    var item = TryParseItem(file, extractDirectory, archive.DisplayName, tags);
                    if (item is not null) parsed.Add(item);
                    // ReadLines opens its reader before enumeration; parsers that
                    // reject a record early can otherwise keep extraction files locked.
                    var lines=File.ReadAllLines(file);
                    if(AffixLabels.ReadSkillName(Path.GetRelativePath(extractDirectory,file),lines,tags) is { } skillName) parsedSkillNames.Add(skillName);
                    var affix=ItemAffixIndex.ReadRecord(Path.GetRelativePath(extractDirectory,file),lines,tags);
                    if(affix is not null) parsedAffixes.Add(affix);
                    foreach (var association in ReadQuestAssociations(file, extractDirectory))
                        parsedQuestAssociations.Add(association);
                    var navigation = QuestNavigationIndex.ReadRecord(Path.GetRelativePath(extractDirectory, file), lines, tags);
                    if (navigation is not null) parsedNavigation.Add(navigation);
                    var current = Interlocked.Increment(ref done);
                    if (current % 750 == 0)
                        progress?.Report(new("Indexing items", current, files.Length, archive.DisplayName));
                }), cancellationToken);
                foreach (var item in parsed) records[item.RecordPath] = item;
                foreach(var record in parsedAffixes) affixRecords[record.RecordPath]=record;
                foreach(var pair in parsedSkillNames) skillNames[pair.Key]=pair.Value;
                foreach (var record in parsedNavigation) navigationRecords[record.RecordPath] = record;
                foreach (var (recordPath, questPath) in parsedQuestAssociations)
                {
                    if (!questAssociations.TryGetValue(recordPath, out var paths))
                        questAssociations[recordPath] = paths = new(StringComparer.OrdinalIgnoreCase);
                    paths.Add(questPath);
                }
                progress?.Report(new("Indexing items", files.Length, files.Length, $"{archive.DisplayName}: {parsed.Count:N0} candidates"));
                SafeDeleteDirectory(extractDirectory, workRoot);
            }
        }
        finally
        {
            try { SafeDeleteDirectory(workRoot, Path.GetDirectoryName(workRoot)!); } catch { }
        }

        var ordered = records.Values
            .OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(x => x.RecordPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (ordered.Count < 1_000) throw new InvalidOperationException($"Catalog validation failed: only {ordered.Count:N0} items were identified.");

        var document = new CatalogDocument
        {
            SourceFingerprint = SourceFingerprint(sources),
            GeneratedAt = DateTimeOffset.UtcNow,
            Sources = sources,
            Items = ordered,
            AffixRecords = affixRecords.Values.Select(r=>AffixLabels.Resolve(r,skillNames)).OrderBy(r=>r.RecordPath,StringComparer.Ordinal).ToList(),
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

    private static async Task ExtractDatabaseAsync(
        GameInstallation game, GameArchive archive, string outputDirectory,
        int archiveIndex, int archiveCount, IProgress<CatalogProgress>? progress, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo
        {
            FileName = game.ArchiveToolPath,
            WorkingDirectory = game.RootDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add(archive.DatabasePath);
        start.ArgumentList.Add("-database");
        start.ArgumentList.Add(outputDirectory);
        using var process = new Process { StartInfo = start };
        process.Start();
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        var maximumObserved = 0;
        var lastReported = 0;
        while (await process.StandardOutput.ReadLineAsync(cancellationToken) is { } line)
        {
            var match = ArchiveProgressRegex().Match(line);
            if (match.Success && int.TryParse(match.Groups[1].Value, out var current) && int.TryParse(match.Groups[2].Value, out var total))
            {
                maximumObserved = Math.Max(maximumObserved, current);
                if (maximumObserved == total || maximumObserved - lastReported >= 100)
                {
                    lastReported = maximumObserved;
                    progress?.Report(new($"Extracting {archive.DisplayName}", maximumObserved, total, $"Database {archiveIndex + 1} of {archiveCount}"));
                }
            }
        }
        await process.WaitForExitAsync(cancellationToken);
        var stderr = await stderrTask;
        if (process.ExitCode != 0) throw new InvalidOperationException($"ArchiveTool failed for {archive.DisplayName} (exit {process.ExitCode}). {stderr}".Trim());
    }

    private static ItemRecord? TryParseItem(string file, string extractionRoot, string source, IReadOnlyDictionary<string, string> tags)
    {
        var relative = Path.GetRelativePath(extractionRoot, file).Replace('\\', '/').ToLowerInvariant();
        if (!IsItemRecordLocation(relative) || IsExcluded(relative)) return null;
        string className = "", nameTag = "", descriptionTag = "", classification = "", template = "";
        var itemLevel = 0;
        using var reader = new StreamReader(file, Encoding.UTF8, true, 4096);
        while (reader.ReadLine() is { } line)
        {
            var comma = line.IndexOf(',');
            if (comma <= 0) continue;
            var key = line[..comma];
            var end = line.LastIndexOf(',');
            var value = end > comma ? line[(comma + 1)..end] : line[(comma + 1)..];
            switch (key)
            {
                case "Class": className = value; break;
                case "itemNameTag": nameTag = value; break;
                case "description": descriptionTag = value; break;
                case "itemClassification": classification = value; break;
                case "itemLevel": int.TryParse(value.Split('.')[0], out itemLevel); break;
                case "templateName": template = value; break;
            }
        }
        if (!relative.StartsWith("records/items/", StringComparison.Ordinal) && !LooksSpawnable(className, template)) return null;
        var displayTag = !string.IsNullOrWhiteSpace(nameTag) ? nameTag : descriptionTag;
        var hasName = tags.TryGetValue(displayTag, out var localized) && !string.IsNullOrWhiteSpace(CleanMarkup(localized));
        var name = hasName ? CleanMarkup(localized!) : Humanize(Path.GetFileNameWithoutExtension(relative));
        if (string.IsNullOrWhiteSpace(name)) name = relative;
        return InternalGearLabels.Apply(new(name, relative, source, CategoryFor(relative, className), classification, itemLevel, displayTag, className) {NameUnavailable=!hasName});
    }

    private static IEnumerable<(string RecordPath, string QuestPath)> ReadQuestAssociations(string file, string extractionRoot)
    {
        var recordPath = Path.GetRelativePath(extractionRoot, file).Replace('\\', '/').ToLowerInvariant();
        foreach (var line in File.ReadLines(file))
        {
            var comma = line.IndexOf(',');
            if (comma <= 0 || !line[..comma].StartsWith("questFile", StringComparison.OrdinalIgnoreCase)) continue;
            var end = line.LastIndexOf(',');
            var value = (end > comma ? line[(comma + 1)..end] : line[(comma + 1)..])
                .Replace('\\', '/').Trim().TrimStart('/').ToLowerInvariant();
            if (value.EndsWith(".qst", StringComparison.OrdinalIgnoreCase))
                yield return (recordPath, value);
        }
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

    private static bool IsExcluded(string path) => new[]
    {
        "/lootaffixes/", "/loottables/", "/lootchests/", "/dropfx/", "/itemskills/", "/controllers/",
        "/merchants/", "/randomizers/", "/transmuters/", "/dynamictables/", "/ui/", "/lootsets/",
        "/skills/", "/itemskills/", "records/skills/", "records/fx/", "/proxies/", "/loottable", "/lorechest"
    }.Any(path.Contains);

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
        value = Regex.Replace(value, "^[a-z]+[0-9]*_", "", RegexOptions.IgnoreCase);
        value = value.Replace('_', ' ').Replace('-', ' ').Trim();
        return CultureInfoInvariantTitle(value);
    }

    private static string CultureInfoInvariantTitle(string value) =>
        System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(value.ToLowerInvariant());

    private static string SourceFingerprint(IEnumerable<CatalogSource> sources) =>
        Hashing.CombineFingerprint(sources.SelectMany(x => new[] { x.Id, x.DatabaseSha256, x.TextSha256 ?? "" }));

    private static string CleanMarkup(string value) => ColorMarkupRegex().Replace(MarkupRegex().Replace(value, ""), "").Trim();

    private static string GetArchiveToolSafeWorkDirectory(string? preferred)
    {
        var path = preferred ?? Path.Combine(CompanionDataPaths.DirectoryPath, "CatalogWork");
        Directory.CreateDirectory(path);
        var buffer = new StringBuilder(1024);
        if (GetShortPathName(path, buffer, buffer.Capacity) > 0) path = buffer.ToString();
        if (path.Contains('.')) throw new InvalidOperationException("ArchiveTool cannot extract to a path containing a period. Move the application cache to a Windows profile path without periods.");
        return path;
    }

    private static void SafeDeleteDirectory(string target, string allowedParent)
    {
        if (!Directory.Exists(target)) return;
        var fullTarget = Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar);
        var fullParent = Path.GetFullPath(allowedParent).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullTarget.StartsWith(fullParent, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Refusing to delete a catalog path outside its work directory.");
        Directory.Delete(fullTarget, true);
    }

    [DllImport("kernel32.dll", EntryPoint = "GetShortPathNameW", CharSet = CharSet.Unicode)]
    private static extern uint GetShortPathName(string longPath, StringBuilder shortPath, int bufferLength);

    [GeneratedRegex("^\\((\\d+)/(\\d+)\\)")]
    private static partial Regex ArchiveProgressRegex();
    [GeneratedRegex("\\{[^}]+\\}")]
    private static partial Regex MarkupRegex();
    [GeneratedRegex("\\^[A-Za-z0-9]")]
    private static partial Regex ColorMarkupRegex();
}
