using AIRsLight.ClashOfRim.Network.Modules.Chat;
using AIRsLight.ClashOfRim.Network.Modules.Admin;
using AIRsLight.ClashOfRim.Network.Modules.World;
using AIRsLight.ClashOfRim.Network.Modules.Achievements;
using AIRsLight.ClashOfRim.Network.Modules.Compatibility;
using AIRsLight.ClashOfRim.Network.Modules.Sessions;
using AIRsLight.ClashOfRim.Network.Modules.Accounts;
using AIRsLight.ClashOfRim.Network.Modules.Diplomacy;
using AIRsLight.ClashOfRim.Network.Modules.Support;
using AIRsLight.ClashOfRim.Network.Modules.Raids;
using AIRsLight.ClashOfRim.Network.Modules.Mercenaries;
using AIRsLight.ClashOfRim.Network.Modules.Shop;
using AIRsLight.ClashOfRim.Network.Modules.Trades;
using AIRsLight.ClashOfRim.Network.Modules.Gifts;
using AIRsLight.ClashOfRim.Network.Modules.Transfers;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network.Plugins;
using AIRsLight.ClashOfRim.Network.Modules.Authorization;
using AIRsLight.ClashOfRim.Network.Modules.Snapshots;
using AIRsLight.ClashOfRim.Network.Modules.Events;
using AIRsLight.ClashOfRim.Network.Modules.Bank;
using AIRsLight.ClashOfRim.Network.Modules.Economy;
using AIRsLight.ClashOfRim.Save;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AIRsLight.ClashOfRim.Network;

