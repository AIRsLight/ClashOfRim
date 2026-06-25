using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Events;

public static class EventLetterProjectionBuilder
{
    public static IReadOnlyList<EventLetterProjection> Build(EventQueueSummary summary)
    {
        return Build(summary, Array.Empty<AuthoritativeEvent>());
    }

    public static IReadOnlyList<EventLetterProjection> Build(
        EventQueueSummary summary,
        IEnumerable<AuthoritativeEvent> sourceEvents)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(sourceEvents);

        IReadOnlyDictionary<string, AuthoritativeEvent> eventsById = BuildFirstEventById(sourceEvents);

        return summary.AllItems
            .Select(item =>
            {
                eventsById.TryGetValue(item.EventId, out AuthoritativeEvent? ledgerEvent);
                return BuildItem(item, ledgerEvent);
            })
            .ToList();
    }

    private static IReadOnlyDictionary<string, AuthoritativeEvent> BuildFirstEventById(IEnumerable<AuthoritativeEvent> sourceEvents)
    {
        var result = new Dictionary<string, AuthoritativeEvent>(StringComparer.Ordinal);
        foreach (AuthoritativeEvent ledgerEvent in sourceEvents)
        {
            result.TryAdd(ledgerEvent.EventId, ledgerEvent);
        }

        return result;
    }

    private static EventLetterProjection BuildItem(EventQueueItem item, AuthoritativeEvent? ledgerEvent)
    {
        EventLetterKind kind = item.NeedsUserChoice && item.Group == EventQueueGroupKind.WaitingForConfirmation
            ? EventLetterKind.Choice
            : EventLetterKind.Standard;

        return new EventLetterProjection(
            item.EventId,
            item.Type,
            item.Status,
            item.Group,
            kind,
            ResolveLetterDef(item, ledgerEvent),
            ResolveLabel(item, ledgerEvent),
            ResolveText(item, ledgerEvent),
            item.TargetWorldObjectId,
            item.TargetMapUniqueId,
            item.TargetTile,
            ResolveActions(item),
            DismissalChangesLedgerState: false);
    }

    private static EventLetterDefName ResolveLetterDef(EventQueueItem item, AuthoritativeEvent? ledgerEvent)
    {
        if (GetAttackerLoss(ledgerEvent) != null)
        {
            return EventLetterDefName.NegativeEvent;
        }

        if (item.Group is EventQueueGroupKind.Conflict)
        {
            return EventLetterDefName.NegativeEvent;
        }

        if (item.Group is EventQueueGroupKind.Rejected)
        {
            return EventLetterDefName.NeutralEvent;
        }

        if (ledgerEvent?.Payload is ServerNotificationEventPayload notification)
        {
            return notification.Severity switch
            {
                ServerNotificationSeverity.Critical => EventLetterDefName.ThreatBig,
                ServerNotificationSeverity.Warning => EventLetterDefName.NegativeEvent,
                _ => EventLetterDefName.NeutralEvent
            };
        }

        return item.Type switch
        {
            ServerEventType.Raid => EventLetterDefName.ThreatBig,
            ServerEventType.WarDeclaration => EventLetterDefName.ThreatBig,
            ServerEventType.Gift => EventLetterDefName.PositiveEvent,
            ServerEventType.GiftReturn => EventLetterDefName.NeutralEvent,
            ServerEventType.Trade => EventLetterDefName.PositiveEvent,
            ServerEventType.SupportPawn => EventLetterDefName.PositiveEvent,
            ServerEventType.AllianceRequest => EventLetterDefName.NeutralEvent,
            ServerEventType.AllianceCancellation => EventLetterDefName.NeutralEvent,
            ServerEventType.PeaceRequest => EventLetterDefName.NeutralEvent,
            ServerEventType.ServerNotification => EventLetterDefName.NeutralEvent,
            _ => EventLetterDefName.NeutralEvent
        };
    }

    private static string ResolveLabel(EventQueueItem item, AuthoritativeEvent? ledgerEvent)
    {
        if (GetAttackerLoss(ledgerEvent) != null)
        {
            return ServerLocalization.Text("Letter.CaravanLost");
        }

        if (ledgerEvent?.Payload is ServerNotificationEventPayload notification
            && !string.IsNullOrWhiteSpace(notification.Title))
        {
            return notification.Title;
        }

        string prefix = item.Group switch
        {
            EventQueueGroupKind.Conflict => ServerLocalization.Text("Letter.GroupConflict"),
            EventQueueGroupKind.Rejected => ServerLocalization.Text("Letter.GroupRejected"),
            EventQueueGroupKind.DeliveredUnconfirmed => ServerLocalization.Text("Letter.GroupDeliveredUnconfirmed"),
            _ => ServerLocalization.Text("Letter.GroupDefault")
        };

        string key = item.Type switch
        {
            ServerEventType.Raid => "Letter.TypeRaid",
            ServerEventType.Gift => "Letter.TypeGift",
            ServerEventType.GiftReturn => "Letter.TypeGiftReturn",
            ServerEventType.Trade => "Letter.TypeTrade",
            ServerEventType.SupportPawn => "Letter.TypeSupportPawn",
            ServerEventType.AllianceRequest => "Letter.TypeAllianceRequest",
            ServerEventType.AllianceCancellation => "Letter.TypeAllianceCancellation",
            ServerEventType.WarDeclaration => "Letter.TypeWarDeclaration",
            ServerEventType.PeaceRequest => "Letter.TypePeaceRequest",
            ServerEventType.ServerNotification => "Letter.TypeServerNotification",
            _ => "Letter.TypeUnknown"
        };
        return ServerLocalization.Text(key, new Dictionary<string, string?> { ["PREFIX"] = prefix });
    }

    private static string ResolveText(EventQueueItem item, AuthoritativeEvent? ledgerEvent)
    {
        RaidAttackerLossRecord? attackerLoss = GetAttackerLoss(ledgerEvent);
        if (attackerLoss != null)
        {
            RaidAttackerLossApplicationPlan plan = RaidAttackerLossApplicationPlanner.FromLoss(attackerLoss);
            string vanilla = plan.ShouldTriggerVanillaCaravanLostEvent
                ? ServerLocalization.Text("Letter.RaidAttackerLossVanilla")
                : ServerLocalization.Text("Letter.RaidAttackerLossSnapshotOnly");
            return ServerLocalization.Text(
                "Letter.RaidAttackerLossText",
                new Dictionary<string, string?>
                {
                    ["VANILLA"] = vanilla,
                    ["PAWNS"] = plan.LostPawnGlobalKeys.Count.ToString(),
                    ["THINGS"] = plan.LostThings.Count.ToString()
                });
        }

        if (ledgerEvent?.Payload is ServerNotificationEventPayload notification
            && !string.IsNullOrWhiteSpace(notification.Message))
        {
            return notification.Message;
        }

        string statusText = item.Group switch
        {
            EventQueueGroupKind.WaitingForConfirmation => ServerLocalization.Text("Letter.StatusWaitingForConfirmation"),
            EventQueueGroupKind.DirectlyProcessable => ServerLocalization.Text("Letter.StatusDirectlyProcessable"),
            EventQueueGroupKind.DeliveredUnconfirmed => ServerLocalization.Text("Letter.StatusDeliveredUnconfirmed"),
            EventQueueGroupKind.Conflict => ServerLocalization.Text("Letter.StatusConflict"),
            EventQueueGroupKind.Rejected => ServerLocalization.Text("Letter.StatusRejected"),
            _ => ServerLocalization.Text("Letter.StatusDefault")
        };
        string eventText = item.Type switch
        {
            ServerEventType.Raid => ServerLocalization.Text("Letter.TextRaid"),
            ServerEventType.Gift => ServerLocalization.Text("Letter.TextGift"),
            ServerEventType.GiftReturn => ServerLocalization.Text("Letter.TextGiftReturn"),
            ServerEventType.Trade => ServerLocalization.Text("Letter.TextTrade"),
            ServerEventType.SupportPawn => ServerLocalization.Text("Letter.TextSupportPawn"),
            ServerEventType.AllianceRequest => ServerLocalization.Text("Letter.TextAllianceRequest"),
            ServerEventType.AllianceCancellation => ServerLocalization.Text("Letter.TextAllianceCancellation"),
            ServerEventType.WarDeclaration => ServerLocalization.Text("Letter.TextWarDeclaration"),
            ServerEventType.PeaceRequest => ServerLocalization.Text("Letter.TextPeaceRequest"),
            ServerEventType.ServerNotification => ServerLocalization.Text("Letter.TextServerNotification"),
            _ => ServerLocalization.Text("Letter.TextDefault")
        };

        string failure = string.IsNullOrWhiteSpace(item.FailureReason)
            ? ""
            : ServerLocalization.Text("Letter.FailureReason", new Dictionary<string, string?> { ["REASON"] = item.FailureReason });

        return $"{eventText}\n\n{statusText}{failure}";
    }

    private static IReadOnlyList<EventLetterAction> ResolveActions(EventQueueItem item)
    {
        var actions = new List<EventLetterAction>();

        if (HasJumpTarget(item))
        {
            actions.Add(new EventLetterAction(
                EventLetterActionKind.JumpToTarget,
                ServerLocalization.Text("Letter.ActionJump"),
                RequiresServerRoundtrip: false,
                ChangesLedgerState: false));
        }

        if (item.NeedsUserChoice && item.Group == EventQueueGroupKind.WaitingForConfirmation)
        {
            actions.Add(new EventLetterAction(
                EventLetterActionKind.Accept,
                ServerLocalization.Text("Letter.ActionAccept"),
                RequiresServerRoundtrip: true,
                ChangesLedgerState: true));
            actions.Add(new EventLetterAction(
                EventLetterActionKind.Reject,
                ServerLocalization.Text("Letter.ActionReject"),
                RequiresServerRoundtrip: true,
                ChangesLedgerState: true));
            actions.Add(new EventLetterAction(
                EventLetterActionKind.Postpone,
                ServerLocalization.Text("Letter.ActionPostpone"),
                RequiresServerRoundtrip: false,
                ChangesLedgerState: false));
        }
        else if (item.RequiresClientApplication)
        {
            switch (item.Group)
            {
                case EventQueueGroupKind.DirectlyProcessable:
                    actions.Add(new EventLetterAction(
                        EventLetterActionKind.ApplyToSnapshot,
                        ServerLocalization.Text("Letter.ActionApplyToSnapshot"),
                        RequiresServerRoundtrip: true,
                        ChangesLedgerState: true));
                    break;
                case EventQueueGroupKind.DeliveredUnconfirmed:
                    actions.Add(new EventLetterAction(
                        EventLetterActionKind.UploadSnapshotConfirmation,
                        ServerLocalization.Text("Letter.ActionUploadSnapshot"),
                        RequiresServerRoundtrip: true,
                        ChangesLedgerState: true));
                    break;
                case EventQueueGroupKind.Conflict:
                    actions.Add(new EventLetterAction(
                        EventLetterActionKind.OpenConflict,
                        ServerLocalization.Text("Letter.ActionOpenConflict"),
                        RequiresServerRoundtrip: false,
                        ChangesLedgerState: false));
                    break;
            }
        }

        actions.Add(new EventLetterAction(
            EventLetterActionKind.Close,
            ServerLocalization.Text("Letter.ActionClose"),
            RequiresServerRoundtrip: false,
            ChangesLedgerState: false));

        return actions;
    }

    private static bool HasJumpTarget(EventQueueItem item)
    {
        return !string.IsNullOrWhiteSpace(item.TargetWorldObjectId)
            || !string.IsNullOrWhiteSpace(item.TargetMapUniqueId)
            || item.TargetTile.HasValue;
    }

    private static RaidAttackerLossRecord? GetAttackerLoss(AuthoritativeEvent? ledgerEvent)
    {
        return ledgerEvent?.Payload is RaidEventPayload raidPayload
            ? raidPayload.AttackerLoss
            : null;
    }
}
