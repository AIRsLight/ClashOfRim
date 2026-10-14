namespace AIRsLight.ClashOfRim.Events;

/// <summary>Expected statuses prevent a stale trade settlement from closing a changed order.</summary>
public sealed record LedgerStatusChange(string EventId, ServerEventStatus ExpectedStatus, ServerEventStatus Status);

public sealed record LedgerApplicationChange(AuthoritativeEvent Expected, string DeliveredSnapshotId,
    string AppliedSnapshotId, DateTimeOffset AppliedAtUtc)
{
    public AuthoritativeEvent Updated => Expected with
    {
        Status = ServerEventStatus.AppliedToSnapshot,
        DeliveredToSnapshotId = Expected.DeliveredToSnapshotId ?? DeliveredSnapshotId,
        DeliveredAtUtc = Expected.DeliveredAtUtc ?? AppliedAtUtc,
        AppliedSnapshotId = AppliedSnapshotId,
        AppliedAtUtc = AppliedAtUtc,
        LastApplicationResult = EventApplicationResultKind.Applied,
        LastFailureReason = null,
        NextRetryAtUtc = null
    };
}

public sealed record LedgerBatch(IReadOnlyList<AuthoritativeEvent> Events, IReadOnlyList<LedgerStatusChange> StatusChanges,
    IReadOnlyList<LedgerApplicationChange>? ApplicationChanges = null);

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
        }
        foreach (LedgerApplicationChange change in batch.ApplicationChanges ?? [])
        {
            if (!changedIds.Add(change.Expected.EventId) || ids.Contains(change.Expected.EventId)
                || string.IsNullOrWhiteSpace(change.AppliedSnapshotId) || string.IsNullOrWhiteSpace(change.DeliveredSnapshotId))
                throw new InvalidOperationException("Batch application targets must be distinct existing events with snapshot identities.");
            var current = find(change.Expected.EventId);
            if (!Same(current, change.Expected) && !(alreadyCommitted && Same(current, change.Updated)))
                throw new InvalidOperationException("Batch application target changed since preparation.");
        }
    }

    private static bool Same(AuthoritativeEvent? left, AuthoritativeEvent right) => left is not null
        && System.Text.Json.JsonSerializer.Serialize(left, LedgerJson.Options) == System.Text.Json.JsonSerializer.Serialize(right, LedgerJson.Options);
}
