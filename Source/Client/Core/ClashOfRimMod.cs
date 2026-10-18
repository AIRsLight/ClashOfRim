using AIRsLight.ClashOfRim.Modules.Presence;
using AIRsLight.ClashOfRim.Modules.Chat;
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
    private readonly ClientColonyRelocation colonyRelocation;
    private readonly ClientColonyRelocationCommands colonyRelocationCommands;
    private readonly ClientAdminAccess adminAccess;
    private readonly ClientAdministration administration;
    private readonly ClientWorldWrites worldWrites;
    private bool statusDefaultsInitialized;
    private string loginStatus = string.Empty;
    private string snapshotUploadStatus = string.Empty;
    private string presenceStatus = string.Empty;
    private readonly ClientDiplomacyActions diplomacyActions;
    private readonly ClientPlayerDirectory playerDirectory;
    // Transitional status bridge for local fulfillment and support, pending their module stages.
    private string tradeStatus { get => tradeState.Message; set => tradeState.Message = value; }
    private string worldMapStatus = string.Empty;
    private readonly ClientCompatibilityBaseline compatibilityBaseline;
    private readonly ClientCompatibilityOverride compatibilityOverride;
    private readonly ClientLocalMutationCoordinator localMutations = new();
    private readonly ClientSessionNotifications sessionNotifications;
    private readonly ClientSessionLifecycle sessionLifecycle;
    private readonly ClientChat chat;
    private readonly ClientPasswordChange passwordChange;
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
    private readonly ClientWorldGeneration worldGeneration;
    private readonly ClientWorldBaselineSubmission worldBaselineSubmission;
    private readonly ClientWorldBaselineCapture worldBaselineCapture;
    private readonly ClientAdminBaselineCapture adminBaselineCapture;
    private readonly ClientInitialWorldSetup initialWorldSetup;
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
        adminAccess = new ClientAdminAccess(sessionLifecycle);
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
        compatibilityPresentation = new ClientCompatibilityPresentation((response, language, accept, cancel) =>
            new AIRsLight.ClashOfRim.CompatibilityClient.CompatibilityMismatchWindow(this, response, language, accept, cancel));
        var entryPresentation = new ClientWorldEntryPresentation(message => { EnsureStatusDefaultsInitialized(); loginStatus = message; },
            (server, account) => Find.WindowStack.Add(new AIRsLight.ClashOfRim.MainMenu.ClashOfRimAccountRegistrationDialog(this, server, account)), compatibilityPresentation);
        var worldTransport = new ClientWorldPreparationTransport(new ClientProtocolEnvironment());
        worldPreparation = new ClientWorldPreparation(sessionLifecycle, new ClientSnapshotGame(), worldTransport, entryPresentation);
        worldBaselineSubmission = new ClientWorldBaselineSubmission(sessionLifecycle, new ClientWorldBaselineRuntime(), worldTransport);
        worldBaselineCapture = new ClientWorldBaselineCapture(worldBaselineSubmission, storytellerConfiguration);
        var sessionReconnect = new ClientSessionReconnect(new ClientSessionReconnectTransport(new ClientProtocolEnvironment()), sessionLifecycle, synchronization);
        passwordChange = new ClientPasswordChange(sessionLifecycle,
            new ClientPasswordTransport(new ClientProtocolEnvironment()),
            new ClientPasswordStore(settings), new ClientPasswordPresentation(), ClientMainThreadDispatcher.EnqueueSession);
        var eventReader = new ClientEventReader(sessionLifecycle, eventInbox, new ClientEventReadTransport(), new ClientSnapshotGame());
        var eventReadPresentation = new ClientEventReadPresentation();
        eventState = new ClientEventState(eventReader, eventReadPresentation);
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
            new ClientLocalSnapshotPresentation(message => snapshotUploadStatus = message));
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
        var supportPresentation = new ClientSupportPresentation(message => tradeStatus = message);
        var supportSending = new ClientSupportSending(sessionLifecycle, new ClientSnapshotGame(), mutationAdmission, synchronization,
            localMutations, snapshotTransactions, snapshotFailureRecovery, new ClientSupportTransport(), supportPresentation);
        supportSending.Completed += StartQueuedAutomaticEventRefreshAfterLocalAtomicMutation;
        supportActions = new ClientSupportActions(supportSending, supportPresentation);
        supportReturns = new ClientSupportReturns(sessionLifecycle, new ClientSnapshotGame(), mutationAdmission, localMutations,
            snapshotTransactions, snapshotFailureRecovery, localSnapshotConfirmation, new ClientSupportReturnRuntime(supportPresentation),
            new ClientSupportTransport(), supportPresentation);
        supportReturns.Completed += StartQueuedAutomaticEventRefreshAfterLocalAtomicMutation;
        var raidTransport = new ClientRaidTransport();
        var raidPresentation = new ClientRaidLaunchPresentation(message => worldMapStatus = message);
        var raidLaunching = new ClientRaidLaunching(sessionLifecycle, mutationAdmission, synchronization, localMutations,
            snapshotTransactions, snapshotFailureRecovery, new ClientRaidPreparation(raidTransport, new ClientRemoteSnapshotTransport()),
            raidTransport, raidPresentation, () => PvpEnabled);
        raidCommands = new ClientRaidCommands(sessionLifecycle, raidLaunching, raidPresentation);
        raidLaunching.Completed += StartQueuedAutomaticEventRefreshAfterLocalAtomicMutation;
        raidSettlements = new ClientRaidSettlements(sessionLifecycle, mutationAdmission, synchronization, localMutations, snapshotTransactions,
            snapshotFailureRecovery, new ClientRaidSupportLosses(snapshotTransactions, new ClientSupportTransport()),
            new ClientEventConfirmationTransport(), eventInbox, localSnapshotConfirmation, new ClientRaidSettlementPresentation(message => worldMapStatus = message));
        raidSettlements.Completed += () => _ = playerDirectory.RefreshAsync(manual: false);
        raidSettlements.Expired += StartRestoreAfterRaid;
        raidRecovery = new ClientRaidRecovery(sessionLifecycle, new ClientSnapshotGame(), snapshotFailureRecovery, raidSettlements,
            new ClientRaidRecoveryPresentation(message => worldMapStatus = message));
        raidRecovery.ReloadRequested += StartRestoreAfterRaid;
        snapshotReload = new ClientSnapshotReload(sessionLifecycle, synchronization, localMutations, snapshotFailureRecovery,
            new ClientSessionAuthenticationTransport(), new ClientColonySnapshotDownload(new ClientRemoteSnapshotTransport()),
            snapshotPublication, new ClientSnapshotReloadRuntime(), new ClientSnapshotReloadPresentation(
                (message, purpose) => { loginStatus = message; if (purpose == ClientSnapshotReloadPurpose.Raid) worldMapStatus = message; },
                response => ShowCompatibilityMismatchWindow(response)));
        var entryStore = new ClientWorldEntryStore(settings);
        var featureCatalogs = new ClientWorldFeatureCatalogs(sessionLifecycle, worldTransport);
        featureCatalogs.Diagnostic += message => Log.Warning(message);
        var configurationRuntime = new ClientWorldConfigurationRuntime();
        worldConfiguration = new ClientWorldConfiguration(sessionLifecycle, configurationRuntime, entryStore, storytellerConfiguration, featureCatalogs);
        occupiedColonySites = new ClientOccupiedColonySites(sessionLifecycle, entryStore, new ClientOccupiedColonySitesRuntime());
        worldMarkers = new ClientWorldMarkers(sessionLifecycle, entryStore, occupiedColonySites, new ClientWorldMarkersRuntime(),
            new ClientWorldMarkersPresentation(message => worldMapStatus = message));
        worldLoginPublication = new ClientWorldLoginPublication(sessionLifecycle, worldMarkers);
        worldSynchronization = new ClientWorldSynchronization(sessionLifecycle, entryStore, localMutations, worldMarkers,
            new ClientWorldSynchronizationRuntime(), new ClientWorldMarkerTransport(new ClientProtocolEnvironment()),
            new ClientWorldWritePresentation(message => worldMapStatus = message), tradeMarket.CaptureRefresh);
        colonyAppearance = new ClientColonyAppearance(sessionLifecycle, new ClientColonyAppearanceStore(settings), new ClientColonyAppearancePresentation());
        var worldPublication = new ClientWorldPublication(entryStore, worldConfiguration, occupiedColonySites, colonyAppearance, worldMarkers);
        var colonyTransport = new ClientColonyTransport(new ClientProtocolEnvironment());
        colonyRegistration = new ClientColonyRegistration(sessionLifecycle, entryStore, localMutations, new ClientColonyRegistrationRuntime(), colonyTransport, worldPublication);
        snapshotSaving = new ClientSnapshotSaving(sessionLifecycle, synchronization, localMutations, ClientSnapshotComposition.Create(settings),
            new ClientSnapshotGame(), entryStore, configurationRuntime, colonyRegistration, colonyAppearance,
            new ClientSnapshotSavingPresentation(message => snapshotUploadStatus = message));
        colonyAbandonment = new ClientColonyAbandonment(sessionLifecycle, entryStore, new ClientWorldCatalogRuntime(), colonyTransport,
            new ClientColonyAbandonPresentation(message => worldMapStatus = message));
        colonyRelocation = new ClientColonyRelocation(sessionLifecycle, entryStore, new ClientWorldCatalogRuntime(), mutationAdmission,
            synchronization, localMutations, snapshotTransactions, ClientSnapshotComposition.Create(settings), snapshotFailureRecovery,
            colonyRegistration, worldPublication, colonyTransport, new ClientColonyRelocationPresentation());
        colonyRelocationCommands = new ClientColonyRelocationCommands(colonyRelocation);
        colonyRelocation.Completed += StartQueuedAutomaticEventRefreshAfterLocalAtomicMutation;
        snapshotFailureRecovery.ReturningToMenu += () => colonyRegistration.Suppressed = false;
        colonyAppearance.RegistrationRequested += (appearance, reason) => _ = colonyRegistration.RequestAsync(
            ClientColonyRegistrationKind.Sites, reason, appearance, suppressNotification: true);
        colonyRegistration.Diagnostic += (message, warning) => { if (warning) Log.Warning(message); else ClashLog.Message(message); };
        worldCatalog = new ClientWorldCatalog(sessionLifecycle, new ClientWorldCatalogRuntime(), worldTransport,
            new ClientWorldCatalogPresentation(message => worldMapStatus = message), worldPublication, serverMetadata);
        var worldWritePresentation = new ClientWorldWritePresentation(message => administration.Message = message);
        worldWrites = new ClientWorldWrites(sessionLifecycle, adminAccess, worldBaselineCapture, worldBaselineSubmission,
            storytellerConfiguration, worldPublication, worldWritePresentation, worldWritePresentation);
        worldGeneration = new ClientWorldGeneration(sessionLifecycle, worldSetup, worldConfiguration, new ClientWorldGenerationRuntime(), new ClientWorldGenerationPresentation(message => loginStatus = message));
        worldEntry = new ClientWorldEntry(sessionLifecycle, new ClientSnapshotGame(), worldSetup,
            entryStore, worldTransport, entryPresentation, snapshotReload, worldPublication, serverMetadata);
        worldEntryStart = new ClientWorldEntryStart(sessionLifecycle, synchronization, new ClientSnapshotGame(),
            entryStore, entryPresentation, worldPreparation, worldEntry);
        observationMaps = new ClientObservationMaps(sessionLifecycle, synchronization, new ClientSnapshotGame(),
            new ClientObservationTransport(), new ClientObservationRuntime(),
            new ClientObservationPresentation(message => worldMapStatus = message, response => ShowCompatibilityMismatchWindow(response)));
        eventConfirmations = new ClientEventConfirmationWorkflow(sessionLifecycle, eventConfirmationPreparation, eventConfirmation,
            localMutations, snapshotFailureRecovery, eventInbox, new ClientEventConfirmationPresentation((purpose, message) =>
            {
                switch (purpose)
                {
                    case ClientEventConfirmationPurpose.Trade: tradeStatus = message; break;
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
            new ClientSessionNotificationPresentation(message => presenceStatus = message));
        sessionRecovery = new ClientSessionRecovery(sessionLifecycle, sessionConnection, sessionReconnect, localMutations, worldLoginPublication,
            new ClientSessionRecoveryRuntime(), new ClientSessionRecoveryPresentation(message => loginStatus = message, message => presenceStatus = message),
            new ClientSessionRecoveryEffects(eventRefresh, playerDirectory, chat, sessionNotifications, raidRecovery));
        notificationEffects.SessionExpired += sessionRecovery.Expired;
        var sessionFollowups = new ClientSessionFollowups(eventRefresh, playerDirectory, chat, colonyRegistration, colonyAppearance,
            worldSynchronization, sessionNotifications, new ClientSessionFollowupText());
        var snapshotWorld = new ClientSnapshotWorld(sourceGame, worldConfiguration, entryStore, configurationRuntime, sessionConnection,
            sessionFollowups, raidRecovery, new ClientRaidRecoveryRuntime(), new ClientSnapshotWorldPresentation(message => worldMapStatus = message));
        snapshotReload.Completed += snapshotWorld.Continue;
        var initialWorldPresentation = new ClientInitialWorldPresentation(message => loginStatus = message, message => snapshotUploadStatus = message, sessionConnection);
        initialWorldSetup = new ClientInitialWorldSetup(sessionLifecycle, worldSetup, synchronization, worldBaselineCapture, worldBaselineSubmission,
            adminBaselineCapture, new AIRsLight.ClashOfRim.Admin.ClientAdminBaselineTransport(new ClientProtocolEnvironment()),
            ClientSnapshotComposition.Create(settings), initialWorldPresentation, initialWorldPresentation, sessionFollowups);
        sessionStartup = new ClientSessionStartup(sessionLifecycle, synchronization, localMutations, sourceGame, worldLoginPublication,
            new ClientSessionLogin(sessionLifecycle, new ClientSessionLoginTransport(new ClientProtocolEnvironment())),
            ClientSnapshotComposition.Create(settings), new ClientSessionStartupRuntime(sessionConnection),
            new ClientSessionStartupPresentation(message => loginStatus = message, message => snapshotUploadStatus = message, response => ShowCompatibilityMismatchWindow(response)), sessionFollowups);
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
        sessionLifecycle.IdentityChanged += serverMetadata.ObserveIdentity;
        ClientSessionCleanup.Attach(sessionLifecycle, synchronization, localMutations, eventInbox, eventState, sourceGame,
            new ClientSessionExitResources(), colonyRegistration, bankState, mercenaryState, mercenaryIncidents, shopState,
            tradeState, chat, worldMarkers, worldSynchronization, sessionIdentity, administration, sessionNotifications,
            worldSetup, playerDirectory, occupiedColonySites);
        var snapshotNotifications = new ClientSnapshotTransactionNotifications(sessionLifecycle, synchronization,
            new ClientSnapshotGame(), new ClientSnapshotTransactionPresentation());
        synchronization.SnapshotChanged += snapshotNotifications.Observe;
        sessionLifecycle.LogoutFailed += message => Log.Warning("[ClashOfRim] " + message);
        _ = new ClientConnectionStatus(sessionLifecycle, sessionConnection, new ClientConnectionStatusPresentation(message => presenceStatus = message));
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

    private void EnsureStatusDefaultsInitialized()
    {
        if (statusDefaultsInitialized)
        {
            return;
        }

        statusDefaultsInitialized = true;
        loginStatus = ClashOfRimText.Key("ClashOfRim.Status.NotLoggedIn");
        snapshotUploadStatus = ClashOfRimText.Key("ClashOfRim.Status.SnapshotUploadNotStarted");
        presenceStatus = ClashOfRimText.Key("ClashOfRim.Status.PresenceNotStarted");
        tradeStatus = ClashOfRimText.Key("ClashOfRim.Status.TradeOrderNotCreated");
        worldMapStatus = ClashOfRimText.Key("ClashOfRim.Status.WorldMapNotSynced");
        bankState.Message = ClashOfRimText.Key("ClashOfRim.Status.BankNotPulled");
        shopState.Message = ClashOfRimText.Key("ClashOfRim.Shop.StatusIdle");
    }

    internal bool SnapshotUploadInProgress => synchronization.SnapshotInProgress;
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

    internal bool IsInActiveMultiplayerSession => sessionStartup.IsInActiveWorld;

    internal bool CanUseVanillaMenuSnapshotUpload => snapshotSaving.CanUseMenu;

    internal bool ShouldInterceptVanillaAutosave => snapshotSaving.InterceptsAutosave;

    internal bool CanStartAutomaticMapServerSession => sessionStartup.CanStartAutomatic;

    internal bool CanSyncRuntimeWorldObjects => worldSynchronization.CanStart(ClientWorldSynchronizationKind.RuntimeObjects);

    internal bool CanRegisterPlayerColonySites => colonyRegistration.CanStart(ClientColonyRegistrationKind.Sites);
    internal bool IsPlayerColonySiteRegistrationSuppressed => colonyRegistration.Suppressed;
    internal bool CanSyncWorldConfigurationExtensions => colonyRegistration.CanStart(ClientColonyRegistrationKind.Extensions);

    internal bool CanRefreshCaravanArrivalTargets => worldSynchronization.CanStart(ClientWorldSynchronizationKind.CaravanTargets);

    internal string LoginStatus
    {
        get
        {
            EnsureStatusDefaultsInitialized();
            return loginStatus;
        }
    }

    internal string EventQueueStatus
    {
        get
        {
            EnsureStatusDefaultsInitialized();
            return eventState.QueueStatus;
        }
    }

    internal string EventDetailsStatus
    {
        get
        {
            EnsureStatusDefaultsInitialized();
            return eventState.DetailsStatus;
        }
    }

    internal string GiftProcessingStatus
    {
        get
        {
            EnsureStatusDefaultsInitialized();
            return eventState.ActionStatus;
        }
    }

    internal string SnapshotUploadStatus
    {
        get
        {
            EnsureStatusDefaultsInitialized();
            return snapshotUploadStatus;
        }
    }

    internal string PresenceStatus
    {
        get
        {
            EnsureStatusDefaultsInitialized();
            return presenceStatus;
        }
    }

    internal string PlayerListStatus => playerDirectory.Message;

    internal string TradeStatus
    {
        get
        {
            EnsureStatusDefaultsInitialized();
            return tradeStatus;
        }
    }

    internal string WorldMapStatus
    {
        get
        {
            EnsureStatusDefaultsInitialized();
            return worldMapStatus;
        }
    }

    internal string ChatStatus => chat.Status;
    internal IReadOnlyList<ModChatMessageDto> ChatMessagesSnapshot => chat.CopyMessages();
    internal int UnreadPrivateChatCount => chat.UnreadPrivate;
    internal int ChatMessagesSnapshotVersion => chat.Version;

    internal string BankStatus
    {
        get
        {
            EnsureStatusDefaultsInitialized();
            return bankState.Message;
        }
    }

    internal bool BankInProgress => bankState.Busy;

    internal ModBankStatusResponseDto? BankStatusSnapshot => bankState.View;

    internal string AdminStatus => administration.Message;
    internal ModAdminStatusResponseDto? AdminStatusSnapshot => administration.Snapshot;

    internal string MercenaryStatus
    {
        get
        {
            EnsureStatusDefaultsInitialized();
            return mercenaryState.Message;
        }
    }

    internal string ServerShopStatus
    {
        get
        {
            EnsureStatusDefaultsInitialized();
            return shopState.Message;
        }
    }

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
