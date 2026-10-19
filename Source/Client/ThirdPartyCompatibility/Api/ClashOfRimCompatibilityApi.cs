using AIRsLight.ClashOfRim.ThirdPartyCompatibility.RemoteMaps;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility.Pawns;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility.Things;
using AIRsLight.ClashOfRim.Modules.Compatibility;
using AIRsLight.ClashOfRim.Modules.Administration;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.World;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.RemoteMaps;
using System;
using Verse;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using AIRsLight.ClashOfRim.Raids;
using RimWorld.Planet;
using System.Xml.Linq;

namespace AIRsLight.ClashOfRim.ThirdPartyCompatibility;

public static class ClashOfRimCompatibilityApi
{
    // Composition binds each existing domain directly. Public compatibility
    // entry points do not own a second session, permission or catalog state.
    private static ClientSessionLifecycle? session;
    private static ClientAdminAccess? administration;
    private static ClientWorldCatalog? catalog;
    internal static void AttachSession(ClientSessionLifecycle? value) => session = value;
    internal static void AttachAdministration(ClientAdminAccess? value) => administration = value;
    internal static void AttachCatalog(ClientWorldCatalog? value) => catalog = value;
    private static readonly CompatibilityRegistrationScope Registrations = new(message => Log.Warning(message));
    private static readonly AdminBaselineCompatibility AdminBaseline = new(Registrations);
    private static readonly WorldConfigurationCompatibility WorldConfiguration = new(Registrations);
    private static readonly WorldGenerationSettingCompatibility WorldGenerationSettings = new(Registrations);
    private static readonly CompatibilityPackageCatalog CompatibilityPackages = new(Registrations);
    private static readonly RemoteMapLifecycleCompatibility RemoteMapLifecycle = new(Registrations);
    private static readonly RemoteMapProjectionCompatibility RemoteMapProjection = new(Registrations);
    private static readonly SnapshotSanitizationCompatibility SnapshotSanitization = new(Registrations);
    private static readonly ColonySiteCompatibility ColonySite = new(Registrations);
    private static readonly FactionPreparationCompatibility FactionPreparation = new(Registrations);
    private static readonly PawnReferenceCompatibility PawnReferences = new(Registrations);
    private static readonly PawnExchangeCompatibility PawnExchange = new(Registrations, message => ClashLog.Message(message));
    private static readonly PawnTradeCompatibility PawnTrade = new(Registrations);
    private static readonly ThingReferenceCompatibility ThingReferences = new(Registrations);
    private static readonly ThingTransferCompatibility ThingTransfers = new(Registrations);

    public static bool IsRemoteMapProjectionLoading => RemoteMapProjectionLoadScope.Active;

    public static bool HasSnapshotSaveSanitizers => SnapshotSanitization.HasSnapshotSaveSanitizers;

    public static CompatibilityRegistrationToken BeginRegistrationCycle(string owner) => Registrations.BeginRegistrationCycle(owner);
    public static CompatibilityRegistrationToken BeginRegistrationCycle(string owner, int priority) => Registrations.BeginRegistrationCycle(owner, priority);
    public static int CurrentRegistrationGeneration => Registrations.CurrentRegistrationGeneration;
    public static CompatibilityRegistrationToken CreateRegistrationToken(string owner) => Registrations.CreateRegistrationToken(owner);
    public static CompatibilityRegistrationToken CreateRegistrationToken(string owner, int priority) => Registrations.CreateRegistrationToken(owner, priority);
    public static bool UseRegistrationToken(CompatibilityRegistrationToken token, Action registration) => Registrations.UseRegistrationToken(token, registration);
    public static bool RevokeRegistrationToken(CompatibilityRegistrationToken? token) => Registrations.RevokeRegistrationToken(token);
    public static int RevokeRegistrationsByOwner(string owner) => Registrations.RevokeRegistrationsByOwner(owner);

    public static bool IsActiveMultiplayerSession => ClientMultiplayerWorldStatus.IsActive(session);

    public static bool IsCurrentUserAdministrator
    {
        get
        {
            try
            {
                return administration?.IsAdministrator == true;
            }
            catch
            {
                return false;
            }
        }
    }

    public static bool RequestServerWorldBaselineRefresh(string reason)
        => RequestWorldBaseline(() => reason);

    public static bool RequestServerWorldBaselineRefreshByKey(string translationKey)
        => RequestWorldBaseline(() => ClashOfRimText.Key(translationKey));

