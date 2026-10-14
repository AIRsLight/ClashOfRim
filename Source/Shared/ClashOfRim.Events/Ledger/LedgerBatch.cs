namespace AIRsLight.ClashOfRim.Events;

/// <summary>Expected statuses prevent a stale trade settlement from closing a changed order.</summary>
public sealed record LedgerStatusChange(string EventId, ServerEventStatus ExpectedStatus, ServerEventStatus Status,
    LedgerEventPayload? ExpectedPayload = null, LedgerEventPayload? Payload = null, AuthoritativeEvent? ExpectedEvent = null);

public sealed record LedgerRejectionChange(AuthoritativeEvent Expected, DateTimeOffset RejectedAtUtc, string? Reason)
{
    public AuthoritativeEvent Updated => Expected.Status == ServerEventStatus.RejectedByTarget ? Expected : Expected with
    {
        Status = ServerEventStatus.RejectedByTarget, TargetDecision = TargetEventDecision.Rejected,
        DecisionAtUtc = RejectedAtUtc, DecisionReason = Reason, LastApplicationResult = EventApplicationResultKind.Rejected
    };
}

public sealed record LedgerApplicationChange(AuthoritativeEvent Expected, string DeliveredSnapshotId,
    string AppliedSnapshotId, DateTimeOffset AppliedAtUtc)
{
    public AuthoritativeEvent Updated => Expected with
    {
        Status = ServerEventStatus.AppliedToSnapshot,
        DeliveredToSnapshotId = string.IsNullOrWhiteSpace(Expected.DeliveredToSnapshotId) ? DeliveredSnapshotId : Expected.DeliveredToSnapshotId,
        DeliveredAtUtc = Expected.DeliveredAtUtc ?? AppliedAtUtc,
        AppliedSnapshotId = AppliedSnapshotId,
        AppliedAtUtc = AppliedAtUtc,
        LastApplicationResult = EventApplicationResultKind.Applied,
        LastFailureReason = null,
        NextRetryAtUtc = null
    };
}

/// <summary>A fixed confirmation decision, including rejected application metadata.</summary>
public sealed record LedgerConfirmationChange(AuthoritativeEvent Expected, AuthoritativeEvent Updated);

public sealed record LedgerBatch(IReadOnlyList<AuthoritativeEvent> Events, IReadOnlyList<LedgerStatusChange> StatusChanges,
    IReadOnlyList<LedgerApplicationChange>? ApplicationChanges = null,
    IReadOnlyList<LedgerConfirmationChange>? ConfirmationChanges = null,
    IReadOnlyList<LedgerRejectionChange>? RejectionChanges = null);

