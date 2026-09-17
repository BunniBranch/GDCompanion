using GrimDawnCompanion.Core;
using System.Text.Json;

static void Require(bool condition, string message)
{
    if (!condition)
    {
        Console.Error.WriteLine("TEST FAILED: " + message);
        Environment.Exit(1);
    }
    Console.WriteLine("PASS  " + message);
}

try
{
var root = Path.GetFullPath(args.Length > 0 ? args[0] : Directory.GetCurrentDirectory());
ReleasePackagingRegression.Run(root, Require);
CatalogFilterUiRegression.Run(root, Require);
MenuWordingRegression.Run(root, Require);
await InternalGearLabelRegression.Run(root, Require);
RadarPillUiRegression.Run(root, Require);
await PositionBookmarkRegression.Run(root, Require);
MasteryAuditRegression.Run(Require);
AppVersionRegressionTests.Run(Require);
NativeRadarFrameRegressionTests.Run(Require);
CharacterLevelRegressionTests.Run(root, Require);
CharacterResourceRegressionTests.Run(root, Require);
AffixAndPoiRegressionTests.Run(root, Require);
var mainWindowMarkup = System.Xml.Linq.XDocument.Load(Path.Combine(root, "src", "GrimDawnCompanion.App", "MainWindow.xaml"));
Require(mainWindowMarkup.Descendants().Any(e=>(string?)e.Attribute("Header")=="POI Type Filter") && !mainWindowMarkup.ToString().Contains("Point of interest types"),"POI filter section uses the requested POI Type Filter heading");
System.Xml.Linq.XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
System.Xml.Linq.XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
var settingsPage = mainWindowMarkup.Descendants().Single(element => (string?)element.Attribute(xaml + "Name") == "SettingsPage");
var refreshButtons = mainWindowMarkup.Descendants(presentation + "Button")
    .Where(button => (string?)button.Attribute("Click") == "RefreshData").ToArray();
Require(refreshButtons.Length == 1 && refreshButtons[0].Ancestors().Contains(settingsPage) &&
        (string?)refreshButtons[0].Attribute("Content") == "Force Refresh Data" &&
        settingsPage.Name == presentation + "ScrollViewer",
    "Force Refresh Data appears only in scrollable Settings, not the shared header");
var mainWindowSource = File.ReadAllText(Path.Combine(root, "src", "GrimDawnCompanion.App", "MainWindow.xaml.cs"));
var refreshHandler = mainWindowSource.Split("private async void RefreshData", 2)[1].Split("private async void ConnectGame", 2)[0];
Require(refreshHandler.Contains("This may take some time. No game archives or character saves are changed.\\n\\nContinue?", StringComparison.Ordinal) &&
        refreshHandler.Contains("MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;", StringComparison.Ordinal) &&
        refreshHandler.IndexOf("MessageBoxResult.Yes) return;", StringComparison.Ordinal) < refreshHandler.IndexOf("await VerifyCompatibilityAsync()", StringComparison.Ordinal),
    "force refresh warns and defaults to No before any refresh work begins");
Require(refreshHandler.Contains("if (_viewModel.IsBusy || _refreshDataBusy) return;", StringComparison.Ordinal) &&
        refreshHandler.Contains("ForceRefreshDataButton.IsEnabled = false;", StringComparison.Ordinal) &&
        refreshHandler.Split("finally", 2)[1].Contains("ForceRefreshDataButton.IsEnabled = true;", StringComparison.Ordinal) &&
        mainWindowSource.Contains("finally { _viewModel.IsBusy = wasBusy; }", StringComparison.Ordinal),
    "force refresh prevents duplicate runs and restores controls after completion or failure");
Require(mainWindowMarkup.Descendants(presentation + "TextBlock").Single(text =>
        (string?)text.Attribute(xaml + "Name") == "SidebarVersionText").Attribute("Text")?.Value == "{x:Static local:AppVersion.Display}" &&
        !mainWindowMarkup.ToString().Contains("Local • Single-Player", StringComparison.Ordinal),
    "sidebar subtitle uses the running app version instead of the old single-player label");
var brandTitles = mainWindowMarkup.Descendants(presentation + "TextBlock")
    .Where(text => ((string?)text.Attribute(xaml + "Name")) is "SidebarGameTitle" or "SidebarCompanionTitle").ToArray();
Require(brandTitles.Length == 1 &&
        (string?)brandTitles[0].Attribute("FontFamily") == "{StaticResource SidebarBrandFont}" &&
        (string?)brandTitles[0].Attribute("Text") == "GDCompanion" &&
        (string?)brandTitles[0].Attribute("FontWeight") == "Normal" &&
        !brandTitles[0].Elements(presentation + "Run").Any() &&
        mainWindowMarkup.Descendants().Count(element => (string?)element.Attribute("FontFamily") == "{StaticResource SidebarBrandFont}") == 1,
    "GDCompanion uses one consistent mixed-case font without synthetic bold");
Require(mainWindowMarkup.Descendants(presentation + "TextBlock").Single(text =>
        (string?)text.Attribute("Text") == "Unofficial companion tool for Grim Dawn")
        .Attribute("Foreground")?.Value == "White",
    "sidebar unofficial-tool subtitle uses white text");
var appMarkup = System.Xml.Linq.XDocument.Load(Path.Combine(root, "src", "GrimDawnCompanion.App", "App.xaml"));
Require(appMarkup.Descendants(presentation + "FontFamily").Single(font =>
        (string?)font.Attribute(xaml + "Key") == "SidebarBrandFont").Value == "/GDCompanion;component/Assets/Fonts/#Pirata One",
    "sidebar uses the bundled Pirata One font");
var brandAssets = Path.Combine(root, "src", "GrimDawnCompanion.App", "Assets", "Fonts");
var fontBytes = File.ReadAllBytes(Path.Combine(brandAssets, "PirataOne-Regular.ttf"));
Require(Directory.GetFiles(brandAssets, "*.ttf").Length == 1 &&
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(fontBytes)) ==
            "5347A2E155589ECF667D4B766613C8EE003EDDE9F83717FD24C09599A4B1ECC0" &&
        File.ReadAllText(Path.Combine(brandAssets, "OFL.txt")).Contains("SIL OPEN FONT LICENSE Version 1.1", StringComparison.Ordinal) &&
        File.ReadAllText(Path.Combine(brandAssets, "OFL.txt")).Contains("Rodrigo Fuenzalida, Nicolas Massi", StringComparison.Ordinal),
    "only the unmodified replacement font ships, with the correct author and OFL license");
