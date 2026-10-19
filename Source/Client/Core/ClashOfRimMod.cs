using AIRsLight.ClashOfRim.Account;
using AIRsLight.ClashOfRim.Achievements;
using AIRsLight.ClashOfRim.Admin;
using AIRsLight.ClashOfRim.Bank;
using AIRsLight.ClashOfRim.Chat;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.ClientSnapshots;
using AIRsLight.ClashOfRim.CompatibilityClient;
using AIRsLight.ClashOfRim.Diplomacy;
using AIRsLight.ClashOfRim.DlcCompatibility;
using AIRsLight.ClashOfRim.EventLetters;
using AIRsLight.ClashOfRim.Gifts;
using AIRsLight.ClashOfRim.MainMenu;
using AIRsLight.ClashOfRim.Mercenaries;
using AIRsLight.ClashOfRim.Modules.Accounts;
using AIRsLight.ClashOfRim.Modules.Achievements;
using AIRsLight.ClashOfRim.Modules.Administration;
using AIRsLight.ClashOfRim.Modules.Bank;
using AIRsLight.ClashOfRim.Modules.Chat;
using AIRsLight.ClashOfRim.Modules.Compatibility;
using AIRsLight.ClashOfRim.Modules.Diagnostics;
using AIRsLight.ClashOfRim.Modules.Diplomacy;
using AIRsLight.ClashOfRim.Modules.Events;
using AIRsLight.ClashOfRim.Modules.Gifts;
using AIRsLight.ClashOfRim.Modules.Maps;
using AIRsLight.ClashOfRim.Modules.Mercenaries;
using AIRsLight.ClashOfRim.Modules.Multiplayer;
using AIRsLight.ClashOfRim.Modules.Presence;
using AIRsLight.ClashOfRim.Modules.Raids;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Shop;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using AIRsLight.ClashOfRim.Modules.Support;
using AIRsLight.ClashOfRim.Modules.Synchronization;
using AIRsLight.ClashOfRim.Modules.Trades;
using AIRsLight.ClashOfRim.Modules.Transport;
using AIRsLight.ClashOfRim.Modules.World;
using AIRsLight.ClashOfRim.Multiplayer.Achievements;
using AIRsLight.ClashOfRim.Multiplayer.Diagnostics;
using AIRsLight.ClashOfRim.Multiplayer.Pages;
using AIRsLight.ClashOfRim.Multiplayer.Players;
using AIRsLight.ClashOfRim.Multiplayer;
using AIRsLight.ClashOfRim.Pawns;
using AIRsLight.ClashOfRim.Raids;
using AIRsLight.ClashOfRim.RemoteMaps;
using AIRsLight.ClashOfRim.ServerShop;
using AIRsLight.ClashOfRim.Support;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility;
using AIRsLight.ClashOfRim.Trades;
using AIRsLight.ClashOfRim.WorldObjects;
using HarmonyLib;
using RimWorld.Planet;
using RimWorld;
using System.Collections.Generic;
using System.Linq;
using System;
using Verse;

namespace AIRsLight.ClashOfRim;

public sealed class ClashOfRimMod : Mod
{
    public const string PackageId = "AIRsLight.ClashOfRim";

    private readonly ClashOfRimSettings settings;
    private readonly MultiplayerPageFactory multiplayerPages;
    private readonly ClientEventInbox eventInbox = new();
    private readonly ClientEventRefresh eventRefresh;
    private readonly ClientEventState eventState;
    private readonly ClientBankState bankState;
    private readonly ClientBankOperations bankOperations;
    private readonly ClientMercenaryState mercenaryState;
    private readonly ClientMercenaryHiring mercenaryHiring;

    private readonly ClientTradeState tradeState;
    private readonly ClientTradeMarket tradeMarket;
    private readonly ClientTradeActions tradeActions;
    private readonly ClientShopState shopState;
    private readonly ClientShopCatalog shopCatalog;
    private readonly ClientShopOperations shopOperations;