public sealed class ClashOfRimNetworkState
{
    public ClashOfRimNetworkState(
        IAuthoritativeEventLedger? ledger = null,
        IColonySnapshotIndexStore? snapshotStore = null,
        SnapshotUploadPolicy? snapshotUploadPolicy = null,
        EventNotificationHub? eventNotifications = null,
        EventNotificationHub? chatNotifications = null,
        EventNotificationHub? worldConfigurationNotifications = null,
        PlayerOnlineNotificationHub? playerOnlineNotifications = null,
        OnlinePresenceRegistry? onlinePresence = null,
        LoginSessionRegistry? loginSessions = null,
        PlayerRegistry? playerRegistry = null,
        ClashOfRimServerConfiguration? serverConfiguration = null,
        IWorldTileDistanceCalculator? worldTileDistanceCalculator = null,
        WorldConfigurationRegistry? worldConfigurationRegistry = null,
        CompatibilityBaselineRegistry? compatibilityBaselineRegistry = null,
        AdminBaselineRegistry? adminBaselineRegistry = null,
        RuntimeWorldObjectMarkerRegistry? runtimeWorldObjectMarkers = null,
        DiplomacyRelationRegistry? diplomacyRelations = null,
        AuthTokenRegistry? authTokens = null,
        AdminControlRegistry? adminControl = null,
        ServerConfigurationRegistry? serverConfigurationOverrides = null,
        ISteamAuthTicketValidator? steamAuthTickets = null,
        OfflineAccountRegistry? offlineAccounts = null,
        PawnPackageRegistry? pawnPackages = null,
        ThingPackageRegistry? thingPackages = null,
        RaidPreparationRegistry? raidPreparations = null,
        RaidProtectionActivationRegistry? raidProtectionActivations = null,
        RaidCooldownOverrideRegistry? raidCooldownOverrides = null,
        BankLoanRegistry? bankLoans = null,
        MercenaryContractRegistry? mercenaryContracts = null,
        MercenaryGuardContractRegistry? mercenaryGuards = null,
        ChatMessageRegistry? chatMessages = null,
        ServerShopRegistry? serverShop = null,
        AchievementRegistry? achievements = null,
        SnapshotPostUploadJobRegistry? snapshotPostUploadJobs = null,
        ISnapshotPostUploadArtifactStore? snapshotPostUploadArtifacts = null,
        ServerPluginRegistry? plugins = null,
        ILogger? runtimeLogger = null,
        SnapshotCommitService? snapshotCommits = null,
        ColonyDeletionService? colonyDeletions = null)
    {
        Plugins = plugins ?? new ServerPluginRegistry().WithBuiltInPlugins(BuiltInServerPlugins.Descriptors);
        Ledger = ledger ?? new InMemoryAuthoritativeEventLedger();
        SnapshotStore = snapshotStore ?? new InMemoryColonySnapshotIndexStore();
        SnapshotUploadPolicy = snapshotUploadPolicy ?? SnapshotUploadPolicy.AllowAnyVersion;
        EventNotifications = eventNotifications ?? new EventNotificationHub();
        ChatNotifications = chatNotifications ?? new EventNotificationHub();
        WorldConfigurationNotifications = worldConfigurationNotifications ?? new EventNotificationHub();
        PlayerOnlineNotifications = playerOnlineNotifications ?? new PlayerOnlineNotificationHub();
        OnlinePresence = onlinePresence ?? new OnlinePresenceRegistry();
        LoginSessions = loginSessions ?? new LoginSessionRegistry();
        Players = playerRegistry ?? new PlayerRegistry();
        WorldTileDistanceCalculator = worldTileDistanceCalculator ?? new StraightLineWorldTileDistanceCalculator();
        WorldConfiguration = worldConfigurationRegistry ?? new WorldConfigurationRegistry(WorldConfigurationExtensionService.Empty);
        CompatibilityBaseline = compatibilityBaselineRegistry ?? new CompatibilityBaselineRegistry();
        AdminBaseline = adminBaselineRegistry ?? new AdminBaselineRegistry();
        RuntimeWorldObjectMarkers = runtimeWorldObjectMarkers ?? new RuntimeWorldObjectMarkerRegistry();
        DiplomacyRelations = diplomacyRelations ?? new DiplomacyRelationRegistry();
        AuthTokens = authTokens ?? new AuthTokenRegistry();
        AdminControl = adminControl ?? new AdminControlRegistry();
        ServerConfigurationOverrides = serverConfigurationOverrides ?? new ServerConfigurationRegistry();
        ConfigurationState = new ServerConfigurationState(ServerConfigurationOverrides.Current is { } savedConfiguration
            ? AdminConfigurationMapping.Apply(savedConfiguration, serverConfiguration ?? new ClashOfRimServerConfiguration())
            : serverConfiguration ?? new ClashOfRimServerConfiguration());
        SteamAuthTickets = steamAuthTickets ?? new DevelopmentSteamAuthTicketValidator();
        OfflineAccounts = offlineAccounts ?? new OfflineAccountRegistry();
        Accounts = new AccountService(OfflineAccounts, AuthTokens, SteamAuthTickets, () => ServerConfiguration);
        CompatibilityHandshake = new CompatibilityHandshakeService(CompatibilityBaseline, WorldConfiguration, () => ServerConfiguration.CompatibilityOptions);
        EntryHandshakes = new EntryHandshakePolicy(AdminControl, WorldConfiguration, LoginSessions, CompatibilityHandshake);
        PawnPackages = pawnPackages ?? new PawnPackageRegistry();
        ThingPackages = thingPackages ?? new ThingPackageRegistry();
        RaidPreparations = raidPreparations ?? new RaidPreparationRegistry();
        RaidProtectionActivations = raidProtectionActivations ?? new RaidProtectionActivationRegistry();
        RaidCooldownOverrides = raidCooldownOverrides ?? new RaidCooldownOverrideRegistry();
        BankLoans = bankLoans ?? new BankLoanRegistry();
        MercenaryContracts = mercenaryContracts ?? new MercenaryContractRegistry();
        MercenaryGuards = mercenaryGuards ?? new MercenaryGuardContractRegistry();
        ChatMessages = chatMessages ?? new ChatMessageRegistry();
        ServerShop = serverShop ?? new ServerShopRegistry();
        Achievements = achievements ?? new AchievementRegistry();
        SnapshotPostUploadJobs = snapshotPostUploadJobs ?? new SnapshotPostUploadJobRegistry();
        SnapshotPostUploadArtifacts = snapshotPostUploadArtifacts ?? new InMemorySnapshotPostUploadArtifactStore();
        RuntimeLogger = runtimeLogger ?? NullLogger.Instance;
        AchievementAwards = new(Achievements, Plugins, CompatibilityBaseline, Ledger, OnlinePresence, EventNotifications, () => RuntimeLogger);
        EventAchievements = new(AchievementAwards, Plugins, CompatibilityBaseline);
        SnapshotAchievements = new(Players, Achievements, AchievementAwards, Plugins, CompatibilityBaseline, () => RuntimeLogger);
        SnapshotCommits = snapshotCommits ?? new SnapshotCommitService();
        SnapshotConfirmations = new SnapshotConfirmationPlanner(Ledger);
        RaidQueries = new Modules.Raids.RaidQueries(Ledger, () => ServerConfiguration);
        RaidProtection = new Modules.Raids.RaidProtection(Ledger, RaidProtectionActivations, RaidCooldownOverrides, () => ServerConfiguration, RuntimeLogger);
        ColonyDeletions = colonyDeletions ?? new ColonyDeletionService();
        SessionPrincipals = new SessionPrincipalAuthorization(AuthTokens, LoginSessions);
        ColonyAuthorization = new ColonyAccessAuthorization(SessionPrincipals, DiplomacyRelations,
            RaidPreparations, Ledger, () => ServerConfiguration.PvpEnabled);
        SnapshotAuthorization = new ColonySnapshotAuthorization(ColonyAuthorization, SnapshotStore);
        // Resolve once after composition; recovery needs the domains that use this coordinator.
        SnapshotMutations = new SnapshotMutationCoordinator(RaidSettlementSnapshotMutationGate, new Lazy<ISnapshotRecovery>(CreateSnapshotRecovery));
        DiplomacyPublications = new DiplomacyPublications(Ledger, DiplomacyRelations, OnlinePresence, user => EventNotifications.SignalUser(user));
        Diplomacy = new DiplomacyService(Ledger, SnapshotAuthorization, SnapshotMutations, DiplomacyRelations,
            OnlinePresence, DiplomacyPublications, () => ServerConfiguration, () => RuntimeLogger);
        AdminAuthorization = new AdministratorAuthorization(SessionPrincipals, WorldConfiguration);
        SnapshotRequests = new SnapshotRequestAuthorization(SessionPrincipals, ColonyAuthorization, SnapshotMutations);
        SnapshotTransactions = new SnapshotTransactions(SnapshotStore, SnapshotUploadPolicy, Plugins, CompatibilityBaseline);
        PendingConfirmations = new PendingConfirmationReconciler(BankLoans, MercenaryContracts,
            () => ServerConfiguration, SnapshotMutations);
        RaidSettlement = new RaidSettlement(Ledger, SnapshotStore, SnapshotMutations, SnapshotCommits, BankLoans, Players,
            EventNotifications, OnlinePresence, AdminBaseline, Plugins, CompatibilityBaseline, () => ServerConfiguration, () => RuntimeLogger);
        RaidSettlementWork = new RaidSettlementWork(RaidSettlement, SnapshotPostUploadArtifacts, SnapshotPostUploadJobs, SnapshotMutations);
        RaidEvidenceScheduling = new RaidEvidenceScheduling(Ledger, SnapshotStore, SnapshotPostUploadArtifacts,
            SnapshotPostUploadJobs, Players, SnapshotMutations);
        MercenaryIncidents = new MercenaryIncidentRecovery(BankLoans, MercenaryContracts, Ledger, OnlinePresence, EventNotifications);
        var supportClosures = new SupportClosures(Ledger, EventNotifications);
        SupportDeaths = new SupportSnapshotDeaths(Ledger, OnlinePresence, EventNotifications, supportClosures, SnapshotMutations, SnapshotCommits);
        WorldChanges = new WorldConfigurationChanges(Players, WorldConfigurationNotifications);
        WorldLayerNotifications = new WorldLayerNotifications(WorldConfiguration, Ledger, Players, OnlinePresence, WorldTileDistanceCalculator,
            SnapshotMutations, EventNotifications, WorldChanges);
        WorldLayers = new SnapshotWorldLayers(SnapshotStore, WorldConfiguration, Plugins, CompatibilityBaseline, Players,
            SnapshotMutations, WorldLayerNotifications, () => RuntimeLogger);
        ColonyLocations = new SnapshotColonyLocations(Plugins, CompatibilityBaseline);
        ColonyProjection = new SnapshotColonyProjection(WorldConfiguration, Plugins, CompatibilityBaseline, ColonyLocations, WorldChanges);
        SnapshotPostUploads = new(new CoreSnapshotPostUpload(Players, WorldLayers, ColonyProjection, SnapshotAchievements,
            SupportDeaths, SnapshotStore, SnapshotMutations, MercenaryIncidents),
            new SnapshotPostUploadRunner(SnapshotPostUploadJobs, () => RuntimeLogger), Plugins, CompatibilityBaseline,
            new SnapshotPostUploadPluginAdapter(this), new RaidSettlementPostUploadProcessor(), RaidEvidenceScheduling, RaidSettlementWork);
        RaidOfflineEvidence = new RaidOfflineEvidence(Ledger, SnapshotStore, SnapshotPostUploadJobs, SnapshotPostUploadArtifacts,
            SnapshotMutations, RaidQueries, RaidEvidenceScheduling, () => RuntimeLogger);
        RaidBattleCleanup = new RaidBattleCleanup(Ledger, SnapshotStore, SnapshotPostUploadJobs, SnapshotMutations,
            RaidQueries, SnapshotCommits, BankLoans, Players, OnlinePresence, EventNotifications, () => RuntimeLogger);
        RaidTimeouts = new RaidTimeouts(Ledger, SnapshotStore, SnapshotPostUploadJobs, SnapshotMutations, OnlinePresence,
            EventNotifications, RaidOfflineEvidence, RaidBattleCleanup, () => ServerConfiguration);
        WorldDelivery = new WorldConfigurationDelivery(Plugins, CompatibilityBaseline);
        PlayerWealth = new PlayerSnapshotWealth(Players, SnapshotStore);
        var playerCatalog = new PlayerColonyCatalog(Players, SnapshotStore);
        PlayerDirectory = new PlayerDirectory(playerCatalog, PlayerWealth, DiplomacyRelations, OnlinePresence, SnapshotMutations);
        AchievementQueries = new AchievementQueries(Achievements, playerCatalog, SnapshotMutations);
        RaidMarkers = new RaidMarkerProjection(Ledger, SnapshotStore);
        WorldMarkers = new WorldMarkerDelivery(Players, OnlinePresence, DiplomacyRelations, SnapshotStore, WorldConfiguration,
            WorldDelivery, RuntimeWorldObjectMarkers, RaidMarkers, RaidProtection, PlayerWealth, () => ServerConfiguration);
        EntryLifecycle = new ClientEntryLifecycle(RaidProtection, PendingConfirmations);
        PlayerActivity = new PlayerSessionActivity(Players, SnapshotStore);
        SessionConnections = new SessionConnections(LoginSessions, AuthTokens, OnlinePresence, PlayerOnlineNotifications,
            PlayerActivity, SnapshotMutations, PendingConfirmations, RaidTimeouts);
        var raidReconciliation = new RaidDeliveryProtection(RaidTimeouts, RaidQueries, RaidProtection);
        var raidAdmission = new RaidAdmission(DiplomacyRelations, OnlinePresence, RaidProtection, () => ServerConfiguration);
        RaidPreparation = new RaidPreparation(SnapshotStore, RaidPreparations, MercenaryGuards, RaidQueries, raidAdmission,
            raidReconciliation, WorldConfiguration, () => ServerConfiguration);
        RaidStarting = new RaidStarting(Ledger, SnapshotStore, RaidQueries, MercenaryGuards, SnapshotRequests, SnapshotTransactions,
            PendingConfirmations, raidAdmission, raidReconciliation, new RaidSnapshotCommitter(SnapshotCommits, SnapshotStore,
                BankLoans, Ledger, MercenaryContracts, MercenaryGuards, EventNotifications, SnapshotPostUploads, () => RuntimeLogger), () => ServerConfiguration);
        BankRequests = new BankRequestAccess(ColonyAuthorization, SnapshotMutations, SnapshotStore, BankLoans, PendingConfirmations);
        Bank = new BankService(BankLoans, MercenaryContracts, SnapshotStore, SnapshotCommits,
            BankRequests, SnapshotRequests, SnapshotTransactions, SnapshotPostUploads, () => ServerConfiguration);
        Mercenaries = new MercenaryService(MercenaryContracts, MercenaryGuards, BankLoans, BankRequests, Bank, () => ServerConfiguration);
        MercenaryHiring = new MercenaryHiring(MercenaryContracts, MercenaryGuards, BankRequests, Bank, SnapshotRequests,
            SnapshotTransactions, new MercenarySnapshotCommitter(SnapshotCommits, SnapshotStore, BankLoans, MercenaryContracts, MercenaryGuards, SnapshotPostUploads),
            () => ServerConfiguration);
        Support = new SupportService(Ledger, SnapshotRequests, OnlinePresence, supportClosures);
        SupportSending = new SupportSending(Ledger, SnapshotStore, SnapshotRequests, SnapshotTransactions, PendingConfirmations,
            OnlinePresence, EventNotifications, new SupportSnapshotCommitter(SnapshotCommits, SnapshotStore, BankLoans, Ledger,
                MercenaryContracts, EventNotifications, SnapshotPostUploads));
        TransferPackages = new ItemTransferPackages(PawnPackages, ThingPackages);
        DeliveryTargets = new DeliveryTargetResolver(SnapshotStore);
        Gifts = new GiftService(Ledger, SnapshotStore, BankLoans, MercenaryContracts, SnapshotCommits,
            SnapshotRequests, SnapshotTransactions, PendingConfirmations, TransferPackages, DeliveryTargets,
            OnlinePresence, EventNotifications, DiplomacyRelations, raidReconciliation,
            SnapshotPostUploads, () => ServerConfiguration);
        TradeDelivery = new TradeDelivery(SnapshotStore, OnlinePresence, Ledger, Plugins, CompatibilityBaseline,
            WorldConfiguration, WorldTileDistanceCalculator, () => ServerConfiguration);
        TradeLifecycle = new TradeLifecycle(Ledger, SnapshotStore, SnapshotMutations, RaidSettlementSnapshotMutationGate,
            TradeDelivery, AdminBaseline, OnlinePresence, EventNotifications, RuntimeLogger, () => ServerConfiguration);
        var tradeCommits = new TradeSnapshotCommitter(SnapshotCommits, SnapshotStore, BankLoans, Ledger, MercenaryContracts,
            EventNotifications, SnapshotPostUploads, EventAchievements);
        Trades = new TradeService(Ledger, SnapshotRequests, ColonyAuthorization, SnapshotMutations, SnapshotTransactions,
            PendingConfirmations, TransferPackages, tradeCommits, TradeLifecycle, TradeDelivery, AdminBaseline,
            Plugins, CompatibilityBaseline, OnlinePresence, EventNotifications, RuntimeLogger, () => ServerConfiguration);
        TradeMarket = new TradeMarket(Ledger, SnapshotRequests, TradeLifecycle, TradeDelivery, () => ServerConfiguration);
        var shopCommits = new ShopSnapshotCommitter(SnapshotCommits, SnapshotStore, BankLoans, ServerShop, MercenaryContracts, SnapshotPostUploads);
        Shop = new ShopService(ServerShop, SnapshotRequests, AdminAuthorization, SnapshotMutations, SnapshotTransactions,
            PendingConfirmations, shopCommits, AdminBaseline, Plugins, CompatibilityBaseline, RuntimeLogger, () => ServerConfiguration);
        EventDelivery = new EventDeliveryService(SnapshotAuthorization, SnapshotMutations, Ledger,
            EventNotifications, OnlinePresence, new SupportDeliveryReconciliation(Ledger, Players, EventNotifications));
        EventFailureReports = new EventFailureReports(ColonyAuthorization, SnapshotMutations, Ledger, TradeLifecycle, EventNotifications, OnlinePresence);
        SessionPresence = new SessionPresence(SessionConnections, () => RuntimeLogger);
        SessionNotifications = new SessionNotifications(EventNotifications, ChatNotifications, WorldConfigurationNotifications, PlayerOnlineNotifications);
        Chat = new ChatService(ColonyAuthorization, SnapshotMutations, ChatMessages, Players, ChatNotifications);
        Login = new LoginService(Accounts, EntryHandshakes, SnapshotMutations, RaidTimeouts, RaidQueries, LoginSessions,
            AuthTokens, WorldConfiguration, EventDelivery, PlayerActivity, EntryLifecycle, WorldMarkers, () => RuntimeLogger);
        WorldPreparation = new WorldSessionPreparation(Accounts, EntryHandshakes, SnapshotMutations, RaidTimeouts, RaidQueries,
            WorldConfiguration, new WorldColonySelection(Players, SnapshotStore, Ledger), PlayerActivity, EntryLifecycle);
        WorldRead = new WorldConfigurationRead(Accounts, WorldConfiguration, WorldDelivery);
        var colonyAccess = new ColonyLifecycleAccess(ColonyAuthorization, Players);
        ColonyDeletion = new ColonyDeletion(SnapshotStore, Ledger, ColonyDeletions, SnapshotCommits, SnapshotMutations,
            WorldConfiguration, Plugins, CompatibilityBaseline, RuntimeWorldObjectMarkers, DiplomacyRelations, BankLoans,
            Players, RaidPreparations, RaidProtectionActivations, MercenaryContracts, SessionConnections, EventNotifications, WorldChanges);
        ColonyAbandonment = new ColonyAbandonment(colonyAccess, SnapshotMutations, SnapshotStore, ColonyDeletion,
            WorldConfiguration, WorldDelivery, WorldMarkers, () => RuntimeLogger);
        ColonyRegistration = new ColonySiteRegistration(colonyAccess, SnapshotMutations, WorldConfiguration,
            CompatibilityBaseline, Plugins, WorldTileDistanceCalculator, PlayerActivity, WorldDelivery, WorldMarkers, WorldChanges, () => RuntimeLogger);
        ColonyRelocation = new ColonyRelocation(colonyAccess, SnapshotMutations, SnapshotStore, WorldConfiguration, ColonyLocations,
            WorldTileDistanceCalculator, RaidMarkers, Ledger, Plugins, CompatibilityBaseline, AchievementAwards,
            WorldDelivery, WorldMarkers, WorldChanges, () => RuntimeLogger);
        WorldSync = new WorldSynchronization(ColonyAuthorization, SnapshotMutations, RaidTimeouts, RuntimeWorldObjectMarkers, WorldMarkers);
        WorldSubmission = new WorldConfigurationSubmission(AdminAuthorization, CompatibilityBaseline, Plugins, SnapshotMutations,
            WorldConfiguration, PlayerActivity, WorldDelivery, () => RuntimeLogger);
        ConfigurationChanges = new AdminConfigurationChanges(ConfigurationState, ServerConfigurationOverrides, CompatibilityBaseline, AdminControl, SnapshotMutations);
        var administratorPlayers = new AdministratorPlayers(AdminControl, SessionConnections, EventNotifications, Players,
            SnapshotStore, ColonyDeletion, OfflineAccounts);
        var administratorBroadcasts = new AdministratorBroadcasts(Ledger, Players, OnlinePresence, EventNotifications);
        AdministratorActions = new AdministratorActions(AdminAuthorization, SnapshotMutations, AdminControl, administratorPlayers,
            administratorBroadcasts, WorldConfiguration, WorldChanges, () => RuntimeLogger);
        AdministratorConfiguration = new AdministratorConfiguration(AdminAuthorization, SnapshotMutations, ConfigurationChanges,
            WorldConfiguration, Players, OnlinePresence, AdminControl, () => RuntimeLogger);
        CompatibilityOverride = new CompatibilityBaselineOverride(Accounts, WorldConfiguration, CompatibilityBaseline,
            CompatibilityHandshake, SnapshotMutations, () => RuntimeLogger);
        AdministratorBaseline = new AdministratorBaseline(AdminAuthorization, WorldConfiguration, CompatibilityBaseline, Plugins,
            AdminBaseline, TradeLifecycle, SnapshotMutations, PlayerActivity);
        WorldFeatureNames = new WorldFeatureNames(Accounts, WorldConfiguration, WorldDelivery, WorldChanges, () => RuntimeLogger);
        WorldSubstrate = new WorldSubstrateTransfer(Accounts, SessionPrincipals, WorldConfiguration, WorldDelivery, WorldChanges, () => RuntimeLogger);
        RaidEvidenceConfirmation = new RaidEvidenceConfirmation(Ledger, SnapshotPostUploadJobs, RaidEvidenceScheduling,
            SnapshotMutations, SnapshotRequests, ColonyAuthorization, SnapshotTransactions, PendingConfirmations,
            SnapshotPostUploads, () => RuntimeLogger);
        SnapshotReception = new SnapshotReceptionService(SnapshotTransactions, SnapshotMutations, SnapshotCommits,
            SnapshotStore, BankLoans, MercenaryContracts, SnapshotPostUploads, Ledger, EventAchievements,
            SnapshotConfirmations, MercenaryIncidents);
        EventConfirmations = new EventConfirmations(ColonyAuthorization, SnapshotReception, SnapshotCommits,
            PendingConfirmations, RaidEvidenceConfirmation);
        SnapshotPostUploadBackground = new(new SnapshotPostUploadWorker(SnapshotPostUploadJobs, () => RuntimeLogger),
            SnapshotPostUploads.ResolveWork, SnapshotMutations, ColonyDeletions, WorldConfiguration, SnapshotCommits,
            Ledger, RaidTimeouts, () => RuntimeLogger);
        ConfigurationChanges.Recover();
    }