var navigationPage = mainWindowMarkup.Descendants(presentation + "TabControl")
    .Single(page => (string?)page.Attribute(xaml + "Name") == "NavigationPage");
var utilitiesPage = mainWindowMarkup.Descendants(presentation + "TabControl")
    .Single(page => (string?)page.Attribute(xaml + "Name") == "UtilitiesPage");
Require(navigationPage.Elements(presentation + "TabItem").Select(tab => (string?)tab.Attribute("Header"))
        .SequenceEqual(new[] { "Radar Overlay", "Position Bookmarks" }),
    "Navigation contains Radar Overlay and Position Bookmarks in order");
Require(utilitiesPage.Elements(presentation + "TabItem").Select(tab => (string?)tab.Attribute("Header"))
        .SequenceEqual(new[] { "Gameplay Assists", "Character Resources", "Mastery Respec", "Blueprints", "Quest-Token Inspector" }),
    "Character Utilities retains only character and progression tabs");
var assistsTab = utilitiesPage.Elements(presentation + "TabItem").Single(tab => (string?)tab.Attribute("Header") == "Gameplay Assists");
Require(assistsTab.Descendants(presentation + "CheckBox").Count() == 4 &&
        assistsTab.Descendants(presentation + "CheckBox").All(box => (string?)box.Attribute("IsChecked") != "True") &&
        assistsTab.Descendants(presentation + "StackPanel").Single(panel => (string?)panel.Attribute(xaml + "Name") == "AssistOptionsPanel").Attribute("IsEnabled")?.Value == "False",
    "four gameplay assists start unchecked and unavailable until verified");
Require(!assistsTab.Descendants(presentation + "Button").Any() &&
        assistsTab.Descendants(presentation + "CheckBox").All(box =>
            (string?)box.Attribute("Click") == "ToggleGameplayAssist" && box.Attribute("Checked") is null && box.Attribute("Unchecked") is null) &&
        (string?)assistsTab.Descendants(presentation + "Slider").Single().Attribute("ValueChanged") == "GameplayAssistSpeedChanged" &&
        !assistsTab.ToString().Contains("Restore Health", StringComparison.Ordinal),
    "assist checkboxes apply user clicks directly, speed updates automatically, and separate action buttons are removed");
var assistSource = File.ReadAllText(Path.Combine(root, "src", "GrimDawnCompanion.App", "MainWindow.GameplayAssists.cs"));
Require(mainWindowSource.Contains("\"Character and progression tools.\"", StringComparison.Ordinal) &&
        !mainWindowSource.Contains("A modular home for guarded", StringComparison.Ordinal),
    "Character Utilities uses the requested concise subtitle");
Require(!assistsTab.ToString().Contains("Check an assist to turn it on;", StringComparison.Ordinal) &&
        !assistsTab.ToString().Contains("Live-game validation is pending; back up your character before testing.", StringComparison.Ordinal) &&
        !assistsTab.ToString().Contains("but does not undo", StringComparison.Ordinal) &&
        !assistsTab.ToString().Contains("Cloud saving is not a backup.", StringComparison.Ordinal) &&
        assistsTab.ToString().Contains("Stopping restores normal behavior.", StringComparison.Ordinal) &&
        !assistSource.Contains("Check an assist to turn it on.", StringComparison.Ordinal),
    "Gameplay Assists uses the requested shortened copy and sentence ending");
var tokenWarning = mainWindowMarkup.Descendants(presentation + "TextBlock").Single(text =>
    ((string?)text.Attribute("Text"))?.StartsWith("Warning: Quest-token changes", StringComparison.Ordinal) == true);
Require((string?)tokenWarning.Attribute("Text") == "Warning: Quest-token changes can permanently break quests or character progression. Change only states you understand. Search for a quest token to inspect or change its state." &&
        (string?)tokenWarning.Attribute("TextWrapping") == "Wrap" &&
        !mainWindowMarkup.ToString().Contains("Rebuilt automatically from the installed base-game and DLC scripts.", StringComparison.Ordinal),
    "Quest Tokens shows the approved wrapped warning and search instruction");
Require(!assistSource.Contains("MessageBox", StringComparison.Ordinal) &&
        assistSource.Contains("_assistSuspended = true;", StringComparison.Ordinal), "assists toggle without confirmation and still stop renewal after uncertainty");
Require(assistSource.Contains("await _assistGate.WaitAsync", StringComparison.Ordinal) &&
        assistSource.Contains("!change.MatchesContext(inspected, _assistStatus)", StringComparison.Ordinal) &&
        assistSource.Contains("if (change.Mask == inspected.Mask && change.SpeedPercent == inspected.SpeedPercent) return;", StringComparison.Ordinal) &&
        assistSource.Contains("var result = change.Mask == 0", StringComparison.Ordinal) &&
        assistSource.Contains("RenderAssistStatus(_assistStatus, true)", StringComparison.Ordinal) &&
        assistSource.Contains("if (_assistRendering || _assistChanging", StringComparison.Ordinal),
    "checkbox changes serialize with polling, guard context, stop the final assist, restore stale selections and suppress render-triggered slider commands");
var assistBadge = mainWindowMarkup.Descendants(presentation + "Border").Single(element =>
    (string?)element.Attribute(xaml + "Name") == "AssistStatusBadge");
Require((string?)assistBadge.Attribute("CornerRadius") == "12" && assistBadge.Attribute("Visibility") is null &&
        (string?)assistBadge.Descendants(presentation + "TextBlock").Single().Attribute("Text") == "Assists Inactive" &&
        assistBadge.Parent!.Descendants(presentation + "TextBlock").Any(text => (string?)text.Attribute("Text") == "{Binding ConnectionText}") &&
        !mainWindowMarkup.ToString().Contains("GlobalAssistBanner", StringComparison.Ordinal) &&
        !mainWindowMarkup.ToString().Contains("AssistActiveBanner", StringComparison.Ordinal),
    "one always-visible assist pill sits beside the bridge indicator and replaces duplicate banners");
