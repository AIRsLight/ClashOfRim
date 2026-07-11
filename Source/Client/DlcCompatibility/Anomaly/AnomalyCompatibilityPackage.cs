using AIRsLight.ClashOfRim.ThirdPartyCompatibility;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim.DlcCompatibility;

internal static class AnomalyCompatibilityPackage
{
    public static BuiltInDlcCompatibilityPackageDescriptor Descriptor { get; } =
        new(AnomalyCompatibilityKeys.PackageId, _ => Apply(), order: 400);

    private static bool Active => ModLister.AnomalyInstalled && ModsConfig.AnomalyActive;

    private static void Apply()
    {
        ClashOfRimCompatibilityApi.RegisterCompatibilityPackage(
            AnomalyCompatibilityKeys.PackageId,
            () => Active,
            new[]
            {
                AnomalyCompatibilityKeys.RemoteStateGuards,
                AnomalyCompatibilityKeys.TradeMetadata
            });

        if (!Active)
        {
            return;
        }

        AnomalyThingTransferCompatibility.Apply();
        ClashOfRimCompatibilityApi.RegisterThingReferenceDefaultMetadataProvider(
            AnomalyTradeThingReferenceCompatibility.ApplyDefaultThingReferenceMetadata);
        ClashOfRimCompatibilityApi.RegisterThingReferenceMetadata(
            AnomalyCompatibilityKeys.PackageId + ".trade-book-kind",
            AnomalyTradeThingReferenceCompatibility.AppendThingReferenceMetadata,
            AnomalyTradeThingReferenceCompatibility.ThingReferenceMatches,
            AnomalyTradeThingReferenceCompatibility.ThingReferenceDtoMatches,
            AnomalyTradeThingReferenceCompatibility.TryApplyThingReferenceMetadata,
            AnomalyTradeThingReferenceCompatibility.ThingReferenceStrictness);
        ClashOfRimCompatibilityApi.RegisterThingReferenceDisplay(
            AnomalyCompatibilityKeys.PackageId + ".trade-book-kind-display",
            AnomalyTradeThingReferenceCompatibility.AppendThingReferenceDisplayParts,
            AnomalyTradeThingReferenceCompatibility.SuppressesStandardThingStats);
        ClashOfRimCompatibilityApi.RegisterThingReferenceCacheKeyPartsProvider(
            AnomalyTradeThingReferenceCompatibility.ThingReferenceCacheKeyParts);
    }
}
