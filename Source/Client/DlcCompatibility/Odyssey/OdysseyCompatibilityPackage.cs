using AIRsLight.ClashOfRim.ThirdPartyCompatibility;
using HarmonyLib;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim.DlcCompatibility;

internal static class OdysseyCompatibilityPackage
{
    public static BuiltInDlcCompatibilityPackageDescriptor Descriptor { get; } =
        new(OdysseyCompatibilityKeys.PackageId, Apply, order: 500);

    private static bool Active => ModLister.OdysseyInstalled && ModsConfig.OdysseyActive;

    internal static bool HasRemoteStateGuards =>
        ClashOfRimCompatibilityApi.HasCompatibilityCapability(OdysseyCompatibilityKeys.RemoteStateGuards);

    private static void Apply(Harmony harmony)
    {
        ClashOfRimCompatibilityApi.RegisterCompatibilityPackage(
            OdysseyCompatibilityKeys.PackageId,
            () => Active,
            new[]
            {
                OdysseyCompatibilityKeys.RemoteStateGuards,
                OdysseyCompatibilityKeys.TradeMetadata,
                OdysseyCompatibilityKeys.WeaponTraitMarketValueBaseline
            });

        if (Active)
        {
            ClashOfRimCompatibilityApi.RegisterPlayerColonySiteRegistrationSuppressor(
                () => OdysseyGravshipLandingSnapshotPatch.SuppressPlayerColonySiteRegistration);
            ClashOfRimCompatibilityApi.RegisterThingReferenceEditor(
                OdysseyCompatibilityKeys.TradeMetadata + ".editor",
                OdysseyThingReferenceCompatibility.DrawThingReferenceEditor,
                OdysseyThingReferenceCompatibility.ClearThingReferenceMetadata,
                OdysseyThingReferenceCompatibility.IsThingReferenceComplete);
            ClashOfRimCompatibilityApi.RegisterThingReferenceUniqueRequestPredicate(OdysseyThingReferenceCompatibility.IsUniqueThingReferenceRequest);
            ClashOfRimCompatibilityApi.RegisterThingReferenceDefaultMetadataProvider(OdysseyThingReferenceCompatibility.ApplyDefaultThingReferenceMetadata);
            ClashOfRimCompatibilityApi.RegisterAdminBaselineExtensionProvider(
                OdysseyCompatibilityKeys.PackageId,
                OdysseyCompatibilityKeys.WeaponTraitMarketValueBaseline,
                OdysseyThingReferenceCompatibility.BuildWeaponTraitMarketValueBaseline);
            OdysseyGravshipLandingSnapshotPatch.Apply(harmony);
        }
    }
}