Require(assistSource.Contains("SetAssistIndicator(state.Active)", StringComparison.Ordinal) &&
        assistSource.Contains("active ? \"Assists Active\" : \"Assists Inactive\"", StringComparison.Ordinal) &&
        assistSource.Contains("if (mayBeActive) SetAssistIndicator(false, stopping: true)", StringComparison.Ordinal) &&
        mainWindowSource.Contains("SetAssistIndicator(false);", StringComparison.Ordinal),
    "assist pill follows confirmed aggregate state and does not report inactive while shutdown is unconfirmed");
Require(!assistsTab.ToString().Contains("Charged skills and item effects need live validation.", StringComparison.Ordinal) &&
        assistsTab.ToString().Contains("Casting animations and other skill requirements still apply.", StringComparison.Ordinal),
    "approved cooldown copy preserves casting and requirement limits without the removed validation sentence");
for (uint mask = 0; mask <= 15; mask++)
    Require(new GameplayAssistStatus(1, 42, mask, 100).Active == (mask != 0),
        "aggregate assist status is active for any enabled combination, inactive only for zero");
GameplayAssistSelectionRegression.Run(Require);
Require(GameplayAssistStatus.Parse("OK ASSISTS 3 42 15 150") == new GameplayAssistStatus(3, 42, 15, 150) &&
        !GameplayAssistStatus.Parse("OK ASSISTS 4 0 0 100").Active, "assist status preserves revision identity mask and bounded speed");
foreach (var invalidAssist in new[] { "OK ASSISTS 0 42 1 100", "OK ASSISTS 1 0 1 100", "OK ASSISTS 1 42 16 100", "OK ASSISTS 1 42 8 201", "OK ASSISTS 1 42 1 150", "OK ASSISTS 1 42 0 100 extra", "ERROR ASSISTS_EXPIRED_OR_STOPPED" })
{
    bool rejected = false;
    try { GameplayAssistStatus.Parse(invalidAssist); } catch (InvalidOperationException) { rejected = true; }
    Require(rejected, "malformed unsafe or failed assist responses are rejected");
}
var masteryTab = utilitiesPage.Elements(presentation + "TabItem").Single(tab => (string?)tab.Attribute("Header") == "Mastery Respec");
Require(masteryTab.Descendants(presentation + "Button").Single(button => (string?)button.Attribute(xaml + "Name") == "MasteryReplaceButton")
        .Attribute("IsEnabled")?.Value == "False" &&
        masteryTab.Descendants(presentation + "CheckBox").Any(box => (string?)box.Attribute(xaml + "Name") == "MasteryResetAcknowledgementBox" &&
            box.Attribute("IsChecked")?.Value != "True") &&
        masteryTab.ToString().Contains("no guaranteed rollback", StringComparison.Ordinal),
    "mastery reset starts disabled and requires an unchecked risk acknowledgement");
Require(!masteryTab.ToString().Contains("experimental", StringComparison.OrdinalIgnoreCase) &&
        !masteryTab.ToString().Contains("disposable", StringComparison.OrdinalIgnoreCase),
    "mastery respec UI no longer carries experimental or disposable-character labels");
var masteryResetSource = File.ReadAllText(Path.Combine(root, "src", "GrimDawnCompanion.App", "MainWindow.MasteryAudit.cs"));
Require(!masteryResetSource.Contains("Class-dependent stats, equipment, pets, buffs and hotbar references have not been exhaustively validated.", StringComparison.Ordinal) &&
        masteryResetSource.Contains("further attempts.\\n\\nChoose new masteries in the game's skill window.", StringComparison.Ordinal),
    "respec confirmation removes the requested warning sentence and gives new-class instructions their own paragraph");
Require(!masteryTab.Descendants(presentation + "Button").Any(button =>
            (string?)button.Attribute("Click") == "InspectMasteryPoints" || (string?)button.Attribute(xaml + "Name") == "MasteryAuditButton") &&
        masteryResetSource.Contains("var audit = await InspectLoadedCharacterAsync();", StringComparison.Ordinal) &&
        masteryResetSource.IndexOf("var audit = await InspectLoadedCharacterAsync();", StringComparison.Ordinal) <
            masteryResetSource.IndexOf("var plan = await _bridge.PrepareMasteryResetAsync", StringComparison.Ordinal) &&
        masteryResetSource.IndexOf("var plan = await _bridge.PrepareMasteryResetAsync", StringComparison.Ordinal) <
            masteryResetSource.IndexOf("if (MessageBox.Show", StringComparison.Ordinal),
    "Respec Character performs and displays fresh inspection before preparation and confirmation without a separate inspect button");
Require(masteryResetSource.Contains("_bridge.SupportsRepeatableMasteryReset", StringComparison.Ordinal) &&
        masteryResetSource.Contains("if (plan.IsVerifiedResponse(result))\n            {\n                _masteryResetAttempted = false;", StringComparison.Ordinal) &&
        masteryResetSource.Contains("if (plan.IsVerifiedResponse(result)) _masteryResetAttempted = false;", StringComparison.Ordinal) &&
        !masteryResetSource.Contains("single-use in this bridge session", StringComparison.Ordinal),
    "verified success or recorded verified outcome unlocks the next fresh respec, with repeat-capability gating");
Require(masteryResetSource.Contains("\"Confirm Mastery Respec\", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes", StringComparison.Ordinal) &&
        masteryResetSource.Contains("MasteryResetAcknowledgementBox.IsChecked != true", StringComparison.Ordinal) &&
        masteryTab.ToString().Contains("no guaranteed rollback", StringComparison.Ordinal),
    "reset retains acknowledgement guard and explicit default-No warning confirmation");
Require(masteryTab.ToString().Contains("Back up your character.", StringComparison.Ordinal) &&
        masteryResetSource.Contains("Back up the character.", StringComparison.Ordinal) &&
        masteryResetSource.Contains("cloud saving is not a backup", StringComparison.Ordinal),
    "character backup reminder appears on the respec page and in the reset confirmation");
