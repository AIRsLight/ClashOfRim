using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Compatibility;
using AIRsLight.ClashOfRim.CoreCompatibility;
using AIRsLight.ClashOfRim.DlcCompatibility;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.RemoteMaps;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility;
using AIRsLight.ClashOfRim.Trades;
using AIRsLight.ClashOfRim.Gifts;
using RimWorld;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Xml.Linq;
using Verse;

var tests = new (string Name, Action Run)[]
{
    ("mercenary quest engine callbacks preserve saved state and original ownership", ClientMercenaryQuestRuntimeTests.Run),
    ("mercenary quest lifecycle owns recall signals and ordered settlement", ClientMercenaryQuestLifecycleTests.Run),
    ("support quest lifecycle owns transitions and reuses the return transaction", ClientSupportQuestLifecycleTests.Run),
    ("ideology reform events reuse scoped extension registration", ClientIdeologyReformTests.Run),
    ("compatibility runtime entries use session permission and world catalog owners", CompatibilityRuntimeEntryTests.Run),
    ("main menu server entry binds native windows to the existing entry forms", ClientMainMenuEntryTests.Run),
    ("main menu saving and engine exits preserve original ownership and save workflow", ClientMainMenuSavingTests.Run),
    ("chat overlay owns game drafts and cadence while reusing account conversation", ClientChatOverlayTests.Run),
    ("raid finish commands bind delayed confirmation and map removal to original ownership", ClientRaidFinishTests.Run),
    ("raid lifecycle coordinates recovery and deadline polling for its source game", ClientRaidLifecycleTests.Run),
    ("world polling shares marker cadence and binds component callbacks to their game", ClientWorldPollingTests.Run),
    ("automatic session startup belongs to its source game and consumes admission once", ClientAutomaticSessionStartupTests.Run),
    ("world setup lifecycle binds page polling and reuses initial provisioning", ClientWorldSetupLifecycleTests.Run),
    ("storyteller editor binds page identity, permission and delayed submission", ClientStorytellerEditorTests.Run),
    ("colony site events share suppression, capture and original registration scope", ClientColonySiteEventTests.Run),
    ("colony gizmos retain original session and target through delayed clicks", ClientColonyGizmoTests.Run),
    ("world object UI preserves frozen choices through native menus and delayed confirmations", ClientWorldObjectUiTests.Run),
    ("world object commands bind delayed choices to session, game and target", ClientWorldObjectCommandsTests.Run),
    ("remote transporter menus and arrivals share policy and existing modules", RemoteTransporterActionsTests.Run),
    ("pawn preview DLC display avoids world registration and gameplay setters", PawnPreviewDlcTests.Run),
    ("pawn Scribe loading separates payload, native lifetime and arrival effects", PawnScribeLoaderTests.Run),
    ("pawn previews own requests, caches and engine publication", ClientPawnPreviewTests.Run),
    ("compatibility mismatch owns report, repairs and deferred continuations", ClientCompatibilityMismatchTests.Run),
    ("world entry forms own credentials, registration validation and window lifetime", ClientWorldEntryFormTests.Run),
    ("multiplayer window composition owns shared models and session-bound navigation", MultiplayerWindowTests.Run),
    ("administrator host owns navigation, refresh cadence and child cleanup", MultiplayerAdminHostTests.Run),
    ("administrator manifest owns baseline confirmations and audit projection", MultiplayerAdminManifestTests.Run),
    ("administrator maintenance panel owns broadcast targets, drafts and explicit lock commands", MultiplayerAdminMaintenanceTests.Run),
    ("administrator player panel owns targets, action menus and password drafts", MultiplayerAdminPlayerTests.Run),
    ("shop listing runtime owns engine templates, metadata and RimTest entry points", ClientShopListingRuntimeTests.Run),
    ("shop listing editor owns search, item edits, culture and price suggestions", ClientShopListingEditorTests.Run),
    ("multiplayer admin shop owns row drafts, editor copies and delayed actions", MultiplayerAdminShopTests.Run),
    ("multiplayer admin configuration owns drafts, parsing and permission-bound saves", MultiplayerAdminConfigurationTests.Run),
    ("multiplayer account drafts own submission and session lifetime", MultiplayerAccountDiagnosticsTests.Account),
    ("multiplayer diagnostics project current status without credentials or side effects", MultiplayerAccountDiagnosticsTests.Diagnostics),
    ("multiplayer mercenary panel unifies quote ownership, selections and hiring", MultiplayerMercenaryPanelTests.Run),
    ("multiplayer bank panel owns drafts, shared views and exact repayment confirmations", MultiplayerBankPanelTests.Run),
    ("multiplayer shop panel owns shared projections, refresh and purchase confirmations", MultiplayerShopPanelTests.Run),
    ("multiplayer trade panel shares market state and owns views and confirmations", MultiplayerTradePanelTests.Run),
    ("achievement page runtime shares trophy placement and localized presentation", AchievementPageRuntimeTests.Run),
    ("multiplayer achievement panel owns selection, refresh and trophy commands", MultiplayerAchievementPanelTests.Run),
    ("player colony locator owns cache invalidation and layered targets", PlayerColonyLocatorTests.Run),
    ("multiplayer player panel owns shared rankings, refresh and dialog lifetime", MultiplayerPlayerPanelTests.Run),
    ("ideology package loading owns validation and Scribe cleanup without catalog ownership", IdeoPackageLoaderTests.Run),
    ("biotech pawn packages and map capabilities own persistence and sanitation", BiotechPawnAndMapCapabilityTests.Run),
    ("biotech item capabilities own sources, identity, restoration and gene caches", BiotechItemCapabilityTests.Run),
    ("biotech trade constraints agree across client and server cultures", BiotechTradeConstraintTests.Run),
    ("biotech metadata shares wire keys, codec and matching across item surfaces", BiotechThingMetadataTests.Run),
    ("world compatibility owns extensions, settings, package catalog and DLC policy", WorldCompatibilityTests.Run),
    ("raid defender proxy protection reuses idempotent health protection", RaidDefenderProxyProtectionTests.Run),
    ("remote map compatibility owns lifecycle, projection and save capabilities", RemoteMapCompatibilityTests.Run),
    ("pawn compatibility owns faction, reference, exchange and trade capabilities", PawnCompatibilityTests.Run),
    ("thing compatibility owns capabilities, snapshots and execution policies", ThingCompatibilityTests.Run),
    ("compatibility registration scope owns generations, priority, cleanup and diagnostics", CompatibilityRegistrationScopeTests.Run),
    ("raid commands retain menu ownership and share caravan, vehicle and transporter launch", ClientRaidCommandTests.Run),
    ("snapshot transaction notifications retain their active upload and session", ClientSnapshotTransactionNotificationTests.Run),
    ("operation followups retain source completion across domains and admission release", ClientOperationFollowupTests.Run),
    ("snapshot saving shares manual, menu and autosave with owned exit", ClientSnapshotSavingTests.Run),
    ("snapshot world continuation preserves loaded-game receipt and domain ordering", ClientSnapshotWorldTests.Run),
    ("server metadata shares compatibility and permission publication under original ownership", ClientServerMetadataTests.Run),
    ("session followups dispatch real domains under the source workflow receipt", ClientSessionFollowupTests.Run),
    ("session cleanup owns domain teardown and persistence ordering", ClientSessionCleanupTests.Run),
    ("HTTP session failures preserve structured semantics and original login ownership", ClientHttpSessionTests.Run),
    ("session recovery UI owns expiry, in-place reconnect and domain followups", ClientSessionRecoveryTests.Run),
    ("connection status owns source identity, localization and reentrant publication", ClientConnectionStatusTests.Run),
    ("session notifications own payload semantics, cursors and bound effects", ClientSessionNotificationTests.Run),
    ("chat owns conversation cache, cursors, unread state and request lifetimes", ClientChatTests.Run),
    ("chat protocol shares send and list contracts with the public facade", ClientChatProtocolTests.Run),
    ("session startup shares manual and map login, credentials and followups", ClientSessionStartupTests.Run),
    ("compatibility override protocol preserves credential recovery wire contract", ClientCompatibilityOverrideProtocolTests.Run),
    ("mod config policy shares manifest identity, enforcement rules and cache", ClientModSettingsPolicyTests.Run),
    ("settings and developer adapters use owned access and isolated profile lifecycle", ClientSettingsAccessTests.Run),
    ("compatibility baseline owns manifest and DLC publication", ClientCompatibilityBaselineTests.Run),
    ("compatibility override owns credential admission and catalog completion", ClientCompatibilityOverrideTests.Run),
    ("world login publication preserves request observations across identity changes", ClientWorldLoginPublicationTests.Run),
    ("world publication preserves request order and local appearance edits", ClientWorldPublicationTests.Run),
    ("world synchronization runtime shares layered object and path capture", ClientWorldSynchronizationRuntimeTests.Run),
    ("world marker protocol shares read and runtime write wire contracts", ClientWorldMarkerProtocolTests.Run),
    ("world synchronization owns refresh, coalescing and accepted polling", ClientWorldSynchronizationTests.Run),
    ("world markers runtime binds engine targets and preserves layered query adapters", ClientWorldMarkersRuntimeTests.Run),
    ("world markers own immutable cache, capabilities and bound projection", ClientWorldMarkersTests.Run),
    ("colony appearance runtime preserves target, dialog and settings boundaries", ClientColonyAppearanceRuntimeTests.Run),
    ("colony appearance owns account preferences, editing and persistence", ClientColonyAppearanceTests.Run),
    ("colony relocation runtime binds maps, layered anchors and vanilla admission", ClientColonyRelocationRuntimeTests.Run),
    ("colony relocation shares explicit settlement, flight and snapshot confirmation", ClientColonyRelocationTests.Run),
    ("colony abandonment runtime binds exact colony, game and world", ClientColonyAbandonRuntimeTests.Run),
    ("colony abandonment owns confirmation, terminal receipt and session exit", ClientColonyAbandonmentTests.Run),
    ("colony protocol shares registration, relocation and abandonment wire contracts", ClientColonyProtocolTests.Run),
    ("colony registration shares capture, writes and accepted publication", ClientColonyRegistrationTests.Run),
    ("occupied colony sites runtime preserves engine layer identity", ClientOccupiedColonySitesRuntimeTests.Run),
    ("occupied colony sites own layered cache and publication boundaries", ClientOccupiedColonySitesTests.Run),
    ("initial world setup owns provisioning and snapshot handoff", ClientInitialWorldSetupTests.Run),
    ("administrator baseline runtime preserves definition and stat catalogs without a game", ClientAdminBaselineRuntimeTests.Run),
    ("administrator baseline protocol freezes setup input and shares two wire contracts", ClientAdminBaselineProtocolTests.Run),
    ("administrator baseline capture owns catalogs and queued session boundaries", ClientAdminBaselineCaptureTests.Run),
    ("administrator protocol shares three wire contracts and captured context", ClientAdminProtocolTests.Run),
    ("administrator control owns cache, commands and follow-up publication", ClientAdministrationTests.Run),
    ("world writes share administrator admission, capture and completion", ClientWorldWritesTests.Run),
    ("world catalog refresh owns replacement, accepted projection and runtime boundaries", ClientWorldCatalogTests.Run),
    ("world baseline runtime binds capture collections, layers and substrate sources", ClientWorldBaselineRuntimeTests.Run),
    ("world baseline capture owns source, validation and layered colony sites", ClientWorldBaselineCaptureTests.Run),
    ("world baseline submission owns frozen input, substrate acceptance and publication", ClientWorldBaselineSubmissionTests.Run),
    ("world feature names share culture, catalog and geometry policy", ClientWorldFeatureLabelsTests.Run),
    ("world feature runtime preserves IDs, layers and tile links", ClientWorldFeaturesRuntimeTests.Run),
    ("world generation runtime binds engine inputs, worlds and setup pages", ClientWorldGenerationRuntimeTests.Run),
    ("world generation owns deferred work, application and catalog receipts", ClientWorldGenerationTests.Run),
    ("storyteller configuration shares setup and runtime ownership", ClientStorytellerConfigurationTests.Run),
    ("world entry start owns input, admission and source game barriers", ClientWorldEntryStartTests.Run),
    ("world entry owns decisions, publication and substrate handoff", ClientWorldEntryTests.Run),
    ("world preparation owns handshake results and pending setup state", ClientWorldPreparationTests.Run),
    ("world protocol owns negotiation, captured setup and seven wire contracts", ClientWorldProtocolTests.Run),
    ("session presence and snapshot upload protocols preserve facade wire contracts", ClientSessionSnapshotProtocolTests.Run),
    ("endgame engine callbacks use owned decisions and the shared award workflow", EndgameCallbackTests.Run),
    ("endgame achievements own pending awards, decisions and snapshot confirmation", ClientEndgameAchievementTests.Run),
    ("achievement queries own replacement, immutable cache and session boundaries", ClientAchievementQueryTests.Run),
    ("colony registration transport binds current session and preserves world fields", ColonyRegistrationTransportTests.Run),
    ("remote map closure shares ordering and retains ownership across failure", ClientRemoteMapClosureTests.Run),
    ("remote map close runtime binds exact game map and session", ClientRemoteMapCloseRuntimeTests.Run),
    ("snapshot reload runtime retains exact game and pending memory load ownership", ClientSnapshotReloadRuntimeTests.Run),
    ("snapshot reload owns authentication, game replacement and recovery retries", ClientSnapshotReloadTests.Run),
    ("colony snapshot restore owns downloads, identity validation and loadable bytes", ClientColonySnapshotDownloadTests.Run),
    ("raid recovery owns loaded-game readiness and authoritative deadline decisions", ClientRaidRecoveryTests.Run),
    ("raid settlements own evidence, stable retries and cleanup handoff", ClientRaidSettlementTests.Run),
    ("raid support loss sources retain exact battle pawn and assignment ownership", ClientRaidSupportLossSourceTests.Run),
    ("raid support losses own captured batch reporting and local removal", ClientRaidSupportLossTests.Run),
    ("raid launch owns game preparation, snapshot commit and stable retries", ClientRaidLaunchingTests.Run),
    ("raid attack forces own captured engine state and battlefield handoff", ClientRaidAttackForceTests.Run),
    ("raid preparation owns accepted authorization, snapshot download and duration rules", ClientRaidPreparationTests.Run),
    ("raid protocol preserves preparation and multipart creation contracts", ClientRaidProtocolTests.Run),
    ("observation maps own login adoption, game opening and stale session cleanup", ClientObservationMapsTests.Run),
    ("remote snapshot downloads share authorization, immutable targets and validation", RemoteSnapshotDownloadModuleTests.Run),
    ("support returns own ordered confirmation and durable notification recovery", ClientSupportReturnsTests.Run),
    ("support return runtime retains and reloads prepared pawn outbox", ClientSupportReturnRuntimeTests.Run),
    ("support sending owns removal, immutable confirmation and recovery", ClientSupportSendingTests.Run),
    ("support source captures caravan identity before departure and revalidates ownership", ClientSupportSourceTests.Run),
    ("pawn exchange capture shares support and gift identity, gear and references", PawnExchangeCaptureTests.Run),
    ("mercenary incidents own ordered debt confirmation and recoverable reports", ClientMercenaryIncidentsTests.Run),
    ("local snapshot confirmation owns retries, cleanup and domain continuations", LocalSnapshotConfirmationModuleTests.Run),
    ("mercenary hiring owns preparation, payment and arrival continuation", ClientMercenaryHiringTests.Run),
    ("mercenary runtime retains pawn, debit and original arrival map", ClientMercenaryRuntimeTests.Run),
    ("mercenary protocol owns six contracts and captured snapshot credentials", ClientMercenaryProtocolTests.Run),
    ("mercenary quotes share execution and preserve session and game ownership", ClientMercenaryQuotesTests.Run),
    ("trade operations own reservation, fulfillment and snapshot retries", ClientTradeOperationsTests.Run),
    ("package writes preserve contracts, previews and partial upload retries", PackageWriteModuleTests.Run),
    ("trade market owns paging, decisions and session state", ClientTradeMarketTests.Run),
    ("gift sends own capture, removal, retries and deferred arrival", ClientGiftModuleTests.Run),
    ("bank workflow owns preparation, retry, session state and game publication", ClientBankModuleTests.Run),
    ("bank protocol shares all seven routes and captured credentials with the facade", ClientBankProtocolTests.Run),
    ("caravan gizmos preserve domain routing and delayed source ownership", ClientCaravanGizmoTests.Run),
    ("client modules cannot depend on the Mod, full settings or broad protocol client", ClientModuleBoundaryTests.Run),
    ("RimTest domain modules preserve commands, admission and independent observations", BridgeDomainModuleTests.Run),
    ("event choice letters bind delayed actions to their original event and session", EventChoiceLetterTests.Run),
    ("event applications own game effects, hydration and confirmation handoff", EventApplicationModuleTests.Run),
    ("event refresh owns coalescing, cursor, state and delayed delivery", EventRefreshModuleTests.Run),
    ("session connection module owns replacement, cancellation and stale completions", SessionConnectionModuleTests.Run),
    ("session lifecycle owns identities, deferred expiry and authenticated exit", SessionLifecycleModuleTests.Run),
    ("session reconnect owns snapshot checks, pending adoption and detached cleanup", SessionReconnectModuleTests.Run),
    ("synchronization admission and completion retain exact transaction ownership", SynchronizationCoordinatorTests.Run),
    ("local mutation admission permits its recovery without admitting unrelated work", LocalMutationModuleTests.Run),
    ("bank and event continuations reach snapshot rebuilding and retain failed mutations", LocalMutationContinuationTests.Run),
    ("snapshot uploader owns capture, upload admission and game publication", SnapshotUploaderModuleTests.Run),
    ("snapshot packaging preserves culture-independent metadata without the engine", SnapshotPackageModuleTests.Run),
    ("event inbox owns isolated data, effects, confirmation order and session reset", EventInboxModuleTests.Run),
    ("event observations reuse stable handles without exposing mutable product state", EventDetailsObservationTests.Run),
    ("event reads share protocol, identity ownership, ordered publication and notification coalescing", EventReadModuleTests.Run),
    ("event delivery owns successful effects, retries, presentation and shared map identities", EventDeliveryModuleTests.Run),
    ("event confirmations own protocol, captured identity, admission and game publication", EventConfirmationModuleTests.Run),
    ("event preparation and failure recovery share admission, game ownership and exact decisions", EventConfirmationModuleTests.RunPreparationAndRecovery),
    ("event confirmation workflows own intent, retry, domain effects and completion order", EventConfirmationModuleTests.RunWorkflow),
    ("event command presentation resolves shipped translations, semantic parameters and authoritative relations", EventCommandPresentationTests.Run),
    ("event decisions own protocol, captured identity, game effects and failure reporting", EventDecisionModuleTests.Run),
    ("diplomacy creation owns frozen targets, authoritative receipts and publication", DiplomacyCreationModuleTests.Run),
    ("player directory owns refresh replacement, projections and target selection", PlayerDirectoryModuleTests.Run),
    ("account password change owns credentials, session publication and persistence", PasswordChangeModuleTests.Run),
    ("compatibility summary matches client wire and full manifest decisions", CompatibilitySummaryContractTests.Run),
    ("package reads and hydration use captured context, shared state restoration and cancellation", PackageReadModuleTests.Run),
    ("package error presentation preserves public and domain contracts", SnapshotPackagePresentationTests.Run),
    ("protocol transport owns request encoding, responses and cancellation", ProtocolTransportModuleTests.Run),
    ("session WebSocket transport owns duplex failure, framing and closure", SessionWebSocketTransportTests.Run),
    ("setup authentication owns temporary credentials, cancellation and bounded cleanup", SetupAuthenticationModuleTests.Run),
    ("actual event letters share rendering, action and automatic application rules", EventLetterPolicyTests.Run),
    ("pawn relationship resolution reads the bound account and preserves global identities", PawnAccountIdentityTests.Run),
    ("repeated pawn transfer preserves global relationship identity", PawnTransferIdentityTests.Run),
    ("item durability uses final quality maximum", ThingMetadataOrderTests.Run),
    ("shop distinguishes minifiable contents from empty wrappers", ShopDefinitionEligibilityTests.Run),
    ("battle reload addiction guard is limited to missing needs during load", AddictionLoadStateTests.Run),
    ("vanilla transfer state survives cultures and rejects partial invalid state", CoreTransferStateTests.Run),
    ("support death returns use explicit state and preserve legacy loss reasons", SupportLossClassificationTests.Run),
    ("embryos and gene packs restore exact genes without regeneration", GeneHolderTransferTests.Run),
    ("server ideology catalog follows replaced faction primary instances", IdeologyCatalogBindingTests.Run),
    ("concurrent localized messages retain their own arguments", ConcurrentFormattingTests.Run),
    ("Scribe XML preserves fractional values across cultures", ScribeCultureTests.Run),
    ("entry continuations survive the game transition without crossing ownership", MainThreadDispatchTests.Run),
    ("failed trade buyer refunds retain their wire purpose", FailedTradeBuyerRefundPurpose),
    ("gift rejection sends recipient authentication", GiftRejectionTransportTests.Run),
    ("support closure sends recipient authentication", SupportClosureTransportTests.Run),
    ("trade commands send colony authentication", TradeCommandTransportTests.Run),
    ("shop engine adapter preserves map, quantity and partial effects", ClientShopRuntimeTests.Run),
    ("shop purchase owns local exchange, snapshot retries and settlement", ClientShopOperationsTests.Run),
    ("shop catalog owns cache, administration and exact request lifecycle", ClientShopCatalogTests.Run),
    ("shop protocol owns four contracts, captured authentication and snapshots", ClientShopProtocolTests.Run),
    ("shared shop pricing preserves rounding, bounds and lifetime counts", ServerShopPricingTests.Run),
    ("transport pod gifts use retryable authenticated snapshots", TransportPodGiftTests.Run),
    ("registered rejection is shared by UI and authoritative preparation", RegisteredRejectionIsShared),
    ("registered capture and finalize handlers run", RegisteredCaptureAndFinalizeHandlersRun),
    ("unfinished things are rejected", UnfinishedThingsAreRejected),
    ("fertilized eggs retain progress but not parents", FertilizedEggsRetainProgressButNotParents),
    ("ideology relics are rejected", IdeologyRelicsAreRejected),
    ("adopted npc ideologies fork without splitting shared player ideologies", AdoptedNpcIdeologiesFork),
    ("biotech operation targets are cleared", BiotechOperationTargetsAreCleared),
    ("unnatural corpses are rejected", UnnaturalCorpsesAreRejected),
    ("unfinished thing defs are hidden from request lists", UnfinishedThingDefsAreHidden),
    ("prepared concrete things carry transfer policy metadata", PreparedThingsCarryPolicyMetadata),
    ("server entry diagnostics list mods in load order", ServerEntryDiagnosticsListModsInLoadOrder),
    ("only accepted language warnings can be bypassed", OnlyAcceptedLanguageWarningsCanBeBypassed),
    ("visible compatibility warnings request the full server manifest", VisibleCompatibilityWarningsRequestFullManifest),
    ("selection labels show a single ellipsis", SelectionLabelsShowSingleEllipsis),
    ("remote projection defers addiction need lookup until pawn needs load", RemoteProjectionDefersAddictionNeedLookup),
    ("hidden trap proxies retain source snapshot identity", HiddenTrapProxiesRetainSourceSnapshotIdentity),
    ("defense duty returns to its radius without consuming wander cadence", DefenseDutyUsesIndependentReturnNode),
    ("session reconnect blocks unfinished atomic mutations", SessionReconnectBlocksAtomicMutations),
    ("session reconnect requires the same authoritative snapshot", SessionReconnectRequiresSameSnapshot),
    ("administrator setup authenticates requests and releases only its temporary session", AdministratorSetupTransportTests.Run),
};

