using GrimDawnCompanion.Core;
using System.Text.Json;

if(args.Length==2 && (args[0]=="--audit-item-names" || args[0]=="--refresh-item-labels"))
{
    var auditGame=GameLocator.Locate() ?? throw new DirectoryNotFoundException("Grim Dawn was not found.");
    var tags=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
    foreach(var archive in GameLocator.GetInstalledArchives(auditGame))
        if(archive.TextPath is {} text && File.Exists(text) && new FileInfo(text).Length>2048)
            foreach(var pair in ArcReader.ReadTags(text)) tags[pair.Key]=pair.Value;
    var catalog=JsonSerializer.Deserialize<CatalogDocument>(await File.ReadAllTextAsync(args[1]),new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    if(args[0]=="--refresh-item-labels")
    {
        if(catalog.SourceFingerprint!=await CatalogService.ComputeSourceFingerprintAsync(auditGame))
            throw new InvalidDataException("Installed text/database sources do not match the catalog. Rebuild instead.");
        var refreshed=new CatalogDocument
        {
            SourceFingerprint=catalog.SourceFingerprint,GeneratedAt=catalog.GeneratedAt,Sources=catalog.Sources,
            Items=catalog.Items.Select(item=>item with {NameUnavailable=!tags.TryGetValue(item.NameTag,out var value) || string.IsNullOrWhiteSpace(AffixLabels.Clean(value))}).ToList(),
            AffixRecords=catalog.AffixRecords,QuestRecordAssociations=catalog.QuestRecordAssociations,QuestNavigationRecords=catalog.QuestNavigationRecords
        };
        InternalGearLabels.ApplyTo(refreshed);
        var temporary=Path.GetFullPath(args[1])+".new";
        await File.WriteAllTextAsync(temporary,JsonSerializer.Serialize(refreshed,new JsonSerializerOptions(JsonSerializerDefaults.Web){WriteIndented=true}));
        File.Move(temporary,Path.GetFullPath(args[1]),true);
        Console.WriteLine($"Refreshed labels: {refreshed.Items.Count(i=>i.NameUnavailable)} records lack localized names; {refreshed.Items.Count} items retained.");
        return 0;
    }
    foreach(var item in catalog.Items.Where(i=>!tags.ContainsKey(i.NameTag)))
        Console.WriteLine($"{item.InternalClass}\t{item.Name}\t{item.NameTag}\t{item.RecordPath}");
    return 0;
}

if (args.Length == 2 && args[0] == "--label-internal-gear")
{
    var path = Path.GetFullPath(args[1]);
    var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
    var document = JsonSerializer.Deserialize<CatalogDocument>(await File.ReadAllTextAsync(path), options)
                   ?? throw new InvalidDataException("Catalog could not be read.");
    if(document.SchemaVersion!=CatalogService.CurrentSchema) throw new InvalidDataException("Catalog schema is not current.");
    var changed=document.Items.Count(item=>InternalGearLabels.Apply(item)!=item);
    InternalGearLabels.ApplyTo(document);
    var temporary=path+".new";
    await File.WriteAllTextAsync(temporary,JsonSerializer.Serialize(document,options));
    File.Move(temporary,path,true);
    Console.WriteLine($"Labeled {changed:N0} internal gear entries; retained {document.Items.Count:N0} items.");
    return 0;
}

if (args.Length == 2 && args[0] == "--sanitize")
{
    var path = Path.GetFullPath(args[1]);
    var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
    var document = JsonSerializer.Deserialize<CatalogDocument>(await File.ReadAllTextAsync(path), options)
                   ?? throw new InvalidDataException("Catalog could not be read.");
    document.Items.RemoveAll(x =>
        x.RecordPath.Contains("/proxies/", StringComparison.OrdinalIgnoreCase) ||
        x.RecordPath.Contains("/loottable", StringComparison.OrdinalIgnoreCase) ||
        x.RecordPath.Contains("/lorechest", StringComparison.OrdinalIgnoreCase));
    var temporary = path + ".new";
    await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(document, options));
    File.Move(temporary, path, true);
    Console.WriteLine($"Sanitized catalog: {document.Items.Count:N0} item records.");
    return 0;
}

if (args.Length >= 2 && args[0] == "--verify-compat")
{
    var installation = GameLocator.Locate(args[1]) ?? throw new DirectoryNotFoundException("Grim Dawn was not found.");
    var profile = args.Length > 2 ? args[2] : Path.Combine(Directory.GetCurrentDirectory(), "compatibility.json");
    var report = await new CompatibilityService(profile).VerifyAndUpdateAsync(installation);
    Console.WriteLine($"Compatibility: {report.Level}; connect={report.CanConnect}; profileUpdated={report.ProfileUpdated}");
    foreach (var symbol in report.Symbols)
        Console.WriteLine($"{(symbol.Found ? "OK" : "--")} {symbol.Module,-10} RVA 0x{symbol.Rva:X8} {symbol.FriendlyName}");
    return report.CanConnect ? 0 : 1;
}

if(args.Length>0 && args[0].StartsWith("--",StringComparison.Ordinal))
    throw new ArgumentException("Unknown CatalogBuilder option: "+args[0]);
var gamePath = args.Length > 0 ? args[0] : null;
var outputPath = args.Length > 1 ? args[1] : Path.Combine(AppContext.BaseDirectory, "catalog.json");
var game = GameLocator.Locate(gamePath);
if (game is null)
{
    Console.Error.WriteLine("Grim Dawn was not found. Pass the installation folder as the first argument.");
    return 2;
}

Console.WriteLine($"Game: {game.RootDirectory}");
Console.WriteLine($"Output: {Path.GetFullPath(outputPath)}");
var progress = new Progress<CatalogProgress>(value =>
{
    Console.Write($"\r{value.Stage,-34} {value.Percent,6:0.0}%  {value.Detail,-50}");
});
try
{
    var workDirectory = args.Length>2 ? Path.GetFullPath(args[2]) : Path.Combine(Directory.GetCurrentDirectory(), "catalog_work", "builder");
    var document = await new CatalogService(workDirectory: workDirectory).RebuildAsync(game, outputPath, progress);
    Console.WriteLine();
    Console.WriteLine($"Built {document.Items.Count:N0} unique item records from {document.Sources.Count} archives.");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine();
    Console.Error.WriteLine(ex);
    return 1;
}
