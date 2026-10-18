using AIRsLight.ClashOfRim.Network.Modules.Sessions;
using static AIRsLight.ClashOfRim.Network.Modules.Trades.TradeRules;
using AIRsLight.ClashOfRim.Network.Modules.Economy;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Network;

public static partial class ClashOfRimNetworkServer
{
    private static void SignalWorldConfigurationChanged(ClashOfRimNetworkState state, params string?[] additionalUserIds)
    {
        IEnumerable<string> knownUsers = state.Players.List().Select(player => player.UserId);
        IEnumerable<string> additionalUsers = additionalUserIds
            .Where(userId => !string.IsNullOrWhiteSpace(userId))
            .Select(userId => userId!);
        state.WorldConfigurationNotifications.SignalUsers(knownUsers.Concat(additionalUsers));
    }

    private static void RunClientLifecycleHooks(
        ClashOfRimNetworkState state,
        ClientLifecycleEvent lifecycleEvent)
    {
        foreach (ClientLifecycleHook hook in ClientLifecycleHooks)
        {
            hook(state, lifecycleEvent);
        }
    }

    private static void ReconcilePendingConfirmationsOnClientLifecycle(
        ClashOfRimNetworkState state,
        ClientLifecycleEvent lifecycleEvent)
    {
        // Abandonment reconciles and closes the colony in one recovered boundary below.
        if (lifecycleEvent.Kind == ClientLifecycleEventKind.ColonyAbandoned
            || string.IsNullOrWhiteSpace(lifecycleEvent.UserId)
            || string.IsNullOrWhiteSpace(lifecycleEvent.ColonyId))
        {
            return;
        }

        ReconcilePendingConfirmationsForColony(
            state,
            lifecycleEvent.UserId,
            lifecycleEvent.ColonyId,
            lifecycleEvent.OccurredAtUtc,
            forceCancel: lifecycleEvent.Kind is ClientLifecycleEventKind.LoggedIn
                or ClientLifecycleEventKind.InitialWorldSessionPrepared);
    }

    private static PendingConfirmationReconciliationResult ReconcilePendingConfirmationsForColony(
        ClashOfRimNetworkState state,
        string userId,
        string colonyId,
        DateTimeOffset nowUtc,
        bool forceCancel)
    {
        using var mutation = EnterRecoveredSnapshotMutation(state);
        return ReconcilePendingConfirmationsLocked(state, userId, colonyId, nowUtc, forceCancel);
    }

    private static PendingConfirmationReconciliationResult ReconcilePendingConfirmationsLocked(
        ClashOfRimNetworkState state, string userId, string colonyId, DateTimeOffset nowUtc, bool forceCancel)
        => state.PendingConfirmations.Reconcile(userId, colonyId, nowUtc, forceCancel);

    private static bool TryRejectExpiredPendingConfirmationSnapshot(
        ClashOfRimNetworkState state,
        string userId,
        string colonyId,
        DateTimeOffset nowUtc,
        out ProtocolResponse? rejection)
    {
        PendingConfirmationReconciliationResult result = ReconcilePendingConfirmationsForColony(
            state,
            userId,
            colonyId,
            nowUtc,
            forceCancel: false);
        rejection = PendingConfirmationRejection(result);
        return rejection is not null;
    }

    private static ProtocolResponse? PendingConfirmationRejection(PendingConfirmationReconciliationResult result)
        => PendingConfirmationReconciler.Rejection(result);

    private static void ReconcileAbandonedColonyOnClientLifecycle(
        ClashOfRimNetworkState state,
        ClientLifecycleEvent lifecycleEvent)
    {
        if (lifecycleEvent.Kind != ClientLifecycleEventKind.ColonyAbandoned
            || string.IsNullOrWhiteSpace(lifecycleEvent.UserId)
            || string.IsNullOrWhiteSpace(lifecycleEvent.ColonyId))
        {
            return;
        }

        using var mutation = EnterRecoveredSnapshotMutation(state);
        ReconcilePendingConfirmationsLocked(state, lifecycleEvent.UserId, lifecycleEvent.ColonyId,
            lifecycleEvent.OccurredAtUtc, forceCancel: false);
        ReconcileAbandonedColonyLocked(state, lifecycleEvent);
    }

