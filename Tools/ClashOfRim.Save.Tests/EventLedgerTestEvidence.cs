using System.Text.Json;
using AIRsLight.ClashOfRim.Events;

internal static class EventLedgerTestEvidence
{
    // Preserve the ledger's returned order for tests that compare its serialized sequence.
    public static string Serialize(IAuthoritativeEventLedger ledger) => JsonSerializer.Serialize(ledger.ListAll());
    public static bool SameRecord(AuthoritativeEvent left, AuthoritativeEvent right)
        => JsonSerializer.Serialize(left) == JsonSerializer.Serialize(right);

    public static string Snapshot(IAuthoritativeEventLedger ledger) => Snapshot(ledger.ListAll());
    public static string Snapshot(IEnumerable<AuthoritativeEvent> events) => string.Join("\n", events.OrderBy(item => item.EventId)
        .Select(item => JsonSerializer.Serialize(item) + JsonSerializer.Serialize(item.Payload, item.Payload.GetType())));
    public static bool Same(AuthoritativeEvent left, AuthoritativeEvent right) => SameRecord(left, right)
        && JsonSerializer.Serialize(left.Payload, left.Payload.GetType()) == JsonSerializer.Serialize(right.Payload, right.Payload.GetType());
}