static void FailedTradeBuyerRefundPurpose()
{
    ItemDeliveryPayloadSummary payload = ItemDeliveryPayloadReader.Read("{\"Purpose\":8,\"Items\":[]}");
    if (payload.Purpose != ItemDeliveryPurpose.TradeApplicationFailedAcceptorReturn || !payload.IsTradeReturn
        || payload.IsTradeDelivery || (int)ItemDeliveryPurpose.TradeApplicationFailedOwnerReturn != 7)
        throw new Exception("Buyer refund wire purpose must remain distinct from a gift or completed trade delivery.");
}

if (args.Length > 0)
{
    if (args[0] != "--filter" || args.Length < 2 || args.Skip(1).Any(string.IsNullOrWhiteSpace))
        throw new ArgumentException("Usage: --filter <name substring> [name substring ...]");
    int total = tests.Length;
    tests = tests.Where(test => args.Skip(1).Any(filter => test.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))).ToArray();
    if (tests.Length == 0) throw new ArgumentException("No tests matched the requested filters.");
    Console.WriteLine($"Running {tests.Length}/{total} test groups.");
}

foreach ((string name, Action run) in tests)
{
    try
    {
        Console.WriteLine("RUN: " + name);
        run();
        Console.WriteLine("PASS: " + name);
    }
    catch (Exception ex)
    {
        Console.WriteLine("FAIL-TYPE: " + ex.GetType().FullName);
        try
        {
            Console.WriteLine("FAIL-MESSAGE: " + ex.Message);
        }
        catch
        {
            Console.WriteLine("FAIL-MESSAGE: <unavailable>");
        }

        Environment.ExitCode = 1;
        break;
    }
}