    private static bool RequestWorldBaseline(Func<string> reason)
    {
        try
        {
            var current = catalog;
            return current is not null && current.TryRefresh(reason, generation: true, () => ReferenceEquals(catalog, current), out _);
        }
        catch
        {
            return false;
        }
    }

    public static bool HasWorldConfigurationExtensionCollector => WorldConfiguration.HasWorldConfigurationExtensionCollector;

    public static void RegisterAdminBaselineExtensionProvider(
        string providerId,
        string kind,
        AdminBaselineExtensionProvider provider) =>
        AdminBaseline.RegisterAdminBaselineExtensionProvider(providerId, kind, provider);

    public static void RegisterCompatibilityPackage(
        string packageId,
        Func<bool> isLocallyActive,
        IReadOnlyCollection<string> capabilities) =>
        CompatibilityPackages.RegisterCompatibilityPackage(packageId, isLocallyActive, capabilities);

    public static void ApplyServerDlcBaseline(IEnumerable<string>? dlcIds) =>
        CompatibilityPackages.ApplyServerDlcBaseline(dlcIds);

    public static bool HasCompatibilityCapability(string capability) =>
        CompatibilityPackages.HasCompatibilityCapability(capability);

    public static void RegisterWorldConfigurationExtensionHandler(
        string providerId,
        string kind,
        WorldConfigurationExtensionCollector? collector,
        WorldConfigurationExtensionApplier? applier) =>
        WorldConfiguration.RegisterWorldConfigurationExtensionHandler(providerId, kind, collector, applier);

    public static void RegisterWorldConfigurationExtensionSummaryProvider(WorldConfigurationExtensionSummaryProvider provider) =>
        WorldConfiguration.RegisterWorldConfigurationExtensionSummaryProvider(provider);

    public static void RegisterWorldGenerationFloatSettingProvider(string settingKey, WorldGenerationFloatSettingProvider provider) =>
        WorldGenerationSettings.RegisterWorldGenerationFloatSettingProvider(settingKey, provider);

    public static float ResolveWorldGenerationFloatSetting(
        ModWorldConfigurationDto configuration,
        string settingKey,
        float defaultValue) =>
        WorldGenerationSettings.ResolveWorldGenerationFloatSetting(configuration, settingKey, defaultValue);

    public static IReadOnlyList<ModWorldConfigurationExtensionDto> CollectCurrentWorldConfigurationExtensions(
        string? userId,
        string? colonyId,
        string worldConfigurationId) =>
        WorldConfiguration.CollectCurrentWorldConfigurationExtensions(userId, colonyId, worldConfigurationId);

    public static int ApplyWorldConfigurationExtensions(
        ModWorldConfigurationDto configuration,
        string? localUserId,
        bool applyWorldState) =>
        WorldConfiguration.ApplyWorldConfigurationExtensions(configuration, localUserId, applyWorldState);

    public static IReadOnlyList<WorldConfigurationExtensionSummaryItem> GetWorldConfigurationExtensionSummary(
        ModWorldConfigurationDto configuration) =>
        WorldConfiguration.GetWorldConfigurationExtensionSummary(configuration);

    public static void AppendAdminBaselineExtensions(
        ModSubmitAdminBaselineRequestDto baseline,
        IEnumerable<ModAdminBaselineExtensionRequirementDto>? requirements) =>
        AdminBaseline.AppendAdminBaselineExtensions(baseline, requirements);

    public static void RegisterFactionPreparedHandler(FactionPreparedHandler handler) =>
        FactionPreparation.RegisterFactionPreparedHandler(handler);

    public static void NotifyFactionPrepared(Faction faction, string purpose, string? ownerUserId = null) =>
        FactionPreparation.NotifyFactionPrepared(faction, purpose, ownerUserId);

    public static void RegisterPlayerProxyFactionPreparedHandler(PlayerProxyFactionPreparedHandler handler) =>
        FactionPreparation.RegisterPlayerProxyFactionPreparedHandler(handler);

    public static void NotifyPlayerProxyFactionPrepared(Faction faction, string ownerUserId) =>
        FactionPreparation.NotifyPlayerProxyFactionPrepared(faction, ownerUserId);

    public static void RegisterPawnReferenceMetadataResolver(string metadataKey, PawnReferenceMetadataResolver resolver) =>
        PawnReferences.RegisterPawnReferenceMetadataResolver(metadataKey, resolver);

