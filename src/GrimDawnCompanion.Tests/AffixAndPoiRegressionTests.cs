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
        require(checks.Count()==13 && checks.Select(c=>Enum.Parse<PoiFilter>((string)c.Attribute("Tag")!)).Aggregate(PoiFilter.None,(a,b)=>a|b)==PoiFilter.All,"all supported POI categories have unique filter toggles");
        var bypass=markup.Descendants().Single(e=>(string?)e.Attribute(x+"Name")=="ResourceCapBypassBox");
        require(bypass.Attribute("IsChecked") is null,"natural cap bypass defaults off");
    }
}
