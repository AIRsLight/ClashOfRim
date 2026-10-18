using AIRsLight.ClashOfRim.DlcCompatibility.Biotech.Items;
using AIRsLight.ClashOfRim.Modules.Compatibility.Biotech;
using AIRsLight.ClashOfRim.RemoteMaps;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility;
using HarmonyLib;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim.DlcCompatibility;

internal static class BiotechCompatibilityPackage
{
    private static readonly BiotechThingReferenceCompatibility ThingReferences = new(TradeMetadataEnabled, new BiotechReproductiveSources(CurrentUserId));
    private static readonly BiotechThingReferenceDisplay ThingDisplay = new(TradeMetadataEnabled);
    private static readonly BiotechThingReferenceEditor ThingEditor = new(TradeMetadataEnabled);

    private static bool TradeMetadataEnabled() => ClashOfRimCompatibilityApi.HasCompatibilityCapability(BiotechCompatibilityKeys.TradeMetadata);
    private static string? CurrentUserId() => (ClashOfRimMod.Instance ?? LoadedModManager.GetMod<ClashOfRimMod>())?.UserId;

    public static BuiltInDlcCompatibilityPackageDescriptor Descriptor { get; } =
        new(BiotechCompatibilityKeys.PackageId, Apply, order: 300);

    private static bool Active => ModLister.BiotechInstalled && ModsConfig.BiotechActive;

    private static void Apply(Harmony harmony)
    {
        ClashOfRimCompatibilityApi.RegisterCompatibilityPackage(
            BiotechCompatibilityKeys.PackageId,
            () => Active,
            new[]
            {
                BiotechCompatibilityKeys.PawnExchange,
                BiotechCompatibilityKeys.TradeMetadata,
                BiotechCompatibilityKeys.WorldGeneration,
                BiotechCompatibilityKeys.WorldPollution
            });

        if (!Active)
        {
            return;
        }

        ClashOfRimCompatibilityApi.RegisterRemoteMapProjectionSanitizer(BiotechCompatibility.SanitizeBiotechRemoteMapProjection);
        ClashOfRimCompatibilityApi.RegisterRemoteMapLoadedHandler(BiotechCompatibility.SanitizeBiotechRemoteMapLoaded);
        ClashOfRimCompatibilityApi.RegisterRemoteMapAreaLoadIdProvider(BiotechCompatibility.BuildBiotechAreaLoadId);
        ClashOfRimCompatibilityApi.RegisterWorldConfigurationExtensionHandler(
            BiotechCompatibilityKeys.PackageId,
            BiotechCompatibilityKeys.WorldGeneration,
            BiotechWorldConfigurationCompatibility.CollectWorldGenerationExtension,
            applier: null);
        ClashOfRimCompatibilityApi.RegisterWorldConfigurationExtensionHandler(
            BiotechCompatibilityKeys.PackageId,
            BiotechCompatibilityKeys.WorldPollution,
            BiotechWorldConfigurationCompatibility.CollectWorldPollutionExtension,
            BiotechWorldConfigurationCompatibility.ApplyWorldPollutionExtension);
        ClashOfRimCompatibilityApi.RegisterWorldConfigurationExtensionSummaryProvider(BiotechWorldConfigurationCompatibility.WorldPollutionSummary);
        ClashOfRimCompatibilityApi.RegisterWorldGenerationFloatSettingProvider("pollution", BiotechWorldConfigurationCompatibility.TryResolveWorldGenerationPollution);
        ClashOfRimCompatibilityApi.RegisterPawnExchangeExtensionAppender(BiotechCompatibility.AppendBiotechPawnExchangeExtension);
        ClashOfRimCompatibilityApi.RegisterPawnExchangePackageRegistrar(BiotechCompatibility.TryRegisterBiotechPawnExchangePayload);
        ClashOfRimCompatibilityApi.RegisterPawnExchangePackageNormalizer(BiotechCompatibility.NormalizeBiotechPawnExchangePackage);
        ClashOfRimCompatibilityApi.RegisterPawnPostRestoreLocalizer(BiotechCompatibility.ResetRestoredPawnGeneCaches);
        RegisterItemCapabilities();
        BiotechThingTransferCompatibility.Apply();
        ClashOfRimCompatibilityApi.RegisterDefensePointDefNameProvider(BiotechCompatibility.BiotechDefensePointDefNames);
        RemoteSessionGlobalStateGuard.RegisterRemoteMapRemovalGlobalEffectsSuppressedHandler(
            RemoteSessionBiotechDissolutionEffectState.ClearPendingRemoteEffectsAfterSuppression);

        BiotechRemoteSessionMechanitorNotificationPatch.Apply(harmony);
    }

    internal static void RegisterItemCapabilities()
    {
        ClashOfRimCompatibilityApi.RegisterThingReferenceEditor(
            BiotechCompatibilityKeys.TradeMetadata + ".editor",
            ThingEditor.DrawThingReferenceEditor,
            ThingReferences.ClearThingReferenceMetadata,
            ThingReferences.IsThingReferenceComplete);
        ClashOfRimCompatibilityApi.RegisterThingReferenceUniqueRequestPredicate(ThingReferences.IsUniqueThingReferenceRequest);
        ClashOfRimCompatibilityApi.RegisterThingReferenceMetadata(
            BiotechCompatibilityKeys.TradeMetadata,
            ThingReferences.AppendThingReferenceMetadata,
            ThingReferences.ThingReferenceMatches,
            ThingReferences.ThingReferenceDtoMatches,
            ThingReferences.TryApplyThingReferenceMetadata,
            ThingReferences.ThingReferenceStrictness);
        ClashOfRimCompatibilityApi.RegisterThingReferenceDisplay(
            BiotechCompatibilityKeys.TradeMetadata + ".display",
            ThingDisplay.AppendThingReferenceDisplayParts,
            ThingDisplay.SuppressesStandardThingStats);
        ClashOfRimCompatibilityApi.RegisterThingReferenceCacheKeyPartsProvider(ThingDisplay.ThingReferenceCacheKeyParts);
        ClashOfRimCompatibilityApi.RegisterThingReferenceMetadataNormalizer(BiotechThingMetadata.Normalize);
    }
}