    private SnapshotRecovery CreateSnapshotRecovery() => new(ColonyDeletions, ConfigurationChanges,
        WorldLayerNotifications, TradeLifecycle, ColonyDeletion, DiplomacyPublications, SnapshotCommits, SnapshotStore,
        BankLoans, SnapshotPostUploads, Ledger, EventNotifications, EventAchievements, ServerShop, MercenaryContracts,
        MercenaryGuards, Players, SnapshotConfirmations, MercenaryIncidents);

    public IAuthoritativeEventLedger Ledger { get; }
    internal Modules.Raids.RaidQueries RaidQueries { get; }
    internal Modules.Raids.RaidProtection RaidProtection { get; }
    internal RaidPreparation RaidPreparation { get; }
    internal RaidStarting RaidStarting { get; }
    internal RaidSettlement RaidSettlement { get; }
    internal RaidSettlementWork RaidSettlementWork { get; }
    internal RaidEvidenceScheduling RaidEvidenceScheduling { get; }
    internal RaidEvidenceConfirmation RaidEvidenceConfirmation { get; }
    internal RaidOfflineEvidence RaidOfflineEvidence { get; }
    internal RaidBattleCleanup RaidBattleCleanup { get; }
    internal RaidTimeouts RaidTimeouts { get; }

    internal ColonyAccessAuthorization ColonyAuthorization { get; }
    internal ColonySnapshotAuthorization SnapshotAuthorization { get; }
    internal EventDeliveryService EventDelivery { get; }
    internal EventFailureReports EventFailureReports { get; }
    internal EventConfirmations EventConfirmations { get; }
    internal SessionPrincipalAuthorization SessionPrincipals { get; }
    internal SnapshotRequestAuthorization SnapshotRequests { get; }
    internal AdministratorAuthorization AdminAuthorization { get; }
    internal AdministratorBaseline AdministratorBaseline { get; }
    internal ShopService Shop { get; }
    internal SnapshotTransactions SnapshotTransactions { get; }
    internal SnapshotReceptionService SnapshotReception { get; }
    internal PendingConfirmationReconciler PendingConfirmations { get; }
    internal BankRequestAccess BankRequests { get; }
    internal BankService Bank { get; }
    internal MercenaryService Mercenaries { get; }
    internal MercenaryHiring MercenaryHiring { get; }
    internal MercenaryIncidentRecovery MercenaryIncidents { get; }
    internal ItemTransferPackages TransferPackages { get; }
    internal DeliveryTargetResolver DeliveryTargets { get; }
    internal GiftService Gifts { get; }
    internal SupportService Support { get; }
    internal DiplomacyService Diplomacy { get; }
    internal DiplomacyPublications DiplomacyPublications { get; }
    internal SupportSending SupportSending { get; }
    internal SupportSnapshotDeaths SupportDeaths { get; }
    internal TradeService Trades { get; }
    internal TradeMarket TradeMarket { get; }
    internal TradeLifecycle TradeLifecycle { get; }
    internal TradeDelivery TradeDelivery { get; }

