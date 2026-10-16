namespace AIRsLight.ClashOfRim.Events;

public interface IAuthoritativeEventLedger
{
    /// <summary>
    /// Appends atomically, or returns the original event when key, ID, type and parties match.
    /// Replay retains the stored payload and lifecycle; callers must validate business-request equivalence.
    /// </summary>
    LedgerAppendResult Append(AuthoritativeEvent ledgerEvent);

    /// <summary>Atomically applies the whole batch; replay preserves the lifecycle of existing events.</summary>
    void ApplyBatch(LedgerBatch batch);

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

    /// <summary>Atomically rejects a gift and creates its return; replay retains the first decision and return lifecycle.</summary>
    GiftReturnResult RejectGiftAndCreateReturn(
        string eventId,
        DateTimeOffset rejectedAtUtc,
        string? reason,
        bool originalActorOnline,
        EventTargetContext? returnTargetContext = null);

    AuthoritativeEvent ReportApplicationResult(
        string eventId,
        EventApplicationResultKind result,
        string? failureReason,
        DateTimeOffset? nextRetryAtUtc);
}
