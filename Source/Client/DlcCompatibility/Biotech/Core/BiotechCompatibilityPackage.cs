using AIRsLight.ClashOfRim.DlcCompatibility.Biotech.Pawns;
using AIRsLight.ClashOfRim.DlcCompatibility.Biotech.RemoteMaps;
using AIRsLight.ClashOfRim.DlcCompatibility.Biotech.Items;
using AIRsLight.ClashOfRim.Modules.Compatibility.Biotech;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.RemoteMaps;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility;
using HarmonyLib;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim.DlcCompatibility;

internal static class BiotechCompatibilityPackage
{
    private static ClientSessionLifecycle? session;
    internal static void Attach(ClientSessionLifecycle? value) => session = value;
    private static readonly BiotechThingReferenceCompatibility ThingReferences = new(TradeMetadataEnabled, new BiotechReproductiveSources(CurrentUserId));
    private static readonly BiotechThingReferenceDisplay ThingDisplay = new(TradeMetadataEnabled);
    private static readonly BiotechThingReferenceEditor ThingEditor = new(TradeMetadataEnabled);
    private static readonly BiotechPawnExchangeCompatibility PawnExchange = new(PawnExchangeEnabled, text => Log.Warning(text));
    private static readonly BiotechRemoteMapProjection Projection = new(PawnExchangeEnabled, WorldPollutionEnabled, text => ClashLog.Message(text));
    private static readonly BiotechRemoteMapRuntimeSanitizer MapRuntime = new(PawnExchangeEnabled, text => ClashLog.Message(text));
    private static readonly BiotechDefensePoints DefensePoints = new(() => PawnExchangeEnabled() || TradeMetadataEnabled() || WorldPollutionEnabled());

    private static bool PawnExchangeEnabled() => ClashOfRimCompatibilityApi.HasCompatibilityCapability(BiotechCompatibilityKeys.PawnExchange);
    private static bool WorldPollutionEnabled() => ClashOfRimCompatibilityApi.HasCompatibilityCapability(BiotechCompatibilityKeys.WorldPollution);

    private static bool TradeMetadataEnabled() => ClashOfRimCompatibilityApi.HasCompatibilityCapability(BiotechCompatibilityKeys.TradeMetadata);
    private static string? CurrentUserId() => session?.ReadContext().UserId;

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

        RegisterCapabilities();
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

    internal static void RegisterCapabilities()
    {
        ClashOfRimCompatibilityApi.RegisterRemoteMapProjectionSanitizer((package, map, pawns) => Projection.Sanitize(map, pawns));
        ClashOfRimCompatibilityApi.RegisterRemoteMapLoadedHandler((map, carrier, scope, package) => MapRuntime.Sanitize(map));
        ClashOfRimCompatibilityApi.RegisterRemoteMapAreaLoadIdProvider(Projection.BuildAreaLoadId);
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
        ClashOfRimCompatibilityApi.RegisterPawnExchangeExtensionAppender(PawnExchange.Append);
        ClashOfRimCompatibilityApi.RegisterPawnExchangePackageRegistrar(PawnExchange.TryRegister);
        ClashOfRimCompatibilityApi.RegisterPawnPreviewRestorer(PawnExchange.RestorePreview);
        ClashOfRimCompatibilityApi.RegisterPawnPostRestoreLocalizer(PawnExchange.ResetRestoredPawnGeneCaches);
        RegisterItemCapabilities();
        BiotechThingTransferCompatibility.Apply();
        ClashOfRimCompatibilityApi.RegisterDefensePointDefNameProvider(DefensePoints.DefNames);
    }
}