    public IColonySnapshotIndexStore SnapshotStore { get; }

    public object RaidSettlementSnapshotMutationGate { get; } = new();
    internal SnapshotMutationCoordinator SnapshotMutations { get; }
    internal SnapshotMutationScope EnterSnapshotMutation([System.Runtime.CompilerServices.CallerMemberName] string operation = "")
        => SnapshotMutations.EnterUnrecovered(operation);
    public SnapshotCommitService SnapshotCommits { get; }
    internal SnapshotConfirmationPlanner SnapshotConfirmations { get; }
    internal SnapshotPostUploads SnapshotPostUploads { get; }
    internal SnapshotPostUploadBackground SnapshotPostUploadBackground { get; }
    public ColonyDeletionService ColonyDeletions { get; }

    public SnapshotUploadPolicy SnapshotUploadPolicy { get; }

    public EventNotificationHub EventNotifications { get; }

    public EventNotificationHub ChatNotifications { get; }

    public EventNotificationHub WorldConfigurationNotifications { get; }

    public PlayerOnlineNotificationHub PlayerOnlineNotifications { get; }

    public OnlinePresenceRegistry OnlinePresence { get; }

    public LoginSessionRegistry LoginSessions { get; }

    public PlayerRegistry Players { get; }
    internal WorldConfigurationDelivery WorldDelivery { get; }
    internal PlayerSnapshotWealth PlayerWealth { get; }
    internal PlayerDirectory PlayerDirectory { get; }
    internal AchievementQueries AchievementQueries { get; }
    internal RaidMarkerProjection RaidMarkers { get; }
    internal WorldMarkerDelivery WorldMarkers { get; }
    internal LoginService Login { get; }
    internal WorldSessionPreparation WorldPreparation { get; }
    internal WorldConfigurationRead WorldRead { get; }
    internal WorldConfigurationChanges WorldChanges { get; }
    internal WorldLayerNotifications WorldLayerNotifications { get; }
    internal SnapshotWorldLayers WorldLayers { get; }
    internal SnapshotColonyLocations ColonyLocations { get; }
    internal SnapshotColonyProjection ColonyProjection { get; }
    internal ColonySiteRegistration ColonyRegistration { get; }
    internal ColonyRelocation ColonyRelocation { get; }
    internal ColonyDeletion ColonyDeletion { get; }
    internal ColonyAbandonment ColonyAbandonment { get; }
    internal WorldSynchronization WorldSync { get; }
    internal WorldConfigurationSubmission WorldSubmission { get; }
    internal WorldFeatureNames WorldFeatureNames { get; }
    internal WorldSubstrateTransfer WorldSubstrate { get; }
    internal SessionPresence SessionPresence { get; }
    internal SessionNotifications SessionNotifications { get; }
    internal ChatService Chat { get; }
    internal ClientEntryLifecycle EntryLifecycle { get; }
    internal PlayerSessionActivity PlayerActivity { get; }
    internal SessionConnections SessionConnections { get; }

