namespace AIRsLight.ClashOfRim.Events;

public interface IAuthoritativeEventLedger
{
    LedgerAppendResult Append(AuthoritativeEvent ledgerEvent);

    AuthoritativeEvent? Find(string eventId);

    AuthoritativeEvent? FindByIdempotencyKey(string idempotencyKey);

    IReadOnlyList<AuthoritativeEvent> ListAll();

    IReadOnlyList<AuthoritativeEvent> ListByType(ServerEventType type);

    IReadOnlyList<AuthoritativeEvent> ListByTypeForActor(ServerEventType type, string actorUserId, string? actorColonyId);

    IReadOnlyList<AuthoritativeEvent> ListByTypeForTarget(ServerEventType type, string targetUserId, string? targetColonyId);

    IReadOnlyList<AuthoritativeEvent> ListForUser(string userId);

    IReadOnlyList<AuthoritativeEvent> ListQueueForTarget(string targetUserId);

    int DeleteForColony(string userId, string colonyId);

    AuthoritativeEvent ChangeStatus(string eventId, ServerEventStatus status);

    AuthoritativeEvent MarkDelivered(string eventId, string deliveredToSnapshotId, DateTimeOffset deliveredAtUtc);

    AuthoritativeEvent MarkApplied(string eventId, string appliedSnapshotId, DateTimeOffset appliedAtUtc);

    AuthoritativeEvent MarkAccepted(string eventId, DateTimeOffset acceptedAtUtc, string? reason);

    AuthoritativeEvent MarkRejected(string eventId, DateTimeOffset rejectedAtUtc, string? reason);

    GiftReturnResult RejectGiftAndCreateReturn(
        string eventId,
        DateTimeOffset rejectedAtUtc,
        string? reason,
        bool originalActorOnline);

    AuthoritativeEvent ReportApplicationResult(
        string eventId,
        EventApplicationResultKind result,
        string? failureReason,
        DateTimeOffset? nextRetryAtUtc);
}
