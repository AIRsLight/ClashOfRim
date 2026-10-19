using AIRsLight.ClashOfRim.Modules.Diagnostics;
using AIRsLight.ClashOfRim.Multiplayer.Diagnostics;
using AIRsLight.ClashOfRim.Modules.Presence;
using AIRsLight.ClashOfRim.Modules.Multiplayer;
using AIRsLight.ClashOfRim.Multiplayer.Pages;
using AIRsLight.ClashOfRim.Multiplayer.Players;
using AIRsLight.ClashOfRim.Multiplayer.Achievements;
using AIRsLight.ClashOfRim.Modules.Chat;
using AIRsLight.ClashOfRim.Chat;
using AIRsLight.ClashOfRim.Modules.Administration;
using AIRsLight.ClashOfRim.Modules.World;
using AIRsLight.ClashOfRim.Modules.Achievements;
using AIRsLight.ClashOfRim.Achievements;
using AIRsLight.ClashOfRim.Account;
using AIRsLight.ClashOfRim.Modules.Accounts;
using AIRsLight.ClashOfRim.Modules.Diplomacy;
using AIRsLight.ClashOfRim.Modules.Raids;
using AIRsLight.ClashOfRim.Modules.Maps;
using AIRsLight.ClashOfRim.Modules.Mercenaries;
using AIRsLight.ClashOfRim.Modules.Support;
using AIRsLight.ClashOfRim.Modules.Shop;
using AIRsLight.ClashOfRim.ServerShop;
using AIRsLight.ClashOfRim.Modules.Trades;
using AIRsLight.ClashOfRim.Modules.Gifts;
using AIRsLight.ClashOfRim.Modules.Bank;
using AIRsLight.ClashOfRim.Modules.Events;
using AIRsLight.ClashOfRim.Modules.Synchronization;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Transport;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.Admin;
using AIRsLight.ClashOfRim.Bank;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.ClientSnapshots;
using AIRsLight.ClashOfRim.CompatibilityClient;
using AIRsLight.ClashOfRim.Modules.Compatibility;
using AIRsLight.ClashOfRim.Diplomacy;
using AIRsLight.ClashOfRim.EventLetters;
using AIRsLight.ClashOfRim.Gifts;
using AIRsLight.ClashOfRim.MainMenu;
using AIRsLight.ClashOfRim.Mercenaries;
using AIRsLight.ClashOfRim.Pawns;
using AIRsLight.ClashOfRim.Raids;
using AIRsLight.ClashOfRim.RemoteMaps;
using AIRsLight.ClashOfRim.Support;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility;
using AIRsLight.ClashOfRim.Trades;
using AIRsLight.ClashOfRim.WorldObjects;
using AIRsLight.ClashOfRim.Protocol;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.Profile;

namespace AIRsLight.ClashOfRim;

public sealed partial class ClashOfRimMod : Mod
{
    public const string PackageId = "AIRsLight.ClashOfRim";
    // Mirrors ClashOfRim.Protocol.ProtocolErrorCode.EventNotFound; the mod assembly does not compile protocol sources.
    private const int ProtocolErrorEventNotFound = 5;

    private readonly ClashOfRimSettings settings;
    private readonly ClientEventInbox eventInbox = new();
    private readonly ClientEventRefresh eventRefresh;
    private readonly ClientEventState eventState;
    private readonly ClientBankState bankState;
    private readonly ClientBankOperations bankOperations;
    private readonly ClientLocalSnapshotConfirmation localSnapshotConfirmation;
    private readonly ClientMercenaryConfirmation mercenaryConfirmation;
    private readonly ClientMercenaryIncidents mercenaryIncidents;
    private readonly ClientMercenaryQuotes mercenaryQuotes;
    private readonly ClientMercenaryState mercenaryState;
    private readonly ClientMercenaryHiring mercenaryHiring;
    internal ClientMercenaryHiring MercenaryHiring => mercenaryHiring;
    private readonly ClientTradeState tradeState;
    private readonly ClientTradeMarket tradeMarket;
    private readonly ClientTradeActions tradeActions;
    private readonly ClientShopState shopState;
    private readonly ClientShopCatalog shopCatalog;
    private readonly ClientShopOperations shopOperations;
    internal ClientShopCatalog ShopCatalog => shopCatalog;
    internal ClientShopOperations ShopOperations => shopOperations;
    internal ClientTradeActions TradeActions => tradeActions;
    private readonly ClientGiftActions giftActions;
    private readonly ClientSupportActions supportActions;
    private readonly ClientSupportReturns supportReturns;
    private readonly ClientObservationMaps observationMaps;
    private readonly ClientRaidCommands raidCommands;
    private readonly ClientRaidSettlements raidSettlements;
    private readonly ClientRaidRecovery raidRecovery;
    private readonly ClientSnapshotReload snapshotReload;
    private readonly ClientSnapshotSaving snapshotSaving;
    internal ClientGiftActions GiftActions => giftActions;
    private readonly ClientEventConfirmationWorkflow eventConfirmations;
    private readonly ClientEventApplications eventApplications;
    private readonly ClientSnapshotFailureRecovery snapshotFailureRecovery;
    internal IReadOnlyList<ModEventDetailDto>? ReadEventDetails(int? knownVersion, out int version) =>
        eventInbox.CopyDetails(knownVersion, out version);
    private readonly ClientSnapshotPreparationAdapter snapshotPreparation = ClientSnapshotComposition.CreatePreparation();
    private readonly ClientSnapshotPublication snapshotPublication;
    private readonly ClientSynchronizationCoordinator synchronization = ClientSnapshotComposition.Synchronization;
    private readonly ClientColonyRegistration colonyRegistration;
    private readonly ClientColonyAppearance colonyAppearance;
    private readonly ClientColonyAbandonment colonyAbandonment;
    private readonly ClientAdminAccess adminAccess;
    private readonly ClientAdministration administration;
    private readonly ClientWorldWrites worldWrites;
    private readonly ClientDiplomacyActions diplomacyActions;
    private readonly ClientPlayerDirectory playerDirectory;
    private readonly ClientCompatibilityBaseline compatibilityBaseline;
    private readonly ClientCompatibilityOverride compatibilityOverride;
    private readonly ClientLocalMutationCoordinator localMutations = new();
    private readonly ClientSessionNotifications sessionNotifications;
    private readonly ClientSessionLifecycle sessionLifecycle;
    private readonly ClientChat chat;
    private readonly ClientPasswordChange passwordChange;
    private readonly ClientRuntimeDiagnostics diagnostics;
    private readonly ClientSessionRecovery sessionRecovery;
    private readonly ClientSessionConnection sessionConnection = new(new ClientSessionWebSocketTransport(new ClientWebSocketFactory(), new ClientProtocolMetadata()));
    private readonly ClientAchievementQueries achievementQueries;
    private readonly ClientEndgameAchievements endgameAchievements;
    private readonly ClientWorldPreparation worldPreparation;
    private readonly ClientWorldEntry worldEntry;
    private readonly ClientWorldEntryStart worldEntryStart;
    private readonly ClientWorldSourceGame sourceGame = new(new ClientWorldSourceGameRuntime());
    private readonly ClientCompatibilityPresentation compatibilityPresentation;
    private readonly ClientWorldSetupState worldSetup;
    private readonly ClientStorytellerConfiguration storytellerConfiguration;
    private readonly ClientWorldConfiguration worldConfiguration;
    private readonly ClientWorldCatalog worldCatalog;
    private readonly ClientWorldBaselineSubmission worldBaselineSubmission;
    private readonly ClientWorldBaselineCapture worldBaselineCapture;
    private readonly ClientAdminBaselineCapture adminBaselineCapture;
    private readonly ClientWorldMarkers worldMarkers;
    private readonly ClientWorldLoginPublication worldLoginPublication;
    private readonly ClientSessionStartup sessionStartup;
    private readonly ClientWorldSynchronization worldSynchronization;
    private readonly ClientOccupiedColonySites occupiedColonySites;