Require(masteryTab.Descendants(presentation + "TextBlock").Any(text => (string?)text.Attribute("Text") == "Respec Character") &&
        masteryTab.Descendants(presentation + "Button").Single(button => (string?)button.Attribute(xaml + "Name") == "MasteryReplaceButton")
            .Attribute("Content")?.Value == "Respec Character" &&
        !masteryTab.ToString().Contains("Reset Character", StringComparison.Ordinal) &&
        masteryTab.ToString().Contains("point totals that match the inspection.", StringComparison.Ordinal) &&
        masteryTab.ToString().Contains("Before you respec", StringComparison.Ordinal) &&
        masteryTab.Descendants(presentation + "DockPanel").Count(panel => panel.Descendants(presentation + "TextBlock")
            .Any(text => (string?)text.Attribute("Text") == "•")) == 3 &&
        masteryTab.ToString().Contains("Remove any equipped items that grant skills.", StringComparison.Ordinal) &&
        masteryResetSource.Contains("Remove equipped items that grant skills.", StringComparison.Ordinal),
    "Respec Character labels and inspection wording retain the confirmation workflow and three pre-reset reminders");
var masteryReminders = masteryTab.Descendants(presentation + "DockPanel")
    .Where(panel => panel.Elements(presentation + "TextBlock").Any(text => (string?)text.Attribute("Text") == "•"))
    .Select(panel => (string?)panel.Elements(presentation + "TextBlock").Last().Attribute("Text"));
Require(masteryReminders.SequenceEqual(new[] {
        "Back up your character. Keep a separate, restorable copy; cloud saving is not a backup.",
        "Remove any equipped items that grant skills.",
        "Open the Skills page once for the loaded character." }) &&
        !masteryTab.ToString().Contains("Open Skills and Devotion once", StringComparison.Ordinal) &&
        !masteryTab.ToString().Contains("Stats, equipment requirements", StringComparison.Ordinal) &&
        !masteryTab.ToString().Contains("Cloud saving is not a rollback mechanism.", StringComparison.Ordinal),
    "Mastery Respec shows the exact ordered reminders and removes the requested explanatory sentences");
var resetPlan = MasteryResetPlan.Parse("OK FULL_RESPEC_PREPARED 100 42 51 4 2 2 3 2");
Require(resetPlan.ExpectedPoints == 55 && resetPlan.ExpectedDevotionPoints == 5 && resetPlan.IsVerifiedResponse("OK FULL_RESPEC_RESET 100 42 55 5 0"), "full reset plan verifies transaction identity and both conserved point budgets");
Require(!resetPlan.IsVerifiedResponse("OK FULL_RESPEC_RESET 101 42 55 5 0") && !resetPlan.IsVerifiedResponse("OK FULL_RESPEC_RESET 100 42 59 5 0") &&
    !resetPlan.IsVerifiedResponse("OK FULL_RESPEC_RESET 100 42 55 8 0") && !resetPlan.IsVerifiedResponse("OK FULL_RESPEC_RESET 100 42 55 5 1"),
    "reset result rejects mismatched transactions, duplicate refunds and leftover classes");
var catalogNav = mainWindowMarkup.Descendants(presentation + "Button")
    .Single(button => (string?)button.Attribute(xaml + "Name") == "CatalogNav");
var navigationNav = catalogNav.ElementsAfterSelf().First();
Require((string?)navigationNav.Attribute(xaml + "Name") == "NavigationNav" &&
        (string?)navigationNav.Attribute("Click") == "ShowNavigation",
    "Navigation sidebar entry follows Item Catalog and has a page handler");
Require((string?)navigationNav.Attribute("Content") == "Navigation" &&
        navigationNav.Descendants(presentation + "Viewbox").Any(icon =>
            (string?)icon.Attribute(xaml + "Name") == "NavigationCompassIcon" &&
            icon.Descendants(presentation + "Ellipse").Any() &&
            icon.Descendants(presentation + "Path").Count() == 2),
    "Navigation uses a scalable compass ring and needle instead of a font glyph");
Require(navigationPage.Descendants(presentation + "Button").Any(button => (string?)button.Attribute("Click") == "SavePositionBookmark") &&
        navigationPage.Descendants(presentation + "Button").Any(button => (string?)button.Attribute("Click") == "RestorePositionBookmark") &&
        navigationPage.Descendants(presentation + "Button").Any(button => (string?)button.Attribute("Click") == "DeletePositionBookmark"),
    "migrated bookmarks retain existing action handlers");
var blueprintTab = mainWindowMarkup.Descendants(presentation + "TabItem")
    .SingleOrDefault(tab => (string?)tab.Attribute("Header") == "Blueprints");
Require(blueprintTab is not null, "utilities tab is named Blueprints");
var blueprintLayout = blueprintTab!.Element(presentation + "Grid")!;
Require(blueprintLayout.Element(presentation + "Grid.ColumnDefinitions") is null &&
        blueprintLayout.Elements(presentation + "Border").Count() == 1,
    "blueprint browser uses one full-width card");
Require(blueprintTab.Descendants(presentation + "DataGrid").Single().Attribute("ItemsSource")?.Value == "{Binding BlueprintsView}" &&
        blueprintTab.Descendants(presentation + "Button").Single().Attribute("Click")?.Value == "SpawnBlueprint",
    "full-width blueprint list retains selection and spawn workflow");
Require(!mainWindowMarkup.ToString().Contains("Appearance", StringComparison.Ordinal),
    "redundant appearance browser and its bindings are removed");
GameBridgeClient.ValidateNativeApiBindings();
Require(true, "native Windows injection API bindings resolve");
await using (var deadGameBridge = new GameBridgeClient())
{
    typeof(GameBridgeClient).GetProperty(nameof(GameBridgeClient.ProcessId))!.SetValue(deadGameBridge, int.MaxValue);
    Require(await deadGameBridge.UnloadAsync(), "a closed game is treated as an already-unloaded bridge");
    Require(deadGameBridge.ProcessId is null && !deadGameBridge.IsConnected, "stale bridge state is cleared without a pipe handshake");
}
var detectedGame = GameLocator.Locate(args.Length > 1 ? args[1] : null);
Require(detectedGame is not null, "Steam Grim Dawn installation is detected");
var game = detectedGame!;
Require(GameLocator.GetInstalledArchives(game).Count >= 4, "base game and installed expansions are enumerated");

