using AIRsLight.ClashOfRim.ThirdPartyCompatibility;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim.DlcCompatibility;

internal static class RoyaltyCompatibilityPackage
{
    public static BuiltInDlcCompatibilityPackageDescriptor Descriptor { get; } =
        new(RoyaltyCompatibilityKeys.PackageId, _ => Apply(), order: 100);

    private static bool Active => ModLister.RoyaltyInstalled && ModsConfig.RoyaltyActive;

    private static void Apply()
    {
        ClashOfRimCompatibilityApi.RegisterCompatibilityPackage(
            RoyaltyCompatibilityKeys.PackageId,
            () => Active,
            new[]
            {
                RoyaltyCompatibilityKeys.RemoteStateGuards,
                RoyaltyCompatibilityKeys.TradeMetadata,
                RoyaltyCompatibilityKeys.WeaponTraitMarketValueBaseline
            });

        if (!Active)
        {
            return;
        }

        ClashOfRimCompatibilityApi.RegisterRemoteMapProjectionSanitizer(RoyaltyPawnReferenceCompatibility.SanitizeRoyaltyRemoteMapProjection);
        ClashOfRimCompatibilityApi.RegisterPawnReferenceMetadataCollector(RoyaltyPawnReferenceCompatibility.CollectRoyaltyPawnReferenceMetadata);
        ClashOfRimCompatibilityApi.RegisterPawnReferenceMetadataResolver(
            RoyaltyPawnReferenceCompatibility.PawnMetadataRoyaltyState,
            RoyaltyPawnReferenceCompatibility.ResolveRoyaltyPawnReferenceMetadata);
        ClashOfRimCompatibilityApi.RegisterPawnReferenceMetadataRestorer(
            RoyaltyPawnReferenceCompatibility.PawnMetadataRoyaltyState,
            RoyaltyPawnReferenceCompatibility.RestoreRoyaltyPawnReferenceMetadata);
        ClashOfRimCompatibilityApi.RegisterPawnExchangeExtensionAppender(RoyaltyPawnReferenceCompatibility.AppendRoyaltyPawnExchangeExtension);
        ClashOfRimCompatibilityApi.RegisterThingReferenceEditor(
            RoyaltyCompatibilityKeys.TradeMetadata + ".persona-weapon-editor",
            RoyaltyThingReferenceCompatibility.DrawThingReferenceEditor,
            RoyaltyThingReferenceCompatibility.ClearThingReferenceMetadata,
            RoyaltyThingReferenceCompatibility.IsThingReferenceComplete);
        ClashOfRimCompatibilityApi.RegisterThingReferenceUniqueRequestPredicate(RoyaltyThingReferenceCompatibility.IsPersonaThingReferenceRequest);
        ClashOfRimCompatibilityApi.RegisterThingReferenceDefaultMetadataProvider(RoyaltyThingReferenceCompatibility.ApplyDefaultThingReferenceMetadata);
        ClashOfRimCompatibilityApi.RegisterAdminBaselineExtensionProvider(
            RoyaltyCompatibilityKeys.PackageId,
            RoyaltyCompatibilityKeys.WeaponTraitMarketValueBaseline,
            RoyaltyThingReferenceCompatibility.BuildWeaponTraitMarketValueBaseline);
    }
}
