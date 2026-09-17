using System.Globalization;
using System.Text.RegularExpressions;

namespace GrimDawnCompanion.Core;

// Presentation only: record identity, compatibility and spawning are unchanged.
public static class InternalGearLabels
{
    // Verified against the player default-piece references and the gear meshes.
    private static readonly Dictionary<string,string> PlayerDefaults = new(StringComparer.OrdinalIgnoreCase)
    {
        ["a00_torso01"]="Default Male Torso Clothing",
        ["a00_legs01"]="Default Male Leg Clothing",
        ["a00_hands01"]="Default Male Hands",
        ["a00_feet01"]="Default Male Feet",
        ["a00f_torso01"]="Default Female Torso Clothing",
        ["a00f_legs01"]="Default Female Leg Clothing",
        ["a00f_hands01"]="Default Female Hands",
        ["a00f_feet01"]="Default Female Feet",
        ["a00f_hair01"]="Default Female Hair — Style 1",
        ["a00f_hair02"]="Default Female Hair — Style 2",
        ["a00f_hair03"]="Default Female Hair — Style 3",
        ["a00f_hairlong01"]="Default Female Long Hair — Style 1"
    };

    public static ItemRecord Apply(ItemRecord item)
    {
        var path=item.RecordPath.Replace('\\','/').ToLowerInvariant();
        var stem=Path.GetFileNameWithoutExtension(path);
        string label;
        if(path.StartsWith("records/creatures/pc/defaultgear/",StringComparison.Ordinal) && PlayerDefaults.TryGetValue(stem,out var known))
            label=known;
        else if(path.Contains("/defaultgear/",StringComparison.Ordinal))
            label="Default Appearance — "+Readable(stem);
        else if(path.Contains("/npcgear/",StringComparison.Ordinal))
            label="NPC Gear — "+Readable(stem.StartsWith("npc_",StringComparison.Ordinal) ? stem[4..] : stem);
        else return MissingItemLabels.Apply(item);
        return item with {Name=label+" (Internal)"};
    }

    public static void ApplyTo(CatalogDocument document)
    {
        for(var i=0;i<document.Items.Count;i++) document.Items[i]=Apply(document.Items[i]);
        document.Items.Sort((a,b)=>
        {
            var order=StringComparer.CurrentCultureIgnoreCase.Compare(a.Name,b.Name);
            return order!=0 ? order : StringComparer.OrdinalIgnoreCase.Compare(a.RecordPath,b.RecordPath);
        });
    }

    private static string Readable(string stem)
    {
        // These are record descriptors, not inferred bonuses or spawn guarantees.
        var text=stem.Replace("blacklegion","black legion").Replace("inquisitorcreed","inquisitor creed")
            .Replace("johnbourbon","john bourbon").Replace("kurnchieftain","kurn chieftain")
            .Replace("kurnchild","kurn child").Replace("solaelleader","solael leader")
            .Replace("stuffedbear","stuffed bear").Replace("hairlong","long hair").Replace("hairshort","short hair")
            .Replace("axe2h","two handed axe").Replace('_',' ').Replace('-',' ');
        text=Regex.Replace(text,@"\bf\b","female");
        text=Regex.Replace(text,@"\bm\b","male");
        text=Regex.Replace(text,@"(?<=[a-z])(?=\d)"," ");
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(text.Trim());
    }
}