var tags = ArcReader.ReadTags(Path.Combine(game.RootDirectory, "resources", "Text_EN.arc"));
Require(tags.Count > 1_000, "ARC v3 localization parser reads the English tags");
Require(tags.TryGetValue("tagWeaponSwordD001", out var sword) && sword.Contains("Crimson Spike", StringComparison.OrdinalIgnoreCase), "known item localization resolves correctly");
var scripts = ArcReader.ReadTextEntries(Path.Combine(game.RootDirectory, "resources", "Scripts.arc"), name => name.EndsWith(".lua", StringComparison.OrdinalIgnoreCase));
Require(scripts.Count >= 100, "ARC reader loads installed Lua scripts without extracting them");
var tokens = await new QuestTokenCatalogService().BuildAsync(game);
Require(tokens.Count >= 1_000, "quest-token catalog is rebuilt from installed base-game and DLC scripts");
Require(tokens.Any(x => x.Name == "DC_REANIMATOR_KILLED"), "known campaign token is discovered from script references");
var catalog = await CatalogFirstRunRegression.Run(root, game, Require);
Require(catalog is not null && catalog.SchemaVersion == CatalogService.CurrentSchema, "locally generated catalog uses the current schema");
Require(catalog!.Items.Where(i=>i.RecordPath.Contains("/defaultgear/") || i.RecordPath.Contains("/npcgear/")).All(i=>i.Name.EndsWith(" (Internal)")),"all generated default and NPC gear is clearly labeled internal");
Require(catalog.Items.Single(i=>i.RecordPath=="records/creatures/pc/defaultgear/a00f_torso01.dbr").Name=="Default Female Torso Clothing (Internal)","generated A00f Torso01 is relabeled descriptively");
Require(catalog.Items.Single(i=>i.RecordPath=="records/items/enchants/a000a_enchant.dbr").Name=="Enchantment — A000A (Name unavailable)","generated A000a Enchant no longer uses its filename as an item name");
Require(catalog.Items.Where(i=>i.NameUnavailable).All(i=>i.Name.EndsWith("(Name unavailable)") || i.Name.EndsWith("(Internal)")),"all unlocalized generated item types have explicit labels");
Require(catalog.Items.Single(i=>i.RecordPath=="records/items/materia/compa_amber.dbr").Name=="Amber","installed Amber is not misclassified as an unnamed component");
Require(catalog!.AffixRecords.Count>1_000,"generated catalog includes installed affix records and loot compatibility tables");
var installedAffixes=new ItemAffixIndex(catalog.AffixRecords);
var basicAffixItem=catalog.Items.Single(i=>i.RecordPath=="records/items/gearhead/a01_head003.dbr");
Require(installedAffixes.Choices(basicAffixItem,true).Count>1 && installedAffixes.Choices(basicAffixItem,false).Count>1,"ordinary equipment offers both prefix and suffix pools");
var badgeAffixItem=catalog.Items.Single(i=>i.RecordPath=="records/items/gearaccessories/medals/d002_medal.dbr");
Require(installedAffixes.Choices(badgeAffixItem,true).Any(a=>a.RecordPath.Contains("prefixunique")) && installedAffixes.Choices(badgeAffixItem,false).Any(a=>a.RecordPath.Contains("suffixunique")),"Badge of Mastery exposes its special crafted affixes");
Require(installedAffixes.Choices(badgeAffixItem,true).Any(a=>a.Name=="+3 to Blade Arc") && installedAffixes.Choices(badgeAffixItem,false).Any(a=>a.Name=="+2 to Devouring Swarm"),"installed Badge of Mastery affixes show actual readable skill bonuses");
Require(catalog.AffixRecords.Where(a=>a.Class=="LootRandomizer").All(a=>!string.IsNullOrWhiteSpace(a.Name) && !a.Name.Contains(".dbr") && !a.Name.StartsWith("tag",StringComparison.OrdinalIgnoreCase)),"all installed affixes have display labels instead of raw record paths or unresolved localization tags");
Require(catalog!.QuestRecordAssociations.TryGetValue("records/creatures/npcs/questnpcs/npc_barnabas_01.dbr", out var barnabasQuests) &&
        barnabasQuests.Contains("quests/mq_helpingout.qst", StringComparer.OrdinalIgnoreCase),
    "catalog retains Barnabas's Helping Out quest association");
Require(catalog.QuestRecordAssociations.TryGetValue("records/creatures/npcs/questnpcs/npc_kasparov_01.dbr", out var kasparovQuests) &&
        kasparovQuests.Contains("quests/mq_helpingout.qst", StringComparer.OrdinalIgnoreCase),
    "catalog retains Kasparov's Helping Out quest association");
MapIndexRegressionTests.Run();
QuestNavigationRegressionTests.Run();
var taskAwareMapFingerprint = await QuestNavigationRegressionTests.RunInstalled(game, catalog);
MinimapGeometryRegressionTests.Run();
var worldMarkers = await new WorldMarkerCatalogService().BuildAsync(game, catalog.QuestRecordAssociations);
Require(worldMarkers.SourceFingerprint == taskAwareMapFingerprint, "quest metadata upgrades preserve existing bookmark map compatibility");
Console.WriteLine($"INFO  {worldMarkers.Markers.Count:N0} world markers decoded");
Require(worldMarkers.Markers.Count >= 50, "world marker catalog is rebuilt from installed level placements");
Require(worldMarkers.Markers.Count(x => x.Category == "Devotion Shrine") >= 10, "world marker catalog contains decoded devotion shrine coordinates");
Require(worldMarkers.Markers.Any(x => x.LevelPath.Equals("Levels/Region0B003.lvl", StringComparison.OrdinalIgnoreCase) &&
                                      x.Category == "Devotion Shrine" &&
                                      Math.Abs(x.WorldX + 1071.57772f) < 0.01f && Math.Abs(x.WorldZ - 585.84719f) < 0.01f),
    "world marker catalog applies signed global level origins to local placements");
Require(worldMarkers.Markers.Any(x => x.Category == "Quest Objective" && x.QuestPaths is { Length: > 0 }), "world marker catalog associates placed quest targets with quest records");
Require(worldMarkers.Markers.Any(x => x.RecordPath.Equals("records/creatures/npcs/questnpcs/npc_kasparov_01.dbr", StringComparison.OrdinalIgnoreCase) &&
                                      x.QuestPaths?.Contains("quests/mq_helpingout.qst", StringComparer.OrdinalIgnoreCase) == true &&
                                      Math.Abs(x.WorldX - 98.32786f) < 0.01f && Math.Abs(x.WorldZ - 27.171303f) < 0.01f),
    "world marker catalog decodes Kasparov coordinates matching the independent live game sample");
