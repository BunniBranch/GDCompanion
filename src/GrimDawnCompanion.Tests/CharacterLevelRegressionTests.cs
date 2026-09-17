using GrimDawnCompanion.Core;
using System.Xml.Linq;

internal static class CharacterLevelRegressionTests
{
    public static void Run(string root, Action<bool, string> require)
    {
        var prepared = CharacterLevelPreparation.Parse("OK LEVEL_PREPARED 7 10 100 100 99 0054006500730074");
        require(prepared is { Current: 10, Target: 100, Cap: 100, Name: "Test" }, "character level confirmation binds name, identity and campaign cap");
        foreach (var invalid in new[] {
            "OK LEVEL_PREPARED 7 10 1001 1001 99 0054", "OK LEVEL_PREPARED 7 10 9 200 99 0054",
            "OK LEVEL_PREPARED 7 10 10 200 99 0054", "OK LEVEL_PREPARED 0 10 20 200 99 0054",
            "OK LEVEL_PREPARED 7 10 150 100 99 0054", "OK LEVEL_PREPARED 7 10 20 200 99 000A" })
        {
            try { CharacterLevelPreparation.Parse(invalid); }
            catch (InvalidDataException) { continue; }
            throw new InvalidOperationException("Invalid character level preparation was accepted.");
        }
        require(true, "managed level preparation rejects unsupported, lowered, stale and malformed confirmations");
        var doc = XDocument.Load(Path.Combine(root, "src/GrimDawnCompanion.App/MainWindow.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var button = doc.Descendants().Single(e => (string?)e.Attribute(x + "Name") == "SetCharacterLevelButton");
        require(button.Ancestors().Any(e => (string?)e.Attribute("Header") == "Character Resources") &&
            (string?)button.Attribute("Click") == "SetCharacterLevel", "level control is under Character Utilities / Character Resources");
        var code = File.ReadAllText(Path.Combine(root, "src/GrimDawnCompanion.App/MainWindow.CharacterLevel.cs"));
        require(code.IndexOf("PrepareCharacterLevelAsync", StringComparison.Ordinal) < code.IndexOf("MessageBox.Show($", StringComparison.Ordinal) &&
            code.IndexOf("MessageBoxResult.No", StringComparison.Ordinal) < code.IndexOf("CommitCharacterLevelAsync", StringComparison.Ordinal) &&
            code.Contains("Cloud saving is not a backup", StringComparison.Ordinal), "level setter inspects before its default-No backup confirmation");
        var client = new GameBridgeClient();
        try
        {
            typeof(GameBridgeClient).GetProperty(nameof(GameBridgeClient.ProtocolVersion))!.SetValue(client, 36);
            typeof(GameBridgeClient).GetProperty(nameof(GameBridgeClient.Capabilities))!.SetValue(client,
                new HashSet<string> { "CHARACTER_LEVEL", "RESOURCE_SET", "LOADED_PROGRESSION", "QUEST_TASKS", "QUEST_TASKS_LIVE" });
            require(client.SupportsCharacterLevel && client.SupportsLiveQuestTasks, "new level and live-quest features require their native capabilities");
            foreach (var invalid in new uint[] { 0, 1, 1001, uint.MaxValue })
            {
                try { client.PrepareCharacterLevelAsync(invalid).GetAwaiter().GetResult(); }
                catch (ArgumentOutOfRangeException) { continue; }
                throw new InvalidOperationException("Invalid target reached bridge transport.");
            }
            typeof(GameBridgeClient).GetProperty(nameof(GameBridgeClient.ProtocolVersion))!.SetValue(client, 32);
            require(!client.SupportsLiveQuestTasks, "protocol 32 cannot claim the fixed active-quest repository filtering");
            typeof(GameBridgeClient).GetProperty(nameof(GameBridgeClient.ProtocolVersion))!.SetValue(client, 31);
            require(!client.SupportsCharacterLevel && !client.SupportsLiveQuestTasks, "old bridge cannot advertise live quest reliability or change levels");
        }
        finally { client.DisposeAsync().AsTask().GetAwaiter().GetResult(); }

        const string region = "levels/world/region0a/area.lvl";
        var target = new WorldMarkerRecord("far", "Quest Objective", "records/target.dbr", region,
            0, 0, 0, ["quests/test.qst"], "Tracked target", [new("quests/test.qst", 1)]);
        var state = QuestNavigationState.Parse("OK QUEST_TASKS 1\tquests/test.qst|1")!;
        foreach (var distance in new[] { 50f, 200f, 2000f, 50000f })
        {
            var player = new WorldPosition(distance, 0, 0);
            var points = NavigationOverlayProjection.Build(player, [target], ["quests/test.qst"], true, false, 32,
                region, new MinimapProjection(distance, 0, 0, 0, .03, 0, 0, .03), state);
            require(points.Count == 1 && points[0].IsDistant && Math.Abs(points[0].HorizontalRatio + 1.05) < .00001,
                $"tracked quest persists {distance} world units away with no live or previously discovered marker");
        }
        require(NavigationOverlayProjection.Build(new(2000, 0, 0), [target], ["quests/test.qst"], true, false, 32,
            region, questState: QuestNavigationState.Parse("OK QUEST_TASKS 1\tquests/test.qst|2")).Count == 0,
            "long-range objective is removed when the task changes, not when it leaves render range");
    }
}
