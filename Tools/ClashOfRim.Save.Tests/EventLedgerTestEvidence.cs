using System.Text.Json;
using AIRsLight.ClashOfRim.Events;

internal static class EventLedgerTestEvidence
{
    public static string Snapshot(IAuthoritativeEventLedger ledger) => Snapshot(ledger.ListAll());
    public static string Snapshot(IEnumerable<AuthoritativeEvent> events) => string.Join("\n", events.OrderBy(item => item.EventId)
        .Select(item => JsonSerializer.Serialize(item) + JsonSerializer.Serialize(item.Payload, item.Payload.GetType())));
    public static bool Same(AuthoritativeEvent left, AuthoritativeEvent right) => JsonSerializer.Serialize(left) == JsonSerializer.Serialize(right)
        && JsonSerializer.Serialize(left.Payload, left.Payload.GetType()) == JsonSerializer.Serialize(right.Payload, right.Payload.GetType());
}
