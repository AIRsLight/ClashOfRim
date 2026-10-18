using AIRsLight.ClashOfRim.Network.Modules.Sessions;
using static AIRsLight.ClashOfRim.Network.Modules.Trades.TradeRules;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Network;

public static partial class ClashOfRimNetworkServer
{
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
        state.PendingConfirmations.Reconcile(lifecycleEvent.UserId, lifecycleEvent.ColonyId,
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
            if (IsTerminalForAbandonedColonyCleanup(item.Status) || ColonyTombstones.IsFor(item, userId, colonyId)) continue;
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

    private static void AddSignalUser(HashSet<string> users, string? userId)
    {
        if (!string.IsNullOrWhiteSpace(userId) && !string.Equals(userId, "server", StringComparison.Ordinal))
        {
            users.Add(userId!);
        }
    }

}
