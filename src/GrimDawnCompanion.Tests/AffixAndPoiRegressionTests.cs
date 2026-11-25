using GrimDawnCompanion.Core;
using System.Text.Json;
using System.Xml.Linq;

internal static class AffixAndPoiRegressionTests
{
    public static void Run(string root,Action<bool,string> require)
    {
        var item=new ItemRecord("Test","records/items/test.dbr","Test","Weapons","Rare",10,"tag","WeaponMelee_Sword");
        var rule="records/items/loottables/test.dbr";var pool="records/items/lootaffixes/pool.dbr";var affix="records/items/lootaffixes/prefix/test.dbr";
        var tags=new Dictionary<string,string>{{"tagAffix","{^G}Fiery"}};
        AffixCatalogRecord Read(string path,params string[] lines)=>ItemAffixIndex.ReadRecord(path,lines,tags)!;
        var records=new[]{
            Read(rule,"Class,LootItemTable_DynWeight,","lootName1,"+item.RecordPath+",","lootWeight1,1,","prefixTableName1,"+pool+",","prefixTableWeight1,10,","suffixTableName1,"+pool+",","suffixTableWeight1,0,"),
            Read(pool,"Class,LootRandomizerTable,","randomizerName1,"+affix+",","randomizerWeight1,100,"),
            Read(affix,"Class,LootRandomizer,","lootRandomizerName,tagAffix,","levelRequirement,8,","itemClassification,Magical,")};
        var index=new ItemAffixIndex(records);var prefixes=index.Choices(item,true);var suffixes=index.Choices(item,false);
        require(prefixes.Count==2 && prefixes[1].Name=="Fiery" && prefixes[1].Level==8 && prefixes[1].Proof==rule+"|lootName1|prefixTableName1|randomizerName1","affix choices resolve localized, leveled compatibility paths from loot tables");
        require(prefixes[1].Display=="Fiery • Level 8 • Magical" && !prefixes[1].Display.Contains("test"),"visible affix labels contain readable names, level and rarity, never record identifiers");
        var skillNames=new Dictionary<string,string>{{"records/skills/blade.dbr","Blade Arc"}};
        var unnamed=AffixLabels.Resolve(Read(affix,"Class,LootRandomizer,","augmentSkillName1,records/skills/blade.dbr,","augmentSkillLevel1,3,"),skillNames);
        require(unnamed.Name=="+3 to Blade Arc" && unnamed.Description=="+3 to Blade Arc","unnamed crafted affixes show localized skill bonuses rather than filenames");
        var missing=AffixLabels.Resolve(Read(affix,"Class,LootRandomizer,","lootRandomizerName,missingTag,","characterStrength,12,"),skillNames);
        require(missing.Name=="+12 Physique" && !missing.Name.Contains("missingTag"),"missing localization falls back to readable bonuses without leaking tags");
        var skillName=AffixLabels.ReadSkillName("RECORDS\\skills\\blade.dbr",["skillDisplayName,tagSkill,"],new Dictionary<string,string>{{"tagSkill","{^G}Blade Arc"}});
        require(skillName?.Key=="records/skills/blade.dbr" && skillName.Value.Value=="Blade Arc","skill names resolve installed localization with normalized paths and no color markup");
        var wrapper=AffixLabels.ReadSkillName("records/skills/wrapper.dbr",["buffSkillName,records/skills/blade.dbr,"],tags)!.Value;
        skillNames[wrapper.Key]=wrapper.Value;
        var wrapped=AffixLabels.Resolve(Read(affix,"Class,LootRandomizer,","augmentSkillName1,records/skills/wrapper.dbr,","augmentSkillLevel1,2,"),skillNames);
        require(wrapped.Name=="+2 to Blade Arc","debuff/projectile skill wrappers resolve their linked effect names");
        skillNames["records/skills/wrapper.dbr"]="records/skills/wrapper.dbr";
        require(AffixLabels.Resolve(Read(affix,"Class,LootRandomizer,","augmentSkillName1,records/skills/wrapper.dbr,","augmentSkillLevel1,2,"),skillNames).Name=="+2 to an unnamed skill","cyclic skill-name links cannot hang catalog rebuilding");
        require(AffixLabels.Resolve(Read(affix,"Class,LootRandomizer,"),skillNames).Name=="Special equipment bonus","unknown affixes get an honest readable fallback, not a guessed effect");
        require(suffixes.Count==1 && suffixes[0]==AffixChoice.None,"disabled affix pools are not offered and None is always available");
        require(index.Choices(item with{Classification="Legendary"},true).Count==2 && index.Choices(item with{InternalClass="ItemRelic"},true).Count==1 && index.Choices(item with{RecordPath="records/items/unrelated.dbr"},true).Count==1,"special crafted legendary gear uses its own pools; components and unrelated items have no affixes");
        require(!index.Contains(item,prefixes[1] with{Proof="forged"},true) && !index.Contains(item,prefixes[1],false),"mismatched side or stale/forged compatibility proof rejected");
        var cycle=Read(pool,"Class,LootRandomizerTable,","randomizerName1,"+pool+",","randomizerWeight1,100,");
        require(new ItemAffixIndex([records[0],cycle,records[2]]).Choices(item,true).Count==1,"cyclic mod affix graphs terminate without inventing choices");
        Exception? uiFailure=null;
        var uiThread=new Thread(()=>
        {
            try
            {
                var vm=new GrimDawnCompanion.App.MainViewModel();
                var other=item with{RecordPath="records/items/other.dbr"};
                var document=new CatalogDocument{Items=[item,other],AffixRecords=records.ToList()};
                vm.SetCatalog(document);vm.SelectedItem=item;vm.SelectedPrefix=vm.PrefixChoices[1];
                vm.SelectedItem=other;
                require(vm.SelectedPrefix==AffixChoice.None && vm.SelectedSuffix==AffixChoice.None && !vm.HasPrefixChoices,"changing base item clears previous affixes");
                vm.SelectedItem=item;vm.SelectedPrefix=vm.PrefixChoices[1];vm.SetCatalog(document);
                require(vm.SelectedPrefix==AffixChoice.None && vm.HasPrefixChoices,"catalog reload clears affixes while retaining available choices");
            }
            catch(Exception ex){uiFailure=ex;}
        });
        uiThread.SetApartmentState(ApartmentState.STA);uiThread.Start();uiThread.Join();
        if(uiFailure is not null) throw uiFailure;

        var categories=new[]{"Travel Utility","Riftgate","Vendor","Blacksmith","Spirit Guide","Smuggler","Illusionist","Devotion Shrine","Monster Shrine","One-Shot Chest","Lore Note","NPC or Site","Ascension Altar","Unknown"};
        var points=categories.Select((c,i)=>new WorldMarkerRecord("live:"+i,c,"record","live://current-map",i+1,0,0,["$live"])).ToArray();
        var quest=new WorldMarkerRecord("live:quest","Quest Objective","quest","live://current-map",500,0,0,["$live"]);
        foreach(var group in Enum.GetValues<PoiFilter>().Where(f=>f!=PoiFilter.None && f!=PoiFilter.All))
        {
            var chosen=NavigationOverlayProjection.Build(new(0,0,0),points.Append(quest),[],true,true,50,poiFilter:group);
            require(chosen.Any(p=>p.Marker==quest) && chosen.Where(p=>p.Marker!=quest).All(p=>PoiFilters.Group(p.Marker.Category)==group),"POI type filter selects only "+group+" without hiding quests");
        }
        var distant=points.Select(p=>p with{Id="catalog:"+p.Id,LevelPath="levels/world/region0a/region.lvl",WorldX=p.WorldX+500,QuestPaths=null}).ToArray();
        require(NavigationOverlayProjection.Build(new(0,0,0),distant,[],false,true,50,poiFilter:PoiFilter.Lore).Single().Marker.Category=="Lore Note","rim selection filters before the fallback quota");
        require(NavigationOverlayProjection.Build(new(0,0,0),points.Append(quest),[],true,true,50,poiFilter:PoiFilter.None).Single().Marker==quest,"all POI types off retains quest markers");
        require(NavigationOverlayProjection.Build(new(0,0,0),points,[],false,false,50,poiFilter:PoiFilter.All).Count==0,"master POI toggle overrides category selection");
        require(JsonSerializer.Deserialize<PoiFilter>(JsonSerializer.Serialize(PoiFilter.Travel|PoiFilter.Lore))==(PoiFilter.Travel|PoiFilter.Lore),"POI flags round-trip independently");

        var domainPlayer=new WorldPosition(100,0,100);
        var domainFallbackPois=new[]{
            new WorldMarkerRecord("ug-chest","One-Shot Chest","records/items/lootchests/oneshotchests/ug.dbr","Levels/Undergrounds/RegionUG_CaveBurial_A02.lvl",170,0,100),
            new WorldMarkerRecord("void-vendor","Vendor","records/creatures/npcs/merchants/void.dbr","Levels/Void/RegionVoid_BastionOfChaosA02.lvl",180,0,100),
            new WorldMarkerRecord("other-ug-lore","Lore Note","records/items/loreobjects/other.dbr","Levels/Undergrounds/RegionUG_DCPrisonCellar_A03.lvl",190,0,100),
            new WorldMarkerRecord("ow-lore","Lore Note","records/items/loreobjects/ow.dbr","Levels/Region0B032.lvl",300,0,100),
            new WorldMarkerRecord("ow-blacksmith","Blacksmith","records/creatures/npcs/blacksmith_ow.dbr","Levels/Region0A010.lvl",320,0,100),
            new WorldMarkerRecord("ow-spirit","Spirit Guide","records/creatures/npcs/spiritguide_ow.dbr","Levels/Region0A010.lvl",340,0,100),
            new WorldMarkerRecord("ow-altar","Ascension Altar","records/interactive/ascension_altar.dbr","Levels/Region0A010.lvl",360,0,100)
        };
        var liveUgPoi=new WorldMarkerRecord("live-ug-vendor","Vendor","live://minimap/vendor","live://current-map",115,0,100,["$live"],"Merchant");
        var owFallback=NavigationOverlayProjection.Build(domainPlayer,domainFallbackPois,[],false,true,40,"Levels/Region0A001.lvl");
        require(owFallback.Select(p=>p.Marker.Id).SequenceEqual(["ow-lore","ow-blacksmith","ow-spirit","ow-altar"]),
            "overworld POI fallback selects only overworld catalog POIs when underground and void POIs are closer");
        require(NavigationOverlayProjection.Build(domainPlayer,domainFallbackPois,[],false,true,40,"Levels/Undergrounds/RegionUG_CaveBurial_A01.lvl")
                .Single().Marker.Id=="ug-chest",
            "underground POI fallback selects only catalog POIs in the same dungeon family");
        require(NavigationOverlayProjection.Build(domainPlayer,domainFallbackPois,[],false,true,40,"Levels/Undergrounds/RegionUG_SmugglersBasin_A01.lvl")
                .Count==0 &&
                NavigationOverlayProjection.Build(domainPlayer,domainFallbackPois.Append(liveUgPoi),[],false,true,40,"Levels/Undergrounds/RegionUG_SmugglersBasin_A01.lvl")
                .Single().Marker.Id=="live-ug-vendor",
            "dungeons without matching catalog POIs hide unrelated catalog POIs while preserving live POIs");
        require(NavigationOverlayProjection.Build(domainPlayer,domainFallbackPois,[],false,true,40,currentLevelPath:null).Count==domainFallbackPois.Length,
            "unknown current level path retains all catalog POIs for backward compatibility");

        var domainRiftgates=new[]{
            new WorldMarkerRecord("ug-lab-rift","Riftgate","records/ui/riftgatemap/locations/riftgatea_wardenslabratory.dbr","Levels/Undergrounds/RegionUG_Labratory_A03.lvl",170,0,100,DisplayName:"Warden's Laboratory Rift"),
            new WorldMarkerRecord("rl-rift","Riftgate","records/ui/riftgatemap/locations/riftgate_rl.dbr","Levels/Roguelike/RL_Dranghoul_A01.lvl",180,0,100,DisplayName:"Dranghoul Rift"),
            new WorldMarkerRecord("ow-rift-1","Riftgate","records/ui/riftgatemap/locations/riftgate_ow1.dbr","Levels/Region0A001.lvl",300,0,100,DisplayName:"Devil's Crossing Rift"),
            new WorldMarkerRecord("ow-rift-2","Riftgate","records/ui/riftgatemap/locations/riftgate_ow2.dbr","Levels/Region0A020.lvl",400,0,100,DisplayName:"Burrwitch Rift")
        };
        var genericUgLiveRift=new WorldMarkerRecord("live-ug-rift","Travel Utility","live://minimap/3","live://current-map",302,0,100,["$live"],"Travel Utility");
        var owRifts=NavigationOverlayProjection.Build(domainPlayer,domainRiftgates,[],false,true,40,"Levels/Region0A005.lvl");
        require(owRifts.Select(p=>p.Marker.Id).SequenceEqual(["ow-rift-1","ow-rift-2"]),
            "overworld riftgate selection excludes closer underground and roguelike riftgates");
        require(NavigationOverlayProjection.Build(domainPlayer,domainRiftgates,[],false,true,40,"Levels/Undergrounds/RegionUG_Labratory_A01.lvl")
                .Single().Marker.Id=="ug-lab-rift",
            "underground riftgate selection includes only the same dungeon family's catalog riftgate");
        var basinRifts=NavigationOverlayProjection.Build(domainPlayer,domainRiftgates.Append(genericUgLiveRift),[],false,true,40,"Levels/Undergrounds/RegionUG_SmugglersBasin_A01.lvl");
        require(basinRifts.Count==1 && basinRifts[0].Marker.Id=="live-ug-rift" && basinRifts[0].Marker.DisplayName=="Travel Utility",
            "underground live riftgate does not inherit a co-located overworld catalog riftgate label");

        var domainTravel=new[]{
            new WorldMarkerRecord("ow-boat","Travel Utility","records/interactive/boat_ow.dbr","Levels/Region0A010.lvl",112,0,100,DisplayName:"Row Boat"),
            new WorldMarkerRecord("ug-boat","Travel Utility","records/interactive/boat_ug.dbr","Levels/Undergrounds/RegionUG_Cellar_Wightmire_A01.lvl",114,0,100,DisplayName:"Row Boat")
        };
        require(NavigationOverlayProjection.Build(domainPlayer,domainTravel,[],false,true,40,"Levels/Region0A001.lvl")
                .Single().Marker.Id=="ow-boat",
            "overworld travel utilities exclude nearby underground catalog boats");
        require(NavigationOverlayProjection.Build(domainPlayer,domainTravel,[],false,true,40,"Levels/Undergrounds/RegionUG_CaveBurial_A01.lvl")
                .Count==0 &&
                NavigationOverlayProjection.Build(domainPlayer,domainTravel,[],false,true,40,"Levels/Undergrounds/RegionUG_Cellar_Wightmire_A02.lvl")
                .Single().Marker.Id=="ug-boat",
            "underground travel utilities appear only in their matching dungeon family");

        var localPoiRangeTest=new[]{
            new WorldMarkerRecord("boat-1000m","Travel Utility","records/interactive/boat_1000.dbr","Levels/Region0A010.lvl",1100,0,100,DisplayName:"Row Boat"),
            new WorldMarkerRecord("boat-1001m","Travel Utility","records/interactive/boat_1001.dbr","Levels/Region0A010.lvl",1101,0,100,DisplayName:"Row Boat"),
            new WorldMarkerRecord("ferry-900m","Travel Utility","records/interactive/ferry_900.dbr","Levels/Region0A010.lvl",1000,0,100,DisplayName:"Ferry"),
            new WorldMarkerRecord("ferry-1001m","Travel Utility","records/interactive/ferry_1001.dbr","Levels/Region0A010.lvl",1101,0,100,DisplayName:"Ferry"),
            new WorldMarkerRecord("lore-1000m","Lore Note","records/items/loreobjects/near.dbr","Levels/Region0A010.lvl",1100,0,100),
            new WorldMarkerRecord("lore-1200m","Lore Note","records/items/loreobjects/far.dbr","Levels/Region0A010.lvl",1300,0,100),
            new WorldMarkerRecord("vendor-1001m","Vendor","records/creatures/npcs/merchants/far.dbr","Levels/Region0A010.lvl",1101,0,100),
            new WorldMarkerRecord("altar-1001m","Ascension Altar","records/interactive/ascension_far.dbr","Levels/Region0A010.lvl",1101,0,100),
            new WorldMarkerRecord("rift-1500m","Riftgate","records/ui/riftgatemap/locations/far.dbr","Levels/Region0A010.lvl",1600,0,100,DisplayName:"Far Rift")
        };
        var localPoiRangeResult=NavigationOverlayProjection.Build(domainPlayer,localPoiRangeTest,[],false,true,40,"Levels/Region0A001.lvl");
        require(localPoiRangeResult.Select(p=>p.Marker.Id).ToHashSet().SetEquals(["boat-1000m","ferry-900m","lore-1000m","rift-1500m"]),
            "non-rift local POIs (boats, ferries, lore, vendors, altars) appear only within 1000m while distant rifts remain visible");
        var liveRowBoats=new[]{
            new WorldMarkerRecord("live-boat-near","Travel Utility","live://minimap/20","live://current-map",110,0,100,["$live"],"Row Boat"),
            new WorldMarkerRecord("live-boat-far","Travel Utility","live://minimap/20","live://current-map",1101,0,100,["$live"],"Row Boat")
        };
        require(NavigationOverlayProjection.Build(domainPlayer,liveRowBoats,[],false,true,40,"Levels/Region0A001.lvl")
                .Single().Marker.Id=="live-boat-near",
            "live row boats follow the 1000m distance cap while nearby boats remain visible");
        require(PoiFilters.Group("Riftgate")==PoiFilter.Riftgates &&
                PoiFilters.Group("Travel Utility")==PoiFilter.Travel &&
                PoiFilters.MigrateFilter((PoiFilter)8191,0)==PoiFilter.All &&
                PoiFilters.MigrateFilter((PoiFilter)8191 & ~PoiFilter.Travel,0)==((PoiFilter)8191 & ~PoiFilter.Travel) &&
                PoiFilters.MigrateFilter(PoiFilter.Travel,PoiFilters.CurrentFilterVersion)==PoiFilter.Travel,
            "rifts and travel utilities use distinct filter flags with legacy settings migration");
        var riftVsBoatMarkers=new[]{
            new WorldMarkerRecord("split-rift","Riftgate","records/ui/riftgatemap/locations/riftgate_1.dbr","Levels/Region0A001.lvl",300,0,100,DisplayName:"Rift"),
            new WorldMarkerRecord("split-boat","Travel Utility","records/interactive/boat_1.dbr","Levels/Region0A010.lvl",112,0,100,DisplayName:"Row Boat")
        };
        require(NavigationOverlayProjection.Build(domainPlayer,riftVsBoatMarkers,[],false,true,40,"Levels/Region0A001.lvl",poiFilter:PoiFilter.All & ~PoiFilter.Travel)
                .Single().Marker.Id=="split-rift" &&
                NavigationOverlayProjection.Build(domainPlayer,riftVsBoatMarkers,[],false,true,40,"Levels/Region0A001.lvl",poiFilter:PoiFilter.All & ~PoiFilter.Riftgates)
                .Single().Marker.Id=="split-boat",
            "rifts and travel utilities toggle independently in navigation projection");

        var chestPoints=new[]{
            new WorldMarkerRecord("chest-1","One-Shot Chest","records/items/lootchests/oneshotchests/d01.dbr","Levels/Region0A001.lvl",120,0,100,ExcludedTokens:["DUNGEONCHEST_A"]),
            new WorldMarkerRecord("chest-2a","One-Shot Chest","records/items/lootchests/oneshotchests/d04.dbr","Levels/Region0A058.lvl",220,0,100,ExcludedTokens:["DUNGEONCHEST_B"]),
            new WorldMarkerRecord("chest-2b","One-Shot Chest","records/items/lootchests/oneshotchests/d04.dbr","Levels/Region0A058.lvl",260,0,100,ExcludedTokens:["DUNGEONCHEST_B"]),
            new WorldMarkerRecord("chest-3","One-Shot Chest","records/items/lootchests/oneshotchests/d08.dbr","Levels/Region0B001.lvl",350,0,100,ExcludedTokens:["DUNGEONCHEST_C"]),
            new WorldMarkerRecord("chest-too-far","One-Shot Chest","records/items/lootchests/oneshotchests/d09.dbr","Levels/Region0B002.lvl",1101,0,100,ExcludedTokens:["DUNGEONCHEST_FAR"]),
            new WorldMarkerRecord("chest-ug","One-Shot Chest","records/items/lootchests/oneshotchests/d02.dbr","Levels/Undergrounds/RegionUG_CaveBurial_A01.lvl",110,0,100,ExcludedTokens:["DUNGEONCHEST_UG"])
        };
        var crowdedWithChests=Enumerable.Range(0,10)
            .Select(i=>new WorldMarkerRecord($"near-npc-chest-{i}","Vendor",$"live://vendor/{i}","live://current-map",105+i,0,100,["$live"]))
            .Concat(chestPoints).ToArray();
        var closestNearbyChest=NavigationOverlayProjection.Build(domainPlayer,crowdedWithChests,[],false,true,40,"Levels/Region0A001.lvl");
        require(closestNearbyChest.Count(x=>x.Marker.Category=="One-Shot Chest")==4 &&
                closestNearbyChest.Any(x=>x.Marker.Id=="chest-1" && !x.IsDistant) &&
                closestNearbyChest.Any(x=>x.Marker.Id=="chest-2a" && x.IsDistant) &&
                closestNearbyChest.Any(x=>x.Marker.Id=="chest-2b" && x.IsDistant) &&
                closestNearbyChest.Any(x=>x.Marker.Id=="chest-3" && x.IsDistant) &&
                closestNearbyChest.All(x=>x.Marker.Id!="chest-too-far"),
            "one-shot chests show all un-opened chests within 1000m and exclude chests beyond 1000m even when nearby POIs exceed MinimumPoiTargets");
        var skippedFirstChest=NavigationOverlayProjection.Build(domainPlayer,crowdedWithChests,[],false,true,40,"Levels/Region0A001.lvl",
            openedChestTokens:new HashSet<string>(StringComparer.OrdinalIgnoreCase){"dungeonchest_a"});
        require(skippedFirstChest.Where(x=>x.Marker.Category=="One-Shot Chest").Select(x=>x.Marker.Id).ToHashSet().SetEquals(["chest-2a","chest-2b","chest-3"]) &&
                skippedFirstChest.Where(x=>x.Marker.Category=="One-Shot Chest").All(x=>x.IsDistant),
            "opened one-shot chests are filtered out case-insensitively while retaining remaining chests within 1000m");
        var skippedSharedSetChest=NavigationOverlayProjection.Build(domainPlayer,chestPoints,[],false,true,40,"Levels/Region0A001.lvl",
            openedChestTokens:new HashSet<string>(StringComparer.OrdinalIgnoreCase){"DUNGEONCHEST_A","DUNGEONCHEST_B"});
        require(skippedSharedSetChest.Single().Marker.Id=="chest-3",
            "multi-placement set-piece chests sharing an opened token are all excluded");
        require(NavigationOverlayProjection.Build(domainPlayer,chestPoints,[],false,true,40,"Levels/Region0A001.lvl",
                poiFilter:PoiFilter.All & ~PoiFilter.Chests).Count==0 &&
                NavigationOverlayProjection.Build(domainPlayer,chestPoints,[],false,true,40,"Levels/Undergrounds/RegionUG_CaveBurial_A02.lvl")
                .Single().Marker.Id=="chest-ug",
            "one-shot chests respect the Chests POI filter and overworld vs. underground locale separation");
        var candidateTokens=NavigationOverlayProjection.CandidateOneShotChestTokens(domainPlayer,chestPoints,"Levels/Region0A001.lvl",
            new HashSet<string>(StringComparer.OrdinalIgnoreCase){"DUNGEONCHEST_A"}).SelectMany(x=>x).ToArray();
        require(candidateTokens.SequenceEqual(["DUNGEONCHEST_B","DUNGEONCHEST_C"]),
            "candidate one-shot chest tokens are ordered closest-first within 1000m and locale, deduplicated, and skip known opened tokens");

        var lorePoints=new[]{
            new WorldMarkerRecord("lore-near","Lore Note","records/items/loreobjects/loreobject_a.dbr","Levels/Region0A001.lvl",115,0,100,DisplayName:"Harbormaster's Log",LoreRecordPath:"records/items/loreobjects/loreobject_a.dbr"),
            new WorldMarkerRecord("lore-far","Lore Note","records/items/loreobjects/lorechest_b.dbr","Levels/Region0A058.lvl",260,0,100,DisplayName:"Creed's Journal",LoreRecordPath:"records/items/loreobjects/loreobject_b.dbr"),
            new WorldMarkerRecord("lore-too-far","Lore Note","records/items/loreobjects/loreobject_c.dbr","Levels/Region0B002.lvl",1101,0,100,DisplayName:"Far Note",LoreRecordPath:"records/items/loreobjects/loreobject_c.dbr"),
            new WorldMarkerRecord("lore-ug","Lore Note","records/items/loreobjects/loreobject_ug.dbr","Levels/Undergrounds/RegionUG_CaveBurial_A01.lvl",110,0,100,DisplayName:"Cave Note",LoreRecordPath:"records/items/loreobjects/loreobject_ug.dbr")
        };
        var crowdedWithLore=Enumerable.Range(0,10)
            .Select(i=>new WorldMarkerRecord($"near-npc-lore-{i}","Vendor",$"live://vendor/{i}","live://current-map",105+i,0,100,["$live"]))
            .Concat(lorePoints).ToArray();
        var visibleLore=NavigationOverlayProjection.Build(domainPlayer,crowdedWithLore,[],false,true,40,"Levels/Region0A001.lvl");
        require(visibleLore.Count(x=>x.Marker.Category=="Lore Note")==2 &&
                visibleLore.Any(x=>x.Marker.Id=="lore-near" && !x.IsDistant) &&
                visibleLore.Any(x=>x.Marker.Id=="lore-far" && x.IsDistant) &&
                visibleLore.All(x=>x.Marker.Id!="lore-too-far"),
            "lore notes stay visible both nearby and distant within 1000m even when nearby POIs exceed MinimumPoiTargets");
        var skippedCollectedLore=NavigationOverlayProjection.Build(domainPlayer,crowdedWithLore,[],false,true,40,"Levels/Region0A001.lvl",
            collectedLoreRecords:new HashSet<string>(StringComparer.OrdinalIgnoreCase){"RECORDS/ITEMS/LOREOBJECTS/LOREOBJECT_A.DBR"});
        require(skippedCollectedLore.Where(x=>x.Marker.Category=="Lore Note").Select(x=>x.Marker.Id).SequenceEqual(["lore-far"]),
            "already-collected lore notes are filtered out case-insensitively while retaining uncollected notes");
        require(NavigationOverlayProjection.Build(domainPlayer,lorePoints,[],false,true,40,"Levels/Region0A001.lvl",
                poiFilter:PoiFilter.All & ~PoiFilter.Lore).Count==0 &&
                NavigationOverlayProjection.Build(domainPlayer,lorePoints,[],false,true,40,"Levels/Undergrounds/RegionUG_CaveBurial_A02.lvl")
                .Single().Marker.Id=="lore-ug",
            "lore notes respect the Lore POI filter and overworld vs. underground locale separation");
        var candidateLore=NavigationOverlayProjection.CandidateLoreRecords(domainPlayer,lorePoints,"Levels/Region0A001.lvl",
            new HashSet<string>(StringComparer.OrdinalIgnoreCase){"records/items/loreobjects/loreobject_a.dbr"}).ToArray();
        require(candidateLore.SequenceEqual(["records/items/loreobjects/loreobject_b.dbr"]),
            "candidate lore records are ordered closest-first within 1000m and locale, deduplicated, and skip known collected records");
        var limits=CharacterResourceLimits.Parse("OK RESOURCE_LIMITS 10 100 3 2000000000 250 109 55 20 9 3 10 10 5 8 0");
        require(limits.Available(1,true)==9980 && limits.Available(2,true)==9991 && limits.Available(3,true)==9997 && limits.EffectiveCap(0,true)==2_000_000_000 && limits.Available(1)==230,"bypass applies only to allocated-adjusted point pools and never changes normal budgets");
        require(CharacterResourcePreparation.Parse("OK RESOURCE_PREPARED 7 3 5 1000 9997 3 10000 0054").Target==1000,"bounded above-cap preparation is parsed without truncation");
        var client=new GameBridgeClient();
        typeof(GameBridgeClient).GetProperty(nameof(GameBridgeClient.Capabilities))!.SetValue(client,new HashSet<string>{"RESOURCE_SET","LOADED_PROGRESSION","RESOURCE_CAP_BYPASS","ITEM_AFFIXES"});
        foreach(var version in new[]{37,38})
        {
            typeof(GameBridgeClient).GetProperty(nameof(GameBridgeClient.ProtocolVersion))!.SetValue(client,version);
            require(client.SupportsResourceCapBypass==(version==38) && client.SupportsItemAffixes==(version==38),"new mutations require updated protocol and capability");
        }
        client.DisposeAsync().AsTask().GetAwaiter().GetResult();
        var markup=XDocument.Load(Path.Combine(root,"src/GrimDawnCompanion.App/MainWindow.xaml"));XNamespace x="http://schemas.microsoft.com/winfx/2006/xaml";
        var checks=markup.Descendants().Single(e=>(string?)e.Attribute(x+"Name")=="PoiTypesPanel").Elements().Where(e=>e.Name.LocalName=="CheckBox");
        require(checks.Count()==14 && checks.Select(c=>Enum.Parse<PoiFilter>((string)c.Attribute("Tag")!)).Aggregate(PoiFilter.None,(a,b)=>a|b)==PoiFilter.All,"all supported POI categories have unique filter toggles");
        var bypass=markup.Descendants().Single(e=>(string?)e.Attribute(x+"Name")=="ResourceCapBypassBox");
        require(bypass.Attribute("IsChecked") is null,"natural cap bypass defaults off");
    }
}