    internal static ClashOfRimMod? Instance { get; private set; }

    public ClashOfRimMod(ModContentPack content)
        : base(content)
    {
        settings = GetSettings<ClashOfRimSettings>();
        snapshotPublication = ClientSnapshotComposition.CreatePublication(settings);
        var sessionIdentity = new ClientSessionIdentityStore(settings);
        sessionLifecycle = new ClientSessionLifecycle(sessionIdentity, new ClientSessionLogoutTransport(), sessionConnection);
        var eventReader = new ClientEventReader(sessionLifecycle, eventInbox, new ClientEventReadTransport(), new ClientSnapshotGame());
        var eventReadPresentation = new ClientEventReadPresentation();
        eventState = new ClientEventState(eventReader, eventReadPresentation);
        diagnostics = new ClientRuntimeDiagnostics(sessionLifecycle, eventState);
        adminAccess = new ClientAdminAccess(sessionLifecycle);
        ClashOfRimCompatibilityApi.AttachSession(sessionLifecycle);
        ClashOfRimCompatibilityApi.AttachAdministration(adminAccess);
        adminBaselineCapture = new ClientAdminBaselineCapture(sessionLifecycle, new AIRsLight.ClashOfRim.Admin.ClientAdminBaselineRuntime(), ClientMainThreadDispatcher.EnqueueSession);
        var localManifest = new ClientLocalCompatibilityManifest();
        compatibilityBaseline = new ClientCompatibilityBaseline(new ClientCompatibilityBaselineRuntime());
        var compatibilityEnvironment = new ClientProtocolEnvironment();
        compatibilityOverride = new ClientCompatibilityOverride(adminAccess, localManifest, compatibilityBaseline, adminBaselineCapture,
            new AIRsLight.ClashOfRim.Admin.ClientAdminBaselineTransport(compatibilityEnvironment, TimeSpan.FromSeconds(15)),
            new ClientCompatibilityOverrideTransport(compatibilityEnvironment), new ClientCompatibilityOverridePresentation(), ClientMainThreadDispatcher.EnqueueSession);
        administration = new ClientAdministration(adminAccess,
            new AIRsLight.ClashOfRim.Admin.ClientAdminTransport(new ClientProtocolEnvironment()),
            new AIRsLight.ClashOfRim.Admin.ClientAdminPresentation());
        var serverMetadata = new ClientServerMetadata(sessionLifecycle, compatibilityBaseline, administration);
        worldSetup = new ClientWorldSetupState(sessionLifecycle);
        storytellerConfiguration = new ClientStorytellerConfiguration(sessionLifecycle, worldSetup, new ClientStorytellerRuntime());
        ClientStorytellerPage.Attach(storytellerConfiguration);
        compatibilityPresentation = new ClientCompatibilityPresentation(CreateCompatibilityMismatch);
        var entryPresentation = new ClientWorldEntryPresentation(message => diagnostics.LoginStatus = message,
            (server, account) => Find.WindowStack.Add(new AIRsLight.ClashOfRim.MainMenu.ClashOfRimAccountRegistrationDialog(CreateAccountRegistrationForm(server, account))), compatibilityPresentation);
        var worldTransport = new ClientWorldPreparationTransport(new ClientProtocolEnvironment());
        worldPreparation = new ClientWorldPreparation(sessionLifecycle, new ClientSnapshotGame(), worldTransport, entryPresentation);
        worldBaselineSubmission = new ClientWorldBaselineSubmission(sessionLifecycle, new ClientWorldBaselineRuntime(), worldTransport);
        worldBaselineCapture = new ClientWorldBaselineCapture(worldBaselineSubmission, storytellerConfiguration);
        var sessionReconnect = new ClientSessionReconnect(new ClientSessionReconnectTransport(new ClientProtocolEnvironment()), sessionLifecycle, synchronization);
        passwordChange = new ClientPasswordChange(sessionLifecycle,
            new ClientPasswordTransport(new ClientProtocolEnvironment()),
            new ClientPasswordStore(settings), new ClientPasswordPresentation(), ClientMainThreadDispatcher.EnqueueSession);
        var eventConfirmation = new ClientEventConfirmation(sessionLifecycle, new ClientSnapshotGame(),
            snapshotPublication, eventInbox, new ClientEventConfirmationTransport());
        var eventConfirmationPreparation = new ClientEventConfirmationPreparation(sessionLifecycle, synchronization, localMutations, eventConfirmation);
        snapshotFailureRecovery = new ClientSnapshotFailureRecovery(sessionLifecycle, localMutations, synchronization,
            new ClientSnapshotGame(), new ClientSnapshotFailureView());
        var bankPresentation = new ClientBankPresentation();
        bankState = new ClientBankState(sessionLifecycle, new ClientSnapshotGame(), bankPresentation);
        var snapshotTransactions = new ClientSnapshotTransaction(sessionLifecycle, new ClientSnapshotGame(), snapshotPublication);
        var mutationAdmission = new ClientSnapshotMutationAdmission(sessionLifecycle, synchronization, localMutations, snapshotTransactions);
        var diplomacyTransport = new ClientDiplomacyTransport(new ClientProtocolEnvironment());
        playerDirectory = new ClientPlayerDirectory(sessionLifecycle, new ClientSnapshotGame(), synchronization, localMutations,
            diplomacyTransport, new ClientPlayerSelectionStore(settings), new ClientPlayerDirectoryPresentation(), new ClientPlayerDirectoryRuntime());
        achievementQueries = new ClientAchievementQueries(sessionLifecycle, new ClientSnapshotGame(),
            new ClientAchievementTransport(new ClientProtocolEnvironment()),
            new ClientAchievementPresentation());
        var diplomacyCreation = new ClientDiplomacyCreation(mutationAdmission, snapshotTransactions, localMutations, diplomacyTransport,
            new ClientDiplomacyCreationPresentation(message => playerDirectory.Message = message));
        diplomacyActions = new ClientDiplomacyActions(playerDirectory, diplomacyCreation);
        localSnapshotConfirmation = new ClientLocalSnapshotConfirmation(sessionLifecycle, new ClientSnapshotGame(), mutationAdmission,
            synchronization, localMutations, snapshotTransactions, snapshotFailureRecovery, ClientSnapshotComposition.Create(settings),
            new ClientLocalSnapshotPresentation(message => diagnostics.SnapshotUploadStatus = message));
        localSnapshotConfirmation.Completed += StartQueuedAutomaticEventRefreshAfterLocalAtomicMutation;
        endgameAchievements = new ClientEndgameAchievements(sessionLifecycle, new ClientSnapshotGame(),
            new ClientEndgameRuntime(), new ClientAchievementPresentation(), localSnapshotConfirmation);
        endgameAchievements.Confirmed += () => _ = achievementQueries.RefreshAsync();
        var shopPresentation = new ClientShopPresentation();
        var shopTransport = new ClientShopTransport();
        shopState = new ClientShopState(sessionLifecycle, new ClientSnapshotGame(), shopPresentation);
        shopCatalog = new ClientShopCatalog(shopState, localMutations, shopTransport, shopPresentation, () => adminAccess.IsAdministrator);
        shopOperations = new ClientShopOperations(shopState, mutationAdmission, synchronization, localMutations, snapshotTransactions,
            snapshotFailureRecovery, new ClientShopRuntime(), shopTransport, shopPresentation, shopPresentation);
        var tradePresentation = new ClientTradeMarketPresentation();
        tradeState = new ClientTradeState(sessionLifecycle, new ClientSnapshotGame(), tradePresentation);
        tradeMarket = new ClientTradeMarket(sessionLifecycle, tradeState, localMutations, mutationAdmission,
            snapshotTransactions, new ClientTradeMarketTransport(), tradePresentation);
        var tradeTransport = new ClientTradeOperationsTransport();
        var tradeOperations = new ClientTradeOperations(mutationAdmission, synchronization, localMutations, snapshotTransactions,
            snapshotFailureRecovery, tradeState, tradeTransport, new ClientTradeOperationsPresentation());
        tradeActions = new ClientTradeActions(tradeOperations, tradeState, sessionLifecycle, tradeTransport, () => WorldMapMarkersSnapshot,
            () => AdminStatusSnapshot?.Configuration?.TradeBaseFeeRate, () => AdminStatusSnapshot?.Configuration?.FixedTradeFees);
        bankOperations = new ClientBankOperations(mutationAdmission, synchronization, localMutations,
            snapshotTransactions, snapshotFailureRecovery,
            bankState, new ClientBankTransport(), new ClientBankRuntime(), bankPresentation);
        bankOperations.Completed += StartQueuedAutomaticEventRefreshAfterLocalAtomicMutation;
        var mercenaryPresentation = new ClientMercenaryPresentation();
        var mercenaryTransport = new ClientMercenaryTransport();
        mercenaryState = new ClientMercenaryState(sessionLifecycle, new ClientSnapshotGame(), mercenaryPresentation);
        mercenaryQuotes = new ClientMercenaryQuotes(sessionLifecycle, new ClientSnapshotGame(), bankState,
            mercenaryTransport, mercenaryPresentation, () => Find.TickManager?.TicksGame ?? 0);
        mercenaryHiring = new ClientMercenaryHiring(mercenaryState, bankState, mutationAdmission, synchronization, localMutations,
            snapshotTransactions, snapshotFailureRecovery, new ClientMercenaryRuntime(), mercenaryTransport, mercenaryPresentation);
        mercenaryHiring.Completed += StartQueuedAutomaticEventRefreshAfterLocalAtomicMutation;
        mercenaryConfirmation = new ClientMercenaryConfirmation(localSnapshotConfirmation, mercenaryState, mercenaryPresentation);
        mercenaryIncidents = new ClientMercenaryIncidents(sessionLifecycle, mercenaryState, bankState, synchronization, localMutations,
            mutationAdmission, snapshotTransactions, snapshotFailureRecovery, mercenaryConfirmation, mercenaryTransport,
            new ClientMercenaryIncidentRuntime(), mercenaryPresentation);
        mercenaryIncidents.BankRefreshRequested += StartRefreshBankStatus;
        mercenaryIncidents.Completed += StartQueuedAutomaticEventRefreshAfterLocalAtomicMutation;
        mercenaryConfirmation.ContractCompleted += (id, key) => _ = mercenaryIncidents.ReportAsync(id, "Completed", key);
        mercenaryConfirmation.BankRefreshRequested += StartRefreshBankStatus;
        Func<ClientGiftCapabilities> giftCapabilities = () => new(GiftsEnabled, PvpEnabled);
        var giftPresentation = new ClientGiftPresentation(message => eventState.ActionStatus = message);
        var gifts = new ClientGiftOperations(mutationAdmission, synchronization, localMutations, snapshotTransactions,
            snapshotFailureRecovery, new ClientGiftTransport(), giftPresentation, giftCapabilities);
        gifts.Completed += StartQueuedAutomaticEventRefreshAfterLocalAtomicMutation;
        giftActions = new ClientGiftActions(gifts, sessionLifecycle, new ClientSnapshotGame(), giftPresentation,
            giftCapabilities, () => WorldMapMarkersSnapshot);
        var supportPresentation = new ClientSupportPresentation(message => tradeState.Message = message);
        var supportSending = new ClientSupportSending(sessionLifecycle, new ClientSnapshotGame(), mutationAdmission, synchronization,
            localMutations, snapshotTransactions, snapshotFailureRecovery, new ClientSupportTransport(), supportPresentation);
        supportSending.Completed += StartQueuedAutomaticEventRefreshAfterLocalAtomicMutation;
        supportActions = new ClientSupportActions(supportSending, supportPresentation);
        supportReturns = new ClientSupportReturns(sessionLifecycle, new ClientSnapshotGame(), mutationAdmission, localMutations,
            snapshotTransactions, snapshotFailureRecovery, localSnapshotConfirmation, new ClientSupportReturnRuntime(supportPresentation),
            new ClientSupportTransport(), supportPresentation);
        supportReturns.Completed += StartQueuedAutomaticEventRefreshAfterLocalAtomicMutation;
        var raidTransport = new ClientRaidTransport();
        var raidPresentation = new ClientRaidLaunchPresentation(message => diagnostics.WorldMapStatus = message);
        var raidLaunching = new ClientRaidLaunching(sessionLifecycle, mutationAdmission, synchronization, localMutations,
            snapshotTransactions, snapshotFailureRecovery, new ClientRaidPreparation(raidTransport, new ClientRemoteSnapshotTransport()),
            raidTransport, raidPresentation, () => PvpEnabled);
        raidCommands = new ClientRaidCommands(sessionLifecycle, raidLaunching, raidPresentation);
        ClientTransporterActions.Attach(new ClientTransporterActions(giftActions, raidCommands, giftCapabilities));
        raidLaunching.Completed += StartQueuedAutomaticEventRefreshAfterLocalAtomicMutation;
        raidSettlements = new ClientRaidSettlements(sessionLifecycle, mutationAdmission, synchronization, localMutations, snapshotTransactions,
            snapshotFailureRecovery, new ClientRaidSupportLosses(snapshotTransactions, new ClientSupportTransport()),
            new ClientEventConfirmationTransport(), eventInbox, localSnapshotConfirmation, new ClientRaidSettlementPresentation(message => diagnostics.WorldMapStatus = message));
        raidSettlements.Completed += () => _ = playerDirectory.RefreshAsync(manual: false);
        var raidFinishActions = new ClientRaidFinishActions(raidSettlements);
        ClientRaidFinishActions.Attach(raidFinishActions);
        ClientRaidBattleOverlay.Attach(new(raidFinishActions, () => diagnostics.WorldMapStatus));
        raidSettlements.Expired += StartRestoreAfterRaid;
        raidRecovery = new ClientRaidRecovery(sessionLifecycle, new ClientSnapshotGame(), snapshotFailureRecovery, raidSettlements,
            new ClientRaidRecoveryPresentation(message => diagnostics.WorldMapStatus = message));
        raidRecovery.ReloadRequested += StartRestoreAfterRaid;
        snapshotReload = new ClientSnapshotReload(sessionLifecycle, synchronization, localMutations, snapshotFailureRecovery,
            new ClientSessionAuthenticationTransport(), new ClientColonySnapshotDownload(new ClientRemoteSnapshotTransport()),
            snapshotPublication, new ClientSnapshotReloadRuntime(), new ClientSnapshotReloadPresentation(
                (message, purpose) => { diagnostics.LoginStatus = message; if (purpose == ClientSnapshotReloadPurpose.Raid) diagnostics.WorldMapStatus = message; },
                response => ShowCompatibilityMismatchWindow(response)));
        ClientRaidLifecycleCallbacks.Attach(new(new ClientRaidLifecycle(new ClientSnapshotGame(), snapshotReload, raidRecovery, synchronization, raidSettlements)));
        var entryStore = new ClientWorldEntryStore(settings);
        var featureCatalogs = new ClientWorldFeatureCatalogs(sessionLifecycle, worldTransport);
        featureCatalogs.Diagnostic += message => Log.Warning(message);
        var configurationRuntime = new ClientWorldConfigurationRuntime();
        worldConfiguration = new ClientWorldConfiguration(sessionLifecycle, configurationRuntime, entryStore, storytellerConfiguration, featureCatalogs);
        occupiedColonySites = new ClientOccupiedColonySites(sessionLifecycle, entryStore, new ClientOccupiedColonySitesRuntime());
        ClientWorldSiteReservations.Attach(new ClientWorldSiteReservations(occupiedColonySites));
        worldMarkers = new ClientWorldMarkers(sessionLifecycle, entryStore, occupiedColonySites, new ClientWorldMarkersRuntime(),
            new ClientWorldMarkersPresentation(message => diagnostics.WorldMapStatus = message));
        worldLoginPublication = new ClientWorldLoginPublication(sessionLifecycle, worldMarkers);
        worldSynchronization = new ClientWorldSynchronization(sessionLifecycle, entryStore, localMutations, worldMarkers,
            new ClientWorldSynchronizationRuntime(), new ClientWorldMarkerTransport(new ClientProtocolEnvironment()),
            new ClientWorldWritePresentation(message => diagnostics.WorldMapStatus = message), tradeMarket.CaptureRefresh);
        ClientWorldSynchronizationCallbacks.Attach(new(worldSynchronization));
        colonyAppearance = new ClientColonyAppearance(sessionLifecycle, new ClientColonyAppearanceStore(settings), new ClientColonyAppearancePresentation());
        var worldPublication = new ClientWorldPublication(entryStore, worldConfiguration, occupiedColonySites, colonyAppearance, worldMarkers);
        var colonyTransport = new ClientColonyTransport(new ClientProtocolEnvironment());
        colonyRegistration = new ClientColonyRegistration(sessionLifecycle, entryStore, localMutations, new ClientColonyRegistrationRuntime(), colonyTransport, worldPublication);
        ClientColonySiteEvents.Attach(new ClientColonySiteEvents(colonyRegistration, colonyAppearance));
        snapshotSaving = new ClientSnapshotSaving(sessionLifecycle, synchronization, localMutations, ClientSnapshotComposition.Create(settings),
            new ClientSnapshotGame(), entryStore, configurationRuntime, colonyRegistration, colonyAppearance,
            new ClientSnapshotSavingPresentation(message => diagnostics.SnapshotUploadStatus = message));
        ClientSnapshotSavingCallbacks.Attach(new(snapshotSaving));
        ClientSessionExitCallbacks.Attach(new(sessionLifecycle));
        colonyAbandonment = new ClientColonyAbandonment(sessionLifecycle, entryStore, new ClientWorldCatalogRuntime(), colonyTransport,
            new ClientColonyAbandonPresentation(message => diagnostics.WorldMapStatus = message));
        ClientColonyGizmos.Attach(new ClientColonyGizmos(colonyAppearance, colonyAbandonment));
        var colonyRelocation = new ClientColonyRelocation(sessionLifecycle, entryStore, new ClientWorldCatalogRuntime(), mutationAdmission,
            synchronization, localMutations, snapshotTransactions, ClientSnapshotComposition.Create(settings), snapshotFailureRecovery,
            colonyRegistration, worldPublication, colonyTransport, new ClientColonyRelocationPresentation());
        ClientColonyRelocationCommands.Attach(new ClientColonyRelocationCommands(colonyRelocation));
        colonyRelocation.Completed += StartQueuedAutomaticEventRefreshAfterLocalAtomicMutation;
        snapshotFailureRecovery.ReturningToMenu += () => colonyRegistration.Suppressed = false;
        colonyAppearance.RegistrationRequested += (appearance, reason) => _ = colonyRegistration.RequestAsync(
            ClientColonyRegistrationKind.Sites, reason, appearance, suppressNotification: true);
        colonyRegistration.Diagnostic += (message, warning) => { if (warning) Log.Warning(message); else ClashLog.Message(message); };
        worldCatalog = new ClientWorldCatalog(sessionLifecycle, new ClientWorldCatalogRuntime(), worldTransport,
            new ClientWorldCatalogPresentation(message => diagnostics.WorldMapStatus = message), worldPublication, serverMetadata);
        ClashOfRimCompatibilityApi.AttachCatalog(worldCatalog);
        var worldWritePresentation = new ClientWorldWritePresentation(message => administration.Message = message);
        worldWrites = new ClientWorldWrites(sessionLifecycle, adminAccess, worldBaselineCapture, worldBaselineSubmission,
            storytellerConfiguration, worldPublication, worldWritePresentation, worldWritePresentation);
        ClientStorytellerEditor.Attach(new ClientStorytellerEditor(worldWrites, colonyAppearance));
        ClientWorldGenerationPage.Attach(new ClientWorldGeneration(sessionLifecycle, worldSetup, worldConfiguration, new ClientWorldGenerationRuntime(), new ClientWorldGenerationPresentation(message => diagnostics.LoginStatus = message)));
        worldEntry = new ClientWorldEntry(sessionLifecycle, new ClientSnapshotGame(), worldSetup,
            entryStore, worldTransport, entryPresentation, snapshotReload, worldPublication, serverMetadata);
        worldEntryStart = new ClientWorldEntryStart(sessionLifecycle, synchronization, new ClientSnapshotGame(),
            entryStore, entryPresentation, worldPreparation, worldEntry);
        ClientServerEntryWindows.Attach(new(worldEntryStart));
        observationMaps = new ClientObservationMaps(sessionLifecycle, synchronization, new ClientSnapshotGame(),
            new ClientObservationTransport(), new ClientObservationRuntime(),
            new ClientObservationPresentation(message => diagnostics.WorldMapStatus = message, response => ShowCompatibilityMismatchWindow(response)));
        RemoteWorldObjectUiUtility.AttachCommands(new ClientWorldObjectCommands(sessionLifecycle, new ClientSnapshotGame(),
            observationMaps, new ClientWorldObjectCommandRuntime()));
        eventConfirmations = new ClientEventConfirmationWorkflow(sessionLifecycle, eventConfirmationPreparation, eventConfirmation,
            localMutations, snapshotFailureRecovery, eventInbox, new ClientEventConfirmationPresentation((purpose, message) =>
            {
                switch (purpose)
                {
                    case ClientEventConfirmationPurpose.Trade: tradeState.Message = message; break;
                    case ClientEventConfirmationPurpose.AutomaticEvents: eventState.SetDetailsStatus(message); break;
                    default: eventState.ActionStatus = message; break;
                }
            }), new ClientEventConfirmationEffects());
        eventConfirmations.Completed += StartQueuedAutomaticEventRefreshAfterLocalAtomicMutation;
        var eventDecisions = new ClientEventDecisions(sessionLifecycle, synchronization, localMutations, new ClientSnapshotGame(),
            new ClientEventDecisionTransport(), new ClientEventDecisionPresentation(message => eventState.ActionStatus = message),
            new ClientEventDecisionRuntime(RequestWorldMapMarkerRefresh));
        var giftPackageHydration = new ClientGiftPackageHydration(new ClientProtocolEnvironment());
        var eventDelivery = new ClientEventDelivery(eventInbox, new ClientEventLetterRuntime(RequestWorldMapMarkerRefresh));
        eventApplications = new ClientEventApplications(sessionLifecycle, synchronization, localMutations, eventInbox,
            new ClientSnapshotGame(), new ClientEventApplicationRuntime(), giftPackageHydration, eventConfirmations, eventDecisions,
            new ClientEventApplicationPresentation((automatic, message) =>
            {
                if (automatic) eventState.SetDetailsStatus(message);
                else eventState.ActionStatus = message;
            }), snapshotFailureRecovery, eventDelivery);
        eventRefresh = new ClientEventRefresh(sessionLifecycle, eventReader, eventInbox, synchronization, localMutations,
            eventApplications, eventDelivery, eventReadPresentation, eventState);
        chat = new ClientChat(sessionLifecycle, new ClientChatTransport(new ClientProtocolEnvironment()), new ClientChatPresentation());
        var notificationEffects = new ClientSessionNotificationEffects(chat, eventRefresh, worldCatalog);
        sessionNotifications = new ClientSessionNotifications(sessionLifecycle, sessionConnection, eventState, notificationEffects,
            new ClientSessionNotificationPresentation(message => diagnostics.PresenceStatus = message));
        sessionRecovery = new ClientSessionRecovery(sessionLifecycle, sessionConnection, sessionReconnect, localMutations, worldLoginPublication,
            new ClientSessionRecoveryRuntime(), new ClientSessionRecoveryPresentation(message => diagnostics.LoginStatus = message, message => diagnostics.PresenceStatus = message),
            new ClientSessionRecoveryEffects(eventRefresh, playerDirectory, chat, sessionNotifications, raidRecovery));
        notificationEffects.SessionExpired += sessionRecovery.Expired;
        var sessionFollowups = new ClientSessionFollowups(eventRefresh, playerDirectory, chat, colonyRegistration, colonyAppearance,
            worldSynchronization, sessionNotifications, new ClientSessionFollowupText());
        var snapshotWorld = new ClientSnapshotWorld(sourceGame, worldConfiguration, entryStore, configurationRuntime, sessionConnection,
            sessionFollowups, raidRecovery, new ClientRaidRecoveryRuntime(), new ClientSnapshotWorldPresentation(message => diagnostics.WorldMapStatus = message));
        snapshotReload.Completed += snapshotWorld.Continue;
        var initialWorldPresentation = new ClientInitialWorldPresentation(message => diagnostics.LoginStatus = message, message => diagnostics.SnapshotUploadStatus = message, sessionConnection);
        var initialWorldSetup = new ClientInitialWorldSetup(sessionLifecycle, worldSetup, synchronization, worldBaselineCapture, worldBaselineSubmission,
            adminBaselineCapture, new AIRsLight.ClashOfRim.Admin.ClientAdminBaselineTransport(new ClientProtocolEnvironment()),
            ClientSnapshotComposition.Create(settings), initialWorldPresentation, initialWorldPresentation, sessionFollowups);
        ClientWorldSetupPages.Attach(new ClientWorldSetupPages(new ClientWorldSetupLifecycle(sessionLifecycle, new ClientWorldCatalogRuntime(),
            sourceGame, initialWorldSetup, colonyAppearance, worldSynchronization, worldMarkers, initialWorldPresentation)));
        sessionStartup = new ClientSessionStartup(sessionLifecycle, synchronization, localMutations, sourceGame, worldLoginPublication,
            new ClientSessionLogin(sessionLifecycle, new ClientSessionLoginTransport(new ClientProtocolEnvironment())),
            ClientSnapshotComposition.Create(settings), new ClientSessionStartupRuntime(sessionConnection),
            new ClientSessionStartupPresentation(message => diagnostics.LoginStatus = message, message => diagnostics.SnapshotUploadStatus = message, response => ShowCompatibilityMismatchWindow(response)), sessionFollowups);
        ClientAutomaticSessionStartup.Attach(new(sessionStartup));
        ClientChatOverlay.Attach(new(new ClientChatOverlayModel(chat, sessionLifecycle, new ClientSnapshotGame(), () => sessionStartup.IsInActiveWorld)));
        var operationFollowups = new ClientOperationFollowups(eventRefresh, shopCatalog, playerDirectory, worldSynchronization, new ClientSessionFollowupText());
        shopOperations.Completed += step => _ = operationFollowups.ShopCompleted(step);
        tradeOperations.Completed += (operation, step) => _ = operationFollowups.TradeCompleted(operation, step);
        tradeMarket.Accepted += step => _ = operationFollowups.TradeAccepted(step);
        diplomacyCreation.RefreshRequired += step => _ = operationFollowups.DiplomacyFinished(step);
        worldWrites.BaselineUpdated += step => _ = operationFollowups.BaselineUpdated(step);
        eventDecisions.FailureReported += step => _ = operationFollowups.FailureReported(step);
        eventApplications.RefreshRequested += StartAutomaticEventRefresh;
        eventApplications.Deferred += eventRefresh.Defer;
        eventApplications.CompletedWithoutConfirmation += StartQueuedAutomaticEventRefreshAfterLocalAtomicMutation;
        if (!string.Equals(content.PackageId, PackageId, StringComparison.OrdinalIgnoreCase))
        {
            Log.Warning("[ClashOfRim] Ignored ClashOfRim mod class loaded from unexpected package: " + (content.PackageId ?? "<null>") + ".");
            return;
        }

        Instance = this;
        var pawnPreviews = new ClientPawnPreviews(sessionLifecycle, new ClientSnapshotGame(),
            new ClientPawnPreviewTransport(new ClientProtocolEnvironment()), new ClientPawnPreviewRuntime());
        TradeUiUtility.AttachPawnPreviews(pawnPreviews);
        sessionLifecycle.IdentityChanged += serverMetadata.ObserveIdentity;
        ClientSessionCleanup.Attach(sessionLifecycle, synchronization, localMutations, eventInbox, eventState, sourceGame,
            new ClientSessionExitResources(pawnPreviews), colonyRegistration, bankState, mercenaryState, mercenaryIncidents, shopState,
            tradeState, chat, worldMarkers, worldSynchronization, sessionIdentity, administration, sessionNotifications,
            worldSetup, playerDirectory, occupiedColonySites);
        var snapshotNotifications = new ClientSnapshotTransactionNotifications(sessionLifecycle, synchronization,
            new ClientSnapshotGame(), new ClientSnapshotTransactionPresentation());
        synchronization.SnapshotChanged += snapshotNotifications.Observe;
        sessionLifecycle.LogoutFailed += message => Log.Warning("[ClashOfRim] " + message);
        _ = new ClientConnectionStatus(sessionLifecycle, sessionConnection, new ClientConnectionStatusPresentation(message => diagnostics.PresenceStatus = message));
        var harmony = new Harmony(PackageId);
        ClashOfRimCompatibilityApi.BeginRegistrationCycle(PackageId);
        BuiltInClientCompatibility.Apply(harmony);
        harmony.PatchAll();
        ClashOfRimModNetworkClient.CaptureSessionUnavailable = sessionRecovery.CaptureUnavailable;
        ClashOfRimModNetworkClient.CompatibilityManifestJsonProvider = () => localManifest.BuildJson(null);
        ClashOfRimModNetworkClient.CompatibilityManifestIdProvider = () => localManifest.Id;
        ClashOfRimModNetworkClient.CompatibilityManifestSummaryJsonProvider = () => localManifest.SummaryJson;
        ClashOfRimModNetworkClient.CompatibilityManifestJsonForPackagesProvider = localManifest.BuildJson;
        ClashOfRimModNetworkClient.ServerEntryDiagnosticsLogger = localManifest.LogEntry;
        Log.Message("[ClashOfRim] Initialized for RimWorld " + VersionControl.CurrentVersionString
            + "; runtime=" + Environment.Version
            + "; Harmony=" + typeof(Harmony).Assembly.GetName().Version
            + "; patched methods=" + harmony.GetPatchedMethods().Count() + ".");
    }

