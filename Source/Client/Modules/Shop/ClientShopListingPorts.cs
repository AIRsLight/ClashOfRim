using System.Collections.Generic;
using AIRsLight.ClashOfRim.ClientNetwork;

namespace AIRsLight.ClashOfRim.Modules.Shop;

internal sealed record ClientShopDefinition(string DefName, string Label);
internal sealed record ClientShopItemFeatures(bool Stuff, bool Quality, bool HitPoints, int HitPointsPercent);
internal sealed record ClientShopItemValue(float UnitPrice, string UnitLabel);

internal interface IClientShopListingRuntime
{
    IReadOnlyList<ClientShopDefinition> Catalog { get; }
    object? PresentationOwner { get; }
    ModThingReferenceDto Create(string defName, string surface);
    void ApplyDefaults(ModThingReferenceDto item, string surface);
    void Normalize(ModThingReferenceDto item, string surface);
    bool IsComplete(ModThingReferenceDto item, string surface);
    ClientShopItemFeatures Features(ModThingReferenceDto item);
    IReadOnlyList<ClientShopDefinition> Materials(ModThingReferenceDto item);
    int HitPointsFromPercent(ModThingReferenceDto item, int percent);
    IReadOnlyList<string> ValueKeyParts(ModThingReferenceDto item);
    ClientShopItemValue Estimate(ModThingReferenceDto item);
}