    public static void RegisterPawnReferenceMetadataCollector(PawnReferenceMetadataCollector collector) =>
        PawnReferences.RegisterPawnReferenceMetadataCollector(collector);

    public static void CollectPawnReferenceMetadata(
        Pawn pawn,
        Dictionary<string, string?> metadata,
        string? userId,
        string? colonyId) =>
        PawnReferences.CollectPawnReferenceMetadata(pawn, metadata, userId, colonyId);

    public static string? ResolvePawnReferenceMetadata(Pawn pawn, string metadataKey, string? userId, string? colonyId) =>
        PawnReferences.ResolvePawnReferenceMetadata(pawn, metadataKey, userId, colonyId);

    public static void RegisterPawnReferenceMetadataRestorer(string metadataKey, PawnReferenceMetadataRestorer restorer) =>
        PawnReferences.RegisterPawnReferenceMetadataRestorer(metadataKey, restorer);

    public static string RestorePawnReferenceMetadata(Pawn pawn, string metadataKey, string? metadataValue, string label) =>
        PawnReferences.RestorePawnReferenceMetadata(pawn, metadataKey, metadataValue, label);

    public static string RestorePawnReferenceMetadata(
        Pawn pawn,
        IReadOnlyDictionary<string, string?>? metadata,
        string label) =>
        PawnReferences.RestorePawnReferenceMetadata(pawn, metadata, label);

    public static void RegisterPawnExchangeExtensionAppender(PawnExchangeExtensionAppender appender) =>
        PawnExchange.RegisterPawnExchangeExtensionAppender(appender);

    public static void AppendPawnExchangeExtensions(Pawn pawn, ModPawnExchangePackageDto package) =>
        PawnExchange.AppendPawnExchangeExtensions(pawn, package);

    public static void RegisterPawnExchangePackageRegistrar(PawnExchangePackageRegistrar registrar) =>
        PawnExchange.RegisterPawnExchangePackageRegistrar(registrar);

    public static void RegisterPawnExchangePackageNormalizer(PawnExchangePackageNormalizer normalizer) =>
        PawnExchange.RegisterPawnExchangePackageNormalizer(normalizer);

    public static void RegisterPawnExchangeLocalSaveOnlyLoadIdPredicate(PawnExchangeLocalSaveOnlyLoadIdPredicate predicate) =>
        PawnExchange.RegisterPawnExchangeLocalSaveOnlyLoadIdPredicate(predicate);

    public static void NormalizePawnExchangePackage(ModPawnExchangePackageDto package) =>
        PawnExchange.NormalizePawnExchangePackage(package);

    public static bool TryRegisterPawnExchangePackageExtensions(ModPawnExchangePackageDto package, out string message) =>
        PawnExchange.TryRegisterPawnExchangePackageExtensions(package, out message);

    public static void RegisterPawnPreviewRestorer(PawnPreviewRestorer restorer) => PawnExchange.RegisterPawnPreviewRestorer(restorer);

    internal static bool TryRestorePawnPreview(Pawn pawn, ModPawnExchangePackageDto package, out string message) =>
        PawnExchange.TryRestorePawnPreview(pawn, package, out message);

    public static bool IsPawnExchangeLocalSaveOnlyLoadId(string? loadId) =>
        PawnExchange.IsPawnExchangeLocalSaveOnlyLoadId(loadId);

    public static void RegisterPawnPostRestoreLocalizer(PawnPostRestoreLocalizer localizer) =>
        PawnExchange.RegisterPawnPostRestoreLocalizer(localizer);

    public static void LocalizeRestoredPawnWithCompatibility(Pawn pawn) =>
        PawnExchange.LocalizeRestoredPawnWithCompatibility(pawn);

    public static void RegisterPawnSoldEffectHandler(PawnSoldEffectHandler handler) =>
        PawnTrade.RegisterPawnSoldEffectHandler(handler);

    public static void ApplyPawnSoldEffects(Pawn pawn, Pawn? negotiator) =>
        PawnTrade.ApplyPawnSoldEffects(pawn, negotiator);

    public static void RegisterTradeablePawnPredicate(TradeablePawnPredicate predicate) =>
        PawnTrade.RegisterTradeablePawnPredicate(predicate);

    public static bool IsTradeablePawnByCompatibility(Pawn pawn) =>
        PawnTrade.IsTradeablePawnByCompatibility(pawn);