public static class LedgerBatchValidation
{
    public static void Validate(LedgerBatch batch, Func<string, AuthoritativeEvent?> find,
        Func<string, AuthoritativeEvent?> findByKey)
    {
        ArgumentNullException.ThrowIfNull(batch);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        bool alreadyCommitted = batch.Events.Count > 0;
        foreach (AuthoritativeEvent item in batch.Events)
        {
            if (!ids.Add(item.EventId) || !keys.Add(item.IdempotencyKey))
                throw new InvalidOperationException("Duplicate event identity in batch.");
            AuthoritativeEvent? byId = find(item.EventId);
            AuthoritativeEvent? byKey = findByKey(item.IdempotencyKey);
            alreadyCommitted &= byId is not null;
            if ((byId is null) != (byKey is null) || byId is not null &&
                (byKey!.EventId != item.EventId || byId.IdempotencyKey != item.IdempotencyKey
                || byId.Actor != item.Actor || byId.Target != item.Target || byId.Type != item.Type
                || System.Text.Json.JsonSerializer.Serialize(byId.Payload, LedgerJson.Options)
                    != System.Text.Json.JsonSerializer.Serialize(item.Payload, LedgerJson.Options)))
                throw new InvalidOperationException("Batch event conflicts with an existing event.");
        }
        var changedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (LedgerStatusChange change in batch.StatusChanges)
        {
            if (!changedIds.Add(change.EventId) || ids.Contains(change.EventId))
                throw new InvalidOperationException("Batch status targets must be distinct existing events.");
            AuthoritativeEvent current = find(change.EventId)
                ?? throw new InvalidOperationException("Batch status target is missing.");
            if (current.Status != change.ExpectedStatus && !(alreadyCommitted && current.Status == change.Status))
                throw new InvalidOperationException("Batch status target changed since preparation.");
            if (change.ExpectedEvent is { } expected && (expected.EventId != change.EventId || expected.Status != change.ExpectedStatus
                || !Same(current, expected) && !(alreadyCommitted && Same(current, expected with { Status = change.Status, Payload = change.Payload ?? expected.Payload }))))
                throw new InvalidOperationException("Batch status source changed since preparation.");
            if ((change.ExpectedPayload is null) != (change.Payload is null)
                || change.ExpectedPayload is not null && !SamePayload(current.Payload, change.ExpectedPayload)
                    && !(alreadyCommitted && SamePayload(current.Payload, change.Payload!)))
                throw new InvalidOperationException("Batch payload target changed since preparation.");
        }
        foreach (LedgerApplicationChange change in batch.ApplicationChanges ?? [])
        {
            if (!changedIds.Add(change.Expected.EventId) || ids.Contains(change.Expected.EventId)
                || string.IsNullOrWhiteSpace(change.AppliedSnapshotId) || string.IsNullOrWhiteSpace(change.DeliveredSnapshotId))
                throw new InvalidOperationException("Batch application targets must be distinct existing events with snapshot identities.");
            var current = find(change.Expected.EventId);
            bool applicationOnlyReplay = batch.Events.Count == 0 && batch.StatusChanges.Count == 0
                && (batch.ConfirmationChanges?.Count ?? 0) == 0 && (batch.RejectionChanges?.Count ?? 0) == 0;
            if (!Same(current, change.Expected) && !((alreadyCommitted || applicationOnlyReplay) && Same(current, change.Updated)))
                throw new InvalidOperationException("Batch application target changed since preparation.");
        }
        foreach (LedgerConfirmationChange change in batch.ConfirmationChanges ?? [])
        {
            var permitted = change.Expected with { Status = change.Updated.Status,
                DeliveredToSnapshotId = change.Updated.DeliveredToSnapshotId, DeliveredAtUtc = change.Updated.DeliveredAtUtc,
                AppliedSnapshotId = change.Updated.AppliedSnapshotId, AppliedAtUtc = change.Updated.AppliedAtUtc,
                LastApplicationResult = change.Updated.LastApplicationResult, LastFailureReason = change.Updated.LastFailureReason,
                NextRetryAtUtc = change.Updated.NextRetryAtUtc };
            if (!changedIds.Add(change.Expected.EventId) || ids.Contains(change.Expected.EventId)
                || !Same(permitted, change.Updated)
                || change.Updated.Status != change.Expected.Status && change.Updated.Status != ServerEventStatus.AppliedToSnapshot
                    && !(change.Updated.Status == ServerEventStatus.Conflict && change.Updated.LastApplicationResult is
                        EventApplicationResultKind.SnapshotBaseMismatch or EventApplicationResultKind.TargetObjectMissing
                        or EventApplicationResultKind.LossNotReflected or EventApplicationResultKind.NeedsManualReview))
                throw new InvalidOperationException("Invalid confirmation batch mutation.");
            var current = find(change.Expected.EventId);
            if (!Same(current, change.Expected) && !Same(current, change.Updated))
                throw new InvalidOperationException("Confirmation target changed since preparation.");
        }
        foreach (LedgerRejectionChange change in batch.RejectionChanges ?? [])
        {
            if (!changedIds.Add(change.Expected.EventId) || ids.Contains(change.Expected.EventId)
                || change.Expected.RejectionPolicy != EventRejectionPolicy.RejectableByTarget
                || change.Expected.Status is ServerEventStatus.AppliedToSnapshot or ServerEventStatus.Cancelled or ServerEventStatus.Failed
                || change.Expected.Status == ServerEventStatus.RejectedByTarget
                    && (change.Expected.TargetDecision != TargetEventDecision.Rejected || change.Expected.DecisionAtUtc is null))
                throw new InvalidOperationException("Invalid batch rejection target.");
            var current = find(change.Expected.EventId);
            if (!Same(current, change.Expected) && !(alreadyCommitted && Same(current, change.Updated)))
                throw new InvalidOperationException("Batch rejection source changed since preparation.");
        }
    }

    private static bool Same(AuthoritativeEvent? left, AuthoritativeEvent right) => left is not null
        && System.Text.Json.JsonSerializer.Serialize(left, LedgerJson.Options) == System.Text.Json.JsonSerializer.Serialize(right, LedgerJson.Options);

    private static bool SamePayload(LedgerEventPayload left, LedgerEventPayload right) =>
        System.Text.Json.JsonSerializer.Serialize(left, LedgerJson.Options) == System.Text.Json.JsonSerializer.Serialize(right, LedgerJson.Options);
}