static void RegisteredRejectionIsShared()
{
    BeginCycle();
    ClashOfRimCompatibilityApi.RegisterThingTransferRule(
        "test.reject",
        (Thing thing, ThingTransferContext context) =>
            ThingTransferDecision.Reject("test.forbidden"),
        null,
        null);

    Thing thing = MakeThing("ForbiddenThing");
    Assert(!ClashOfRimCompatibilityApi.CanTransferThing(
        thing,
        ThingTransferContext.Outbound("trade"),
        out string? uiReason));
    Assert(uiReason == "test.forbidden");

    var reference = new ModThingReferenceDto { GlobalKey = "thing:1", DefName = "ForbiddenThing", StackCount = 1 };
    Assert(!ClashOfRimCompatibilityApi.PrepareThingTransfer(
        thing,
        reference,
        ThingTransferContext.Outbound("trade"),
        out string? authoritativeReason));
    Assert(authoritativeReason == uiReason);
}

static void RegisteredCaptureAndFinalizeHandlersRun()
{
    BeginCycle();
    bool finalized = false;
    ClashOfRimCompatibilityApi.RegisterThingTransferRule(
        "test.capture",
        null,
        (Thing thing, ModThingReferenceDto reference, ThingTransferContext context) =>
            reference.Metadata["test.capture"] = context.Surface,
        (ModThingReferenceDto reference, Thing thing, ThingTransferContext context, out string? missingDefName) =>
        {
            missingDefName = null;
            finalized = reference.Metadata.TryGetValue("test.capture", out string? value)
                && value == context.Surface;
            return finalized;
        });

    Thing thing = MakeThing("CapturedThing");
    var reference = new ModThingReferenceDto { GlobalKey = "thing:2", DefName = "CapturedThing", StackCount = 1 };
    ThingTransferContext context = ThingTransferContext.Outbound("gift");
    Assert(ClashOfRimCompatibilityApi.PrepareThingTransfer(thing, reference, context, out _));
    Assert(reference.Metadata["test.capture"] == "gift");
    Assert(ClashOfRimCompatibilityApi.FinalizeThingTransfer(reference, thing, context, out _));
    Assert(finalized);
}

