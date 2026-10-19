using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Protocol;
using static SnapshotTestAssertions;

internal sealed class GiftRejectionCase
{
    public static readonly DateTimeOffset At = DateTimeOffset.UnixEpoch.AddDays(4);
    public AuthoritativeEvent Source { get; }

    public GiftRejectionCase(IAuthoritativeEventLedger ledger)
    {
        Source = CreateGift();
        ledger.Append(Source);
    }

    public static AuthoritativeEvent CreateGift(string key = "gift") => AuthoritativeEventFactory.Create(ServerEventType.ItemDelivery,
        new("sender", "home"), new("receiver", "away"), key, false,
        new ItemDeliveryEventPayload([new("steel", "Steel", 10, "source")], "gift"), At);

    public void AssertReturned(IAuthoritativeEventLedger ledger, int expectedEventCount)
    {
        var source = ledger.Find(Source.EventId)!;
        var returned = ledger.FindByIdempotencyKey(Source.IdempotencyKey + ":return");
        Check(source.Status == ServerEventStatus.RejectedByTarget && source.DecisionReason == "first" && returned is not null
            && returned.Actor == Source.Target && returned.Target == Source.Actor
            && ((ItemDeliveryEventPayload)returned.Payload).Items.SequenceEqual(((ItemDeliveryEventPayload)Source.Payload).Items)
            && ledger.ListAll().Count == expectedEventCount, "Gift return lost the original items, parties or decision.");
    }
}