Require(worldMarkers.Markers.Any(x => x.RecordPath.Equals("records/creatures/npcs/questnpcs/npc_barnabas_01.dbr", StringComparison.OrdinalIgnoreCase) &&
                                      x.QuestPaths?.Contains("quests/mq_helpingout.qst", StringComparer.OrdinalIgnoreCase) == true &&
                                      Math.Abs(x.WorldX - 98.93785f) < 0.01f && Math.Abs(x.WorldZ - 76.79735f) < 0.01f),
    "world marker catalog decodes group-linked Barnabas quest coordinates");
Require(worldMarkers.Markers.All(x => !x.Id.Contains("|region-anchor|", StringComparison.OrdinalIgnoreCase)),
    "world marker catalog never substitutes a level origin for a dynamic quest objective");
Require(worldMarkers.Markers.Any(x => x.Category == "Travel Utility" && x.DisplayName == "Row Boat" &&
                                      Math.Abs(x.WorldX - 12.929131f) < 0.01f && Math.Abs(x.WorldZ + 32.815216f) < 0.01f),
    "world marker catalog row boat matches the independent live sample across a different level origin");
Require(worldMarkers.SourceFingerprint.Length == 64, "world marker catalog records its update fingerprint");
var helpingOutFromLowerCrossing = NavigationOverlayProjection.Build(new WorldPosition(61.9f, 0, -127.3f),
    worldMarkers.Markers, ["quests/mq_helpingout.qst"], true, false, 45, "Levels/Region0A001.lvl");
Require(helpingOutFromLowerCrossing.Any(x => x.Marker.RecordPath.EndsWith("npc_kasparov_01.dbr", StringComparison.OrdinalIgnoreCase)) &&
        helpingOutFromLowerCrossing.Any(x => x.Marker.RecordPath.EndsWith("npc_barnabas_01.dbr", StringComparison.OrdinalIgnoreCase)),
    "installed Helping Out targets project from the live Lower Crossing position before their region is loaded");
QuestMarkerHandoffRegressionTests.Run(worldMarkers.Markers);

var projectionPlayer = new WorldPosition(100, 0, 100);
var projectionMarkers = new[]
{
    new WorldMarkerRecord("east", "Vendor", "live://east", "live://current-map", 120, 0, 100, ["$live"]),
    new WorldMarkerRecord("south", "Vendor", "live://south", "live://current-map", 100, 0, 80, ["$live"]),
    new WorldMarkerRecord("far-quest", "Quest Objective", "live://far-quest", "live://current-map", 300, 0, 100, ["$live"]),
    new WorldMarkerRecord("ghost", "Vendor", "records/ghost.dbr", "Levels/OtherMap.lvl", 101, 0, 100),
    new WorldMarkerRecord("nearby-boat", "Travel Utility", "records/boat.dbr", "Levels/Region0A010.lvl", 102, 0, 100, DisplayName: "Row Boat"),
    new WorldMarkerRecord("regional-quest", "Quest Objective", "records/regional.dbr", "Levels/Region0A071.lvl", 600, 0, 100, ["quests/test.qst"]),
    new WorldMarkerRecord("other-region-quest", "Quest Objective", "records/other.dbr", "Levels/Region0B001.lvl", 110, 0, 100, ["quests/test.qst"]),
    new WorldMarkerRecord("distant", "Riftgate", "records/distant.dbr", "Levels/Test.lvl", 100, 0, -100)
};
var projected = NavigationOverlayProjection.Build(projectionPlayer, projectionMarkers, [], false, true, 40);
var east = projected.Single(x => x.Marker.Id == "east");
var south = projected.Single(x => x.Marker.Id == "south");
Require(east.HorizontalRatio > 0 && east.VerticalRatio > 0 &&
        Math.Abs(east.HorizontalRatio - east.VerticalRatio) < 0.001,
    "navigation projection maps increasing world X diagonally down-right");
Require(south.HorizontalRatio > 0 && south.VerticalRatio < 0 &&
        Math.Abs(south.HorizontalRatio + south.VerticalRatio) < 0.001,
    "navigation projection maps decreasing world Z diagonally up-right");
Require(projected.Single(x => x.Marker.Id == "distant").IsDistant,
    "navigation projection retains nearest distant POIs as rim directions");
Require(projected.All(x => x.Marker.Id != "ghost"),
    "navigation projection excludes nearby catalog POIs that may belong to another map");
Require(projected.Any(x => x.Marker.Id == "nearby-boat"),
    "navigation projection includes nearby catalog travel utilities missing from the live minimap");
var projectedQuests = NavigationOverlayProjection.Build(projectionPlayer, projectionMarkers, ["quests/test.qst"], true, false, 40);
Require(projectedQuests.Any(x => x.Marker.Id == "far-quest" && x.IsDistant),
    "navigation projection draws distant live quest objectives at the radar rim");
Require(projectedQuests.Any(x => x.Marker.Id == "regional-quest" && x.IsDistant),
    "navigation projection supplements live stars across the player's entire connected map region");
Require(projectedQuests.All(x => x.Marker.Id != "other-region-quest"),
    "navigation projection excludes active objectives from unrelated map regions with overlapping coordinates");
var duplicateQuestMarkers = new[]
{
    new WorldMarkerRecord("live-barnabas", "Quest Objective", "live://quest", "live://current-map",
        90, 0, 95, ["$live"], "Barnabas"),
    new WorldMarkerRecord("catalog-barnabas", "Quest Objective", "records/npc_barnabas_01.dbr", "Levels/Region0A001.lvl",
        300, 0, -200, ["quests/test.qst"], "Barnabas")
};
var deduplicatedQuest = NavigationOverlayProjection.Build(projectionPlayer, duplicateQuestMarkers,
    ["quests/test.qst"], true, false, 40, "Levels/Region0A001.lvl");
Require(deduplicatedQuest.Count == 1 && deduplicatedQuest[0].Marker.Id == "live-barnabas",
    "navigation projection prefers a verified live objective over its named catalog copy");
