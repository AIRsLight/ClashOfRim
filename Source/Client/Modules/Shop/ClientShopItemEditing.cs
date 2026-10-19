using AIRsLight.ClashOfRim.ClientNetwork;

namespace AIRsLight.ClashOfRim.Modules.Shop;

internal static class ClientShopItemEditing
{
    internal static void SetStuff(ModThingReferenceDto item, string? value)
    {
        if (!string.IsNullOrWhiteSpace(item.MinifiedInnerDefName)) item.MinifiedInnerStuffDefName = value;
        else item.StuffDefName = value;
    }
    internal static void SetQuality(ModThingReferenceDto item, string? value)
    {
        item.Quality = value;
        if (!string.IsNullOrWhiteSpace(item.MinifiedInnerDefName)) item.MinifiedInnerQuality = value;
    }
    internal static void SetHitPoints(ModThingReferenceDto item, int value)
    {
        item.HitPoints = value;
        if (!string.IsNullOrWhiteSpace(item.MinifiedInnerDefName)) item.MinifiedInnerHitPoints = value;
    }
}
