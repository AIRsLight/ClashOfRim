using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace ClashOfRim.RimTestBridge;

// The bridge and its unit checks consume the same declared action/state surface.
// Product code has no dependency on this test contract.
internal static class ClientActionContract
{
    internal static readonly string[] ActionNames = {
        "StartMainMenuServerEntryFlow", "DisconnectMultiplayerSessionForExit", "StartChangeOfflinePassword",
        "StartRefreshPlayers", "SelectPlayerTarget", "StartRefreshChatMessages", "StartSendChatMessage", "MarkPrivateChatRead",
        "StartRefreshAchievements", "StartRefreshEventsForTest", "HandleEventLetterAction", "StartManualSnapshotUpload",
        "StartSyncWorldMapMarkers", "StartRefreshBankStatus", "StartCreateBankLoan", "StartRepayBankLoan", "StartRepayBankDebt",
        "StartHireMercenary", "StartHireMercenaryGuard", "StartRefreshServerShop", "StartUpsertServerShopListing",
        "StartRemoveServerShopListing", "StartPurchaseServerShopListing", "StartRefreshTradeOrders", "StartLoadMoreTradeOrders",
        "StartCancelTradeOrder", "StartCancelAcceptedTradeMemo", "StartAcceptTradeOrder", "StartFulfillTradeOrderByDropPod",
        "OpenTradeOrderDialog", "OpenTradeMarketWindow", "OpenCaravanTradeFulfillmentMenu", "OpenCaravanGiftDeliveryMenu",
        "OpenCaravanSupportPawnMenu", "OpenCaravanRaidMenu", "StartObserveRemoteMarker", "StartFinishActiveRaidBattle",
        "StartCreateDiplomacyEvent", "StartRefreshAdminStatus",
        "StartUpdateAdminConfiguration", "StartAdminAction", "OpenColonyAppearanceWindow", "SaveColonyAppearance",
        "OpenAbandonPlayerColonyConfirmation"
    };
    internal static readonly string[] StateProperties = {
        "UserId", "ColonyId", "CurrentSnapshotId", "ServerBaseUrl", "IsNetworkConfigured", "IsInActiveMultiplayerSession",
        "IsServerWorldSetupFlow", "IsAdministrator", "SnapshotUploadInProgress", "ManualSyncInProgress", "LocalAtomicMutationPending",
        "LocalAtomicMutationOperation", "PresenceInProgress", "ChatInProgress", "BankInProgress", "MercenaryInProgress",
        "ServerShopInProgress", "AdminInProgress", "LoginStatus", "SnapshotUploadStatus", "EventQueueStatus", "EventDetailsStatus",
        "BankStatus", "ChatStatus", "TradeStatus", "WorldMapStatus", "MercenaryStatus", "ServerShopStatus", "AdminStatus",
        "GiftProcessingStatus", "PlayerListStatus", "AchievementStatus", "TradeOrdersPageLoadInProgress", "UnreadPrivateChatCount",
        "AccountPasswordInProgress", "AccountPasswordStatus"
    };
    internal static readonly Dictionary<string, string> RecordProperties = new()
    {
        ["players"] = "PlayersSnapshot",
        ["chat"] = "ChatMessagesSnapshot",
        ["trades"] = "TradeOrdersSnapshot",
        ["shop"] = "ServerShopListingsSnapshot",
        ["markers"] = "WorldMapMarkersSnapshot",
        ["achievements"] = "OwnAchievementsSnapshot",
        ["leaderboards"] = "AchievementLeaderboardsSnapshot"
    };

    internal static MethodInfo[] ResolveActions(Type owner)
    {
        var actions = owner.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(method => ActionNames.Contains(method.Name)).ToArray();
        foreach (string name in ActionNames)
            if (!actions.Any(method => method.Name == name)) throw new MissingMethodException(owner.FullName, name);
        return actions;
    }

    internal static Dictionary<string, PropertyInfo> ResolveProperties(Type owner)
        => StateProperties.Concat(RecordProperties.Values).Concat(new[] { "BankStatusSnapshot", "AdminStatusSnapshot",
                "AchievementLeaderboardsSnapshotVersion", "WorldConfigurationId" })
            .Distinct().ToDictionary(name => name, name =>
            {
                var property = owner.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (property?.GetGetMethod(nonPublic: true) is null || property.GetIndexParameters().Length != 0)
                    throw new MissingMemberException(owner.FullName, name);
                return property;
            });
}