    internal ClientTradeActions TradeActions => tradeActions;
    private readonly ClientGiftActions giftActions;
    private readonly ClientSupportActions supportActions;
    private readonly ClientObservationMaps observationMaps;
    private readonly ClientRaidCommands raidCommands;
    private readonly ClientRaidSettlements raidSettlements;
    private readonly ClientSnapshotReload snapshotReload;
    private readonly ClientSnapshotSaving snapshotSaving;
    internal ClientGiftActions GiftActions => giftActions;
    private readonly ClientEventApplications eventApplications;
    internal IReadOnlyList<ModEventDetailDto>? ReadEventDetails(int? knownVersion, out int version) =>
        eventInbox.CopyDetails(knownVersion, out version);
    private readonly ClientSnapshotPreparationAdapter snapshotPreparation = ClientSnapshotComposition.CreatePreparation();
    private readonly ClientSynchronizationCoordinator synchronization = ClientSnapshotComposition.Synchronization;
    private readonly ClientColonyAppearance colonyAppearance;
    private readonly ClientColonyAbandonment colonyAbandonment;
    private readonly ClientAdminAccess adminAccess;
    private readonly ClientAdministration administration;
    private readonly ClientDiplomacyActions diplomacyActions;
    private readonly ClientPlayerDirectory playerDirectory;
    private readonly ClientCompatibilityOverride compatibilityOverride;
    private readonly ClientLocalMutationCoordinator localMutations = new();
    private readonly ClientSessionLifecycle sessionLifecycle;
    private readonly ClientChat chat;
    private readonly ClientPasswordChange passwordChange;
    private readonly ClientRuntimeDiagnostics diagnostics;
    private readonly ClientSessionConnection sessionConnection = new(new ClientSessionWebSocketTransport(new ClientWebSocketFactory(), new ClientProtocolMetadata()));
    private readonly ClientAchievementQueries achievementQueries;
    private readonly ClientWorldEntryStart worldEntryStart;
    private readonly ClientWorldSourceGame sourceGame = new(new ClientWorldSourceGameRuntime());
    private readonly ClientCompatibilityPresentation compatibilityPresentation;
    private readonly ClientWorldSetupState worldSetup;
    private readonly ClientWorldMarkers worldMarkers;
    private readonly ClientSessionStartup sessionStartup;
    private readonly ClientWorldSynchronization worldSynchronization;

