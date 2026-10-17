using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Network.Modules.Support;

internal sealed class SupportClosures(IAuthoritativeEventLedger ledger, EventNotificationHub notifications)
{
    internal enum SupportClosureKind { Return, Loss, Rejection }
    internal sealed record SupportClosure(AuthoritativeEvent Event, SupportClosureKind Kind);

    private static bool IsSupportClosureCandidate(AuthoritativeEvent source, AuthoritativeEvent item) =>
        item.IdempotencyKey == source.IdempotencyKey + ":rejected-return"
        || item.Payload is SupportPawnEventPayload payload
            && (payload.ReturnToSender && payload.SourceEventId == source.EventId
                || SupportClosureKeys.IsFinish(item.IdempotencyKey, source.EventId, payload.ReturnReason)
                || SupportClosureKeys.IsLoss(item.IdempotencyKey, source.EventId, payload.ReturnReason));

    internal SupportClosure? Find(AuthoritativeEvent source)
    {
        // IDs and reasons may both contain colons. A source prefix is not a relation:
        // compare the complete key using the stored reason as well as the explicit source ID.
        // Either match still exposes a malformed source/return flag to validation below.
        var candidates = ledger.ListAll()
            .Where(item => IsSupportClosureCandidate(source, item))
            .ToDictionary(item => item.EventId, StringComparer.Ordinal);
        if (candidates.Count == 0) return null;
        if (candidates.Count != 1) throw new InvalidOperationException("Support has conflicting completion events.");
        var item = candidates.Values.Single();
        var payload = (SupportPawnEventPayload)source.Payload;
        if (item.Type != ServerEventType.SupportPawn || item.Payload is not SupportPawnEventPayload result
            || !result.ReturnToSender || result.TemporaryControl || result.SourceEventId != source.EventId
            || item.Actor != source.Target || item.Target != source.Actor || result.PawnGlobalKey != payload.PawnGlobalKey
            || result.SourceSnapshotId != payload.SourceSnapshotId)
            throw new InvalidOperationException("Support completion conflicts with its source.");
        if (item.IdempotencyKey == source.IdempotencyKey + ":rejected-return" && result.ReturnReason == "Rejected")
            return new(item, SupportClosureKind.Rejection);
        if (SupportClosureKeys.IsFinish(item.IdempotencyKey, source.EventId, result.ReturnReason) && result.PawnPackage is not null)
            return new(item, SupportClosureKind.Return);
        if (SupportClosureKeys.IsLoss(item.IdempotencyKey, source.EventId, result.ReturnReason) && result.PawnPackage is null)
            return new(item, SupportClosureKind.Loss);
        throw new InvalidOperationException("Unrecognized support completion identity.");
    }

    internal bool Commit(AuthoritativeEvent source, SupportClosure closure)
    {
        bool created = ledger.Find(closure.Event.EventId) is null;
        ServerEventStatus status = closure.Kind switch
        {
            SupportClosureKind.Loss => ServerEventStatus.Failed,
            SupportClosureKind.Rejection => ServerEventStatus.RejectedByTarget,
            _ => ServerEventStatus.AppliedToSnapshot
        };
        if (source.Status is ServerEventStatus.Cancelled or ServerEventStatus.Failed or ServerEventStatus.RejectedByTarget
            && source.Status != status)
            throw new InvalidOperationException("Support completion conflicts with an ended source.");
        if (closure.Kind == SupportClosureKind.Rejection && source.Status == ServerEventStatus.RejectedByTarget
            && (source.DecisionAtUtc != closure.Event.CreatedAtUtc
                || source.DecisionReason != ((SupportPawnEventPayload)closure.Event.Payload).RejectionReason))
            throw new InvalidOperationException("Support return conflicts with its original rejection decision.");
        LedgerBatch batch = closure.Kind == SupportClosureKind.Rejection
            ? new([closure.Event], [], RejectionChanges: [new(source, closure.Event.CreatedAtUtc,
                ((SupportPawnEventPayload)closure.Event.Payload).RejectionReason)])
            : new([closure.Event], [new(source.EventId, source.Status, status, ExpectedEvent: source)]);
        ledger.ApplyBatch(batch);
        // The event itself is durable. Retry the transient wake-up even after a previous batch committed.
        notifications.SignalUser(source.Actor.UserId);
        return created;
    }
    internal static bool HasSupportFinishEvent(
        AuthoritativeEvent supportEvent,
        IReadOnlyList<AuthoritativeEvent> allEvents)
    {
        string deathId = $"support-death:{supportEvent.EventId}:RaidSettlementLost";
        return allEvents.Any(evt =>
            IsSupportClosureCandidate(supportEvent, evt)
            || (evt.Payload is ServerNotificationEventPayload notification
                && string.Equals(notification.NotificationId, deathId, StringComparison.Ordinal)));
    }

}