    internal bool SnapshotUploadInProgress => synchronization.SnapshotInProgress;
    internal (ClientPlayerPanel Players, MultiplayerDiplomacyPage Diplomacy) CreatePlayerPages()
    {
        var players = new ClientPlayerPanel(sessionLifecycle, new ClientSnapshotGame(), playerDirectory, diplomacyActions, synchronization);
        var markers = worldMarkers;
        var worldSync = worldSynchronization;
        var locations = new PlayerColonyLocator(() => markers.Snapshot,
            reason => _ = worldSync.RequestAsync(ClientWorldSynchronizationKind.Markers, reason), () => Time.realtimeSinceStartup);
        return (players, new MultiplayerDiplomacyPage(players, locations));
    }

    internal MultiplayerAchievementsPage CreateAchievementsPage(ClientPlayerPanel players) =>
        new(new ClientAchievementPanel(sessionLifecycle, new ClientSnapshotGame(), achievementQueries,
            players, synchronization, new ClientAchievementTrophyPlacement()));

    internal MultiplayerTradePage CreateTradePage() =>
        new(new ClientTradePanel(sessionLifecycle, new ClientSnapshotGame(), tradeState, tradeMarket, synchronization, tradeActions));

    internal (ClientShopPanel Model, MultiplayerShopPage Page) CreateShopPage()
    {
        var model = CreateShopPanel();
        return (model, new(model));
    }

