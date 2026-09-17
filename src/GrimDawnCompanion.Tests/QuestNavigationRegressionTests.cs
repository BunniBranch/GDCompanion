using GrimDawnCompanion.Core;

internal static class QuestNavigationRegressionTests
{
    private static void Check(bool value, string message)
    {
        if (!value) throw new Exception("FAIL " + message);
        Console.WriteLine("PASS " + message);
    }

    public static void Run()
    {
        var records = new Dictionary<string, QuestNavigationRecord>();
        void Add(string path, params string[] lines)
        {
            var record = QuestNavigationIndex.ReadRecord(path, lines, new Dictionary<string, string>())!;
            records[record.RecordPath] = record;
        }
        Add("records/npc.dbr", "questFile1,quests/any.qst,", "taskUID1,-1,");
        Add("records/boss.dbr", "questFile1,quests/any.qst,", "taskUID1,42,");
        Add("records/pool.dbr", "templateName,database/templates/proxypool.tpl,", "name1,records/boss.dbr,");
        Add("records/championpool.dbr", "templateName,database/templates/proxypool.tpl,", "nameChampion1,records/boss.dbr,");
        Add("records/epicproxy.dbr", "Class,Proxy,", "poolEpic1,records/championpool.dbr,");
        Add("records/legendaryproxy.dbr", "Class,Proxy,", "poolLegendary1,records/championpool.dbr,");
        Add("records/proxy.dbr", "Class,Proxy,", "pool1,records/pool.dbr,", "lootMisc1Item1,records/npc.dbr,");
        Add("records/script.dbr", "Class,ScriptEntity,", "onAddToWorld,gd.npc.onAdd,");
        const string script = """
            local states = orderedTable()
            states["MOVED"] = { state = 1, dbr = nil }
            states[""] = { state = 0, dbr = "records/npc.dbr" }
            function gd.npc.onAdd(objectId)
                if Server then
                    local userdata = {}
                    TokenStateBasedObjectSwap(objectId, userdata, states)
                end
            end
            """;
        var resolved = QuestNavigationIndex.Resolve(records, [script]);
        Check(resolved["records/proxy.dbr"].Single().RecordPath == "records/boss.dbr", "generic proxy/pool resolution follows spawn links, never loot references");
        Check(resolved["records/epicproxy.dbr"].Single().RecordPath == "records/boss.dbr" &&
              resolved["records/legendaryproxy.dbr"].Single().RecordPath == "records/boss.dbr",
            "difficulty-specific pools and champion spawn records use the shared resolver");
        var target = resolved["records/script.dbr"].Single();
        Check(target.Bindings.Single().TaskUid == uint.MaxValue && target.ExcludedTokens.SequenceEqual(["MOVED"]),
            "scripted NPC uses ordered token precedence and preserves signed DBR task bits");
        var stage = QuestNavigationState.Parse("OK QUEST_TASKS 1\tquests/any.qst|4294967295")!;
        var marker = new WorldMarkerRecord("npc", "Quest Objective", "records/npc.dbr", "Levels/Region0A001.lvl",
            1000, 0, 2000, ["quests/any.qst"], "NPC", target.Bindings, target.RequiredTokens, target.ExcludedTokens);
        Check(!stage.Allows(marker), "unknown token state never selects a speculative NPC location");
        stage.Tokens["MOVED"] = false;
        Check(stage.Allows(marker), "current quest stage selects NPC before its object has streamed in");
        var points = NavigationOverlayProjection.Build(new(0, 0, 0), [marker], [], true, false, 40,
            "Levels/Region0A090.lvl", questState: stage);
        Check(points.Count == 1 && points[0].IsDistant, "task-qualified markers cover the whole connected region without a discovery radius");
        var killed = QuestNavigationState.Parse("OK QUEST_TASKS 1\tquests/any.qst|42")!;
        Check(!killed.Allows(marker) && stage.Signature != killed.Signature, "task changes invalidate cached bearings even when the quest path stays the same");
        stage.Tokens["MOVED"] = true;
        Check(!stage.Allows(marker), "progression token removes old NPC spawn instead of leaving a ghost");
        Check(QuestNavigationState.Parse("OK QUEST_TASKS 0")!.Tasks.Count == 0 &&
              QuestNavigationState.Parse("OK QUEST_TASKS WAITING") is null, "empty and unavailable quest snapshots remain distinct");
        foreach (var invalid in new[] { "OK QUEST_TASKS 2\tquests/a.qst|1", "OK QUEST_TASKS 1\tquests/a.qst|-1", "OK QUEST_TASKS 1\tquests/a.qst|4294967296" })
        {
            try { QuestNavigationState.Parse(invalid); throw new Exception("Malformed snapshot accepted"); }
            catch (InvalidDataException) { }
        }
        Check(QuestNavigationIndex.ReadScriptSpawns(script.Replace("objectId, userdata, states)", "objectId, userdata, states, Monster, args, true, override)")).Count == 0,
            "unsupported script overrides are not promoted into guessed distant markers");
        Check(QuestNavigationIndex.ReadScriptSpawns(script.Replace("if Server then", "if SomeOtherQuestCondition then")).Count == 0,
            "unresolved script control flow cannot produce a phantom distant NPC");
        var trailingCondition = script.Replace(
            "TokenStateBasedObjectSwap(objectId, userdata, states)",
            "TokenStateBasedObjectSwap(objectId, userdata, states)\nif UnrelatedQuestCondition then DoQuestWork() end");
        Check(QuestNavigationIndex.ReadScriptSpawns(trailingCondition).Count == 1,
            "unrelated later quest logic does not hide an unconditional token-state spawn");
        records["records/pool.dbr"] = records["records/pool.dbr"] with { Children = ["records/proxy.dbr", "records/boss.dbr"] };
        Check(QuestNavigationIndex.Resolve(records, [script])["records/proxy.dbr"].Length == 1, "cyclic spawn references are bounded");
    }

