namespace AIRsLight.ClashOfRim.Events;

/// <summary>Commits delivery, snapshot application and its successful result as one ledger mutation.</summary>
public static class SnapshotApplicationCommit
{
    public static AuthoritativeEvent Apply(IAuthoritativeEventLedger ledger, AuthoritativeEvent expected,
        string deliveredSnapshotId, string appliedSnapshotId, DateTimeOffset appliedAtUtc)
    {
        if (expected.Status == ServerEventStatus.AppliedToSnapshot)
        {
            // Repair a legacy MarkApplied/ReportApplicationResult interruption without
            // attributing an already-consumed event to the retry's snapshot or time.
            if (string.IsNullOrWhiteSpace(expected.AppliedSnapshotId) || expected.AppliedAtUtc is null) return expected;
            appliedSnapshotId = expected.AppliedSnapshotId;
            appliedAtUtc = expected.AppliedAtUtc.Value;
            if (!string.IsNullOrWhiteSpace(expected.DeliveredToSnapshotId)) deliveredSnapshotId = expected.DeliveredToSnapshotId;
        }
        var change = new LedgerApplicationChange(expected, deliveredSnapshotId, appliedSnapshotId, appliedAtUtc);
        var updated = change.Updated;
        if (updated == expected) return expected;
        ledger.ApplyBatch(new LedgerBatch([], [], [change]));
        return updated;
    }
}
