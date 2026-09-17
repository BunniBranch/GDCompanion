using System.Globalization;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization;

namespace GrimDawnCompanion.Core;

public sealed record AffixCatalogRecord(string RecordPath, string Class, string Name, int Level,
    string Classification, Dictionary<string,string> Links)
{
    public string Description { get; init; } = "";
    [JsonIgnore] public Dictionary<string,string> Fields { get; init; } = [];
}

public sealed record AffixChoice(string RecordPath, string Proof, string Name, int Level, string Classification)
{
    public static AffixChoice None { get; } = new("", "", "None", 0, "");
    public string Description { get; init; } = "";
    public int Variant { get; init; }
    public string Detail => Description==Name ? "" : Description;
    public string Tooltip => RecordPath.Length==0 ? "No affix" : Name+
        (Description.Length>0 ? "\nBase bonuses: "+Description+"\nActual rolls may vary." : "")+"\nRecord: "+RecordPath;
    public string Display
    {
        get
        {
            if(RecordPath.Length==0) return "None";
            return Name+(Level>0 ? $" • Level {Level}" : "")+(Classification.Length>0 ? $" • {Classification}" : "")+(Variant>0 ? $" • Variant {Variant}" : "");
        }
    }
}

// Compatibility comes from the installed loot graph, not affix filename guesses.
// Keep the field path as a proof that the native bridge rechecks against loaded data.
public sealed partial class ItemAffixIndex
{
    private readonly Dictionary<string,AffixCatalogRecord> _records;
    private readonly Dictionary<string,List<(AffixCatalogRecord Table,string ItemField)>> _items=new(StringComparer.OrdinalIgnoreCase);
    public ItemAffixIndex(IEnumerable<AffixCatalogRecord> records)
    {
        _records=records.ToDictionary(r=>r.RecordPath,StringComparer.OrdinalIgnoreCase);
        foreach(var table in _records.Values.Where(r=>r.Class.StartsWith("LootItemTable",StringComparison.Ordinal)))
            foreach(var link in table.Links.Where(l=>l.Key.StartsWith("lootName",StringComparison.Ordinal)))
            {
                if(!_items.TryGetValue(link.Value,out var rules)) _items[link.Value]=rules=[];
                rules.Add((table,link.Key));
            }
    }
    public static bool Applicable(ItemRecord? item) => item is not null &&
        (item.InternalClass.StartsWith("Weapon",StringComparison.Ordinal) || item.InternalClass.StartsWith("Armor",StringComparison.Ordinal)) &&
        item.Classification != "Quest";

    public IReadOnlyList<AffixChoice> Choices(ItemRecord? item,bool prefix)
    {
        var result=new Dictionary<string,AffixChoice>(StringComparer.OrdinalIgnoreCase);
        if(Applicable(item) && _items.TryGetValue(item!.RecordPath,out var rules))
            foreach(var (table,itemField) in rules.OrderBy(r=>r.Table.RecordPath,StringComparer.Ordinal))
                foreach(var link in table.Links.Where(l=>RootField(l.Key,prefix)))
                    Visit(link.Value,$"{table.RecordPath}|{itemField}|{link.Key}",new HashSet<string>(StringComparer.OrdinalIgnoreCase),0);
        var choices=result.Values.OrderBy(a=>a.Name,StringComparer.CurrentCultureIgnoreCase).ThenBy(a=>a.Level).ThenBy(a=>a.RecordPath,StringComparer.Ordinal).ToArray();
        var duplicates=choices.GroupBy(a=>(a.Name,a.Level,a.Classification)).Where(g=>g.Count()>1)
            .SelectMany(g=>g.Select((a,i)=>(a.RecordPath,Ordinal:i+1))).ToDictionary(a=>a.RecordPath,a=>a.Ordinal);
        return new[]{AffixChoice.None}.Concat(choices.Select(a=>duplicates.TryGetValue(a.RecordPath,out var ordinal) ? a with{Variant=ordinal} : a)).ToArray();
        void Visit(string path,string proof,HashSet<string> seen,int depth)
        {
            if(depth>12 || proof.Length>1400 || !seen.Add(path) || !_records.TryGetValue(path,out var record)) return;
            if(record.Class=="LootRandomizer")
                result.TryAdd(path,new(path,proof,record.Name,record.Level,record.Classification){Description=record.Description});
            else if(record.Class=="LootRandomizerTable")
                foreach(var link in record.Links.Where(l=>l.Key.StartsWith("randomizerName",StringComparison.Ordinal)))
                    Visit(link.Value,proof+"|"+link.Key,new(seen,StringComparer.OrdinalIgnoreCase),depth+1);
        }
    }
    public bool Contains(ItemRecord item,AffixChoice choice,bool prefix) => choice.RecordPath.Length==0 ||
        Choices(item,prefix).Any(a=>a.RecordPath==choice.RecordPath && a.Proof==choice.Proof);
    private static bool RootField(string field,bool prefix) => prefix
        ? field.StartsWith("prefixTableName",StringComparison.Ordinal) || field.StartsWith("rarePrefixTableName",StringComparison.Ordinal)
        : field.StartsWith("suffixTableName",StringComparison.Ordinal) || field.StartsWith("rareSuffixTableName",StringComparison.Ordinal);