    private ClientShopPanel CreateShopPanel() =>
        new(sessionLifecycle, new ClientSnapshotGame(), shopState, shopCatalog, shopOperations, synchronization);

    private ClientAdminShopPanel CreateAdminShopPanel(ClientShopPanel shop) =>
        new(sessionLifecycle, adminAccess, shopState, shopCatalog, shop);

    internal ClientAdminShopPanel.Edit CreateAdminShopEdit(ModServerShopListingDto? listing)
    {
        var panel = CreateAdminShopPanel(CreateShopPanel());
        return listing is null ? panel.PrepareEdit() : panel.PrepareExistingEdit(listing);
    }

    internal (ClientBankPanel Model, MultiplayerBankPage Page) CreateBankPage()
    {
        var model = new ClientBankPanel(sessionLifecycle, new ClientSnapshotGame(), bankState, bankOperations, synchronization, new ClientBankRuntime());
        return (model, new(model));
    }

    internal MultiplayerMercenaryPage CreateMercenaryPage(ClientBankPanel bank) =>
        new(new ClientMercenaryPanel(sessionLifecycle, new ClientSnapshotGame(), mercenaryState, mercenaryQuotes,
            mercenaryHiring, synchronization, bank.Read, () => worldMarkers.PvpEnabled));

    internal MultiplayerAdminPage CreateAdminPage(ClientShopPanel shop)
    {
        var configuration = new ClientAdminConfigurationEditor(sessionLifecycle, adminAccess, administration);
        var players = new ClientAdminPlayerPanel(sessionLifecycle, adminAccess, administration);
        return new(new ClientAdminPanel(sessionLifecycle, adminAccess, administration), configuration, new(configuration),
            new(CreateAdminShopPanel(shop)), new(players),
            new(new ClientAdminMaintenancePanel(sessionLifecycle, players, administration)),
            new(new ClientAdminManifestPanel(configuration, new ClientLocalCompatibilityManifest(), compatibilityOverride, worldWrites, colonyAppearance.Capture)),
            new(new ClientAdminAuditPanel(sessionLifecycle, adminAccess, administration)));
    }