    public static async Task<string> RunInstalled(GameInstallation game, CatalogDocument catalog)
    {
        var world = await new WorldMarkerCatalogService().BuildAsync(game, catalog.QuestRecordAssociations,
            navigationRecords: catalog.QuestNavigationRecords);
        var quests = world.Markers.Where(marker => marker.QuestTargets is { Length: > 0 }).ToArray();
        Console.WriteLine($"INFO Task-qualified navigation: {quests.Length} placed targets, {quests.SelectMany(m => m.QuestTargets!).Select(t => t.QuestPath).Distinct().Count()} quest files");
        const string waking = "quests/mq_wakingtomisery.qst";
        var returnStage = QuestNavigationState.Parse("OK QUEST_TASKS 1\t" + waking + "|1064558272")!;
        foreach (var token in returnStage.NeededTokens(quests)) returnStage.Tokens[token] = false;
        var outside = NavigationOverlayProjection.Build(new(20, 0, -137.2f), quests, [], true, false, 32.7,
            "Levels/Region0A020.lvl", questState: returnStage);
        Check(outside.Count == 1 && outside[0].Marker.RecordPath.EndsWith("npc_johnbourbon_01.dbr") && outside[0].IsDistant,
            "installed return-to-Captain stage shows exactly one Bourbon direction before visiting him");
        returnStage.Tokens["DC_BOURBON_OFFICE"] = true;
        var office = NavigationOverlayProjection.Build(new(20, 0, -137.2f), quests, [], true, false, 32.7,
            "Levels/Region0A020.lvl", questState: returnStage);
        Check(office.Count == 1 && office[0].Marker.Id != outside[0].Marker.Id, "installed Bourbon relocation selects the office and removes the intro location");
        returnStage.Tokens["AREAE_NPCS"] = true;
        var expansion = NavigationOverlayProjection.Build(new(20, 0, -137.2f), quests, [], true, false, 32.7,
            "Levels/Region0A020.lvl", questState: returnStage);
        Check(expansion.Count == 1 && expansion[0].Marker.RecordPath.EndsWith("gdareae/npc_johnbourbon_02.dbr"),
            "later expansion token takes precedence over both earlier Captain locations");
        var killStage = QuestNavigationState.Parse("OK QUEST_TASKS 1\t" + waking + "|" + unchecked((uint)-1533546496))!;
        var cave = NavigationOverlayProjection.Build(new(0, 0, 0), quests, [], true, false, 32.7,
            "Levels/Undergrounds/RegionUG_CaveBurial_A01.lvl", questState: killStage);
        Check(cave.Any(p => p.Marker.RecordPath.EndsWith("/reanimator.dbr")), "installed Reanimator resolves through its proxy before approaching the target");
        Check(NavigationOverlayProjection.Build(new(0, 0, 0), quests, [], true, false, 32.7,
            "Levels/Undergrounds/RegionUG_CaveBurial_A01.lvl", questState: returnStage).Count == 0,
            "return stage removes Reanimator and does not project the captain into overlapping dungeon coordinates");
        Check(quests.SelectMany(m => m.QuestTargets!).Select(t => t.QuestPath).Distinct().Count() > 40,
            "shared resolver covers campaign and expansion quest data rather than a named quest exception");
        var liveSession = QuestNavigationState.Parse("OK QUEST_TASKS 4\tquests/sq_somethingfornothing.qst|596675072\tquests/mq_waterpump.qst|1352658176\tquests/mq_helpingout.qst|2205221632\tquests/sq_lost_apprentice.qst|4055106304")!;
        foreach (var token in liveSession.NeededTokens(quests)) liveSession.Tokens[token] = false;
        var liveDirections = NavigationOverlayProjection.Build(new(20, 0, -137.2f), quests, [], true, false, 32.7,
            "Levels/Region0A020.lvl", questState: liveSession);
        Check(liveDirections.Any(p => p.Marker.RecordPath.EndsWith("npc_barnabas_01.dbr") && p.IsDistant) &&
              liveDirections.Any(p => p.Marker.RecordPath.EndsWith("npc_kasparov_01.dbr") && p.IsDistant),
            "current four-quest session resolves distant Barnabas and Kasparov without live or cached markers");
        Check(!liveDirections.Any(p => p.Marker.RecordPath.Contains("npc_johnbourbon")),
            "current session does not promote the inactive return-to-Captain task");
        return world.SourceFingerprint;
    }
}