    internal ServerConfigurationState ConfigurationState { get; }
    internal AdminConfigurationChanges ConfigurationChanges { get; }
    internal AdministratorConfiguration AdministratorConfiguration { get; }
    internal AdministratorActions AdministratorActions { get; }
    internal CompatibilityBaselineOverride CompatibilityOverride { get; }
    public ClashOfRimServerConfiguration ServerConfiguration => ConfigurationState.Current;
    public ClashOfRimServerConfiguration UpdateServerConfiguration(ClashOfRimServerConfiguration configuration)
        => ConfigurationState.Replace(configuration);

    public IWorldTileDistanceCalculator WorldTileDistanceCalculator { get; }

    public WorldConfigurationRegistry WorldConfiguration { get; }

    public CompatibilityBaselineRegistry CompatibilityBaseline { get; }

    public AdminBaselineRegistry AdminBaseline { get; }

    public RuntimeWorldObjectMarkerRegistry RuntimeWorldObjectMarkers { get; }

    public DiplomacyRelationRegistry DiplomacyRelations { get; }

    public AuthTokenRegistry AuthTokens { get; }

    public AdminControlRegistry AdminControl { get; }

    public ServerConfigurationRegistry ServerConfigurationOverrides { get; }

    public ISteamAuthTicketValidator SteamAuthTickets { get; }

