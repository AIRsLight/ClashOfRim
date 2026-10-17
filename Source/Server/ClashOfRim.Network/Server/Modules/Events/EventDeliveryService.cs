using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network.Modules.Authorization;
using AIRsLight.ClashOfRim.Network.Modules.Snapshots;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;

namespace AIRsLight.ClashOfRim.Network.Modules.Events;

internal sealed class EventDeliveryService(
    ColonyAccessAuthorization authorization,
    SnapshotMutationCoordinator mutations,
    IColonySnapshotIndexStore snapshots,
    IAuthoritativeEventLedger ledger,
    EventNotificationHub notifications,
    OnlinePresenceRegistry presence,
    PlayerRegistry players)
{
    private static readonly TimeSpan DeliveredEventLease = TimeSpan.FromMinutes(2);

    internal PullPendingEventsResponse PullPendingEvents(PullPendingEventsRequest request)
    {
        using var mutation = EnterRead(request.AuthToken, request.UserId, request.ColonyId, request.CurrentSnapshotId);
        if (mutation.Rejection is { } denied)
            return new PullPendingEventsResponse(denied, EmptyEventQueue());
        ReconcileDeliveryLeases(request.UserId, DateTimeOffset.UtcNow);
        ReconcileDeletedSupportActors(request.UserId);

        return new PullPendingEventsResponse(
            ProtocolResponse.Ok(ServerLocalization.Text("Events.QueueReturned")),
            ReadQueue(request.UserId, request.ColonyId));
    }

    internal async Task<WaitForEventsResponse> WaitForEvents(
        WaitForEventsRequest request,
        CancellationToken cancellationToken)
    {
        WaitForEventsResponse Denied(ProtocolResponse failure) => new WaitForEventsResponse(failure,
            false, Math.Max(0, request.KnownNotificationVersion), EmptyEventQueue());
        // Never hold the mutation gate across the asynchronous wait.
        using (mutations.EnterUnrecovered())
        {
            if (ValidateRead(request.AuthToken, request.UserId, request.ColonyId, request.CurrentSnapshotId) is { } denied)
                return Denied(denied);
        }

        EventNotificationWaitResult wait = await notifications.WaitAsync(
            request.UserId,
            Math.Max(0, request.KnownNotificationVersion),
            TimeSpan.FromSeconds(request.TimeoutSeconds),
            cancellationToken);

        using var mutation = EnterRead(request.AuthToken, request.UserId, request.ColonyId, request.CurrentSnapshotId);
        if (mutation.Rejection is { } failure) return Denied(failure);
        ReconcileDeliveryLeases(request.UserId, DateTimeOffset.UtcNow);
        ReconcileDeletedSupportActors(request.UserId);

        return new WaitForEventsResponse(
            ProtocolResponse.Ok(wait.Changed ? ServerLocalization.Text("Events.WaitChanged") : ServerLocalization.Text("Events.WaitTimeout")),
            wait.Changed,
            wait.Version,
            ReadQueue(request.UserId, request.ColonyId));
    }

    internal PullEventDetailsResponse PullEventDetails(PullEventDetailsRequest request)
    {
        using var mutation = EnterRead(request.AuthToken, request.UserId, request.ColonyId, request.CurrentSnapshotId);
        if (mutation.Rejection is { } denied)
            return new PullEventDetailsResponse(denied, Array.Empty<EventDetailDto>());

        if (request.EventIds is null || request.EventIds.Count == 0)
        {
            return new PullEventDetailsResponse(
                ProtocolResponse.Ok(ServerLocalization.Text("Events.DetailsNoneRequested")),
                Array.Empty<EventDetailDto>());
        }

        string[] eventIds = request.EventIds.Distinct(StringComparer.Ordinal).ToArray();
        // Validate the entire batch before maintenance, consumption, or delivery side effects.
        foreach (string eventId in eventIds)
        {
            AuthoritativeEvent? ledgerEvent = ledger.Find(eventId);
            if (ledgerEvent is null || !EventVisibility.IsVisibleTo(ledgerEvent, request.UserId, request.ColonyId))
            {
                return new PullEventDetailsResponse(
                    ProtocolResponse.Reject(ProtocolErrorCode.EventNotFound, ServerLocalization.Text("Events.NotFoundOrInvisible")),
                    Array.Empty<EventDetailDto>());
            }
        }

        ReconcileDeliveryLeases(request.UserId, DateTimeOffset.UtcNow);
        ReconcileDeletedSupportActors(request.UserId);
        var details = new List<EventDetailDto>();
        var deliveries = new List<LedgerDeliveryChange>();
        var acceptances = new List<LedgerAcceptanceChange>();
        var statuses = new List<LedgerStatusChange>();
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        foreach (string eventId in eventIds)
        {
            AuthoritativeEvent ledgerEvent = ledger.Find(eventId)!;
            bool visibleToTarget = EventVisibility.MatchesParty(ledgerEvent.Target, request.UserId, request.ColonyId);
            if (visibleToTarget
                && EventSemanticClassifier.IsSnapshotlessNotification(ledgerEvent)
                && ledgerEvent.Status is ServerEventStatus.PendingOfflineDelivery
                    or ServerEventStatus.ReadyForImmediateDelivery
                    or ServerEventStatus.DeliveredToClient)
            {
                var change = new LedgerAcceptanceChange(ledgerEvent, nowUtc, ServerLocalization.Text("Events.ServerNotificationDelivered"));
                acceptances.Add(change);
                ledgerEvent = change.Updated;
            }
            else if (visibleToTarget
                && (ledgerEvent.Status is ServerEventStatus.PendingOfflineDelivery or ServerEventStatus.ReadyForImmediateDelivery
                    // A player may save while postponing a choice. Re-downloading it
                    // binds to the current authoritative baseline, never an arbitrary
                    // client snapshot or a delivery whose decision is already made.
                    || ledgerEvent.Status == ServerEventStatus.DeliveredToClient
                        && EventSemanticClassifier.RequiresTargetDecision(ledgerEvent)
                        && ledgerEvent.DeliveredToSnapshotId != request.CurrentSnapshotId))
            {
                var change = new LedgerDeliveryChange(ledgerEvent, request.CurrentSnapshotId, nowUtc);
                deliveries.Add(change);
                ledgerEvent = change.Updated;
            }

            details.Add(ProtocolDtoMapper.ToDetailDto(ledgerEvent));
            if (EventVisibility.MatchesParty(ledgerEvent.Target, request.UserId, request.ColonyId)
                && IsReadOnlyTerminalEvent(ledgerEvent))
            {
                statuses.Add(new LedgerStatusChange(ledgerEvent.EventId, ledgerEvent.Status, ServerEventStatus.Cancelled, ExpectedEvent: ledgerEvent));
            }
        }

        // Serialize all details before publishing the batch; storage backends commit it atomically.
        if (deliveries.Count + acceptances.Count + statuses.Count > 0)
            ledger.ApplyBatch(new LedgerBatch([], statuses, DeliveryChanges: deliveries, AcceptanceChanges: acceptances));
        return new PullEventDetailsResponse(
            ProtocolResponse.Ok(ServerLocalization.Text("Events.DetailsReturned")),
            details);
    }

    private static bool IsReadOnlyTerminalEvent(AuthoritativeEvent ledgerEvent)
    {
        return ledgerEvent.Status is ServerEventStatus.Conflict
            or ServerEventStatus.Failed
            or ServerEventStatus.RejectedByTarget;
    }

    private RecoveredSnapshotMutation EnterRead(
        string? token, string userId, string colonyId, string snapshotId,
        [System.Runtime.CompilerServices.CallerMemberName] string operation = "") =>
        mutations.Enter(
            () => ValidateRead(token, userId, colonyId, snapshotId), operation);

    // Called under the snapshot gate, including again after recovery or a long poll.
    private ProtocolResponse? ValidateRead(
        string? token, string userId, string colonyId, string snapshotId)
    {
        if (!authorization.TryAuthorize(token, userId, colonyId, null, null, DateTimeOffset.UtcNow, out string failure))
            return ProtocolResponse.Reject(ProtocolErrorCode.Unauthorized, failure);
        if (string.IsNullOrWhiteSpace(snapshotId)
            || snapshots.GetLatest(userId, colonyId)?.Identity.SnapshotId != snapshotId)
            return ProtocolResponse.Reject(ProtocolErrorCode.SnapshotMismatch, ServerLocalization.Text("Events.SnapshotNotLatest"));
        return null;
    }

    private static EventQueueSummaryDto EmptyEventQueue() => new([], [], [], [], []);

    // Login owns its recovered mutation scope and uses this same queue projection before publishing a session.
    internal EventQueueSummaryDto ReadQueue(string userId, string colonyId) =>
        ProtocolDtoMapper.ToDto(EventQueueSummaryBuilder.BuildForTarget(userId,
            ledger.ListQueueForTarget(userId).Where(evt => EventVisibility.MatchesParty(evt.Target, userId, colonyId))));

    internal void ReconcileDeliveryLeases(
        string userId,
        DateTimeOffset nowUtc)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return;
        }

        DateTimeOffset leaseDeadline = nowUtc - DeliveredEventLease;
        bool userOnline = presence.IsUserOnline(userId);
        bool changed = false;
        foreach (AuthoritativeEvent ledgerEvent in ledger.ListForUser(userId)
            .Where(evt => evt.Target.UserId == userId)
            .Where(evt => evt.Status == ServerEventStatus.DeliveredToClient)
            .Where(evt => evt.DeliveredAtUtc is not null && evt.DeliveredAtUtc <= leaseDeadline)
            .ToList())
        {
            if (EventSemanticClassifier.IsOnlineOnlyServerNotification(ledgerEvent))
            {
                ledger.ChangeStatus(ledgerEvent.EventId, ServerEventStatus.Cancelled);
                changed = true;
                continue;
            }

            if (EventSemanticClassifier.IsSnapshotlessNotification(ledgerEvent))
            {
                ledger.MarkAccepted(
                    ledgerEvent.EventId,
                    DateTimeOffset.UtcNow,
                    ServerLocalization.Text("Events.ServerNotificationDelivered"));
                changed = true;
                continue;
            }

            ServerEventStatus nextStatus = userOnline
                ? ServerEventStatus.ReadyForImmediateDelivery
                : ServerEventStatus.PendingOfflineDelivery;
            ledger.ChangeStatus(ledgerEvent.EventId, nextStatus);
            changed = true;
        }

        if (changed && userOnline)
        {
            notifications.SignalUser(userId);
        }
    }

    private int ReconcileDeletedSupportActors(
        string? visibleUserId)
    {
        if (string.IsNullOrWhiteSpace(visibleUserId))
        {
            return 0;
        }

        int updated = 0;
        foreach (AuthoritativeEvent ledgerEvent in ledger.ListForUser(visibleUserId!))
        {
            if (ledgerEvent.Type != ServerEventType.SupportPawn
                || ledgerEvent.Status != ServerEventStatus.Failed
                || !string.IsNullOrWhiteSpace(ledgerEvent.AppliedSnapshotId)
                || string.IsNullOrWhiteSpace(ledgerEvent.Actor.UserId)
                || string.IsNullOrWhiteSpace(ledgerEvent.Actor.ColonyId)
                || !players.IsDeleted(ledgerEvent.Actor.UserId, ledgerEvent.Actor.ColonyId))
            {
                continue;
            }

            ledger.ChangeStatus(ledgerEvent.EventId, ServerEventStatus.Cancelled);
            updated++;
        }

        if (updated > 0)
        {
            notifications.SignalUser(visibleUserId!);
        }

        return updated;
    }

}
