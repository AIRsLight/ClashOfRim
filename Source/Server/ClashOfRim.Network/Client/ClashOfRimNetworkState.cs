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
        this.serverConfiguration = serverConfiguration ?? new ClashOfRimServerConfiguration();
        WorldTileDistanceCalculator = worldTileDistanceCalculator ?? new StraightLineWorldTileDistanceCalculator();
        WorldConfiguration = worldConfigurationRegistry ?? new WorldConfigurationRegistry(WorldConfigurationExtensionService.Empty);
        CompatibilityBaseline = compatibilityBaselineRegistry ?? new CompatibilityBaselineRegistry();
        AdminBaseline = adminBaselineRegistry ?? new AdminBaselineRegistry();
        RuntimeWorldObjectMarkers = runtimeWorldObjectMarkers ?? new RuntimeWorldObjectMarkerRegistry();
        DiplomacyRelations = diplomacyRelations ?? new DiplomacyRelationRegistry();
        AuthTokens = authTokens ?? new AuthTokenRegistry();
        AdminControl = adminControl ?? new AdminControlRegistry();
        ServerConfigurationOverrides = serverConfigurationOverrides ?? new ServerConfigurationRegistry();
        SteamAuthTickets = steamAuthTickets ?? new DevelopmentSteamAuthTicketValidator();
        OfflineAccounts = offlineAccounts ?? new OfflineAccountRegistry();
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
        SnapshotCommits = snapshotCommits ?? new SnapshotCommitService();
        ColonyDeletions = colonyDeletions ?? new ColonyDeletionService();
        ColonyAuthorization = new ColonyAccessAuthorization(AuthTokens, LoginSessions, DiplomacyRelations,
            RaidPreparations, Ledger, () => ServerConfiguration.PvpEnabled);
        SnapshotAuthorization = new ColonySnapshotAuthorization(ColonyAuthorization, SnapshotStore);
        var recovery = new LegacySnapshotRecoveryAdapter(this);
        SnapshotMutations = new SnapshotMutationCoordinator(RaidSettlementSnapshotMutationGate, recovery);
        SnapshotPrincipals = new SnapshotPrincipalAuthorization(AuthTokens, LoginSessions);
        AdminAuthorization = new AdministratorAuthorization(AuthTokens, LoginSessions, WorldConfiguration);
        SnapshotRequests = new SnapshotRequestAuthorization(SnapshotPrincipals, ColonyAuthorization, SnapshotMutations);
        SnapshotTransactions = new SnapshotTransactions(SnapshotStore, SnapshotUploadPolicy, Plugins, CompatibilityBaseline);
        PendingConfirmations = new PendingConfirmationReconciler(BankLoans, MercenaryContracts,
            () => ServerConfiguration, RaidSettlementSnapshotMutationGate);
        BankRequests = new BankRequestAccess(ColonyAuthorization, SnapshotMutations, SnapshotStore, BankLoans, PendingConfirmations);
        Bank = new BankService(BankLoans, MercenaryContracts, SnapshotStore, SnapshotCommits,
            BankRequests, SnapshotRequests, SnapshotTransactions, recovery, () => ServerConfiguration);
        TransferPackages = new ItemTransferPackages(PawnPackages, ThingPackages);
        DeliveryTargets = new DeliveryTargetResolver(SnapshotStore);
        Gifts = new GiftService(Ledger, SnapshotStore, BankLoans, MercenaryContracts, SnapshotCommits,
            SnapshotRequests, SnapshotTransactions, PendingConfirmations, TransferPackages, DeliveryTargets,
            OnlinePresence, EventNotifications, DiplomacyRelations, new LegacyRaidDeliveryProtection(this),
            recovery, () => ServerConfiguration);
        TradeDelivery = new TradeDelivery(SnapshotStore, OnlinePresence, Ledger, Plugins, CompatibilityBaseline,
            WorldConfiguration, WorldTileDistanceCalculator, () => ServerConfiguration);
        TradeLifecycle = new TradeLifecycle(Ledger, SnapshotStore, SnapshotMutations, RaidSettlementSnapshotMutationGate,
            TradeDelivery, AdminBaseline, OnlinePresence, EventNotifications, RuntimeLogger, () => ServerConfiguration);
        var tradeCommits = new TradeSnapshotCommitter(SnapshotCommits, SnapshotStore, BankLoans, Ledger, MercenaryContracts,
            EventNotifications, recovery, new LegacyEventAchievementRecorder(this));
        Trades = new TradeService(Ledger, SnapshotRequests, ColonyAuthorization, SnapshotMutations, SnapshotTransactions,
            PendingConfirmations, TransferPackages, tradeCommits, TradeLifecycle, TradeDelivery, AdminBaseline,
            Plugins, CompatibilityBaseline, OnlinePresence, EventNotifications, RuntimeLogger, () => ServerConfiguration);
        TradeMarket = new TradeMarket(Ledger, SnapshotRequests, TradeLifecycle, TradeDelivery, () => ServerConfiguration);
        var shopCommits = new ShopSnapshotCommitter(SnapshotCommits, SnapshotStore, BankLoans, ServerShop, MercenaryContracts, recovery);
        Shop = new ShopService(ServerShop, SnapshotRequests, AdminAuthorization, SnapshotMutations, SnapshotTransactions,
            PendingConfirmations, shopCommits, AdminBaseline, Plugins, CompatibilityBaseline, RuntimeLogger, () => ServerConfiguration);
        EventDelivery = new EventDeliveryService(SnapshotAuthorization, SnapshotMutations, Ledger,
            EventNotifications, OnlinePresence, Players);
    }

    public IAuthoritativeEventLedger Ledger { get; }

    internal ColonyAccessAuthorization ColonyAuthorization { get; }
    internal ColonySnapshotAuthorization SnapshotAuthorization { get; }
    internal EventDeliveryService EventDelivery { get; }
    internal SnapshotPrincipalAuthorization SnapshotPrincipals { get; }
    internal SnapshotRequestAuthorization SnapshotRequests { get; }
    internal AdministratorAuthorization AdminAuthorization { get; }
    internal ShopService Shop { get; }
    internal SnapshotTransactions SnapshotTransactions { get; }
    internal PendingConfirmationReconciler PendingConfirmations { get; }
    internal BankRequestAccess BankRequests { get; }
    internal BankService Bank { get; }
    internal ItemTransferPackages TransferPackages { get; }
    internal DeliveryTargetResolver DeliveryTargets { get; }
    internal GiftService Gifts { get; }
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
    public ColonyDeletionService ColonyDeletions { get; }

    public SnapshotUploadPolicy SnapshotUploadPolicy { get; }

    public EventNotificationHub EventNotifications { get; }

    public EventNotificationHub ChatNotifications { get; }

    public EventNotificationHub WorldConfigurationNotifications { get; }

    public PlayerOnlineNotificationHub PlayerOnlineNotifications { get; }

    public OnlinePresenceRegistry OnlinePresence { get; }

    public LoginSessionRegistry LoginSessions { get; }

    public PlayerRegistry Players { get; }

    private readonly object configurationGate = new();
    private ClashOfRimServerConfiguration serverConfiguration;

    public ClashOfRimServerConfiguration ServerConfiguration
    {
        get
        {
            lock (configurationGate)
            {
                return serverConfiguration;
            }
        }
    }

    public ClashOfRimServerConfiguration UpdateServerConfiguration(ClashOfRimServerConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        lock (configurationGate)
        {
            serverConfiguration = configuration;
            return serverConfiguration;
        }
    }

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

    public SnapshotPostUploadJobRegistry SnapshotPostUploadJobs { get; }

    public ISnapshotPostUploadArtifactStore SnapshotPostUploadArtifacts { get; }

    public ServerPluginRegistry Plugins { get; }

    public ILogger RuntimeLogger { get; private set; }

    public void SetRuntimeLogger(ILogger runtimeLogger)
    {
        RuntimeLogger = runtimeLogger ?? NullLogger.Instance;
    }
}
