using GrimDawnCompanion.Core;
using System.Text.Json;

static class InternalGearLabelRegression
{
    public static async Task Run(string root,Action<bool,string> require)
    {
        ItemRecord Item(string path,string name="Helm")=>new(name,path,"Base Game","Armor","Common",1,"tagHeadA001","ArmorProtective_Head");
        var female=Item("records/creatures/pc/defaultgear/a00f_torso01.dbr","A00f Torso01");
        var labeled=InternalGearLabels.Apply(female);
        require(labeled.Name=="Default Female Torso Clothing (Internal)","default female torso has an explicit readable internal label");
        require(labeled with {Name=female.Name}==female,"internal labels preserve record identity and all item metadata");
        require(InternalGearLabels.Apply(Item("records/creatures/pc/defaultgear/a00_torso01.dbr")).Name=="Default Male Torso Clothing (Internal)","male and female defaults are distinguished");
        require(InternalGearLabels.Apply(Item("records/creatures/pc/defaultgear/a00f_hairlong01.dbr")).Name=="Default Female Long Hair — Style 1 (Internal)","hair assets are not labeled as ordinary helmets");
        require(InternalGearLabels.Apply(Item("records/creatures/npcs/npcgear/npc_child_stuffedbear01a.dbr","Alkamos' Scythe")).Name=="NPC Gear — Child Stuffed Bear 01A (Internal)","NPC props use record descriptors instead of misleading shared loot names");
        require(InternalGearLabels.Apply(Item("records/creatures/npcs/npcgear/npc_f202a_head.dbr")).Name=="NPC Gear — F 202A Head (Internal)","unidentified model codes are not interpreted as character gender");
        require(InternalGearLabels.Apply(Item("records/items/gearhead/a01_head003.dbr","Explorer's Hat")).Name=="Explorer's Hat","ordinary item names are unchanged");
        require(InternalGearLabels.Apply(Item("RECORDS\\CREATURES\\PC\\DEFAULTGEAR\\a00f_torso01.dbr")).Name==labeled.Name,"internal path recognition tolerates casing and backslashes");
        require(InternalGearLabels.Apply(labeled)==labeled,"internal relabeling is idempotent");
        var unnamedEnchant=Item("records/items/enchants/a000a_enchant.dbr","A000a Enchant") with {InternalClass="ItemEnchantment",NameUnavailable=true};
        require(InternalGearLabels.Apply(unnamedEnchant).Name=="Enchantment — A000A (Name unavailable)","unlocalized enchantments show a readable type and honest missing-name label");
        require(InternalGearLabels.Apply(unnamedEnchant).Name!=InternalGearLabels.Apply(unnamedEnchant with {RecordPath="records/items/enchants/a0000a_enchant.dbr"}).Name,"unnamed enchantment variants remain distinguishable");
        require(!InternalGearLabels.Apply(unnamedEnchant).Name.Contains("Internal"),"missing localization alone does not imply internal-only equipment");
        require(InternalGearLabels.Apply(Item("records/items/materia/compa_amber.dbr","Amber")).Name=="Amber","valid localized names that resemble filenames are preserved");
        require(InternalGearLabels.Apply(InternalGearLabels.Apply(unnamedEnchant))==InternalGearLabels.Apply(unnamedEnchant),"missing-name labels are idempotent");
        require(InternalGearLabels.Apply(Item("records/mod/defaultgear/custom_torso.dbr")).Name=="Default Appearance — Custom Torso (Internal)","unknown mod default gear gets a neutral label without an invented gender");
        var testDir=Path.Combine(root,".tmp","internal-gear-label-tests",Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDir);
        var oldCatalog=new CatalogDocument{Items=[female,Item("records/items/gearhead/a01_head003.dbr","Explorer's Hat")]};
        var cache=Path.Combine(testDir,"catalog.json");
        await File.WriteAllTextAsync(cache,JsonSerializer.Serialize(oldCatalog,new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var originalBytes=await File.ReadAllBytesAsync(cache);
        var loaded=await new CatalogService(cacheDirectory:testDir).LoadAsync();
        require(loaded?.Items.Single(i=>i.RecordPath==female.RecordPath).Name==labeled.Name,"existing cached catalogs get new labels without requiring a database rebuild");
        require(originalBytes.SequenceEqual(await File.ReadAllBytesAsync(cache)),"loading updated labels does not overwrite the user's cache");
        var newBundle=Path.Combine(testDir,"bundle.json");
        await File.WriteAllTextAsync(newBundle,JsonSerializer.Serialize(new CatalogDocument{Items=[unnamedEnchant]},new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        await File.WriteAllTextAsync(cache,JsonSerializer.Serialize(new CatalogDocument{SchemaVersion=5,Items=[female]},new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var upgraded=await new CatalogService(cacheDirectory:testDir).LoadAsync(newBundle);
        require(upgraded?.Items.Single().Name=="Enchantment — A000A (Name unavailable)","schema-5 caches cannot mask the updated bundled labels");
    }
}
