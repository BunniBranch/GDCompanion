using GrimDawnCompanion.Core;
using System.Xml.Linq;

internal static class CatalogFirstRunRegression
{
    public static async Task<CatalogDocument> Run(string root, GameInstallation game, Action<bool,string> require)
    {
        var project = XDocument.Load(Path.Combine(root, "src", "GrimDawnCompanion.App", "GrimDawnCompanion.App.csproj"));
        require(!project.Descendants().Attributes("Include").Any(a => a.Value.Contains("catalog.json")),
            "app project does not bundle a generated catalog");
        var startup = File.ReadAllText(Path.Combine(root, "src", "GrimDawnCompanion.App", "MainWindow.xaml.cs"));
        require(startup.Contains("_catalog.LoadAsync(cancellationToken: _lifetime.Token)") &&
            startup.Contains("if (_document is null ||") && startup.Contains("await RebuildCatalogAsync(automatic: true)"),
            "desktop startup uses only local cache and rebuilds when absent");
        var build = File.ReadAllText(Path.Combine(root, "scripts", "build.ps1"));
        require(!build.Contains("The bundled catalog is missing") && build.Contains("Packaging stopped."),
            "release build needs no generated catalog and rejects accidental game-data packaging");

        var testRoot = Path.Combine(root, "tmp", "catalogfirstrun", Guid.NewGuid().ToString("N"));
        var cache = Path.Combine(testRoot, "profile");
        var service = new CatalogService(cache, Path.Combine(testRoot, "work"));
        require(!Directory.Exists(cache) && await service.LoadAsync() is null,
            "first run has neither cached nor bundled catalog");
        Console.WriteLine("INFO  Building fresh catalog from installed game into isolated test profile");
        var document = await service.RebuildAsync(game);
        require(File.Exists(service.CachePath) && document.Items.Count > 0,
            "first run rebuilds and persists locally without bundled data");
        var original = await File.ReadAllBytesAsync(service.CachePath);
        var warm = await new CatalogService(cache).LoadAsync();
        require(warm?.Items.Count == document.Items.Count && warm.SourceFingerprint == document.SourceFingerprint &&
            original.SequenceEqual(await File.ReadAllBytesAsync(service.CachePath)),
            "second run reuses generated cache without rewriting it");

        var missing = new CatalogService(Path.Combine(testRoot, "missing"));
        bool failed = false;
        try { await missing.RebuildAsync(new GameInstallation(Path.Combine(testRoot, "no-game"))); }
        catch (FileNotFoundException) { failed = true; }
        require(failed && !File.Exists(missing.CachePath), "missing game tools fail without writing a partial catalog");
        Console.WriteLine("INFO  Isolated first-run cache: " + service.CachePath);
        return document;
    }
}