    public ClashOfRimMod(ModContentPack content)
        : base(content)
    {
        settings = GetSettings<ClashOfRimSettings>();
        var snapshotPublication = ClientSnapshotComposition.CreatePublication(settings);
        var sessionIdentity = new ClientSessionIdentityStore(settings);
        sessionLifecycle = new ClientSessionLifecycle(sessionIdentity, new ClientSessionLogoutTransport(), sessionConnection);
        var eventReader = new ClientEventReader(sessionLifecycle, eventInbox, new ClientEventReadTransport(), new ClientSnapshotGame());
        var eventReadPresentation = new ClientEventReadPresentation();
        eventState = new ClientEventState(eventReader, eventReadPresentation);
        diagnostics = new ClientRuntimeDiagnostics(sessionLifecycle, eventState);
        adminAccess = new ClientAdminAccess(sessionLifecycle);
        var adminBaselineCapture = new ClientAdminBaselineCapture(sessionLifecycle, new AIRsLight.ClashOfRim.Admin.ClientAdminBaselineRuntime(), ClientMainThreadDispatcher.EnqueueSession);
        var localManifest = new ClientLocalCompatibilityManifest();
        var compatibilityBaseline = new ClientCompatibilityBaseline(new ClientCompatibilityBaselineRuntime());
        var compatibilityEnvironment = new ClientProtocolEnvironment();
        compatibilityOverride = new ClientCompatibilityOverride(adminAccess, localManifest, compatibilityBaseline, adminBaselineCapture,
            new AIRsLight.ClashOfRim.Admin.ClientAdminBaselineTransport(compatibilityEnvironment, TimeSpan.FromSeconds(15)),
            new ClientCompatibilityOverrideTransport(compatibilityEnvironment), new ClientCompatibilityOverridePresentation(), ClientMainThreadDispatcher.EnqueueSession);
        administration = new ClientAdministration(adminAccess,
            new AIRsLight.ClashOfRim.Admin.ClientAdminTransport(new ClientProtocolEnvironment()),
            new AIRsLight.ClashOfRim.Admin.ClientAdminPresentation());
        var serverMetadata = new ClientServerMetadata(sessionLifecycle, compatibilityBaseline, administration);
        worldSetup = new ClientWorldSetupState(sessionLifecycle);
        var storytellerConfiguration = new ClientStorytellerConfiguration(sessionLifecycle, worldSetup, new ClientStorytellerRuntime());
        compatibilityPresentation = new ClientCompatibilityPresentation(CreateCompatibilityMismatch);
        var entryPresentation = new ClientWorldEntryPresentation(message => diagnostics.LoginStatus = message,
            (server, account) => Find.WindowStack.Add(new AIRsLight.ClashOfRim.MainMenu.ClashOfRimAccountRegistrationDialog(CreateAccountRegistrationForm(server, account))), compatibilityPresentation);
        var worldTransport = new ClientWorldPreparationTransport(new ClientProtocolEnvironment());
        var worldPreparation = new ClientWorldPreparation(sessionLifecycle, new ClientSnapshotGame(), worldTransport, entryPresentation);
        var worldBaselineSubmission = new ClientWorldBaselineSubmission(sessionLifecycle, new ClientWorldBaselineRuntime(), worldTransport);
        var worldBaselineCapture = new ClientWorldBaselineCapture(worldBaselineSubmission, storytellerConfiguration);
        var sessionReconnect = new ClientSessionReconnect(new ClientSessionReconnectTransport(new ClientProtocolEnvironment()), sessionLifecycle, synchronization);
        passwordChange = new ClientPasswordChange(sessionLifecycle,
            new ClientPasswordTransport(new ClientProtocolEnvironment()),
            new ClientPasswordStore(settings), new ClientPasswordPresentation(), ClientMainThreadDispatcher.EnqueueSession);
        var eventConfirmation = new ClientEventConfirmation(sessionLifecycle, new ClientSnapshotGame(),
            snapshotPublication, eventInbox, new ClientEventConfirmationTransport());
        var eventConfirmationPreparation = new ClientEventConfirmationPreparation(sessionLifecycle, synchronization, localMutations, eventConfirmation);
        var snapshotFailureRecovery = new ClientSnapshotFailureRecovery(sessionLifecycle, localMutations, synchronization,
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
        var localSnapshotConfirmation = new ClientLocalSnapshotConfirmation(sessionLifecycle, new ClientSnapshotGame(), mutationAdmission,
            synchronization, localMutations, snapshotTransactions, snapshotFailureRecovery, ClientSnapshotComposition.Create(settings),
            new ClientLocalSnapshotPresentation(message => diagnostics.SnapshotUploadStatus = message));
        localSnapshotConfirmation.Completed += StartQueuedAutomaticEventRefreshAfterLocalAtomicMutation;
        var endgameAchievements = new ClientEndgameAchievements(sessionLifecycle, new ClientSnapshotGame(),
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
        var mercenaryQuotes = new ClientMercenaryQuotes(sessionLifecycle, new ClientSnapshotGame(), bankState,
            mercenaryTransport, mercenaryPresentation, () => Find.TickManager?.TicksGame ?? 0);
        mercenaryHiring = new ClientMercenaryHiring(mercenaryState, bankState, mutationAdmission, synchronization, localMutations,
            snapshotTransactions, snapshotFailureRecovery, new ClientMercenaryRuntime(), mercenaryTransport, mercenaryPresentation);
        mercenaryHiring.Completed += StartQueuedAutomaticEventRefreshAfterLocalAtomicMutation;
        var mercenaryConfirmation = new ClientMercenaryConfirmation(localSnapshotConfirmation, mercenaryState, mercenaryPresentation);
        var mercenaryIncidents = new ClientMercenaryIncidents(sessionLifecycle, mercenaryState, bankState, synchronization, localMutations,
            mutationAdmission, snapshotTransactions, snapshotFailureRecovery, mercenaryConfirmation, mercenaryTransport,
            new ClientMercenaryIncidentRuntime(), mercenaryPresentation);
        mercenaryIncidents.BankRefreshRequested += StartRefreshBankStatus;
        mercenaryIncidents.Completed += StartQueuedAutomaticEventRefreshAfterLocalAtomicMutation;
        mercenaryConfirmation.ContractCompleted += (id, key) => _ = mercenaryIncidents.ReportAsync(id, "Completed", key);
        mercenaryConfirmation.BankRefreshRequested += StartRefreshBankStatus;
        Func<ClientGiftCapabilities> giftCapabilities = () => new(worldMarkers.GiftsEnabled, worldMarkers.PvpEnabled);
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
        var supportReturns = new ClientSupportReturns(sessionLifecycle, new ClientSnapshotGame(), mutationAdmission, localMutations,
            snapshotTransactions, snapshotFailureRecovery, localSnapshotConfirmation, new ClientSupportReturnRuntime(supportPresentation),
            new ClientSupportTransport(), supportPresentation);
        supportReturns.Completed += StartQueuedAutomaticEventRefreshAfterLocalAtomicMutation;
        var raidTransport = new ClientRaidTransport();
        var raidPresentation = new ClientRaidLaunchPresentation(message => diagnostics.WorldMapStatus = message);
        var raidLaunching = new ClientRaidLaunching(sessionLifecycle, mutationAdmission, synchronization, localMutations,
            snapshotTransactions, snapshotFailureRecovery, new ClientRaidPreparation(raidTransport, new ClientRemoteSnapshotTransport()),
            raidTransport, raidPresentation, () => worldMarkers.PvpEnabled);
        raidCommands = new ClientRaidCommands(sessionLifecycle, raidLaunching, raidPresentation);
        raidLaunching.Completed += StartQueuedAutomaticEventRefreshAfterLocalAtomicMutation;
        raidSettlements = new ClientRaidSettlements(sessionLifecycle, mutationAdmission, synchronization, localMutations, snapshotTransactions,
            snapshotFailureRecovery, new ClientRaidSupportLosses(snapshotTransactions, new ClientSupportTransport()),
            new ClientEventConfirmationTransport(), eventInbox, localSnapshotConfirmation, new ClientRaidSettlementPresentation(message => diagnostics.WorldMapStatus = message));
        raidSettlements.Completed += () => _ = playerDirectory.RefreshAsync(manual: false);
        var raidFinishActions = new ClientRaidFinishActions(raidSettlements);
        raidSettlements.Expired += StartRestoreAfterRaid;
        var raidRecovery = new ClientRaidRecovery(sessionLifecycle, new ClientSnapshotGame(), snapshotFailureRecovery, raidSettlements,
            new ClientRaidRecoveryPresentation(message => diagnostics.WorldMapStatus = message));
        raidRecovery.ReloadRequested += StartRestoreAfterRaid;
        snapshotReload = new ClientSnapshotReload(sessionLifecycle, synchronization, localMutations, snapshotFailureRecovery,
            new ClientSessionAuthenticationTransport(), new ClientColonySnapshotDownload(new ClientRemoteSnapshotTransport()),
            snapshotPublication, new ClientSnapshotReloadRuntime(), new ClientSnapshotReloadPresentation(
                (message, purpose) => { diagnostics.LoginStatus = message; if (purpose == ClientSnapshotReloadPurpose.Raid) diagnostics.WorldMapStatus = message; },
                response => ShowCompatibilityMismatchWindow(response)));
        var entryStore = new ClientWorldEntryStore(settings);
        var featureCatalogs = new ClientWorldFeatureCatalogs(sessionLifecycle, worldTransport);
        featureCatalogs.Diagnostic += message => Log.Warning(message);
        var configurationRuntime = new ClientWorldConfigurationRuntime();
        var worldConfiguration = new ClientWorldConfiguration(sessionLifecycle, configurationRuntime, entryStore, storytellerConfiguration, featureCatalogs);
        var occupiedColonySites = new ClientOccupiedColonySites(sessionLifecycle, entryStore, new ClientOccupiedColonySitesRuntime());
        worldMarkers = new ClientWorldMarkers(sessionLifecycle, entryStore, occupiedColonySites, new ClientWorldMarkersRuntime(),
            new ClientWorldMarkersPresentation(message => diagnostics.WorldMapStatus = message));
        var worldLoginPublication = new ClientWorldLoginPublication(sessionLifecycle, worldMarkers);
        worldSynchronization = new ClientWorldSynchronization(sessionLifecycle, entryStore, localMutations, worldMarkers,
            new ClientWorldSynchronizationRuntime(), new ClientWorldMarkerTransport(new ClientProtocolEnvironment()),
            new ClientWorldWritePresentation(message => diagnostics.WorldMapStatus = message), tradeMarket.CaptureRefresh);
        colonyAppearance = new ClientColonyAppearance(sessionLifecycle, new ClientColonyAppearanceStore(settings), new ClientColonyAppearancePresentation());
        var worldPublication = new ClientWorldPublication(entryStore, worldConfiguration, occupiedColonySites, colonyAppearance, worldMarkers);
        var colonyTransport = new ClientColonyTransport(new ClientProtocolEnvironment());
        var colonyRegistration = new ClientColonyRegistration(sessionLifecycle, entryStore, localMutations, new ClientColonyRegistrationRuntime(), colonyTransport, worldPublication);
        snapshotSaving = new ClientSnapshotSaving(sessionLifecycle, synchronization, localMutations, ClientSnapshotComposition.Create(settings),
            new ClientSnapshotGame(), entryStore, configurationRuntime, colonyRegistration, colonyAppearance,
            new ClientSnapshotSavingPresentation(message => diagnostics.SnapshotUploadStatus = message));
        colonyAbandonment = new ClientColonyAbandonment(sessionLifecycle, entryStore, new ClientWorldCatalogRuntime(), colonyTransport,
            new ClientColonyAbandonPresentation(message => diagnostics.WorldMapStatus = message));
        var colonyRelocation = new ClientColonyRelocation(sessionLifecycle, entryStore, new ClientWorldCatalogRuntime(), mutationAdmission,
            synchronization, localMutations, snapshotTransactions, ClientSnapshotComposition.Create(settings), snapshotFailureRecovery,
            colonyRegistration, worldPublication, colonyTransport, new ClientColonyRelocationPresentation());
        colonyRelocation.Completed += StartQueuedAutomaticEventRefreshAfterLocalAtomicMutation;
        snapshotFailureRecovery.ReturningToMenu += () => colonyRegistration.Suppressed = false;
        colonyAppearance.RegistrationRequested += (appearance, reason) => _ = colonyRegistration.RequestAsync(
            ClientColonyRegistrationKind.Sites, reason, appearance, suppressNotification: true);
        colonyRegistration.Diagnostic += (message, warning) => { if (warning) Log.Warning(message); else ClashLog.Message(message); };
        var worldCatalog = new ClientWorldCatalog(sessionLifecycle, new ClientWorldCatalogRuntime(), worldTransport,
            new ClientWorldCatalogPresentation(message => diagnostics.WorldMapStatus = message), worldPublication, serverMetadata);
        var worldWritePresentation = new ClientWorldWritePresentation(message => administration.Message = message);
        var worldWrites = new ClientWorldWrites(sessionLifecycle, adminAccess, worldBaselineCapture, worldBaselineSubmission,
            storytellerConfiguration, worldPublication, worldWritePresentation, worldWritePresentation);
        var worldEntry = new ClientWorldEntry(sessionLifecycle, new ClientSnapshotGame(), worldSetup,
            entryStore, worldTransport, entryPresentation, snapshotReload, worldPublication, serverMetadata);
        worldEntryStart = new ClientWorldEntryStart(sessionLifecycle, synchronization, new ClientSnapshotGame(),
            entryStore, entryPresentation, worldPreparation, worldEntry);
        observationMaps = new ClientObservationMaps(sessionLifecycle, synchronization, new ClientSnapshotGame(),
            new ClientObservationTransport(), new ClientObservationRuntime(),
            new ClientObservationPresentation(message => diagnostics.WorldMapStatus = message, response => ShowCompatibilityMismatchWindow(response)));
        var eventConfirmations = new ClientEventConfirmationWorkflow(sessionLifecycle, eventConfirmationPreparation, eventConfirmation,
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
        var sessionNotifications = new ClientSessionNotifications(sessionLifecycle, sessionConnection, eventState, notificationEffects,
            new ClientSessionNotificationPresentation(message => diagnostics.PresenceStatus = message));
        var sessionRecovery = new ClientSessionRecovery(sessionLifecycle, sessionConnection, sessionReconnect, localMutations, worldLoginPublication,
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
        sessionStartup = new ClientSessionStartup(sessionLifecycle, synchronization, localMutations, sourceGame, worldLoginPublication,
            new ClientSessionLogin(sessionLifecycle, new ClientSessionLoginTransport(new ClientProtocolEnvironment())),
            ClientSnapshotComposition.Create(settings), new ClientSessionStartupRuntime(sessionConnection),
            new ClientSessionStartupPresentation(message => diagnostics.LoginStatus = message, message => diagnostics.SnapshotUploadStatus = message, response => ShowCompatibilityMismatchWindow(response)), sessionFollowups);
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
        multiplayerPages = new(sessionLifecycle, synchronization, playerDirectory, diplomacyActions,
            worldMarkers, worldSynchronization, achievementQueries, tradeState, tradeMarket, tradeActions,
            shopState, shopCatalog, shopOperations, bankState, bankOperations, mercenaryState, mercenaryQuotes, mercenaryHiring,
            adminAccess, administration, compatibilityOverride, worldWrites, colonyAppearance, passwordChange, diagnostics);
        if (!string.Equals(content.PackageId, PackageId, StringComparison.OrdinalIgnoreCase))
        {
            Log.Warning("[ClashOfRim] Ignored ClashOfRim mod class loaded from unexpected package: " + (content.PackageId ?? "<null>") + ".");
            return;
        }

        // Publish engine bindings only after package admission and complete domain construction.
        DeveloperToolAccessPolicy.Attach(adminAccess);
        ClashOfRimCompatibilityApi.AttachSession(sessionLifecycle);
        ClashOfRimCompatibilityApi.AttachAdministration(adminAccess);
        ModSettingsBaselinePolicy.Attach(adminAccess, compatibilityBaseline);
        CompatibilityConfigOverlayPath.Attach(sessionLifecycle, compatibilityBaseline);
        AIRsLight.ClashOfRim.DlcCompatibility.ClientSharedIdeologyWorld.Attach(new(worldSetup, sessionLifecycle));
        ClientStorytellerPage.Attach(storytellerConfiguration);
        ClientMercenaryQuestCallbacks.Attach(new(new ClientMercenaryQuestLifecycle(sessionLifecycle, new ClientSnapshotGame(), mercenaryIncidents, mercenaryConfirmation)));
        ClientSupportQuestCallbacks.Attach(new(new ClientSupportQuestLifecycle(sessionLifecycle, new ClientSnapshotGame(), supportReturns)));
        GiftCaravanGizmoPatches.Attach(sessionLifecycle, giftActions);
        TradeCaravanGizmoPatches.Attach(sessionLifecycle, tradeActions);
        SupportCaravanGizmoPatches.Attach(sessionLifecycle, supportActions, worldMarkers);
        RaidCaravanGizmoPatches.Attach(sessionLifecycle, raidCommands, worldMarkers);
        ClientTransporterActions.Attach(new ClientTransporterActions(giftActions, raidCommands, giftCapabilities));
        ClientRaidFinishActions.Attach(raidFinishActions);
        ClientRaidBattleOverlay.Attach(new(raidFinishActions, () => diagnostics.WorldMapStatus));
        ClientRaidLifecycleCallbacks.Attach(new(new ClientRaidLifecycle(new ClientSnapshotGame(), snapshotReload, raidRecovery, synchronization, raidSettlements)));
        ClientWorldSiteReservations.Attach(new ClientWorldSiteReservations(occupiedColonySites));
        ClientWorldSynchronizationCallbacks.Attach(new(worldSynchronization));
        ClientColonySiteEvents.Attach(new ClientColonySiteEvents(colonyRegistration, colonyAppearance));
        ClientSnapshotSavingCallbacks.Attach(new(snapshotSaving));
        ClientSessionExitCallbacks.Attach(new(sessionLifecycle));
        ClientColonyGizmos.Attach(new ClientColonyGizmos(colonyAppearance, colonyAbandonment));
        ClientColonyRelocationCommands.Attach(new ClientColonyRelocationCommands(colonyRelocation));
        AIRsLight.ClashOfRim.DlcCompatibility.ClientIdeologyReforms.Attach(new(colonyRegistration));
        ClashOfRimCompatibilityApi.AttachCatalog(worldCatalog);
        ClientStorytellerEditor.Attach(new ClientStorytellerEditor(worldWrites, colonyAppearance));
        ClientWorldGenerationPage.Attach(new ClientWorldGeneration(sessionLifecycle, worldSetup, worldConfiguration, new ClientWorldGenerationRuntime(), new ClientWorldGenerationPresentation(message => diagnostics.LoginStatus = message)));
        ClientServerEntryWindows.Attach(new(worldEntryStart));
        RemoteWorldObjectUiUtility.AttachCommands(new ClientWorldObjectCommands(sessionLifecycle, new ClientSnapshotGame(),
            observationMaps, new ClientWorldObjectCommandRuntime()));
        ClashOfRimEventChoiceLetter.Attach(eventApplications);
        ClientWorldSetupPages.Attach(new ClientWorldSetupPages(new ClientWorldSetupLifecycle(sessionLifecycle, new ClientWorldCatalogRuntime(),
            sourceGame, initialWorldSetup, colonyAppearance, worldSynchronization, worldMarkers, initialWorldPresentation)));
        ClientAutomaticSessionStartup.Attach(new(sessionStartup));
        ClientChatOverlay.Attach(new(new ClientChatOverlayModel(chat, sessionLifecycle, new ClientSnapshotGame(), () => sessionStartup.IsInActiveWorld)));

        EndgameAchievementRuntime.Attach(endgameAchievements);
        BiotechCompatibilityPackage.Attach(sessionLifecycle);
        PawnExchangeScribeRestorer.Attach(sessionLifecycle);
        MainButtonWorker_ClashOfRimMultiplayer.Attach(sessionLifecycle);
        ClashOfRimMultiplayerWindow.Attach(multiplayerPages);
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

    // Legacy UI constructors and the explicit RimTest contract forward to the
    // same domain instances. These entries own no independent business state.
    internal bool SnapshotUploadInProgress => synchronization.SnapshotInProgress;
    // Existing standalone windows retain these constructor compatibility entries.
    internal MultiplayerTradePage CreateTradePage() => multiplayerPages.CreateTradePage();
    internal ClientAdminShopPanel.Edit CreateAdminShopEdit(ModServerShopListingDto? listing) => multiplayerPages.CreateAdminShopEdit(listing);

    private ClientCompatibilityMismatch CreateCompatibilityMismatch(ModLoginResponseDto response, string? language,
        Action? accept, Action? cancel, Func<bool>? current)
    {
        var local = new ClientLocalCompatibilityManifest();
        return new(sessionLifecycle, new ClientSnapshotGame(), adminAccess, compatibilityOverride, local, local,
            new ClientCompatibilityOverridePresentation(), new ClientCompatibilityRepairRuntime(), response, language, accept, cancel, current);
    }

    internal ClientWorldEntryForm CreateServerEntryForm() => worldEntryStart.CreateForm();
    internal ClientWorldEntryForm CreateAccountRegistrationForm(string server, string user) => worldEntryStart.CreateRegistrationForm(server, user);

    internal bool ManualSyncInProgress => synchronization.ManualInProgress;

    // Retained by the RimTest state protocol; the domain modules own both states.
    internal bool IsInActiveMultiplayerSession => sessionStartup.IsInActiveWorld;
    internal bool IsServerWorldSetupFlow => worldSetup.Pending;

    internal bool LocalAtomicMutationPending => localMutations.IsPending;

    internal string LocalAtomicMutationOperation => localMutations.Current is { } mutation
        ? ClientLocalMutationPresentation.OperationName(mutation.Kind) : string.Empty;

    internal bool MercenaryInProgress => mercenaryState.Busy;

    internal bool ServerShopInProgress => shopState.Busy;

    internal bool PresenceInProgress => sessionConnection.IsRunning;

    internal bool ChatInProgress => chat.Busy;

    internal bool IsAdministrator => adminAccess.IsAdministrator;

    internal bool AdminInProgress => adminAccess.Busy;

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

    internal bool TradeOrdersPageLoadInProgress => tradeState.Loading;

    internal List<ModTradeOrderSummaryDto> TradeOrdersSnapshot => tradeState.CopyOrders();

    internal List<ModServerShopListingDto> ServerShopListingsSnapshot => shopState.CopyListings();

    internal List<ModPlayerSummaryDto> PlayersSnapshot => playerDirectory.CopyPlayers();

    internal List<ModAchievementLeaderboardDto> AchievementLeaderboardsSnapshot => achievementQueries.CopyLeaderboards();
    internal int AchievementLeaderboardsSnapshotVersion => achievementQueries.Version;
    internal List<ModAchievementSummaryDto> OwnAchievementsSnapshot => achievementQueries.CopyAchievements();

    internal string AchievementStatus => achievementQueries.Message;
    internal void StartRefreshAchievements(string? targetUserId = null, string? targetColonyId = null)
        => _ = achievementQueries.RefreshAsync(targetUserId, targetColonyId);

    internal List<ModWorldMapMarkerDto> WorldMapMarkersSnapshot => worldMarkers.Snapshot;
    internal string WorldConfigurationId => settings.CurrentWorldConfigurationId;

    internal bool AccountPasswordInProgress => passwordChange.Busy;
    internal string AccountPasswordStatus => passwordChange.Message;
    internal void StartChangeOfflinePassword(string currentPassword, string newPassword)
        => _ = passwordChange.StartAsync(currentPassword, newPassword);
    internal void StartRefreshAdminStatus() => _ = administration.RefreshAsync();
    internal void StartUpdateAdminConfiguration(ModAdminConfigurationDto configuration) => _ = administration.SaveAsync(configuration);
    internal void StartAdminAction(string actionKind, string? targetUserId = null, string? targetColonyId = null,
        string? message = null, string? notificationSeverity = null, bool persistentNotification = true)
        => _ = administration.ActAsync(new(actionKind, targetUserId, targetColonyId, message, notificationSeverity, persistentNotification));
    internal void StartRefreshBankStatus() => _ = bankOperations.RefreshAsync();
    internal void StartCreateBankLoan(int principalSilver, int durationDays) => _ = bankOperations.BorrowAsync(principalSilver, durationDays);
    internal void StartRepayBankLoan() => _ = bankOperations.RepayLoanAsync();
    internal void StartRepayBankDebt(ModBankDebtSummaryDto debt) => _ = bankOperations.RepayDebtAsync(debt?.DebtId);

    internal void StartHireMercenary(string skill, int level, int days) => _ = mercenaryHiring.StartAsync(new(skill, level, days));
    internal void StartHireMercenaryGuard(string tier) => _ = mercenaryHiring.StartAsync(new("", 0, 0, tier));

    internal void StartRefreshEventsForTest() => _ = eventRefresh.StartManual(ClientEventReadKind.Refresh);

    internal void DisconnectMultiplayerSessionForExit() => _ = sessionLifecycle.End(sendLogout: true);

    internal void StartRefreshChatMessages(bool initialLoad = false) => _ = chat.RefreshAsync(initialLoad);
    internal void StartSendChatMessage(string rawText) => _ = chat.SendAsync(rawText);
    internal void MarkPrivateChatRead() => chat.MarkPrivateRead();

    internal void StartSyncWorldMapMarkers() => RequestWorldMapMarkerRefresh("manual-refresh-world-map");

    internal void RequestWorldMapMarkerRefresh(string reason)
        => _ = worldSynchronization.RequestAsync(ClientWorldSynchronizationKind.Markers, reason);

    internal void OpenAbandonPlayerColonyConfirmation(MapParent colony)
        => colonyAbandonment.Open(new ClientColonyAbandonTarget(colony));
    private void StartAutomaticEventRefresh(string reason) => _ = eventRefresh.StartAutomatic(reason);
    private void StartQueuedAutomaticEventRefreshAfterLocalAtomicMutation() => _ = eventRefresh.ResumeDeferred();

    internal void StartManualSnapshotUpload() => _ = snapshotSaving.StartAsync(ClientSnapshotSaveKind.Manual);
    private void ShowCompatibilityMismatchWindow(
        ModLoginResponseDto? response,
        Action? continueAnyway = null,
        Action? cancelContinuation = null,
        string? authoritativeServerGameLanguage = null)
        => compatibilityPresentation.Show(response, worldSetup.LanguageAccepted, authoritativeServerGameLanguage, continueAnyway, cancelContinuation);

    internal void StartRefreshPlayers() => _ = playerDirectory.RefreshAsync(manual: true);

    internal void SelectPlayerTarget(ModPlayerSummaryDto player) => diplomacyActions.Select(player);

    internal void StartCreateDiplomacyEvent(string kind, ModPlayerSummaryDto target, string? customMessage = null) =>
        diplomacyActions.Create(kind, target, customMessage, out _);

    internal void StartCreateDiplomacyEvent(string kind, string? customMessage = null, string? targetDisplayName = null) =>
        diplomacyActions.Create(kind, customMessage, targetDisplayName, out _);
    internal bool HandleEventLetterAction(string eventId, ClashOfRimEventLetterActionKind actionKind)
        => eventApplications.HandleLetterAction(eventId, actionKind);
    internal bool StartMainMenuServerEntryFlow(string? serverBaseUrl, string? userId, string? password, bool createAccountIfMissing = false)
        => worldEntryStart.TryStart(serverBaseUrl, userId, password, createAccountIfMissing);

    private void StartRestoreAfterRaid(string eventId)
        => _ = snapshotReload.StartAsync(new(sessionLifecycle.ReadContext().CurrentSnapshotId, ClientSnapshotReloadPurpose.Raid));
    internal void OpenCaravanRaidMenu(Caravan caravan, IReadOnlyList<ModWorldMapMarkerDto> targets)
        => raidCommands.OpenCaravanMenu(ClientRaidCommandSource.ForCaravan(caravan), targets);

    public void StartRaidFromVehicleLanding(Caravan caravan, ModWorldMapMarkerDto target)
        => _ = raidCommands.StartVehicle(ClientRaidCommandSource.ForCaravan(caravan), target);

    internal void StartObserveRemoteMarker(ModWorldMapMarkerDto target, string mode)
        => observationMaps.OpenAsync(target, mode);

    internal void StartFinishActiveRaidBattle(ActiveRaidBattleSession session, string finishReason)
        => _ = raidSettlements.StartAsync(new ClientRaidSettlementRuntime(session), finishReason);
    internal void StartRefreshServerShop() => _ = shopCatalog.RefreshAsync();

    internal void StartUpsertServerShopListing(string? listingId, string listingKind, ModThingReferenceDto item,
        int priceSilver, int stockCount, double priceIncreaseRatio = 1d,
        string qualityRequirementMode = "AtLeast", string hitPointsRequirementMode = "AtLeast") =>
        _ = shopCatalog.UpsertAsync(new(listingId, listingKind, item, priceSilver, stockCount,
            priceIncreaseRatio, qualityRequirementMode, hitPointsRequirementMode));

    internal void StartRemoveServerShopListing(ModServerShopListingDto listing) => _ = shopCatalog.RemoveAsync(listing.ListingId);

    internal void StartPurchaseServerShopListing(ModServerShopListingDto listing, int purchaseCount) => _ = shopOperations.StartAsync(listing, purchaseCount);

    internal void OpenCaravanSupportPawnMenu(Caravan caravan, IReadOnlyList<ModWorldMapMarkerDto> targets, IReadOnlyList<Pawn> pawns) =>
        supportActions.OpenMenu(caravan, targets, pawns);

    internal void StartAcceptTradeOrder(ModTradeOrderSummaryDto order, bool postagePaidByAcceptor) => _ = tradeMarket.AcceptAsync(order, postagePaidByAcceptor);

    // Retained for the explicit RimTest action protocol; game Gizmos use domain bindings.
    internal void OpenCaravanGiftDeliveryMenu(Caravan caravan, IReadOnlyList<ModWorldMapMarkerDto> targets, bool forcedDelivery)
        => giftActions.OpenMenu(caravan, targets, forcedDelivery);

    internal void OpenCaravanTradeFulfillmentMenu(Caravan caravan, IReadOnlyList<ModTradeOrderSummaryDto> orders) => tradeActions.OpenCaravanMenu(caravan, orders);

    internal void StartFulfillTradeOrderByDropPod(ModTradeOrderSummaryDto order) => tradeActions.FulfillByDropPod(order);

    internal void StartRefreshTradeOrders(string scope = "Open") => _ = tradeMarket.RefreshAsync(scope);

    internal void StartLoadMoreTradeOrders(string scope = "Open") => _ = tradeMarket.RefreshAsync(scope, reset: false);

    internal void StartCancelTradeOrder(ModTradeOrderSummaryDto order) => _ = tradeMarket.CancelAsync(order, acceptedMemo: false);

    internal void StartCancelAcceptedTradeMemo(ModTradeOrderSummaryDto order) => _ = tradeMarket.CancelAsync(order, acceptedMemo: true);

    internal void OpenTradeOrderDialog() => tradeActions.OpenCreate();

    internal void OpenTradeMarketWindow() => tradeActions.OpenMarket(multiplayerPages.CreateTradePage);
    internal void OpenColonyAppearanceWindow(MapParent colony) => colonyAppearance.Open(new ClientColonyAppearanceTarget(colony));
    internal void SaveColonyAppearance(string? mode, string? iconDefName, string? colorDefName, string? colorHex)
        => colonyAppearance.SaveCurrent(new(mode, iconDefName, colorDefName, colorHex));
}
