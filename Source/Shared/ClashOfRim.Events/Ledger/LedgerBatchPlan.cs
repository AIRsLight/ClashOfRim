namespace AIRsLight.ClashOfRim.Events;

// Compute once while the caller owns its storage lock/transaction; this step never publishes records.
internal sealed record LedgerBatchPlan(IReadOnlyList<AuthoritativeEvent> Additions, IReadOnlyList<AuthoritativeEvent> Updates)
{
    internal static LedgerBatchPlan Prepare(LedgerBatch batch, Func<string, AuthoritativeEvent?> find,
        Func<string, AuthoritativeEvent?> findByKey)
    {
        LedgerBatchValidation.Validate(batch, find, findByKey);
        var additions = batch.Events.Where(item => find(item.EventId) is null).ToArray();
        var updates = new List<AuthoritativeEvent>();
        foreach (var change in batch.StatusChanges)
        {
            var current = find(change.EventId)!;
            updates.Add(current with { Status = change.Status, Payload = change.Payload ?? current.Payload });
        }
        updates.AddRange((batch.ApplicationChanges ?? []).Select(change => change.Updated));
        updates.AddRange((batch.ConfirmationChanges ?? []).Select(change => change.Updated));
        updates.AddRange((batch.RejectionChanges ?? []).Select(change => change.Updated));
        return new(additions, updates);
    }

    internal (Dictionary<string, AuthoritativeEvent> Events, Dictionary<string, string> Keys) BuildCandidate(
        IReadOnlyDictionary<string, AuthoritativeEvent> current)
    {
        var events = new Dictionary<string, AuthoritativeEvent>(current, StringComparer.Ordinal);
        foreach (var item in Additions) events.Add(item.EventId, item);
        foreach (var item in Updates) events[item.EventId] = item;
        return (events, events.Values.ToDictionary(item => item.IdempotencyKey, item => item.EventId, StringComparer.Ordinal));
    }
}