    private ClientCompatibilityMismatch CreateCompatibilityMismatch(ModLoginResponseDto response, string? language,
        Action? accept, Action? cancel, Func<bool>? current)
    {
        var local = new ClientLocalCompatibilityManifest();
        return new(sessionLifecycle, new ClientSnapshotGame(), adminAccess, compatibilityOverride, local, local,
            new ClientCompatibilityOverridePresentation(), new ClientCompatibilityRepairRuntime(), response, language, accept, cancel, current);
    }

    internal ClientWorldEntryForm CreateServerEntryForm() => worldEntryStart.CreateForm();
    internal ClientWorldEntryForm CreateAccountRegistrationForm(string server, string user) => worldEntryStart.CreateRegistrationForm(server, user);

    internal MultiplayerAccountPage CreateAccountPage() => new(new ClientAccountPanel(sessionLifecycle, passwordChange));
    internal MultiplayerDiagnosticsPage CreateDiagnosticsPage() => new(diagnostics);

    internal bool ManualSyncInProgress => synchronization.ManualInProgress;

    internal bool IsServerWorldSetupFlow => worldSetup.Pending;

    internal bool LocalAtomicMutationPending => localMutations.IsPending;

    internal string LocalAtomicMutationOperation => localMutations.Current is { } mutation
        ? ClientLocalMutationPresentation.OperationName(mutation.Kind) : string.Empty;