var genericLiveQuestMarkers = new[]
{
    new WorldMarkerRecord("live-generic-1", "Quest Objective", "live://quest/1", "live://current-map",
        90, 0, 95, ["$live"], "Active quest objective"),
    new WorldMarkerRecord("live-generic-2", "Quest Objective", "live://quest/2", "live://current-map",
        95, 0, 110, ["$live"], "Active quest objective"),
    new WorldMarkerRecord("catalog-current-1", "Quest Objective", "records/current-1.dbr", "Levels/Region0A001.lvl",
        90, 0, 95, ["quests/test.qst"], "Barnabas"),
    new WorldMarkerRecord("catalog-current-2", "Quest Objective", "records/current-2.dbr", "Levels/Region0A001.lvl",
        95, 0, 110, ["quests/test.qst"], "Kasparov"),
    new WorldMarkerRecord("catalog-distant-tile", "Quest Objective", "records/distant-tile.dbr", "Levels/Region0A071.lvl",
        600, 0, 100, ["quests/test.qst"], "A distant objective")
};
var genericLiveDeduplicated = NavigationOverlayProjection.Build(projectionPlayer, genericLiveQuestMarkers,
    ["quests/test.qst"], true, false, 40, "Levels/Region0A001.lvl");
Require(genericLiveDeduplicated.Count == 3 &&
        genericLiveDeduplicated.Count(x => x.Marker.Id.StartsWith("live-generic", StringComparison.Ordinal)) == 2 &&
        genericLiveDeduplicated.Any(x => x.Marker.Id == "catalog-distant-tile") &&
        genericLiveDeduplicated.All(x => !x.Marker.Id.StartsWith("catalog-current", StringComparison.Ordinal)),
    "native generic stars match world coordinates without hiding objectives in distant region tiles");
var taskLabeledNeighboringTileMarkers = genericLiveQuestMarkers
    .Where(x => x.Id != "catalog-distant-tile")
    .Select(x => x.Id switch
    {
        "live-generic-1" => x with { DisplayName = "Help Barnabas with the Water Pump" },
        "live-generic-2" => x with { DisplayName = "Help Kasparov with his research" },
        _ => x
    }).ToArray();
var neighboringTileLiveDeduplicated = NavigationOverlayProjection.Build(projectionPlayer,
    taskLabeledNeighboringTileMarkers,
    ["quests/test.qst"], true, false, 40, "Levels/Region0A002.lvl");
Require(neighboringTileLiveDeduplicated.Count == 2 &&
        neighboringTileLiveDeduplicated.All(x => x.Marker.Id.StartsWith("live-generic", StringComparison.Ordinal)),
    "a complete task-labeled native quest set replaces catalog copies stored in a neighboring region tile");
var oneLabeledQuest = NavigationOverlayProjection.Build(projectionPlayer,
    [duplicateQuestMarkers[0] with { DisplayName = "Barnabas {^y}Helping Out" },
     duplicateQuestMarkers[1],
     new WorldMarkerRecord("unseen-kasparov", "Quest Objective", "records/kasparov.dbr",
         "Levels/Region0A001.lvl", 350, 0, -150, ["quests/test.qst"], "Kasparov")],
    ["quests/test.qst"], true, false, 40, "Levels/Region0A002.lvl");
Require(oneLabeledQuest.Count == 2 && oneLabeledQuest.Any(x => x.Marker.Id == "live-barnabas") &&
        oneLabeledQuest.Any(x => x.Marker.Id == "unseen-kasparov"),
    "one labeled live objective suppresses only its own catalog copy while retaining an unseen target");
var unrelatedGenericQuest = NavigationOverlayProjection.Build(projectionPlayer,
    [genericLiveQuestMarkers[0], duplicateQuestMarkers[1]], ["quests/test.qst"], true, false, 40,
    "Levels/Region0A001.lvl");
Require(unrelatedGenericQuest.Count == 2,
    "a generic live star cannot suppress an unrelated catalog objective based on marker count");
var ambiguousNamedQuest = NavigationOverlayProjection.Build(projectionPlayer,
    [duplicateQuestMarkers[0], duplicateQuestMarkers[1], duplicateQuestMarkers[1] with
        { Id = "second-barnabas", WorldX = 500 }], ["quests/test.qst"], true, false, 40,
    "Levels/Region0A001.lvl");
Require(ambiguousNamedQuest.Count == 3,
    "ambiguous objective names cannot suppress multiple distinct catalog destinations");
var cappedLiveQuests = NavigationOverlayProjection.Build(projectionPlayer,
    Enumerable.Range(0, 70).Select(index => genericLiveQuestMarkers[0] with
        { Id = "live-cap-" + index, WorldX = index * 10 }), [], true, false, 40);
Require(cappedLiveQuests.Count == 64,
    "navigation projection caps live quest targets even when a map pass exceeds the limit");

NavigationCacheRegressionTests.Run();

var questCache = new RegionalQuestMarkerCache();
var barnabas = new LiveMapMarkerRecord(21, new WorldPosition(64, 0, -128), "Barnabas");
var kasparov = new LiveMapMarkerRecord(21, new WorldPosition(92, 0, -112), "Kasparov");
var genericGoldGlyph = new LiveMapMarkerRecord(14, new WorldPosition(20, 0, -20), "Road Block");
var genericGoldGlyphs = questCache.Update("levels/region0a", ["quests/mq_helpingout.qst"], [genericGoldGlyph],
    new WorldPosition(0, 0, 0), 120);
Require(genericGoldGlyphs.Count == 0, "regional quest cache rejects generic gold minimap glyphs as quest objectives");
var cached = questCache.Update("levels/region0a", ["quests/mq_helpingout.qst"], [barnabas, kasparov],
    new WorldPosition(70, 0, -120), 120);
Require(cached.Count == 2, "regional quest cache captures every exact live objective in the current region");
cached = questCache.Update("levels/region0a", ["quests/mq_helpingout.qst"], [barnabas],
    new WorldPosition(500, 0, 500), 120);
Require(cached.Count == 2, "regional quest cache retains unloaded objectives while the player travels farther away");
cached = questCache.Update("levels/region0a", ["quests/mq_helpingout.qst"], [barnabas],
    new WorldPosition(92, 0, -112), 120);
Require(cached.Count == 1, "regional quest cache expires missing objectives inside the authoritative nearby range");
cached = questCache.Update("levels/region0a", ["quests/another.qst"], [barnabas],
    new WorldPosition(64, 0, -128), 120);
Require(cached.Count == 1, "regional quest cache resets when the active quest set changes");

