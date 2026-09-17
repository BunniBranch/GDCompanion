namespace GrimDawnCompanion.Core;

[Flags]
public enum PoiFilter
{
    None = 0, Travel = 1, Vendors = 2, Smugglers = 4, Illusionists = 8,
    DevotionShrines = 16, MonsterShrines = 32, Chests = 64, Lore = 128,
    Npcs = 256, Other = 512, Blacksmiths = 1024, SpiritGuides = 2048,
    AscensionAltars = 4096, All = 8191
}

public static class PoiFilters
{
    public static PoiFilter Group(string category) => category switch
    {
        "Travel Utility" or "Riftgate" => PoiFilter.Travel,
        "Vendor" => PoiFilter.Vendors,
        "Smuggler" => PoiFilter.Smugglers,
        "Illusionist" => PoiFilter.Illusionists,
        "Devotion Shrine" => PoiFilter.DevotionShrines,
        "Monster Shrine" => PoiFilter.MonsterShrines,
        "One-Shot Chest" => PoiFilter.Chests,
        "Lore Note" => PoiFilter.Lore,
        "NPC or Site" => PoiFilter.Npcs,
        "Blacksmith" => PoiFilter.Blacksmiths,
        "Spirit Guide" => PoiFilter.SpiritGuides,
        "Ascension Altar" => PoiFilter.AscensionAltars,
        _ => PoiFilter.Other
    };
    public static bool Allows(PoiFilter enabled, string category) => (enabled & Group(category)) != 0;
}
