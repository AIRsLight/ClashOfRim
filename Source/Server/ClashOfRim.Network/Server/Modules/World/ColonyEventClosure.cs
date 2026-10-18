using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Network.Modules.Sessions;
using static AIRsLight.ClashOfRim.Network.Modules.Trades.TradeRules;

namespace AIRsLight.ClashOfRim.Network.Modules.World;

internal static class ColonyEventClosure
{
    // Build the complete graph before changing any status. An order and its
    // acceptance memos must not be split by a persistence failure.
    internal static LedgerBatch Build(
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
            bool actorMatches = EventVisibility.MatchesParty(item.Actor, userId, colonyId);
            if (!actorMatches && !EventVisibility.MatchesParty(item.Target, userId, colonyId)) continue;

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
