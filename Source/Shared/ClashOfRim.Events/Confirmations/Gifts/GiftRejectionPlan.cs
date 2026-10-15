namespace AIRsLight.ClashOfRim.Events;

/// <summary>Prepare under the ledger lock/transaction; publish the rejection and return together.</summary>
internal sealed record GiftRejectionPlan(LedgerBatch Batch, GiftReturnResult Result)
{
    internal static GiftRejectionPlan Prepare(AuthoritativeEvent source,
        Func<string, AuthoritativeEvent?> findByKey, DateTimeOffset rejectedAtUtc, string? reason, bool actorOnline)
    {
        var rejection = new LedgerRejectionChange(source, rejectedAtUtc, reason);
        var rejected = rejection.Updated;
        var candidate = GiftReturnEventFactory.CreateReturnEvent(rejected, rejected.DecisionAtUtc ?? rejectedAtUtc, actorOnline);
        var existing = findByKey(candidate.IdempotencyKey);
        if (existing is not null) candidate = candidate with { EventId = existing.EventId };
        // Batch validation binds the complete return identity/payload and the original source record.
        // Existing returns retain their delivery/application state; legacy source decisions stay fixed.
        return new(new LedgerBatch([candidate], [], RejectionChanges: [rejection]),
            new GiftReturnResult(rejected, existing ?? candidate, existing is null));
    }
}
