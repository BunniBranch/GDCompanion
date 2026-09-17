using System.Globalization;
using System.Text.RegularExpressions;

namespace GrimDawnCompanion.Core;

public static class MissingItemLabels
{
    public static ItemRecord Apply(ItemRecord item)
    {
        // Never infer missing localization from a name that resembles a filename:
        // real names such as Amber happen to match their record's short name.
        if(!item.NameUnavailable) return item;
        var path=item.RecordPath.Replace('\\','/').ToLowerInvariant();
        var stem=Path.GetFileNameWithoutExtension(path);
        var type=item.InternalClass switch
        {
            "ItemEnchantment"=>"Enchantment",
            "ItemArtifactFormula"=>"Blueprint",
            "ItemAscensionFormula"=>"Ascension Recipe",
            "ItemRerollFormula"=>"Reroll Recipe",
            "ItemRandomSetFormula"=>"Random Set Recipe",
            "ItemSetFormula"=>"Set Recipe",
            "ItemTransmuterSet"=>"Set Transmutation",
            "ArmorProtective_Chest"=>"Chest Armor",
            "ArmorProtective_Hands"=>"Hand Armor",
            "ArmorProtective_Head"=>"Headgear",
            "ArmorProtective_Feet"=>"Foot Armor",
            "ArmorProtective_Legs"=>"Leg Armor",
            "ArmorProtective_Shoulders"=>"Shoulder Armor",
            "ArmorProtective_Waist"=>"Belt",
            "ArmorJewelry_Ring"=>"Ring",
            "ArmorJewelry_Amulet"=>"Amulet",
            "ArmorJewelry_Medal"=>"Medal",
            "WeaponHunting_Ranged1h"=>"One-Handed Ranged Weapon",
            "WeaponHunting_Ranged2h"=>"Two-Handed Ranged Weapon",
            "WeaponMelee_Axe"=>"One-Handed Axe",
            "WeaponMelee_Axe2h"=>"Two-Handed Axe",
            "WeaponMelee_Sword"=>"One-Handed Sword",
            "WeaponMelee_Sword2h"=>"Two-Handed Sword",
            "WeaponMelee_Mace"=>"One-Handed Mace",
            "WeaponMelee_Mace2h"=>"Two-Handed Mace",
            "WeaponMelee_Scepter"=>"Scepter",
            "WeaponMelee_Dagger"=>"Dagger",
            "WeaponMelee_Spear2h"=>"Two-Handed Spear",
            "WeaponArmor_Offhand"=>"Off-Hand",
            "WeaponArmor_Shield"=>"Shield",
            "ItemRelic"=>"Component",
            "QuestItem"=>"Quest Item",
            "OneShot_Scroll"=>"Scroll",
            "AreaOfInterest"=>"Area Marker",
            "Prop"=>"Prop",
            _=>"Item"
        };
        var descriptor=Regex.Replace(stem,@"^(craft|quest)_","");
        if(item.InternalClass=="ItemEnchantment") descriptor=Regex.Replace(descriptor,@"_enchant$","");
        descriptor=descriptor.Replace("affixreroll","affix reroll").Replace("ascendantreroll","ascendant reroll")
            .Replace("hairlong","long hair").Replace("hairshort","short hair")
            .Replace('_',' ').Replace('-',' ');
        descriptor=CultureInfo.InvariantCulture.TextInfo.ToTitleCase(descriptor);
        // Alphanumeric variant identifiers distinguish otherwise unnamed records.
        descriptor=Regex.Replace(descriptor,@"\b[a-z]*\d+[a-z\d]*\b",m=>m.Value.ToUpperInvariant(),RegexOptions.IgnoreCase);
        return item with {Name=$"{type} — {descriptor} (Name unavailable)"};
    }
}