    internal bool MercenaryInProgress => mercenaryState.Busy;

    internal bool ServerShopInProgress => shopState.Busy;

    internal bool PresenceInProgress => sessionConnection.IsRunning;

    internal bool ChatInProgress => chat.Busy;

    internal bool IsAdministrator => adminAccess.IsAdministrator;

    internal bool CanUseDeveloperTools => Current.ProgramState != ProgramState.Playing
        || !settings.IsConfigured
        || adminAccess.IsAdministrator;

    internal bool CanEditModSettings => !settings.IsConfigured
        || adminAccess.IsAdministrator;

    internal string ServerCompatibilityManifestJson => compatibilityBaseline.Manifest;

    internal bool AdminInProgress => adminAccess.Busy;

    internal bool IsAnyManualOrSnapshotSyncInProgress() => synchronization.IsBusy;

    internal string CurrentUserId => settings.UserId;

    internal bool ShouldShowMultiplayerMainButton => sessionLifecycle.HasConfiguredSession;




    internal bool CanSyncRuntimeWorldObjects => worldSynchronization.CanStart(ClientWorldSynchronizationKind.RuntimeObjects);

    internal bool CanSyncWorldConfigurationExtensions => colonyRegistration.CanStart(ClientColonyRegistrationKind.Extensions);