    public OfflineAccountRegistry OfflineAccounts { get; }

    internal AccountService Accounts { get; }

    internal CompatibilityHandshakeService CompatibilityHandshake { get; }

    internal EntryHandshakePolicy EntryHandshakes { get; }

    public PawnPackageRegistry PawnPackages { get; }

    public ThingPackageRegistry ThingPackages { get; }

    public RaidPreparationRegistry RaidPreparations { get; }

    public RaidProtectionActivationRegistry RaidProtectionActivations { get; }

    public RaidCooldownOverrideRegistry RaidCooldownOverrides { get; }

    public BankLoanRegistry BankLoans { get; }

    public MercenaryContractRegistry MercenaryContracts { get; }

    public MercenaryGuardContractRegistry MercenaryGuards { get; }

    public ChatMessageRegistry ChatMessages { get; }

    public ServerShopRegistry ServerShop { get; }

    public AchievementRegistry Achievements { get; }
    internal AchievementAwards AchievementAwards { get; }
    internal EventAchievementRecorder EventAchievements { get; }
    internal SnapshotAchievements SnapshotAchievements { get; }

    public SnapshotPostUploadJobRegistry SnapshotPostUploadJobs { get; }

    public ISnapshotPostUploadArtifactStore SnapshotPostUploadArtifacts { get; }

    public ServerPluginRegistry Plugins { get; }

    public ILogger RuntimeLogger { get; private set; }

    public void SetRuntimeLogger(ILogger runtimeLogger)
    {
        RuntimeLogger = runtimeLogger ?? NullLogger.Instance;
    }
}
