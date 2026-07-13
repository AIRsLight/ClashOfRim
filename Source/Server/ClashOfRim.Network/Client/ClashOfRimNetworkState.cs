using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network.Plugins;
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
        ILogger? runtimeLogger = null)
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
    }

    public IAuthoritativeEventLedger Ledger { get; }

    public IColonySnapshotIndexStore SnapshotStore { get; }

    public object RaidSettlementSnapshotMutationGate { get; } = new();

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