    public static void RegisterTradePawnRestoreValidator(TradePawnRestoreValidator validator) =>
        PawnTrade.RegisterTradePawnRestoreValidator(validator);

    public static bool IsTradePawnRestoreAllowedByCompatibility(Pawn pawn) =>
        PawnTrade.IsTradePawnRestoreAllowedByCompatibility(pawn);

    public static void RegisterThingTransferRule(
        string key,
        ThingTransferValidator? validator,
        ThingTransferMetadataCapturer? capturer,
        ThingTransferFinalizer? finalizer) =>
        ThingTransfers.RegisterThingTransferRule(key, validator, capturer, finalizer);

    public static bool CanTransferThing(
        Thing thing,
        ThingTransferContext context,
        out string? rejectionCode) =>
        ThingTransfers.CanTransferThing(thing, context, out rejectionCode);

    public static bool PrepareThingTransfer(
        Thing thing,
        ModThingReferenceDto reference,
        ThingTransferContext context,
        out string? rejectionCode) =>
        ThingTransfers.PrepareThingTransfer(thing, reference, context, out rejectionCode);

    public static bool FinalizeThingTransfer(
        ModThingReferenceDto reference,
        Thing thing,
        ThingTransferContext context,
        out string? missingDefName) =>
        ThingTransfers.FinalizeThingTransfer(reference, thing, context, out missingDefName);

    public static void RegisterThingReferenceEditor(
        ThingReferenceEditorHandler editor,
        ThingReferenceMetadataCleaner cleaner,
        ThingReferenceCompletenessValidator validator) =>
        ThingReferences.RegisterThingReferenceEditor(editor, cleaner, validator);

    public static void RegisterThingReferenceEditor(
        string key,
        ThingReferenceEditorHandler editor,
        ThingReferenceMetadataCleaner cleaner,
        ThingReferenceCompletenessValidator validator) =>
        ThingReferences.RegisterThingReferenceEditor(key, editor, cleaner, validator);

    public static void RegisterThingReferenceUniqueRequestPredicate(ThingReferenceUniqueRequestPredicate predicate) =>
        ThingReferences.RegisterThingReferenceUniqueRequestPredicate(predicate);

    public static void RegisterThingReferenceDefaultMetadataProvider(ThingReferenceDefaultMetadataProvider provider) =>
        ThingReferences.RegisterThingReferenceDefaultMetadataProvider(provider);

    public static void RegisterThingReferenceMetadata(
        ThingReferenceMetadataAppender appender,
        ThingReferenceThingMatcher thingMatcher,
        ThingReferenceDtoMatcher dtoMatcher,
        ThingReferenceMetadataApplier applier,
        ThingReferenceStrictnessProvider strictnessProvider) =>
        ThingReferences.RegisterThingReferenceMetadata(appender, thingMatcher, dtoMatcher, applier, strictnessProvider);

    public static void RegisterThingReferenceMetadata(
        string key,
        ThingReferenceMetadataAppender appender,
        ThingReferenceThingMatcher thingMatcher,
        ThingReferenceDtoMatcher dtoMatcher,
        ThingReferenceMetadataApplier applier,
        ThingReferenceStrictnessProvider strictnessProvider) =>
        ThingReferences.RegisterThingReferenceMetadata(key, appender, thingMatcher, dtoMatcher, applier, strictnessProvider);

    public static void RegisterThingReferenceDisplay(
        ThingReferenceDisplayPartsProvider partsProvider,
        ThingReferenceStandardStatSuppressor standardStatSuppressor) =>
        ThingReferences.RegisterThingReferenceDisplay(partsProvider, standardStatSuppressor);

    public static void RegisterThingReferenceDisplay(
        string key,
        ThingReferenceDisplayPartsProvider partsProvider,
        ThingReferenceStandardStatSuppressor standardStatSuppressor) =>
        ThingReferences.RegisterThingReferenceDisplay(key, partsProvider, standardStatSuppressor);

    public static void RegisterThingReferenceCacheKeyPartsProvider(ThingReferenceCacheKeyPartsProvider provider) =>
        ThingReferences.RegisterThingReferenceCacheKeyPartsProvider(provider);

    public static void RegisterThingReferenceMetadataNormalizer(ThingReferenceMetadataNormalizer normalizer) =>
        ThingReferences.RegisterThingReferenceMetadataNormalizer(normalizer);

