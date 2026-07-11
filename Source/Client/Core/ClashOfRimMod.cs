using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
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
    private static readonly TimeSpan RaidBattleDuration = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan RaidSettlementGraceDuration = TimeSpan.FromMinutes(2);

    private readonly ClashOfRimSettings settings;
    private readonly object syncStateGate = new();
    private bool snapshotUploadInProgress;
    private bool manualSyncInProgress;
    private string snapshotUploadTransactionOwner = string.Empty;
    private long snapshotUploadTransactionSequence;
    private DateTime snapshotUploadTransactionStartedAtUtc;
    private bool worldMapMarkerSyncInProgress;
    private bool runtimeWorldObjectSyncInProgress;
    private bool playerColonySiteRegistrationInProgress;
    private bool worldConfigurationExtensionSyncInProgress;
    private bool abandonPlayerColonyInProgress;
    private bool playerColonySiteRegistrationSuppressed;
    private bool caravanArrivalTargetRefreshInProgress;
    private bool bankInProgress;
    private bool mercenaryInProgress;
    private bool serverShopInProgress;
    private bool adminInProgress;
    private bool isAdministrator;
    private string? lastRegisteredPlayerColonySiteSignature;
    private string? lastSyncedWorldConfigurationExtensionSignature;
    private bool statusDefaultsInitialized;
    private string loginStatus = string.Empty;
    private string eventQueueStatus = string.Empty;
    private string eventDetailsStatus = string.Empty;
    private string giftProcessingStatus = string.Empty;
    private string snapshotUploadStatus = string.Empty;
    private string presenceStatus = string.Empty;
    private string playerListStatus = string.Empty;
    private string tradeStatus = string.Empty;
    private string worldMapStatus = string.Empty;
    private string bankStatus = string.Empty;
    private string mercenaryStatus = string.Empty;
    private string serverShopStatus = string.Empty;
    private string chatStatus = string.Empty;
    private string adminStatus = string.Empty;
    private string serverCompatibilityManifestJson = string.Empty;
    private bool tradeMarketplaceEnabled = true;
    private bool giftsEnabled = true;
    private bool pvpEnabled = true;
    private const int TradeOrdersPageSize = 10;
    private const int TradeOrdersHistoryPageSize = 5;
    private bool tradeOrdersHasMore;
    private bool tradeOrdersPageLoadInProgress;
    private int tradeOrdersTotalCount;
    private string tradeOrdersScope = "Open";
    private ModBankStatusResponseDto? lastBankStatus;
    private ModAdminStatusResponseDto? lastAdminStatus;
    private bool localAtomicMutationPending;
    private string localAtomicMutationOperation = string.Empty;
    private string localAtomicMutationStatus = string.Empty;
    private bool pendingInitialWorldConfigurationSubmit;
    private ModWorldConfigurationDto? pendingServerWorldConfiguration;
    private WorldSubstratePackage? pendingServerWorldSubstrate;
    private string? lastSessionId;
    private long lastNotificationVersion;
    private long lastWorldConfigurationVersion;
    private bool presenceInProgress;
    private bool automaticEventRefreshInProgress;
    private bool automaticEventRefreshQueued;
    private bool chatRefreshInProgress;
    private bool chatSendInProgress;
    private bool sessionExpiredHandling;
    private bool blockAutomaticMapSessionForServerEntrySourceGame;
    private CancellationTokenSource? presenceCancellation;
    private Game? serverEntrySourceGame;
    private readonly List<string> lastEventQueueEventIds = new();
    private readonly List<ModEventDetailDto> lastEventDetails = new();
    private readonly List<ModPlayerSummaryDto> lastPlayers = new();
    private int playersSnapshotVersion;
    private readonly List<ModAchievementLeaderboardDto> lastAchievementLeaderboards = new();
    private readonly List<ModAchievementSummaryDto> lastOwnAchievements = new();
    private int achievementLeaderboardsSnapshotVersion;
    private string achievementTargetUserId = string.Empty;
    private string achievementTargetColonyId = string.Empty;
    private string achievementStatus = string.Empty;
    private readonly List<ModTradeOrderSummaryDto> lastTradeOrders = new();
    private int tradeOrdersSnapshotVersion;
    private readonly List<ModServerShopListingDto> lastServerShopListings = new();
    private int serverShopListingsSnapshotVersion;
    private readonly List<ModChatMessageDto> lastChatMessages = new();
    private readonly List<ModWorldMapMarkerDto> lastWorldMapMarkers = new();
    private readonly Dictionary<string, ModPlayerColonySiteDto> occupiedPlayerColonySites = new(StringComparer.Ordinal);
    private readonly List<string> pendingGiftConfirmationEventIds = new();
    private readonly Dictionary<string, ModEventReferenceDto> lastEventReferences = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> lastEventReferenceGroups = new(StringComparer.Ordinal);
    private readonly HashSet<string> postedEventLetterIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> appliedServerNotificationSideEffectIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> appliedDiplomacyEventSideEffectIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> finalDeadlineRaidCleanupEventIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> submittedWorldFeatureNameCatalogKeys = new(StringComparer.Ordinal);
    private readonly object automaticEventRefreshLock = new();
    private readonly object eventStateLock = new();
    private readonly object chatStateLock = new();
    private readonly object colonySiteStateLock = new();
    private long lastChatSequence;
    private long lastReadPrivateChatSequence;
    private int chatMessagesSnapshotVersion;
    private int unreadPrivateChatCount;

    internal static ClashOfRimMod? Instance { get; private set; }

    public ClashOfRimMod(ModContentPack content)
        : base(content)
    {
        settings = GetSettings<ClashOfRimSettings>();
        if (!string.Equals(content.PackageId, PackageId, StringComparison.OrdinalIgnoreCase))
        {
            Log.Warning("[ClashOfRim] Ignored ClashOfRim mod class loaded from unexpected package: " + (content.PackageId ?? "<null>") + ".");
            return;
        }

        Instance = this;
        var harmony = new Harmony(PackageId);
        ClashOfRimCompatibilityApi.BeginRegistrationCycle(PackageId);
        BuiltInClientCompatibility.Apply(harmony);
        harmony.PatchAll();
        ClashOfRimModNetworkClient.SessionExpired = (message, authToken) => HandleSessionExpired(message, authToken, observedSessionId: null);
        ClashOfRimModNetworkClient.CompatibilityManifestJsonProvider = BuildCompatibilityManifestJsonForLogin;
        ClashOfRimModNetworkClient.CompatibilityManifestIdProvider = BuildCompatibilityManifestIdForLogin;
        ClashOfRimModNetworkClient.CompatibilityManifestSummaryJsonProvider = BuildCompatibilityManifestSummaryJsonForLogin;
        ClashOfRimModNetworkClient.CompatibilityManifestJsonForPackagesProvider = BuildCompatibilityManifestJsonForPackages;
        ClashLog.Message("[ClashOfRim] Harmony patches applied.");
    }

    private void EnsureStatusDefaultsInitialized()
    {
        if (statusDefaultsInitialized)
        {
            return;
        }

        statusDefaultsInitialized = true;
        loginStatus = ClashOfRimText.Key("ClashOfRim.Status.NotLoggedIn");
        eventQueueStatus = ClashOfRimText.Key("ClashOfRim.Status.EventQueueNotPulled");
        eventDetailsStatus = ClashOfRimText.Key("ClashOfRim.Status.EventDetailsNotPulled");
        giftProcessingStatus = ClashOfRimText.Key("ClashOfRim.Status.GiftNotProcessed");
        snapshotUploadStatus = ClashOfRimText.Key("ClashOfRim.Status.SnapshotUploadNotStarted");
        presenceStatus = ClashOfRimText.Key("ClashOfRim.Status.PresenceNotStarted");
        playerListStatus = ClashOfRimText.Key("ClashOfRim.Status.PlayerListNotPulled");
        tradeStatus = ClashOfRimText.Key("ClashOfRim.Status.TradeOrderNotCreated");
        worldMapStatus = ClashOfRimText.Key("ClashOfRim.Status.WorldMapNotSynced");
        bankStatus = ClashOfRimText.Key("ClashOfRim.Status.BankNotPulled");
        mercenaryStatus = ClashOfRimText.Key("ClashOfRim.Status.MercenaryIdle");
        serverShopStatus = ClashOfRimText.Key("ClashOfRim.Shop.StatusIdle");
        chatStatus = ClashOfRimText.Key("ClashOfRim.Chat.StatusIdle");
        adminStatus = ClashOfRimText.Key("ClashOfRim.Admin.StatusIdle");
    }

    internal bool SnapshotUploadInProgress
    {
        get
        {
            lock (syncStateGate)
            {
                return snapshotUploadInProgress;
            }
        }
    }

    internal bool ManualSyncInProgress
    {
        get
        {
            lock (syncStateGate)
            {
                return manualSyncInProgress;
            }
        }
    }

    internal bool IsServerWorldSetupFlow => pendingInitialWorldConfigurationSubmit
        || pendingServerWorldConfiguration is not null;

    internal bool LocalAtomicMutationPending => localAtomicMutationPending;

    internal string LocalAtomicMutationOperation => localAtomicMutationOperation;

    internal bool MercenaryInProgress => mercenaryInProgress;

    internal bool ServerShopInProgress => serverShopInProgress;

    internal bool PresenceInProgress => presenceInProgress;

    internal bool ChatInProgress => chatRefreshInProgress || chatSendInProgress;

    internal bool IsAdministrator => isAdministrator;

    internal bool CanUseDeveloperTools => Current.ProgramState != ProgramState.Playing
        || !settings.IsConfigured
        || isAdministrator;

    internal bool CanEditModSettings => !settings.IsConfigured
        || isAdministrator;

    internal string ServerCompatibilityManifestJson => serverCompatibilityManifestJson;

    internal bool AdminInProgress => adminInProgress;

    internal bool TryBeginSnapshotUploadTransaction(
        bool allowExistingManualSync = false,
        [CallerMemberName] string callerMember = "",
        [CallerFilePath] string callerFile = "",
        [CallerLineNumber] int callerLine = 0)
    {
        lock (syncStateGate)
        {
            if (snapshotUploadInProgress || (manualSyncInProgress && !allowExistingManualSync))
            {
                Log.Warning("[ClashOfRim][SnapshotTxn] begin rejected: requested="
                    + FormatSnapshotUploadTransactionCaller(callerMember, callerFile, callerLine)
                    + ", allowExistingManualSync="
                    + allowExistingManualSync
                    + ", snapshotUploadInProgress="
                    + snapshotUploadInProgress
                    + ", manualSyncInProgress="
                    + manualSyncInProgress
                    + ", owner="
                    + (string.IsNullOrWhiteSpace(snapshotUploadTransactionOwner) ? "<none>" : snapshotUploadTransactionOwner)
                    + ", seq="
                    + snapshotUploadTransactionSequence
                    + ", age="
                    + FormatSnapshotUploadTransactionAge());
                return false;
            }

            manualSyncInProgress = true;
            snapshotUploadInProgress = true;
            snapshotUploadTransactionSequence++;
            snapshotUploadTransactionOwner = FormatSnapshotUploadTransactionCaller(callerMember, callerFile, callerLine);
            snapshotUploadTransactionStartedAtUtc = DateTime.UtcNow;
            ClashLog.Message("[ClashOfRim][SnapshotTxn] begin: owner="
                + snapshotUploadTransactionOwner
                + ", seq="
                + snapshotUploadTransactionSequence
                + ", allowExistingManualSync="
                + allowExistingManualSync
                + ".");
            NotifyPlayerMessage(
                ClashOfRimText.Key("ClashOfRim.SnapshotUpload.Uploading"),
                MessageTypeDefOf.NeutralEvent);
            return true;
        }
    }

    internal void EndSnapshotUploadTransaction()
    {
        lock (syncStateGate)
        {
            ClashLog.Message("[ClashOfRim][SnapshotTxn] end: owner="
                + (string.IsNullOrWhiteSpace(snapshotUploadTransactionOwner) ? "<none>" : snapshotUploadTransactionOwner)
                + ", seq="
                + snapshotUploadTransactionSequence
                + ", snapshotUploadInProgress="
                + snapshotUploadInProgress
                + ", manualSyncInProgress="
                + manualSyncInProgress
                + ", age="
                + FormatSnapshotUploadTransactionAge()
                + ".");
            snapshotUploadInProgress = false;
            manualSyncInProgress = false;
            snapshotUploadTransactionOwner = string.Empty;
            snapshotUploadTransactionStartedAtUtc = default;
        }
    }

    private static string FormatSnapshotUploadTransactionCaller(string callerMember, string callerFile, int callerLine)
    {
        string fileName = string.IsNullOrWhiteSpace(callerFile)
            ? "<unknown>"
            : Path.GetFileName(callerFile);
        return string.IsNullOrWhiteSpace(callerMember)
            ? fileName + ":" + callerLine.ToString(CultureInfo.InvariantCulture)
            : callerMember + "@" + fileName + ":" + callerLine.ToString(CultureInfo.InvariantCulture);
    }

    private string FormatSnapshotUploadTransactionAge()
    {
        if (snapshotUploadTransactionStartedAtUtc == default)
        {
            return "n/a";
        }

        TimeSpan age = DateTime.UtcNow - snapshotUploadTransactionStartedAtUtc;
        return age.TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture) + "s";
    }

    private static void NotifyPlayerMessage(string message, MessageTypeDef messageType, bool historical = false)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        EnqueueClashOfRimMainThreadAction(() =>
        {
            if (Current.ProgramState != ProgramState.Playing)
            {
                return;
            }

            Messages.Message(message, messageType, historical);
        });
    }

    internal bool IsAnyManualOrSnapshotSyncInProgress()
    {
        lock (syncStateGate)
        {
            return manualSyncInProgress || snapshotUploadInProgress;
        }
    }

    internal string CurrentUserId => settings.UserId;

    internal bool ShouldShowMultiplayerMainButton =>
        settings.IsConfigured
        && !string.IsNullOrWhiteSpace(lastSessionId);

    internal bool IsInActiveMultiplayerSession =>
        settings.IsConfigured
        && !string.IsNullOrWhiteSpace(settings.CurrentSnapshotId)
        && !string.IsNullOrWhiteSpace(lastSessionId)
        && !ClashOfRimGameComponent.HasActiveObservationSession;

    internal bool CanUseVanillaMenuSnapshotUpload =>
        settings.IsConfigured
        && !string.IsNullOrWhiteSpace(settings.CurrentSnapshotId)
        && !string.IsNullOrWhiteSpace(lastSessionId);

    internal bool ShouldInterceptVanillaAutosave =>
        settings.IsConfigured
        && !string.IsNullOrWhiteSpace(lastSessionId);

    internal bool CanStartAutomaticMapServerSession =>
        settings.IsConfigured
        && Current.ProgramState == ProgramState.Playing
        && Find.CurrentMap is not null
        && !manualSyncInProgress
        && !snapshotUploadInProgress
        && !IsBlockedServerEntrySourceGame()
        && !ClashOfRimGameComponent.HasActiveRaidBattleSession
        && !ClashOfRimGameComponent.HasActiveObservationSession
        && string.IsNullOrWhiteSpace(lastSessionId);

    internal bool CanSyncRuntimeWorldObjects =>
        settings.IsConfigured
        && !runtimeWorldObjectSyncInProgress
        && !ClashOfRimGameComponent.HasActiveRaidBattleSession
        && !ClashOfRimGameComponent.HasActiveObservationSession
        && !string.IsNullOrWhiteSpace(lastSessionId)
        && Find.WorldObjects is not null;

    internal bool CanRegisterPlayerColonySites =>
        settings.IsConfigured
        && !playerColonySiteRegistrationInProgress
        && !playerColonySiteRegistrationSuppressed
        && !localAtomicMutationPending
        && !ClashOfRimGameComponent.HasActiveRaidBattleSession
        && !ClashOfRimGameComponent.HasActiveObservationSession
        && !string.IsNullOrWhiteSpace(lastSessionId)
        && !string.IsNullOrWhiteSpace(settings.CurrentSnapshotId)
        && Current.ProgramState == ProgramState.Playing
        && Find.WorldObjects is not null
        && HasLoadedPlayerColonyContext();

    internal bool IsPlayerColonySiteRegistrationSuppressed => playerColonySiteRegistrationSuppressed;

    internal bool CanSyncWorldConfigurationExtensions =>
        settings.IsConfigured
        && ClashOfRimCompatibilityApi.HasWorldConfigurationExtensionCollector
        && !worldConfigurationExtensionSyncInProgress
        && !ClashOfRimGameComponent.HasActiveRaidBattleSession
        && !ClashOfRimGameComponent.HasActiveObservationSession
        && !string.IsNullOrWhiteSpace(lastSessionId)
        && !string.IsNullOrWhiteSpace(settings.CurrentSnapshotId)
        && Current.ProgramState == ProgramState.Playing
        && Find.World is not null;

    internal bool CanRefreshCaravanArrivalTargets =>
        settings.IsConfigured
        && !caravanArrivalTargetRefreshInProgress
        && !ClashOfRimGameComponent.HasActiveRaidBattleSession
        && !ClashOfRimGameComponent.HasActiveObservationSession
        && !string.IsNullOrWhiteSpace(lastSessionId)
        && !string.IsNullOrWhiteSpace(settings.CurrentSnapshotId)
        && Find.WorldObjects is not null;

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
            return eventQueueStatus;
        }
    }

    internal string EventDetailsStatus
    {
        get
        {
            EnsureStatusDefaultsInitialized();
            return eventDetailsStatus;
        }
    }

    internal string GiftProcessingStatus
    {
        get
        {
            EnsureStatusDefaultsInitialized();
            return giftProcessingStatus;
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

    internal string PlayerListStatus
    {
        get
        {
            EnsureStatusDefaultsInitialized();
            return playerListStatus;
        }
    }

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

    internal string ChatStatus
    {
        get
        {
            EnsureStatusDefaultsInitialized();
            return chatStatus;
        }
    }

    internal IReadOnlyList<ModChatMessageDto> ChatMessagesSnapshot
    {
        get
        {
            lock (chatStateLock)
            {
                return lastChatMessages.ToList();
            }
        }
    }

    internal int UnreadPrivateChatCount
    {
        get
        {
            lock (chatStateLock)
            {
                return unreadPrivateChatCount;
            }
        }
    }

    internal int ChatMessagesSnapshotVersion
    {
        get
        {
            lock (chatStateLock)
            {
                return chatMessagesSnapshotVersion;
            }
        }
    }

    internal string BankStatus
    {
        get
        {
            EnsureStatusDefaultsInitialized();
            return bankStatus;
        }
    }

    internal bool BankInProgress => bankInProgress;

    internal ModBankStatusResponseDto? BankStatusSnapshot => lastBankStatus;

    internal string AdminStatus
    {
        get
        {
            EnsureStatusDefaultsInitialized();
            return adminStatus;
        }
    }

    internal ModAdminStatusResponseDto? AdminStatusSnapshot => lastAdminStatus;

    internal string MercenaryStatus
    {
        get
        {
            EnsureStatusDefaultsInitialized();
            return mercenaryStatus;
        }
    }

    internal string ServerShopStatus
    {
        get
        {
            EnsureStatusDefaultsInitialized();
            return serverShopStatus;
        }
    }

    internal string UserId => settings.UserId;

    internal string ServerBaseUrl => settings.ServerBaseUrl;

    internal string ColonyId => settings.ColonyId;

    internal string CurrentSnapshotId => settings.CurrentSnapshotId;

    internal bool IsNetworkConfigured => settings.IsConfigured;

    internal bool TradeMarketplaceEnabled => tradeMarketplaceEnabled;

    internal bool TradeOrdersHasMore => tradeOrdersHasMore;

    internal bool TradeOrdersPageLoadInProgress => tradeOrdersPageLoadInProgress;

    internal int TradeOrdersTotalCount => tradeOrdersTotalCount;

    internal int TradeOrdersLoadedCount
    {
        get
        {
            lock (eventStateLock)
            {
                return lastTradeOrders.Count;
            }
        }
    }

    internal string TargetUserId => settings.TargetUserId;

    internal string TargetColonyId => settings.TargetColonyId;

    internal List<ModTradeOrderSummaryDto> TradeOrdersSnapshot
    {
        get
        {
            lock (eventStateLock)
            {
                return lastTradeOrders.ToList();
            }
        }
    }

    internal int TradeOrdersSnapshotVersion
    {
        get
        {
            lock (eventStateLock)
            {
                return tradeOrdersSnapshotVersion;
            }
        }
    }

    internal List<ModServerShopListingDto> ServerShopListingsSnapshot
    {
        get
        {
            lock (eventStateLock)
            {
                return lastServerShopListings.ToList();
            }
        }
    }

    internal int ServerShopListingsSnapshotVersion
    {
        get
        {
            lock (eventStateLock)
            {
                return serverShopListingsSnapshotVersion;
            }
        }
    }

    internal List<ModPlayerSummaryDto> PlayersSnapshot
    {
        get
        {
            lock (eventStateLock)
            {
                return lastPlayers.ToList();
            }
        }
    }

    internal int PlayersSnapshotVersion
    {
        get
        {
            lock (eventStateLock)
            {
                return playersSnapshotVersion;
            }
        }
    }

    internal List<ModAchievementLeaderboardDto> AchievementLeaderboardsSnapshot
    {
        get
        {
            lock (eventStateLock)
            {
                return lastAchievementLeaderboards.ToList();
            }
        }
    }

    internal List<ModAchievementSummaryDto> OwnAchievementsSnapshot
    {
        get
        {
            lock (eventStateLock)
            {
                return lastOwnAchievements.ToList();
            }
        }
    }

    internal int AchievementLeaderboardsSnapshotVersion
    {
        get
        {
            lock (eventStateLock)
            {
                return achievementLeaderboardsSnapshotVersion;
            }
        }
    }

    internal string AchievementTargetUserId
    {
        get
        {
            lock (eventStateLock)
            {
                return achievementTargetUserId;
            }
        }
    }

    internal string AchievementTargetColonyId
    {
        get
        {
            lock (eventStateLock)
            {
                return achievementTargetColonyId;
            }
        }
    }

    internal string AchievementStatus => achievementStatus;

    internal bool GiftsEnabled => giftsEnabled;

    internal bool PvpEnabled => pvpEnabled;

    internal List<ModWorldMapMarkerDto> WorldMapMarkersSnapshot
    {
        get
        {
            lock (eventStateLock)
            {
                return lastWorldMapMarkers.ToList();
            }
        }
    }

    private void BeginLocalAtomicMutation(string operation, string? status = null)
    {
        localAtomicMutationPending = true;
        localAtomicMutationOperation = operation ?? string.Empty;
        localAtomicMutationStatus = status ?? string.Empty;
    }

    private void CompleteLocalAtomicMutation()
    {
        ClearLocalAtomicMutation();
        ClearPendingUnconfirmedSnapshotFailure();
        StartQueuedAutomaticEventRefreshAfterLocalAtomicMutation();
    }

    private void ClearLocalAtomicMutation()
    {
        localAtomicMutationPending = false;
        localAtomicMutationOperation = string.Empty;
        localAtomicMutationStatus = string.Empty;
    }

    private bool TryRejectBlockedByLocalAtomicMutation(out string message)
    {
        if (!localAtomicMutationPending)
        {
            message = string.Empty;
            return false;
        }

        string operation = string.IsNullOrWhiteSpace(localAtomicMutationOperation)
            ? ClashOfRimText.Key("ClashOfRim.LocalAtomicMutation.DefaultOperation")
            : localAtomicMutationOperation;
        message = string.IsNullOrWhiteSpace(localAtomicMutationStatus)
            ? ClashOfRimText.Key("ClashOfRim.LocalAtomicMutation.Waiting", operation.Named("OPERATION"))
            : ClashOfRimText.Key(
                "ClashOfRim.LocalAtomicMutation.WaitingWithStatus",
                operation.Named("OPERATION"),
                localAtomicMutationStatus.Named("STATUS"));
        return true;
    }

    private bool TryRejectBlockedByDifferentLocalAtomicMutation(string operation, out string message)
    {
        if (!localAtomicMutationPending)
        {
            message = string.Empty;
            return false;
        }

        string requestedOperation = operation ?? string.Empty;
        if (string.Equals(localAtomicMutationOperation, requestedOperation, StringComparison.Ordinal))
        {
            message = string.Empty;
            return false;
        }

        return TryRejectBlockedByLocalAtomicMutation(out message);
    }

    private bool CanRunManualSync(out string failureReason)
    {
        if (!settings.IsConfigured)
        {
            failureReason = ClashOfRimText.Key("ClashOfRim.Sync.StatusNotConfigured");
            return false;
        }

        if (TryRejectBlockedByLocalAtomicMutation(out string atomicMessage))
        {
            failureReason = atomicMessage;
            return false;
        }

        if (string.IsNullOrWhiteSpace(settings.CurrentSnapshotId))
        {
            failureReason = ClashOfRimText.Key("ClashOfRim.Sync.StatusSnapshotMissing");
            return false;
        }

        failureReason = string.Empty;
        return true;
    }

    private bool CanContinueManualSyncForLocalAtomicMutation(string operation, out string failureReason)
    {
        if (!settings.IsConfigured)
        {
            failureReason = ClashOfRimText.Key("ClashOfRim.Sync.StatusNotConfigured");
            return false;
        }

        if (TryRejectBlockedByDifferentLocalAtomicMutation(operation, out string atomicMessage))
        {
            failureReason = atomicMessage;
            return false;
        }

        if (string.IsNullOrWhiteSpace(settings.CurrentSnapshotId))
        {
            failureReason = ClashOfRimText.Key("ClashOfRim.Sync.StatusSnapshotMissing");
            return false;
        }

        failureReason = string.Empty;
        return true;
    }

}

