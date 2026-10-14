namespace AIRsLight.ClashOfRim.Events;

/// <summary>The stable identity bound to a globally unique ledger idempotency key.</summary>
internal static class LedgerEventIdentity
{
    public static bool Matches(AuthoritativeEvent existing, AuthoritativeEvent candidate) =>
        existing.EventId == candidate.EventId && existing.IdempotencyKey == candidate.IdempotencyKey
        && existing.Type == candidate.Type && SameParty(existing.Actor, candidate.Actor) && SameParty(existing.Target, candidate.Target);

    private static bool SameParty(EventParty left, EventParty right) => left.UserId == right.UserId
        && OptionalIdentity(left.ColonyId) == OptionalIdentity(right.ColonyId)
        && OptionalIdentity(left.FactionId) == OptionalIdentity(right.FactionId);

    // SQLite has always persisted blank optional party fields as NULL. Match that identity on every backend.
    private static string? OptionalIdentity(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    public static void ValidateReplay(AuthoritativeEvent existing, AuthoritativeEvent candidate)
    {
        if (!Matches(existing, candidate))
            throw new InvalidOperationException("Idempotency key is already bound to a different event identity.");
    }
}