    public static void RegisterThingReferenceThingFactory(ThingReferenceThingFactory factory) =>
        ThingReferences.RegisterThingReferenceThingFactory(factory);

    public static void RegisterThingReferenceThingFactory(string key, ThingReferenceThingFactory factory) =>
        ThingReferences.RegisterThingReferenceThingFactory(key, factory);

    public static bool TryMakeThingReferenceThing(ThingDef def, ThingDef? stuff, out Thing? thing) =>
        ThingReferences.TryMakeThingReferenceThing(def, stuff, out thing);

    public static void RegisterThingReferenceDefKindPredicate(string kind, ThingReferenceDefKindPredicate predicate) =>
        ThingReferences.RegisterThingReferenceDefKindPredicate(kind, predicate);

    public static bool HasThingReferenceDefKind(string kind, ThingDef? def) =>
        ThingReferences.HasThingReferenceDefKind(kind, def);

    public static void RegisterDefensePointDefNameProvider(DefensePointDefNameProvider provider) =>
        ThingReferences.RegisterDefensePointDefNameProvider(provider);

    public static IEnumerable<string> GetCompatibilityDefensePointDefNames() =>
        ThingReferences.GetCompatibilityDefensePointDefNames();

    public static bool TryDrawThingReferenceEditor(
        string surface,
        ThingDef? def,
        ModThingReferenceDto item,
        Rect rect,
        out float consumedHeight) =>
        ThingReferences.TryDrawThingReferenceEditor(surface, def, item, rect, out consumedHeight);

    public static void ClearThingReferenceMetadata(string surface, ThingDef? def, ModThingReferenceDto item) =>
        ThingReferences.ClearThingReferenceMetadata(surface, def, item);

    public static void NormalizeThingReferenceMetadata(ModThingReferenceDto reference) =>
        ThingReferences.NormalizeThingReferenceMetadata(reference);

    public static bool IsThingReferenceComplete(string surface, ThingDef? def, ModThingReferenceDto item) =>
        ThingReferences.IsThingReferenceComplete(surface, def, item);

    public static void ApplyThingReferenceDefaultMetadata(string surface, ThingDef? def, ModThingReferenceDto item) =>
        ThingReferences.ApplyThingReferenceDefaultMetadata(surface, def, item);

    public static bool IsThingReferenceUniqueRequest(ThingDef? def) =>
        ThingReferences.IsThingReferenceUniqueRequest(def);

    public static void AppendThingReferenceMetadata(Thing metadataThing, ModThingReferenceDto reference) =>
        ThingReferences.AppendThingReferenceMetadata(metadataThing, reference);

    public static bool ThingReferenceMetadataMatches(ModThingReferenceDto requirement, Thing metadataThing) =>
        ThingReferences.ThingReferenceMetadataMatches(requirement, metadataThing);

    public static bool ThingReferenceMetadataMatches(ModThingReferenceDto requirement, ModThingReferenceDto candidate) =>
        ThingReferences.ThingReferenceMetadataMatches(requirement, candidate);

    public static bool TryApplyThingReferenceMetadata(ModThingReferenceDto reference, Thing thing, out string? missingDefName) =>
        ThingReferences.TryApplyThingReferenceMetadata(reference, thing, out missingDefName);

    public static int ThingReferenceMetadataStrictness(ModThingReferenceDto requirement) =>
        ThingReferences.ThingReferenceMetadataStrictness(requirement);

    public static void AppendThingReferenceDisplayParts(ModThingReferenceDto thing, bool asRequirement, List<string> parts) =>
        ThingReferences.AppendThingReferenceDisplayParts(thing, asRequirement, parts);

    public static bool SuppressesStandardThingStats(ThingDef? def) =>
        ThingReferences.SuppressesStandardThingStats(def);

    public static IEnumerable<string> ThingReferenceCacheKeyParts(ModThingReferenceDto thing) =>
        ThingReferences.ThingReferenceCacheKeyParts(thing);

    public static void RegisterRaidCaravanMapEntryHandler(RaidCaravanMapEntryHandler handler) =>
        RemoteMapLifecycle.RegisterRaidCaravanMapEntryHandler(handler);

    public static void RegisterRemoteDefenderMapPreparedHandler(RemoteDefenderMapPreparedHandler handler) =>
        RemoteMapLifecycle.RegisterRemoteDefenderMapPreparedHandler(handler);

