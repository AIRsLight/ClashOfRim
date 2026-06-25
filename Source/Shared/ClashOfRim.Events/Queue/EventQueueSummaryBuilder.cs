namespace AIRsLight.ClashOfRim.Events;

public static class EventQueueSummaryBuilder
{
    public static EventQueueSummary BuildForTarget(string targetUserId, IEnumerable<AuthoritativeEvent> events)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetUserId);
        ArgumentNullException.ThrowIfNull(events);

        var directlyProcessable = new List<EventQueueItem>();
        var waitingForConfirmation = new List<EventQueueItem>();
        var deliveredUnconfirmed = new List<EventQueueItem>();
        var conflicts = new List<EventQueueItem>();
        var rejected = new List<EventQueueItem>();
        var allItems = new List<EventQueueItem>();
        foreach (AuthoritativeEvent ledgerEvent in events)
        {
            if (!string.Equals(ledgerEvent.Target.UserId, targetUserId, StringComparison.Ordinal))
            {
                continue;
            }

            EventQueueItem? item = ToQueueItem(ledgerEvent);
            if (item is null)
            {
                continue;
            }

            AddToGroup(item, directlyProcessable, waitingForConfirmation, deliveredUnconfirmed, conflicts, rejected);
            allItems.Add(item);
        }

        SortQueueItems(directlyProcessable);
        SortQueueItems(waitingForConfirmation);
        SortQueueItems(deliveredUnconfirmed);
        SortQueueItems(conflicts);
        SortQueueItems(rejected);
        SortQueueItems(allItems);

        return new EventQueueSummary(
            targetUserId,
            directlyProcessable,
            waitingForConfirmation,
            deliveredUnconfirmed,
            conflicts,
            rejected,
            allItems);
    }

    private static EventQueueItem? ToQueueItem(AuthoritativeEvent ledgerEvent)
    {
        if (IsPlayerRaidSourceEvent(ledgerEvent))
        {
            return null;
        }

        if (IsTradeAcceptanceMemoEvent(ledgerEvent))
        {
            return null;
        }

        EventQueueGroupKind? group = ResolveGroup(ledgerEvent);
        if (group is null)
        {
            return null;
        }

        EventTargetContext? targetContext = ledgerEvent.TargetContext;
        return new EventQueueItem(
            ledgerEvent.EventId,
            ledgerEvent.Type,
            ledgerEvent.Status,
            ledgerEvent.CreatedAtUtc,
            targetContext?.WorldObjectId,
            targetContext?.MapUniqueId,
            targetContext?.Tile,
            targetContext?.LandingMode ?? EventLandingMode.Unspecified,
            ledgerEvent.LastFailureReason,
            ledgerEvent.RejectionPolicy == EventRejectionPolicy.RejectableByTarget,
            EventSemanticClassifier.RequiresClientApplication(ledgerEvent),
            group.Value);
    }

    private static bool IsPlayerRaidSourceEvent(AuthoritativeEvent ledgerEvent)
    {
        return ledgerEvent.Type == ServerEventType.Raid
            && ledgerEvent.Payload is RaidEventPayload
            {
                OpponentKind: RaidOpponentKind.Player,
                AttackForce: not null,
                AttackerLoss: null,
                Settlement: null,
                ReturnedSnapshotId: null,
                FinishedAtUtc: null
            };
    }

    private static bool IsTradeAcceptanceMemoEvent(AuthoritativeEvent ledgerEvent)
    {
        return ledgerEvent.Type == ServerEventType.Trade
            && ledgerEvent.Payload is TradeEventPayload { Stage: TradeStage.AcceptedMemo };
    }

    private static EventQueueGroupKind? ResolveGroup(AuthoritativeEvent ledgerEvent)
    {
        return ledgerEvent.Status switch
        {
            ServerEventStatus.PendingOfflineDelivery or ServerEventStatus.ReadyForImmediateDelivery
                when ledgerEvent.RejectionPolicy == EventRejectionPolicy.RejectableByTarget
                => EventQueueGroupKind.WaitingForConfirmation,
            ServerEventStatus.PendingOfflineDelivery or ServerEventStatus.ReadyForImmediateDelivery
                => EventQueueGroupKind.DirectlyProcessable,
            ServerEventStatus.DeliveredToClient => EventQueueGroupKind.DeliveredUnconfirmed,
            ServerEventStatus.Conflict or ServerEventStatus.Failed => EventQueueGroupKind.Conflict,
            ServerEventStatus.RejectedByTarget => EventQueueGroupKind.Rejected,
            _ => null
        };
    }

    private static void AddToGroup(
        EventQueueItem item,
        List<EventQueueItem> directlyProcessable,
        List<EventQueueItem> waitingForConfirmation,
        List<EventQueueItem> deliveredUnconfirmed,
        List<EventQueueItem> conflicts,
        List<EventQueueItem> rejected)
    {
        switch (item.Group)
        {
            case EventQueueGroupKind.DirectlyProcessable:
                directlyProcessable.Add(item);
                break;
            case EventQueueGroupKind.WaitingForConfirmation:
                waitingForConfirmation.Add(item);
                break;
            case EventQueueGroupKind.DeliveredUnconfirmed:
                deliveredUnconfirmed.Add(item);
                break;
            case EventQueueGroupKind.Conflict:
                conflicts.Add(item);
                break;
            case EventQueueGroupKind.Rejected:
                rejected.Add(item);
                break;
        }
    }

    private static void SortQueueItems(List<EventQueueItem> items)
    {
        if (items.Count <= 1)
        {
            return;
        }

        items.Sort(CompareQueueItems);
    }

    private static int CompareQueueItems(EventQueueItem left, EventQueueItem right)
    {
        int createdAtComparison = left.CreatedAtUtc.CompareTo(right.CreatedAtUtc);
        return createdAtComparison != 0
            ? createdAtComparison
            : string.Compare(left.EventId, right.EventId, StringComparison.Ordinal);
    }
}