static void UnfinishedThingsAreRejected()
{
    BeginCycle();
    CoreThingTransferCompatibility.Apply();
    var thing = (UnfinishedThing)FormatterServices.GetUninitializedObject(typeof(UnfinishedThing));
    thing.def = MakeDef("UnfinishedGun");

    Assert(!ClashOfRimCompatibilityApi.CanTransferThing(
        thing,
        ThingTransferContext.Outbound("trade"),
        out string? reason));
    Assert(reason == CoreThingTransferCompatibility.UnfinishedThingRejectionCode);
}

static void FertilizedEggsRetainProgressButNotParents()
{
    BeginCycle();
    CoreThingTransferCompatibility.Apply();
    ThingWithComps egg = MakeThingWithComp("EggTestFertilized", out CompHatcher hatcher);
    Pawn parent = (Pawn)FormatterServices.GetUninitializedObject(typeof(Pawn));
    Faction sourceFaction = (Faction)FormatterServices.GetUninitializedObject(typeof(Faction));
    Faction receivingFaction = (Faction)FormatterServices.GetUninitializedObject(typeof(Faction));
    hatcher.hatcheeParent = parent;
    hatcher.otherParent = parent;
    hatcher.hatcheeFaction = sourceFaction;
    typeof(CompHatcher).GetField("gestateProgress", BindingFlags.Instance | BindingFlags.NonPublic)!
        .SetValue(hatcher, 0.625f);

    var reference = new ModThingReferenceDto { GlobalKey = "egg:1", DefName = egg.def.defName, StackCount = 1 };
    Assert(ClashOfRimCompatibilityApi.PrepareThingTransfer(
        egg,
        reference,
        ThingTransferContext.Outbound("gift"),
        out _));

    ThingWithComps restored = MakeThingWithComp("EggTestFertilized", out CompHatcher restoredHatcher);
    restoredHatcher.hatcheeParent = parent;
    restoredHatcher.otherParent = parent;
    restoredHatcher.hatcheeFaction = sourceFaction;
    Assert(ClashOfRimCompatibilityApi.FinalizeThingTransfer(
        reference,
        restored,
        ThingTransferContext.Inbound("gift", receivingFaction),
        out _));

    float progress = (float)typeof(CompHatcher)
        .GetField("gestateProgress", BindingFlags.Instance | BindingFlags.NonPublic)!
        .GetValue(restoredHatcher)!;
    Assert(Math.Abs(progress - 0.625f) < 0.0001f);
    Assert(restoredHatcher.hatcheeParent is null);
    Assert(restoredHatcher.otherParent is null);
    Assert(ReferenceEquals(restoredHatcher.hatcheeFaction, receivingFaction));
}