    internal bool CanRefreshCaravanArrivalTargets => worldSynchronization.CanStart(ClientWorldSynchronizationKind.CaravanTargets);

    internal string LoginStatus => ClientDiagnosticsText.Login(diagnostics.LoginStatus);

    internal string EventQueueStatus => eventState.QueueStatus;

    internal string EventDetailsStatus => eventState.DetailsStatus;

    internal string GiftProcessingStatus => eventState.ActionStatus;

    internal string SnapshotUploadStatus => ClientDiagnosticsText.SnapshotUpload(diagnostics.SnapshotUploadStatus);

    internal string PresenceStatus => ClientDiagnosticsText.Presence(diagnostics.PresenceStatus);

    internal string PlayerListStatus => playerDirectory.Message;

    internal string TradeStatus => tradeState.Message;

    internal string WorldMapStatus => ClientDiagnosticsText.WorldMap(diagnostics.WorldMapStatus);

    internal string ChatStatus => chat.Status;
    internal IReadOnlyList<ModChatMessageDto> ChatMessagesSnapshot => chat.CopyMessages();
    internal int UnreadPrivateChatCount => chat.UnreadPrivate;

    internal string BankStatus => bankState.Message;

    internal bool BankInProgress => bankState.Busy;

    internal ModBankStatusResponseDto? BankStatusSnapshot => bankState.View;