    public static void RegisterRemoteMapLoadedHandler(RemoteMapLoadedHandler handler) =>
        RemoteMapLifecycle.RegisterRemoteMapLoadedHandler(handler);

    public static void RegisterRemoteMapProjectionReferenceRewriter(RemoteMapProjectionReferenceRewriter rewriter) =>
        RemoteMapProjection.RegisterRemoteMapProjectionReferenceRewriter(rewriter);

    public static void RegisterRemoteMapProjectionSanitizer(RemoteMapProjectionSanitizer sanitizer) =>
        RemoteMapProjection.RegisterRemoteMapProjectionSanitizer(sanitizer);

    public static void RegisterRemoteSessionPawnCleanupHandler(RemoteSessionPawnCleanupHandler handler) =>
        RemoteMapLifecycle.RegisterRemoteSessionPawnCleanupHandler(handler);

    public static void RegisterRemoteMapIntegerLoadIdRewriter(
        string loadIdPrefix,
        Func<XElement, bool> predicate,
        Func<int> nextId) =>
        RemoteMapProjection.RegisterRemoteMapIntegerLoadIdRewriter(loadIdPrefix, predicate, nextId);

    public static IReadOnlyList<RemoteMapIntegerLoadIdRewriterRegistration> GetRemoteMapIntegerLoadIdRewriters() =>
        RemoteMapProjection.GetRemoteMapIntegerLoadIdRewriters();

    public static void RegisterRemoteMapAreaLoadIdProvider(RemoteMapAreaLoadIdProvider provider) =>
        RemoteMapProjection.RegisterRemoteMapAreaLoadIdProvider(provider);

    public static string? BuildRemoteMapAreaLoadId(XElement area, int id) =>
        RemoteMapProjection.BuildRemoteMapAreaLoadId(area, id);

    public static void RegisterSnapshotSaveSanitizer(CompatibilitySnapshotSaveSanitizer sanitizer) =>
        SnapshotSanitization.RegisterSnapshotSaveSanitizer(sanitizer);

    public static void RegisterPlayerColonySiteRegistrationSuppressor(PlayerColonySiteRegistrationSuppressor suppressor) =>
        ColonySite.RegisterPlayerColonySiteRegistrationSuppressor(suppressor);

    public static bool IsPlayerColonySiteRegistrationSuppressedByCompatibility() =>
        ColonySite.IsPlayerColonySiteRegistrationSuppressedByCompatibility();

    public static RaidCaravanMapEntryResult TryEnterRaidCaravanMap(Caravan caravan, Map map, IReadOnlyList<Pawn> attackPawns) =>
        RemoteMapLifecycle.TryEnterRaidCaravanMap(caravan, map, attackPawns);

    public static void NotifyRemoteDefenderMapPrepared(Map map, Faction defenderFaction) =>
        RemoteMapLifecycle.NotifyRemoteDefenderMapPrepared(map, defenderFaction);

    public static void NotifyRemoteMapLoaded(Map map, RemoteSessionMapParent carrier, string scope, ModSnapshotPackageMetadataDto package) =>
        RemoteMapLifecycle.NotifyRemoteMapLoaded(map, carrier, scope, package);

    public static IReadOnlyList<ILoadReferenceable> RewriteRemoteMapProjectionReferences(
        ModSnapshotPackageMetadataDto package,
        XDocument sourceDocument,
        XElement projectionElement) =>
        RemoteMapProjection.RewriteRemoteMapProjectionReferences(package, sourceDocument, projectionElement);

    public static void SanitizeRemoteMapProjection(
        ModSnapshotPackageMetadataDto package,
        XElement mapElement,
        XElement referencePawnsElement) =>
        RemoteMapProjection.SanitizeRemoteMapProjection(package, mapElement, referencePawnsElement);

    public static bool TryCleanupRemoteSessionPawn(Pawn pawn, string reason) =>
        RemoteMapLifecycle.TryCleanupRemoteSessionPawn(pawn, reason);

    public static int SanitizeSnapshotSave(XDocument document) =>
        SnapshotSanitization.SanitizeSnapshotSave(document);

    public static void ApplyRaidDefenderProxyPawnProtection(Pawn pawn, Faction defenderFaction) =>
        RaidDefenderLootProtectionUtility.ApplyProxyPawnProtection(pawn, defenderFaction);
}
