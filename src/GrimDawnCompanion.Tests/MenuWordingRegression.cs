using System.Text.RegularExpressions;
using System.Xml.Linq;

internal static class MenuWordingRegression
{
    public static void Run(string root,Action<bool,string> require)
    {
        var approved=File.ReadAllText(Path.Combine(root,"src","GrimDawnCompanion.Tests","Fixtures","approved-menu-wording.md"));
        var appDir=Path.Combine(root,"src","GrimDawnCompanion.App");
        var markup=XDocument.Load(Path.Combine(appDir,"MainWindow.xaml"));
        var attributes=markup.Descendants().Attributes().Select(a=>a.Value).ToHashSet();
        var source=File.ReadAllText(Path.Combine(appDir,"MainWindow.xaml.cs"));
        var viewModel=File.ReadAllText(Path.Combine(appDir,"MainViewModel.cs"));
        var respec=File.ReadAllText(Path.Combine(appDir,"MainWindow.MasteryAudit.cs"));
        var level=File.ReadAllText(Path.Combine(appDir,"MainWindow.CharacterLevel.cs"));
        var rows=Regex.Matches(approved,@"^\| ([A-Z]\d+) \| [^|]+ \| (.*?) \|\r?$",RegexOptions.Multiline);
        require(rows.Count==41,"approved menu document contains all 41 description rows");
        foreach(Match row in rows)
        {
            var text=row.Groups[2].Value.Trim();
            require(attributes.Contains(text) || source.Contains("\""+text+"\"") || viewModel.Contains("\""+text+"\""),
                "approved menu wording applied: "+row.Groups[1].Value);
        }
        var calibration=markup.Descendants().Single(e=>e.Name.LocalName=="Expander" &&
            (string?)e.Attribute("Header")=="Advanced Calibration(Not Recommended)");
        require((string?)calibration.Attribute("IsExpanded")=="False", "requested advanced calibration heading retains collapsed default");
        require(attributes.Contains("Travel is supported between outdoor campaign areas. Bookmarks are blocked if their map data no longer matches."),"bookmark travel note follows the edited B6 wording, not the older review commentary");
        var howItWorks=attributes.Single(a=>a.StartsWith("• Distant active quest objectives"));
        var approvedBullets=approved.Split("## Radar bullets")[1]
            .Split('\n').Select(s=>s.Trim()).Where(s=>s.StartsWith("- ")).Select(s=>"• "+s[2..]);
        require(howItWorks==string.Join("\n",approvedBullets),"radar bullets match the edited list exactly");
        require(source.Contains("No game archives or character saves are changed.\\n\\nContinue?") &&
            source.Contains("\"Force Refresh Data\", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes"),"refresh copy retains default-No confirmation");
        foreach(var field in new[]{"prepared.Name","prepared.Current:N0","prepared.Target:N0","prepared.Maximum:N0","prepared.Spent:N0","prepared.Cap:N0"})
            require(source.Contains("{"+field+"}"),"resource confirmation retains dynamic "+field);
        require(source.Contains("resource==\"money\" ? \"\\n\" :") && source.Contains("Natural Cap Bypass is enabled. Attempted safety caps are in place, but save and mod compatibility above normal limits is not guaranteed.") && source.Contains("Back up the character first; Cloud saving is not a backup."),"resource copy includes approved money-specific wording and conditional bypass warning");
        foreach(var field in new[]{"audit.CharacterName","audit.Level","plan.DevotionRefund","plan.DevotionUnspent","plan.ExpectedDevotionPoints","plan.Refund","plan.Unspent","plan.ExpectedPoints","plan.SelectedClasses"})
            require(respec.Contains("{"+field+"}"),"respec confirmation retains dynamic "+field);
        require(respec.Contains("Confirm Mastery Respec") && respec.Contains("Open the Skills page once for the loaded character.") && respec.Contains("A partial or unknown result blocks further attempts.") && !respec.Contains("The game may autosave it to the cloud."),"respec confirmation matches approved checklist and uncertain-result wording");
        require(level.Contains("Current game or mod level cap: {prepared.Cap}") && level.Contains("Companion cannot undo this change.") && level.Contains("level-related achievements"),"level confirmation retains live cap, irreversible-change and achievement information");
        require(source.Contains("Confirm Quest Token Change") && source.Contains("quest token “{_viewModel.SelectedToken.Name}”?") && source.Contains("MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes"),"quest confirmation preserves dynamic token and existing buttons");
        require(!File.ReadAllText(Path.Combine(appDir,"MainWindow.GameplayAssists.cs")).Contains("MessageBox") &&
            !source.Contains("\"Return to bookmarked position\", MessageBoxButton.YesNo"),"wording update does not add assist or bookmark-return confirmations");
    }
}