static void HiddenTrapProxiesRetainSourceSnapshotIdentity()
{
    var identities = new[]
    {
        new RemoteMapThingIdentityRecord
        {
            MapUniqueId = "3",
            ProjectedThingId = "cor_remote_raid_TrapIED_Smoke24411",
            OriginalThingId = "TrapIED_Smoke24411"
        }
    };

    Assert(RemoteMapThingIdentityResolver.TryResolveOriginalThingId(
        identities,
        "Map_3",
        "Thing_cor_remote_raid_TrapIED_Smoke24411",
        out string originalThingId));
    Assert(originalThingId == "TrapIED_Smoke24411");
}

static void SessionReconnectBlocksAtomicMutations()
{
    SessionReconnectBlockReason reason = SessionReconnectSafetyPolicy.EvaluateLocalState(
        isPlaying: true,
        hasGame: true,
        isConfigured: true,
        userId: "player-a",
        colonyId: "colony-a",
        currentSnapshotId: "snapshot-a",
        synchronizationBusy: false,
        atomicMutationPending: true);

    Assert(reason == SessionReconnectBlockReason.AtomicMutationPending);
}

static void SessionReconnectRequiresSameSnapshot()
{
    SessionReconnectBlockReason local = SessionReconnectSafetyPolicy.EvaluateLocalState(
        isPlaying: true,
        hasGame: true,
        isConfigured: true,
        userId: "player-a",
        colonyId: "colony-a",
        currentSnapshotId: "snapshot-a",
        synchronizationBusy: false,
        atomicMutationPending: false);
    Assert(local == SessionReconnectBlockReason.None);
    Assert(SessionReconnectSafetyPolicy.MatchesAuthoritativeSnapshot(
        "colony-a",
        "snapshot-a",
        hasExistingColony: true,
        assignedColonyId: "colony-a",
        latestSnapshotId: "snapshot-a"));
    Assert(!SessionReconnectSafetyPolicy.MatchesAuthoritativeSnapshot(
        "colony-a",
        "snapshot-a",
        hasExistingColony: true,
        assignedColonyId: "colony-a",
        latestSnapshotId: "snapshot-b"));
}