    public static AffixCatalogRecord? ReadRecord(string path,IEnumerable<string> lines,IReadOnlyDictionary<string,string> tags)
    {
        path=path.Replace('\\','/').ToLowerInvariant();
        var fields=new Dictionary<string,string>(StringComparer.Ordinal);
        foreach(var line in lines)
        {
            int comma=line.IndexOf(',');if(comma<=0) continue;
            fields[line[..comma]]=line[(comma+1)..].TrimEnd(',').Trim();
        }
        var cls=fields.GetValueOrDefault("Class","");
        if(cls!="LootRandomizer" && cls!="LootRandomizerTable" && !cls.StartsWith("LootItemTable",StringComparison.Ordinal)) return null;
        var links=new Dictionary<string,string>(StringComparer.Ordinal);
        foreach(var (key,value) in fields)
        {
            var match=LinkField().Match(key);
            if(!match.Success || !value.EndsWith(".dbr",StringComparison.OrdinalIgnoreCase)) continue;
            var weightKey=match.Groups[1].Value.Replace("Name","Weight",StringComparison.Ordinal)+match.Groups[2].Value;
            if(!fields.TryGetValue(weightKey,out var weight) || !double.TryParse(weight,NumberStyles.Float,CultureInfo.InvariantCulture,out var n) || n<=0) continue;
            links[key]=value.Replace('\\','/').ToLowerInvariant();
        }
        var tag=fields.GetValueOrDefault("lootRandomizerName","");
        var name=tags.GetValueOrDefault(tag,"");
        name=Regex.Replace(name,@"\{[^}]*\}","");
        int.TryParse(fields.GetValueOrDefault("levelRequirement","0"),out var level);
        return new(path,cls,name,level,fields.GetValueOrDefault("itemClassification",""),links){Fields=cls=="LootRandomizer" ? fields : []};
    }
    [GeneratedRegex(@"^(lootName|randomizerName|prefixTableName|suffixTableName|rarePrefixTableName|rareSuffixTableName)([1-9][0-9]{0,3})$")]
    private static partial Regex LinkField();
}

public sealed partial class GameBridgeClient
{
    public bool SupportsItemAffixes => ProtocolVersion>=38 && Capabilities.Contains("ITEM_AFFIXES");
    public async Task<string> SpawnAffixedItemAsync(ItemRecord item,AffixChoice prefix,AffixChoice suffix,int quantity,
        ItemAffixIndex index,CancellationToken cancellationToken=default)
    {
        if(!SupportsItemAffixes) throw new InvalidOperationException("Reconnect with the updated Companion to spawn affixed items.");
        if(!ItemAffixIndex.Applicable(item) || !index.Contains(item,prefix,true) || !index.Contains(item,suffix,false) ||
            (prefix.RecordPath.Length==0 && suffix.RecordPath.Length==0)) throw new InvalidOperationException("Select compatible affixes for the current item.");
        if(quantity is <1 or >100) throw new ArgumentOutOfRangeException(nameof(quantity),"Affixed items are limited to 100 per operation.");
        var fields=new[]{item.RecordPath,prefix.RecordPath,suffix.RecordPath,prefix.Proof,suffix.Proof};
        if(fields.Any(f=>f.Any(char.IsControl))) throw new InvalidDataException("Invalid item or affix record.");
        var command=$"ITEM_AFFIX\t{quantity}\t"+string.Join('\t',fields);
        if(command.Length>4000) throw new InvalidDataException("Affix compatibility path is too long.");
        var response=await SendAsync(command,cancellationToken);
        if(response!=$"OK ITEM_AFFIX {quantity}") throw new InvalidOperationException(response+". Do not retry automatically; inspect the inventory first.");
        return response;
    }
}
