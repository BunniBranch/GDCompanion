using System.Xml.Linq;

internal static class ReleasePackagingRegression
{
    public static void Run(string root, Action<bool,string> require)
    {
        var license = File.ReadAllText(Path.Combine(root, "LICENSE"));
        require(license.Length > 35000 && license.Contains("Version 3, 29 June 2007") &&
            license.Contains("END OF TERMS AND CONDITIONS"), "complete GPLv3 license is present");
        require(File.ReadAllText(Path.Combine(root,"COPYRIGHT.md")).Contains("GPL-3.0-only"),
            "project declares GPL version 3 only and preserves third-party notices");
        var properties = XDocument.Load(Path.Combine(root,"Directory.Build.props"));
        require(properties.Descendants("PathMap").Any() && properties.Descendants("DebugType").Any(e=>e.Value=="none"),
            "release builds normalize source paths and omit debug symbols");
        var manifest = File.ReadAllLines(Path.Combine(root,"scripts","source-manifest.txt"))
            .Where(s=>!string.IsNullOrWhiteSpace(s) && !s.StartsWith('#')).ToHashSet(StringComparer.Ordinal);
        foreach(var required in new[]{"LICENSE","COPYRIGHT.md","Directory.Build.props","README.md",
            "scripts/build.ps1","scripts/package-source.ps1","scripts/source-manifest.txt",
            "src/ThirdParty/minhook/LICENSE.txt","src/GrimDawnCompanion.App/Assets/Fonts/OFL.txt"})
            require(manifest.Contains(required), "source manifest retains " + required);
        require(!manifest.Any(p=>p is "ORIGINAL-BUILD-GUIDE.md" or "FINAL-EQUIPMENT-MATRIX.md" or "research-original-gear.md"),
            "unrelated local research is not in the source release");
        var publicDocs = new[] { "docs/developer-guide.md", "docs/user-guide.md" };
        require(manifest.Where(p=>p.StartsWith("docs/",StringComparison.Ordinal)).OrderBy(p=>p,StringComparer.Ordinal)
            .SequenceEqual(publicDocs), "source release contains only the two current public guides");
        require(manifest.Contains("src/GrimDawnCompanion.Tests/Fixtures/approved-menu-wording.md"),
            "source release retains the approved menu test fixture");
        var ignore = File.ReadAllText(Path.Combine(root,".gitignore"));
        var attributes = File.ReadAllText(Path.Combine(root,".gitattributes"));
        require(ignore.Contains("/docs/*") && publicDocs.All(p=>ignore.Contains("!/"+p)) &&
            attributes.Contains("/docs/* export-ignore") && publicDocs.All(p=>attributes.Contains("/"+p+" -export-ignore")),
            "historical docs stay local while public guides remain publishable");
        var buildScript = File.ReadAllText(Path.Combine(root,"scripts","build.ps1"));
        require(buildScript.Contains("foreach ($doc in 'user-guide.md','developer-guide.md')"),
            "Windows release copies the same two guides as the source release");
        foreach(var doc in publicDocs.Prepend("README.md"))
        {
            var docPath=Path.Combine(root,doc.Replace('/',Path.DirectorySeparatorChar));
            foreach(System.Text.RegularExpressions.Match link in
                System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(docPath),@"\]\(([^)]+)\)"))
            {
                var target=link.Groups[1].Value;
                if(target.StartsWith("https://",StringComparison.Ordinal) || target.StartsWith("#",StringComparison.Ordinal)) continue;
                var linkedPath=Path.GetFullPath(Path.Combine(Path.GetDirectoryName(docPath)!,target));
                require(File.Exists(linkedPath), "public documentation link resolves: " + doc + " -> " + target);
                var linkedRelative=Path.GetRelativePath(root,linkedPath).Replace('\\','/');
                require(manifest.Contains(linkedRelative), "public documentation link is included in source release: " + linkedRelative);
            }
        }
        foreach(var file in Directory.EnumerateFiles(Path.Combine(root,"src"),"*",SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root,file).Replace('\\','/');
            if(relative.Contains("/bin/") || relative.Contains("/obj/") || relative.Contains("/Data/")) continue;
            require(manifest.Contains(relative), "source release includes " + relative);
        }
        var markup = XDocument.Load(Path.Combine(root,"src","GrimDawnCompanion.App","MainWindow.xaml"));
        require(markup.Descendants().Attributes("Text").Any(a=>a.Value.StartsWith("GPLv3 — Copyright © 2026 BunniBranch")) &&
            markup.Descendants().Attributes("Click").Count(a=>a.Value=="OpenLicenseNotice")==2,
            "Settings includes the approved GPL notice and two local license viewers");
    }
}