static void IdeologyRelicsAreRejected()
{
    BeginCycle();
    IdeologyThingTransferCompatibility.Apply();
    ThingWithComps relicThing = MakeThingWithComp("RelicWeapon", out CompStyleable styleable);
    typeof(CompStyleable).GetField("sourcePrecept", BindingFlags.Instance | BindingFlags.NonPublic)!
        .SetValue(styleable, FormatterServices.GetUninitializedObject(typeof(Precept_Relic)));

    Assert(!ClashOfRimCompatibilityApi.CanTransferThing(
        relicThing,
        ThingTransferContext.Outbound("gift"),
        out string? reason));
    Assert(reason == IdeologyThingTransferCompatibility.RelicRejectionCode);
}

static void AdoptedNpcIdeologiesFork()
{
    Assert(PlayerIdeoForkPolicy.ShouldFork(
        hasServerGlobalKey: true,
        referencedByNpcFaction: true,
        referencedByPlayerProxy: false));
    Assert(PlayerIdeoForkPolicy.ShouldFork(
        hasServerGlobalKey: false,
        referencedByNpcFaction: true,
        referencedByPlayerProxy: false));
    Assert(!PlayerIdeoForkPolicy.ShouldFork(
        hasServerGlobalKey: false,
        referencedByNpcFaction: false,
        referencedByPlayerProxy: true));
    Assert(!PlayerIdeoForkPolicy.ShouldFork(
        hasServerGlobalKey: false,
        referencedByNpcFaction: false,
        referencedByPlayerProxy: false));
}