var profile = Path.Combine(root, "build_test", "compatibility-test.json");
var compatibility = await new CompatibilityService(profile).VerifyAndUpdateAsync(game);
var bookmarkSymbols = compatibility.Symbols.Where(x => x.FriendlyName.StartsWith("Bookmark ", StringComparison.Ordinal)).ToArray();
Require(bookmarkSymbols.Length == 19 && bookmarkSymbols.All(x => x.Found && x.Fingerprint.Length == 64),
    "all persistent bookmark identity readiness and travel symbols resolve without executing game code");
Require(!compatibility.Symbols.Any(x => x.FriendlyName == "GameEngine::SaveGame()"), "unused SaveGame export is not included in compatibility verification");
Require(compatibility.Symbols.Any(x => x.FriendlyName == "Character::SetInvincible(bool)"), "invincibility export remains available for planned character utility support");
Require(compatibility.CanConnect, "required Lua bridge exports resolve in executable sections");
Require(compatibility.Symbols.Where(x => x.Required).All(x => x.Rva > 0 && x.Fingerprint.Length == 64), "required symbol fingerprints are recorded");
Require(compatibility.Symbols.Any(x => x.FriendlyName == "GameEngine global instance" && x.Found), "native utility instance export is verified in readable data memory");
Require(compatibility.Symbols.Where(x => x.FriendlyName.StartsWith("Character::", StringComparison.Ordinal) || x.FriendlyName.StartsWith("Player::", StringComparison.Ordinal)).All(x => x.Found), "character utility exports resolve for this game patch");
var navigationSymbols = new[]
{
    "Entity::GetCoords()",
    "World::GetRegionContainingXZ()", "WorldVec3::SetFromWorldPosition()", "Region::GetLoadFileName()",
    "GameEngine::GetDetailMapData()", "AreaOfInterest::AppendDetailMapData()", "Player::IsMarkerUIDKnown()",
    "Quest2Repository::GetQuests()", "Quest2::GetFileName()", "AscendantAltar::IsOfInterest()", "DynamicTeleporter::IsOfInterest()",
    "FixedDoor::IsOfInterest()", "FixedDoor::UpdateSelf(int)", "FixedDoor::GetGameDescription()",
    "FixedItemContainer::IsOfInterest()", "FixedItemShrine::IsOfInterest()", "MonsterShrine::IsOfInterest()",
    "StaticShrine::IsOfInterest()", "StaticTeleporter::IsOfInterest()", "StaticTeleporter::AppendDetailMapData()"
};
Require(navigationSymbols.All(name => compatibility.Symbols.Any(x => x.FriendlyName == name && x.Found)), "external radar data exports resolve for this game patch");
var assistNativeSource = File.ReadAllText(Path.Combine(root, "src", "GrimDawnBridge", "GameplayAssists.h"));
var gameExports = new PeExportReader(game.GameDllPath);
var assistHookMatches = System.Text.RegularExpressions.Regex.Matches(assistNativeSource,
    "\\{ \\\"([^\\\"]+)\\\", reinterpret_cast<void\\*>.*?, (0x[0-9A-Fa-f]+), (0x[0-9A-Fa-f]+) \\}");
Require(assistHookMatches.Count == 4, "all four assist hooks have explicit code fingerprints");
foreach (System.Text.RegularExpressions.Match hook in assistHookMatches)
{
    var export = gameExports.Find(hook.Groups[1].Value);
    uint hash = 2166136261;
    if (export is not null) foreach (var value in export.Prologue.Take(32)) hash = unchecked((hash ^ value) * 16777619);
    Require(export is { IsExecutable: true } && export.Rva == Convert.ToUInt32(hook.Groups[2].Value[2..], 16) &&
        hash == Convert.ToUInt32(hook.Groups[3].Value[2..], 16), "assist hook fingerprint matches installed game without executing game code");
}
await using (var assistCapabilityClient = new GameBridgeClient())
{
    var parse = typeof(GameBridgeClient).GetMethod("ParseHandshake", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
    parse.Invoke(assistCapabilityClient, ["PONG 23 READY CAPS=GAMEPLAY_ASSISTS"]);
    Require(!assistCapabilityClient.SupportsGameplayAssists, "old bridge cannot enable new assists");
    parse.Invoke(assistCapabilityClient, ["PONG 24 READY CAPS=LUA_SYNC"]);
    Require(!assistCapabilityClient.SupportsGameplayAssists, "new protocol still needs verified native assist capability");
    parse.Invoke(assistCapabilityClient, ["PONG 24 READY CAPS=GAMEPLAY_ASSISTS"]);
    Require(assistCapabilityClient.SupportsGameplayAssists, "new verified assist capability is recognized");
}

Require(catalog is not null && catalog.Items.Count >= 10_000, "generated catalog contains at least 10,000 item records");
Require(catalog!.Items.Select(x => x.RecordPath).Distinct(StringComparer.OrdinalIgnoreCase).Count() == catalog.Items.Count, "catalog DBR paths are unique");
Require(catalog.Items.All(x => x.RecordPath.StartsWith("records/", StringComparison.Ordinal) && x.RecordPath.EndsWith(".dbr", StringComparison.Ordinal)), "all catalog entries are normalized DBR paths");
Require(catalog.Items.All(x => !x.Name.Contains('^')), "localization color markup is removed from display names");
Require(catalog.Items.Where(x => x.Category.Contains("Off-", StringComparison.Ordinal)).All(x => x.Category == "Weapons & Off-Hands"), "hyphenated catalog categories use consistent title capitalization");
Require(catalog.Items.Any(x => x.Name == "Crimson Spike" && x.RecordPath == "records/items/gearweapons/swords1h/d001_sword.dbr"), "known legendary item appears with the expected name and path");
var fingerprint = await CatalogService.ComputeSourceFingerprintAsync(game);
Require(string.Equals(fingerprint, catalog.SourceFingerprint, StringComparison.OrdinalIgnoreCase), "catalog fingerprint matches every installed source archive");

Console.WriteLine($"ALL TESTS PASSED — {catalog.Items.Count:N0} items, {tokens.Count:N0} tokens, {worldMarkers.Markers.Count:N0} world markers, {compatibility.Symbols.Count(x => x.Found)} verified symbols.");
}
catch (Exception exception)
{
    Console.Error.WriteLine("TEST FAILED: " + exception);
    Environment.ExitCode = 1;
}
