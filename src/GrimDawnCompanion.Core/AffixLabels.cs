using System.Globalization;
using System.Text.RegularExpressions;

namespace GrimDawnCompanion.Core;

public static class AffixLabels
{
    public static string Normalize(string path)=>path.Replace('\\','/').ToLowerInvariant();
    public static string Clean(string text)=>Regex.Replace(text,@"\{[^}]*\}|\^[A-Za-z]","").Trim();
    public static KeyValuePair<string,string>? ReadSkillName(string path,IEnumerable<string> lines,IReadOnlyDictionary<string,string> tags)
    {
        string? resolved=null,child=null;
        foreach(var line in lines)
        {
            if(line.StartsWith("skillDisplayName,",StringComparison.Ordinal))
            {
                var tag=line[17..].TrimEnd(',').Trim();
                resolved=tags.TryGetValue(tag,out var name) ? Clean(name) : "";
            }
            else if(line.StartsWith("buffSkillName,",StringComparison.Ordinal)) child=Normalize(line[14..].TrimEnd(',').Trim());
        }
        // Projectile/debuff wrappers often put the localized name on their buff.
        return !string.IsNullOrWhiteSpace(resolved) ? new(Normalize(path),resolved) :
            child?.EndsWith(".dbr",StringComparison.Ordinal)==true ? new(Normalize(path),child) :
            resolved is not null ? new(Normalize(path),"") : null;
    }
    public static AffixCatalogRecord Resolve(AffixCatalogRecord record,IReadOnlyDictionary<string,string> skills)
    {
        if(record.Class!="LootRandomizer") return record;
        var effects=new List<string>();var fields=record.Fields;
        foreach(var (field,path) in fields.OrderBy(p=>p.Key,StringComparer.Ordinal))
        {
            var match=Regex.Match(field,@"^augmentSkillName(\d+)$");
            if(match.Success && Number(fields.GetValueOrDefault("augmentSkillLevel"+match.Groups[1].Value,""),out var n) && n!=0)
                effects.Add($"{Signed(n)} to {Skill(path)}");
        }
        foreach(var (field,label,percent) in StatLabels)
            if(Number(fields.GetValueOrDefault(field,""),out var n) && n!=0)
                effects.Add($"{Signed(n)}{(percent ? "%" : "")} {label}");
        foreach(var (field,path) in fields)
        {
            if(!path.EndsWith(".dbr",StringComparison.OrdinalIgnoreCase)) continue;
            if(field is "itemSkillName" or "itemSkillAutoController") effects.Add("Grants "+Skill(path));
            else if(Regex.IsMatch(field,@"^(modifiedSkillName|skillModifierName)\d*$")) effects.Add("Modifies "+Skill(path));
        }
        var description=string.Join("; ",effects.Distinct());
        var name=Clean(record.Name);
        if(string.IsNullOrWhiteSpace(name)) name=effects.Count>0 ? string.Join("; ",effects.Distinct().Take(2)) : "Special equipment bonus";
        return record with{Name=name,Description=description,Fields=[]};
        string Skill(string path)
        {
            var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for(var depth=0;depth<12;depth++)
            {
                path=Normalize(path);
                if(!seen.Add(path) || !skills.TryGetValue(path,out var name) || string.IsNullOrWhiteSpace(name)) break;
                if(!name.EndsWith(".dbr",StringComparison.OrdinalIgnoreCase)) return name;
                path=name;
            }
            return "an unnamed skill";
        }
    }
    private static bool Number(string value,out double n)=>double.TryParse(value,NumberStyles.Float,CultureInfo.InvariantCulture,out n) && double.IsFinite(n);
    private static string Signed(double n)=>(n>0 ? "+" : "")+n.ToString("0.##",CultureInfo.InvariantCulture);
    private static readonly (string Field,string Label,bool Percent)[] StatLabels =
    [
        ("characterStrength","Physique",false),("characterDexterity","Cunning",false),("characterIntelligence","Spirit",false),
        ("characterStrengthModifier","Physique",true),("characterDexterityModifier","Cunning",true),("characterIntelligenceModifier","Spirit",true),
        ("characterLife","Health",false),("characterLifeModifier","Health",true),("characterMana","Energy",false),("characterManaModifier","Energy",true),
        ("characterOffensiveAbility","Offensive Ability",false),("characterDefensiveAbility","Defensive Ability",false),
        ("characterOffensiveAbilityModifier","Offensive Ability",true),("characterDefensiveAbilityModifier","Defensive Ability",true),
        ("characterAttackSpeedModifier","Attack Speed",true),("characterCastSpeedModifier","Casting Speed",true),("characterRunSpeedModifier","Movement Speed",true),
        ("defensiveFire","Fire Resistance",true),("defensiveCold","Cold Resistance",true),("defensiveLightning","Lightning Resistance",true),
        ("defensivePoison","Poison & Acid Resistance",true),("defensivePierce","Pierce Resistance",true),("defensiveBleeding","Bleeding Resistance",true),
        ("defensiveAether","Aether Resistance",true),("defensiveChaos","Chaos Resistance",true),("defensiveLife","Vitality Resistance",true),
        ("offensivePhysicalModifier","Physical Damage",true),("offensiveFireModifier","Fire Damage",true),("offensiveColdModifier","Cold Damage",true),
        ("offensiveLightningModifier","Lightning Damage",true),("offensiveAetherModifier","Aether Damage",true),("offensiveChaosModifier","Chaos Damage",true),
        ("offensiveLifeModifier","Vitality Damage",true),("offensivePoisonModifier","Acid Damage",true),("offensivePierceModifier","Pierce Damage",true)
    ];
}