    private static void ReconcileAbandonedColonyLocked(
        ClashOfRimNetworkState state, ClientLifecycleEvent lifecycleEvent)
    {
        string userId = lifecycleEvent.UserId;
        string colonyId = lifecycleEvent.ColonyId;

        state.RaidPreparations.RemoveForColony(userId, colonyId);
        state.RaidProtectionActivations.RemoveForColony(userId, colonyId);
        state.MercenaryContracts.CloseForColony(userId, colonyId);

        LedgerBatch batch = BuildAbandonedColonyEventClosure(state.Ledger, userId, colonyId, out HashSet<string> usersToSignal);
        if (batch.StatusChanges.Count > 0) state.Ledger.ApplyBatch(batch);
        if (usersToSignal.Count > 0) state.EventNotifications.SignalUsers(usersToSignal);
    }

    // Build the complete graph before changing any status. An order and its
    // acceptance memos must not be split by a persistence failure.
    private static LedgerBatch BuildAbandonedColonyEventClosure(
        IAuthoritativeEventLedger ledger, string userId, string colonyId, out HashSet<string> usersToSignal)
    {
        var users = new HashSet<string>(StringComparer.Ordinal);
        var changes = new Dictionary<string, LedgerStatusChange>(StringComparer.Ordinal);
        void Close(AuthoritativeEvent item, ServerEventStatus status)
        {
            changes[item.EventId] = new LedgerStatusChange(item.EventId, item.Status, status);
            AddSignalUser(users, item.Actor.UserId);
            AddSignalUser(users, item.Target.UserId);
        }

        foreach (AuthoritativeEvent item in ledger.ListAll())
        {
            if (IsTerminalForAbandonedColonyCleanup(item.Status) || IsColonyTombstoneEvent(item, userId, colonyId)) continue;
            bool actorMatches = IsVisibleParty(item.Actor, userId, colonyId);
            if (!actorMatches && !IsVisibleParty(item.Target, userId, colonyId)) continue;

            if (item.Type == ServerEventType.Trade && actorMatches
                && item.Payload is TradeEventPayload { Stage: TradeStage.MarketOrder })
            {
                foreach (AuthoritativeEvent memo in FindActiveTradeAcceptanceMemos(ledger, item.EventId))
                    Close(memo, ServerEventStatus.Cancelled);
            }
            Close(item, item.Type == ServerEventType.Raid && actorMatches ? ServerEventStatus.Failed : ServerEventStatus.Cancelled);
        }
        usersToSignal = users;
        return new LedgerBatch(Array.Empty<AuthoritativeEvent>(), changes.Values.ToArray());
    }

    private static bool IsTerminalForAbandonedColonyCleanup(ServerEventStatus status)
    {
        return status is ServerEventStatus.AppliedToSnapshot
            or ServerEventStatus.RejectedByTarget
            or ServerEventStatus.Conflict
            or ServerEventStatus.Cancelled
            or ServerEventStatus.Failed;
    }

    private static bool IsColonyTombstoneEvent(AuthoritativeEvent ledgerEvent, string userId, string colonyId)
    {
        return ledgerEvent.Payload is ServerNotificationEventPayload payload
            && string.Equals(payload.NotificationId, BuildColonyTombstoneNotificationId(userId, colonyId), StringComparison.Ordinal);
    }

    private static void AddSignalUser(HashSet<string> users, string? userId)
    {
        if (!string.IsNullOrWhiteSpace(userId) && !string.Equals(userId, "server", StringComparison.Ordinal))
        {
            users.Add(userId!);
        }
    }

    private delegate void ClientLifecycleHook(
        ClashOfRimNetworkState state,
        ClientLifecycleEvent lifecycleEvent);


}