static void BiotechOperationTargetsAreCleared()
{
    BeginCycle();
    BiotechThingTransferCompatibility.Apply();
    Thing target = MakeThing("Target");
    var genepack = (Genepack)FormatterServices.GetUninitializedObject(typeof(Genepack));
    genepack.def = MakeDef("Genepack");
    genepack.targetContainer = target;
    var reference = new ModThingReferenceDto { GlobalKey = "gene:1", DefName = "Genepack", StackCount = 1 };

    Assert(ClashOfRimCompatibilityApi.FinalizeThingTransfer(
        reference,
        genepack,
        ThingTransferContext.Inbound("trade", null),
        out _));
    Assert(genepack.targetContainer is null);
}

static void UnnaturalCorpsesAreRejected()
{
    BeginCycle();
    AnomalyThingTransferCompatibility.Apply();
    var corpse = (UnnaturalCorpse)FormatterServices.GetUninitializedObject(typeof(UnnaturalCorpse));
    corpse.def = MakeDef("UnnaturalCorpse");

    Assert(!ClashOfRimCompatibilityApi.CanTransferThing(
        corpse,
        ThingTransferContext.Outbound("gift"),
        out string? reason));
    Assert(reason == AnomalyThingTransferCompatibility.UnnaturalCorpseRejectionCode);
}

static void UnfinishedThingDefsAreHidden()
{
    ThingDef def = MakeDef("UnfinishedThing");
    def.thingClass = typeof(UnfinishedThing);

    Assert(!TradeThingReferenceUtility.IsTradeableItemDef(def));
}

static void PreparedThingsCarryPolicyMetadata()
{
    BeginCycle();
    ClashOfRimCompatibilityApi.RegisterThingTransferRule(
        "test.policy-marker",
        null,
        (Thing thing, ModThingReferenceDto reference, ThingTransferContext context) =>
            reference.Metadata["test.policy-marker"] = "captured",
        null);
    Thing thing = MakeThing("Steel");
    var reference = new ModThingReferenceDto
    {
        GlobalKey = "owner:user-a/colony:colony-a/snapshot:snapshot-a/map:0/thing:steel",
        DefName = "Steel",
        StackCount = 10
    };

    Assert(ThingTransferPipeline.TryPrepareOutbound(
        thing,
        reference,
        ThingReferenceSurfaces.TradeOffer,
        out _));
    Assert(reference.Metadata[ThingTransferPolicy.VersionMetadataKey] == ThingTransferPolicy.CurrentVersion);
    Assert(reference.Metadata[ThingTransferPolicy.DecisionMetadataKey] == ThingTransferPolicy.AcceptedDecision);
}

static void ServerEntryDiagnosticsListModsInLoadOrder()
{
    var manifest = new CompatibilityManifest
    {
        ManifestId = "manifest-123",
        ProtocolVersion = "protocol-2",
        RimWorldVersion = "1.6.9999",
        GameLanguage = "English",
        Mods = new List<ModManifestEntry>
        {
            new()
            {
                LoadOrder = 1,
                PackageId = "author.second",
                Name = "Second\nInjected line",
                Source = "SteamWorkshop",
                WorkshopId = "200",
                Role = ModCompatibilityRole.Required
            },
            new()
            {
                LoadOrder = 0,
                PackageId = "ludeon.rimworld",
                Name = "Core",
                Source = "Official",
                Role = ModCompatibilityRole.Required
            }
        }
    };

    string text = AIRsLight.ClashOfRim.CompatibilityClient.ClientCompatibilityManifestDiagnostics
        .FormatForServerEntry(manifest);

    Assert(text.Contains("mods=2"));
    Assert(text.Contains("manifest=manifest-123"));
    Assert(text.Contains("protocol=protocol-2"));
    Assert(text.Contains("RimWorld=1.6.9999"));
    Assert(text.Contains("language=English"));
    Assert(text.IndexOf("[0] ludeon.rimworld", StringComparison.Ordinal)
        < text.IndexOf("[1] author.second", StringComparison.Ordinal));
    Assert(text.Contains("Second Injected line"));
    Assert(!text.Contains("Second\nInjected line"));
}

