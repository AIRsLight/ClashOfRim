using AIRsLight.ClashOfRim.RemoteMaps;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility;
using HarmonyLib;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim.DlcCompatibility;

internal static class BiotechCompatibilityPackage
{
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

        ClashOfRimCompatibilityApi.RegisterRemoteMapIntegerLoadIdRewriter(
            "Gene_",
            BiotechCompatibility.IsBiotechGeneLoadIdNode,
            BiotechCompatibility.NextBiotechGeneLoadId);
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
        ClashOfRimCompatibilityApi.RegisterPawnPostRestoreLocalizer(BiotechCompatibility.LocalizePawnGenes);
        ClashOfRimCompatibilityApi.RegisterThingReferenceEditor(
            BiotechCompatibilityKeys.TradeMetadata + ".editor",
            BiotechCompatibility.DrawThingReferenceEditor,
            BiotechCompatibility.ClearThingReferenceMetadata,
            BiotechCompatibility.IsThingReferenceComplete);
        ClashOfRimCompatibilityApi.RegisterThingReferenceUniqueRequestPredicate(BiotechCompatibility.IsUniqueThingReferenceRequest);
        ClashOfRimCompatibilityApi.RegisterThingReferenceMetadata(
            BiotechCompatibilityKeys.TradeMetadata,
            BiotechCompatibility.AppendThingReferenceMetadata,
            BiotechCompatibility.ThingReferenceMatches,
            BiotechCompatibility.ThingReferenceDtoMatches,
            BiotechCompatibility.TryApplyThingReferenceMetadata,
            BiotechCompatibility.ThingReferenceStrictness);
        ClashOfRimCompatibilityApi.RegisterThingReferenceDisplay(
            BiotechCompatibilityKeys.TradeMetadata + ".display",
            BiotechCompatibility.AppendThingReferenceDisplayParts,
            BiotechCompatibility.SuppressesStandardThingStats);
        ClashOfRimCompatibilityApi.RegisterThingReferenceCacheKeyPartsProvider(BiotechCompatibility.ThingReferenceCacheKeyParts);
        ClashOfRimCompatibilityApi.RegisterThingReferenceMetadataNormalizer(BiotechCompatibility.NormalizeBiotechThingReferenceMetadata);
        ClashOfRimCompatibilityApi.RegisterDefensePointDefNameProvider(BiotechCompatibility.BiotechDefensePointDefNames);
        RemoteSessionGlobalStateGuard.RegisterRemoteMapRemovalGlobalEffectsSuppressedHandler(
            RemoteSessionBiotechDissolutionEffectState.ClearPendingRemoteEffectsAfterSuppression);

        BiotechRemoteSessionMechanitorNotificationPatch.Apply(harmony);
    }
}