    internal string AdminStatus => administration.Message;
    internal ModAdminStatusResponseDto? AdminStatusSnapshot => administration.Snapshot;

    internal string MercenaryStatus => mercenaryState.Message;

    internal string ServerShopStatus => shopState.Message;

    internal string UserId => settings.UserId;

    internal string ServerBaseUrl => settings.ServerBaseUrl;

    internal string ColonyId => settings.ColonyId;

    internal string CurrentSnapshotId => settings.CurrentSnapshotId;

    internal bool IsNetworkConfigured => settings.IsConfigured;

    internal ClashOfRimClientNetworkContext CreateNetworkContext()
    {
        return ClashOfRimClientNetworkContext.FromSettings(settings);
    }

    internal bool TradeMarketplaceEnabled => tradeState.Enabled;

    internal bool TradeOrdersHasMore => tradeState.HasMore;

    internal bool TradeOrdersPageLoadInProgress => tradeState.Loading;

    internal int TradeOrdersTotalCount => tradeState.Total;

    internal int TradeOrdersLoadedCount => tradeState.Count;

    internal string TargetUserId => playerDirectory.Selected.UserId;

    internal string TargetColonyId => playerDirectory.Selected.ColonyId;

    internal List<ModTradeOrderSummaryDto> TradeOrdersSnapshot => tradeState.CopyOrders();

    internal int TradeOrdersSnapshotVersion => tradeState.Version;

    internal List<ModServerShopListingDto> ServerShopListingsSnapshot => shopState.CopyListings();

    internal int ServerShopListingsSnapshotVersion => shopState.Version;

    internal List<ModPlayerSummaryDto> PlayersSnapshot => playerDirectory.CopyPlayers();

    internal int PlayersSnapshotVersion => playerDirectory.Version;

    internal List<ModAchievementLeaderboardDto> AchievementLeaderboardsSnapshot => achievementQueries.CopyLeaderboards();
    internal List<ModAchievementSummaryDto> OwnAchievementsSnapshot => achievementQueries.CopyAchievements();
    internal int AchievementLeaderboardsSnapshotVersion => achievementQueries.Version;
    internal string AchievementTargetUserId => achievementQueries.Target.UserId;
    internal string AchievementTargetColonyId => achievementQueries.Target.ColonyId;
    internal string AchievementStatus => achievementQueries.Message;
    internal bool ConfirmEndgameOrDefer(string key, Action continuation) => endgameAchievements.ConfirmOrDefer(key, continuation);
    internal void StartEndgameAchievementSnapshotConfirmation(IReadOnlyList<ModSnapshotAchievementCandidateDto> candidates)
        => _ = endgameAchievements.SubmitAsync(candidates);
    internal void StartRefreshAchievements(string? targetUserId = null, string? targetColonyId = null)
        => _ = achievementQueries.RefreshAsync(targetUserId, targetColonyId);

    internal bool GiftsEnabled => worldMarkers.GiftsEnabled;
    internal bool PvpEnabled => worldMarkers.PvpEnabled;
    internal List<ModWorldMapMarkerDto> WorldMapMarkersSnapshot => worldMarkers.Snapshot;

}