static void OnlyAcceptedLanguageWarningsCanBeBypassed()
{
    Assert(AIRsLight.ClashOfRim.CompatibilityClient.CompatibilityLanguageMismatchPolicy.IsLanguageMismatch(
        "GameLanguageMismatch"));
    Assert(!AIRsLight.ClashOfRim.CompatibilityClient.CompatibilityLanguageMismatchPolicy.IsLanguageMismatch(
        "ModListMismatch"));
    Assert(AIRsLight.ClashOfRim.CompatibilityClient.CompatibilityLanguageMismatchPolicy.ResolveServerLanguage(
        "ChineseSimplified",
        "English") == "ChineseSimplified");

    var response = new ModLoginResponseDto
    {
        Result = new ModProtocolResponseDto { Accepted = true },
        CompatibilityIssues = new List<ModCompatibilityIssueDto>
        {
            new() { Severity = "Warning", Code = "GameLanguageMismatch" },
            new() { Severity = "Info", Code = "AllowedPureTranslationMod" }
        }
    };

    Assert(AIRsLight.ClashOfRim.CompatibilityClient.CompatibilityLanguageMismatchPolicy.CanContinue(response));

    response.CompatibilityIssues.Add(new ModCompatibilityIssueDto
    {
        Severity = "Error",
        Code = "ModListMismatch"
    });
    Assert(!AIRsLight.ClashOfRim.CompatibilityClient.CompatibilityLanguageMismatchPolicy.CanContinue(response));

    response.CompatibilityIssues.RemoveAt(response.CompatibilityIssues.Count - 1);
    response.CompatibilityIssues.Add(new ModCompatibilityIssueDto
    {
        Severity = "Info",
        Code = "RimWorldVersionMismatch"
    });
    Assert(!AIRsLight.ClashOfRim.CompatibilityClient.CompatibilityLanguageMismatchPolicy.CanContinue(response));

    response.CompatibilityIssues.RemoveAt(response.CompatibilityIssues.Count - 1);
    response.Result.Accepted = false;
    Assert(!AIRsLight.ClashOfRim.CompatibilityClient.CompatibilityLanguageMismatchPolicy.CanContinue(response));
}

static void VisibleCompatibilityWarningsRequestFullManifest()
{
    Assert(!CompatibilityManifestDeliveryPolicy.ShouldIncludeFullManifest(Array.Empty<CompatibilityIssue>()));
    Assert(!CompatibilityManifestDeliveryPolicy.ShouldIncludeFullManifest(new[]
    {
        new CompatibilityIssue(
            CompatibilityIssueSeverity.Info,
            CompatibilityIssueCode.AllowedPureTranslationMod,
            "allowed")
    }));
    Assert(CompatibilityManifestDeliveryPolicy.ShouldIncludeFullManifest(new[]
    {
        new CompatibilityIssue(
            CompatibilityIssueSeverity.Warning,
            CompatibilityIssueCode.GameLanguageMismatch,
            "language")
    }));
}

static void BeginCycle()
{
    ClashOfRimCompatibilityApi.BeginRegistrationCycle("client-tests");
}

static void SelectionLabelsShowSingleEllipsis()
{
    Assert(AIRsLight.ClashOfRim.ClashOfRimUiUtility.SelectionLabel("Notice") == "Notice ...");
    Assert(AIRsLight.ClashOfRim.ClashOfRimUiUtility.SelectionLabel("Notice ...") == "Notice ...");
    Assert(AIRsLight.ClashOfRim.ClashOfRimUiUtility.SelectionLabel("  ") == "...");
}

static void RemoteProjectionDefersAddictionNeedLookup()
{
    var pawn = (Pawn)FormatterServices.GetUninitializedObject(typeof(Pawn));
    var addiction = (Hediff_Addiction)FormatterServices.GetUninitializedObject(typeof(Hediff_Addiction));
    addiction.pawn = pawn;

    Need_Chemical? result = null;
    Assert(RemoteMapProjectionAddictionLoadPatch.Prefix(addiction, ref result));

    using (RemoteMapProjectionLoadScope.Begin())
    {
        Assert(!RemoteMapProjectionAddictionLoadPatch.Prefix(addiction, ref result));
        Assert(result is null);
    }
}

static void DefenseDutyUsesIndependentReturnNode()
{
    string dutiesPath = FindRepositoryFile(Path.Combine("Defs", "DutyDefs", "ClashOfRim_Duties.xml"));
    XDocument document = XDocument.Load(dutiesPath);
    XElement defendDuty = document
        .Descendants("DutyDef")
        .Single(def => string.Equals(
            def.Element("defName")?.Value,
            "ClashOfRim_DefendDefensePoint",
            StringComparison.Ordinal));
    List<string> jobGivers = defendDuty
        .Descendants("subNodes")
        .Elements("li")
        .Select(node => node.Attribute("Class")?.Value ?? string.Empty)
        .ToList();

    Assert(jobGivers.Contains("AIRsLight.ClashOfRim.Raids.JobGiver_ClashReturnToDefensePoint"));
    Assert(!jobGivers.Contains("JobGiver_GotoTravelDestination"));
}

static string FindRepositoryFile(string relativePath)
{
    foreach (string startPath in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
    {
        DirectoryInfo? directory = new(startPath);
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }
    }

    throw new FileNotFoundException("Could not locate repository file.", relativePath);
}

static Thing MakeThing(string defName)
{
    var thing = (Thing)FormatterServices.GetUninitializedObject(typeof(Thing));
    thing.def = MakeDef(defName);
    return thing;
}

static ThingDef MakeDef(string defName)
{
    var def = (ThingDef)FormatterServices.GetUninitializedObject(typeof(ThingDef));
    def.defName = defName;
    def.category = ThingCategory.Item;
    def.stackLimit = 1;
    return def;
}

static ThingWithComps MakeThingWithComp<TComp>(string defName, out TComp comp)
    where TComp : ThingComp
{
    var thing = (ThingWithComps)FormatterServices.GetUninitializedObject(typeof(ThingWithComps));
    thing.def = MakeDef(defName);
    comp = (TComp)FormatterServices.GetUninitializedObject(typeof(TComp));
    comp.parent = thing;
    typeof(ThingWithComps).GetField("comps", BindingFlags.Instance | BindingFlags.NonPublic)!
        .SetValue(thing, new List<ThingComp> { comp });
    if (comp is CompStyleable styleable)
    {
        thing.compStyleable = styleable;
    }
    return thing;
}

static void Assert(bool condition)
{
    if (!condition)
    {
        throw new InvalidOperationException("assertion failed");
    }
}
