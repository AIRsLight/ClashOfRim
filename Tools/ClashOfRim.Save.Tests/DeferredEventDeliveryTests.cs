using System.Reflection;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using Microsoft.AspNetCore.Http;

internal static class DeferredEventDeliveryTests
{
    public static void Run(Func<string, SaveSnapshotPackage> fixture)
    {
        var now = DateTimeOffset.UtcNow;
        var package = fixture("current");
        var store = new InMemoryColonySnapshotIndexStore();
        store.StoreLatest(new LatestSnapshotRecord(package.Envelope.Identity, package.Envelope, package.Index, now));
        var state = new ClashOfRimNetworkState(snapshotStore: store);
        var gift = AuthoritativeEventFactory.Create(ServerEventType.ItemDelivery,
            new EventParty("sender", "other"), new EventParty("userA", "colonyA"), "deferred", false,
            new ItemDeliveryEventPayload([new EventThingReference("wood", "WoodLog", 3)], "later"), now) with
        { Status = ServerEventStatus.DeliveredToClient, DeliveredToSnapshotId = "old", DeliveredAtUtc = now };
        state.Ledger.Append(gift);
        Pull(state, gift.EventId, "stale", expectedAccepted: false);
        Require(state.Ledger.Find(gift.EventId) == gift, "Stale request rebound deferred delivery.");
        Pull(state, gift.EventId, "current", "sender", "other", expectedAccepted: false);
        Require(state.Ledger.Find(gift.EventId) == gift, "Sender rebound receiver delivery.");
        Pull(state, gift.EventId, "current");
        var rebound = state.Ledger.Find(gift.EventId)!;
        Require(rebound.DeliveredToSnapshotId == "current" && rebound.TargetDecision == TargetEventDecision.None
            && rebound.AppliedSnapshotId is null, "Current re-download must rebind without deciding/applying.");
        Pull(state, gift.EventId, "current");
        Require(state.Ledger.Find(gift.EventId) == rebound, "Unchanged baseline rewrote delivery time.");
        var confirmed = fixture("confirmed");
        var result = new ItemDeliveryApplicationConfirmationConsumer(state.Ledger).Consume(
            new(gift.EventId, "userA", "colonyA", "current",
                new LatestSnapshotRecord(confirmed.Envelope.Identity, confirmed.Envelope, confirmed.Index, now), "ItemDeliveryAnchored"), now);
        Require(result.Accepted, "Deferred gift could not confirm on the current baseline.");
        var applied = state.Ledger.Find(gift.EventId)!;
        Pull(state, gift.EventId, "current");
        Require(state.Ledger.Find(gift.EventId) == applied, "Re-downloading resurrected applied delivery.");

        foreach (var immutable in new[]
        {
            gift with { EventId = "accepted", IdempotencyKey = "accepted", TargetDecision = TargetEventDecision.Accepted },
            gift with { EventId = "forced", IdempotencyKey = "forced", RejectionPolicy = EventRejectionPolicy.NotRejectable }
        })
        {
            state.Ledger.Append(immutable); Pull(state, immutable.EventId, "current");
            Require(state.Ledger.Find(immutable.EventId) == immutable, "Read changed an automatic recovery baseline.");
        }
    }

    private static void Pull(ClashOfRimNetworkState state, string id, string snapshot, string user = "userA", string colony = "colonyA", bool expectedAccepted = true)
    {
        state.LoginSessions.EndUser(user);
        state.LoginSessions.TryCreate(user, colony, DateTimeOffset.UtcNow, out string session);
        string token = state.AuthTokens.Issue(user, user, colony, session, DateTimeOffset.UtcNow);
        var endpoint = typeof(ClashOfRimNetworkServer).GetMethod("PullEventDetails", BindingFlags.Static | BindingFlags.NonPublic)!;
        var response = (PullEventDetailsResponse)((IValueHttpResult)endpoint.Invoke(null,
            [new PullEventDetailsRequest(user, colony, snapshot, [id], token), state])!).Value!;
        Require(response.Result.Accepted == expectedAccepted, "Unexpected details result.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
