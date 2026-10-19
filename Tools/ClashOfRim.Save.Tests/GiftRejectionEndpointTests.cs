using System.Reflection;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Protocol;
using Microsoft.Data.Sqlite;
using static GiftRejectionCase;
using static GiftRejectionTestClient;
using static SessionTestAuthentication;
using static EventLedgerTestEvidence;
using static SnapshotTestAssertions;

internal static class GiftRejectionEndpointTests
{
    public static void Run()
    {
        SnapshotEndpointTestClient.LoadLocalization();
        Guards();
        foreach (bool replay in new[] { false, true })
        foreach (bool replaced in new[] { false, true })
        {
            var state = new ClashOfRimNetworkState(); var scenario = new GiftRejectionCase(state.Ledger); var gift = scenario.Source;
            string token = Token(state, "receiver", "away");
            if (replay) Check(Reject(state, Request(gift, token)).Result.Accepted, "initial gift rejection for queued retry failed");
            SnapshotMutationAuthorizationProbe.RevokedWhileWaiting(state, "receiver", "away", replaced,
                () => Reject(state, Request(gift, token)).Result.Accepted);
        }
        foreach (string storage in new[] { "memory", "file", "sqlite" })
        {
            foreach (bool reopen in new[] { false, true }) InterruptedNotification(storage, reopen);
            foreach (string conflict in new[] { "actor", "target", "quantity", "purpose", "type" }) ConflictingReturn(storage, conflict);
            LegacyRepair(storage);
        }
        foreach (string phase in new[] { "insert", "update" }) SqlRollback(phase);
        ConcurrentRejections();
    }

    private static void Guards()
    {
        var state = new ClashOfRimNetworkState(); var scenario = new GiftRejectionCase(state.Ledger); var gift = scenario.Source;
        string token = Token(state, "receiver", "away");
        string ownerToken = Token(state, "sender", "home");
        var valid = Request(gift, token);
        string original = Serialize(state.Ledger);
        foreach (var bad in new[] { Request(gift, null), Request(gift, "invalid"), Request(gift, ownerToken),
            new RejectGiftRequest(gift.EventId, "receiver", "other-colony", "base", null, token) })
            Check(!Reject(state, bad).Result.Accepted && Serialize(state.Ledger) == original, "unauthenticated/impersonated rejection changed the gift");
        var invisible = new RejectGiftRequest(gift.EventId, "sender", "home", "base", null, ownerToken);
        Check(Reject(state, invisible).Result.ErrorCode == ProtocolErrorCode.EventNotFound, "authenticated sender could reject receiver's gift");
        state.LoginSessions.EndUser("receiver");
        Check(!Reject(state, valid).Result.Accepted, "expired session accepted");
        token = Token(state, "receiver", "away"); valid = Request(gift, token);
        foreach (var status in new[] { ServerEventStatus.AppliedToSnapshot, ServerEventStatus.Cancelled, ServerEventStatus.Failed })
        {
            state.Ledger.ChangeStatus(gift.EventId, status); original = Serialize(state.Ledger);
            Check(Reject(state, valid).Result.ErrorCode == ProtocolErrorCode.Conflict && Serialize(state.Ledger) == original,
                "closed gift accepted: " + status);
        }
        var forced = CreateGift("forced") with { RejectionPolicy = EventRejectionPolicy.NotRejectable };
        state.Ledger.Append(forced);
        Check(!Reject(state, Request(forced, token)).Result.Accepted, "forced gift was rejected");
        var trade = CreateGift("not-gift") with { Payload = new ItemDeliveryEventPayload([], null, Purpose: ItemDeliveryPurpose.TradeCancelledOwnerReturn) };
        state.Ledger.Append(trade);
        Check(!Reject(state, Request(trade, token)).Result.Accepted, "non-gift accepted");
        Check(state.EventNotifications.GetVersion("sender") == 0 && state.EventNotifications.GetVersion("receiver") == 0,
            "invalid requests notified players");
    }

    private static void InterruptedNotification(string storage, bool reopen) => SqliteTestDirectory.With("cor-gift-endpoint-", root =>
    {
        var ledger = Open(storage, root); var scenario = new GiftRejectionCase(ledger); var gift = scenario.Source;
        var proxy = DispatchProxy.Create<IAuthoritativeEventLedger, InterruptedEventLedger>();
        var interceptor = (InterruptedEventLedger)proxy; interceptor.Inner = ledger;
        interceptor.AfterCall = method =>
        {
            if (method == nameof(IAuthoritativeEventLedger.RejectGiftAndCreateReturn))
                throw new IOException("committed before notification");
        };
        var state = new ClashOfRimNetworkState(ledger: proxy);
        string token = Token(state, "receiver", "away");
        bool failed = false;
        try { Reject(state, Request(gift, token)); } catch (IOException) { failed = true; }
        Check(failed && ledger.Find(gift.EventId)!.Status == ServerEventStatus.RejectedByTarget && ledger.ListAll().Count == 2,
            "post-commit interruption not exercised");
        Check(state.EventNotifications.GetVersion("sender") == 0, "interruption followed notification");
        var returned = ledger.FindByIdempotencyKey(gift.IdempotencyKey + ":return")!;
        ledger.MarkDelivered(returned.EventId, "base", At); ledger.MarkApplied(returned.EventId, "home", At);
        string before = Serialize(ledger);
        interceptor.AfterCall = null;
        if (reopen)
        {
            state = new ClashOfRimNetworkState(ledger: storage == "memory" ? ledger : Open(storage, root));
            token = Token(state, "receiver", "away");
        }
        // Duplicate requests must not bypass authentication either.
        Check(!Reject(state, Request(gift, null)).Result.Accepted && state.EventNotifications.GetVersion("sender") == 0,
            "duplicate bypassed authentication");
        var result = Reject(state, Request(gift, token, "different retry reason"));
        Check(result.Result.Accepted && result.Result.ErrorCode == ProtocolErrorCode.DuplicateRequest
            && result.ReturnEventId == returned.EventId && !result.ReturnEventCreated && Serialize(state.Ledger) == before,
            "retry changed first decision, return identity or consumption");
        Check(state.EventNotifications.GetVersion("sender") == 1 && state.EventNotifications.GetVersion("receiver") == 1,
            "retry lost source or return notification");
    });

    private static void ConflictingReturn(string storage, string conflict) => SqliteTestDirectory.With("cor-gift-endpoint-", root =>
    {
        var ledger = Open(storage, root); var scenario = new GiftRejectionCase(ledger); var gift = scenario.Source;
        ledger.MarkRejected(gift.EventId, At, "original");
        var returned = GiftReturnEventFactory.CreateReturnEvent(gift, At, false);
        returned = conflict switch
        {
            "actor" => returned with { Actor = new("stranger", "away") },
            "target" => returned with { Target = new("stranger", "home") },
            "quantity" => returned with { Payload = ((ItemDeliveryEventPayload)returned.Payload) with { Items = [] } },
            "purpose" => returned with { Payload = ((ItemDeliveryEventPayload)returned.Payload) with { Purpose = ItemDeliveryPurpose.Gift } },
            _ => returned with { Type = ServerEventType.Trade }
        };
        ledger.Append(returned);
        if (storage != "memory") ledger = Open(storage, root);
        var state = new ClashOfRimNetworkState(ledger: ledger); var token = Token(state, "receiver", "away");
        string before = Serialize(ledger);
        Check(Reject(state, Request(gift, token)).Result.ErrorCode == ProtocolErrorCode.Conflict && Serialize(ledger) == before,
            "duplicate accepted a conflicting return: " + conflict);
        Check(state.EventNotifications.GetVersion("sender") == 0 && state.EventNotifications.GetVersion("stranger") == 0,
            "conflicting return woke a recipient");
    });

    private static void LegacyRepair(string storage) => SqliteTestDirectory.With("cor-gift-endpoint-", root =>
    {
        var ledger = Open(storage, root); var scenario = new GiftRejectionCase(ledger); var gift = scenario.Source;
        ledger.MarkRejected(gift.EventId, At, "first");
        var state = new ClashOfRimNetworkState(ledger: ledger); var token = Token(state, "receiver", "away");
        var result = Reject(state, Request(gift, token, "later"));
        Check(result.Result.Accepted && result.ReturnEventCreated && ledger.Find(gift.EventId)!.DecisionReason == "first"
            && ledger.Find(gift.EventId)!.DecisionAtUtc == At, "old partial rejection could not be repaired");
    });

    private static void SqlRollback(string phase) => SqliteTestDirectory.With("cor-gift-endpoint-", root =>
    {
        var ledger = Open("sqlite", root); var scenario = new GiftRejectionCase(ledger); var gift = scenario.Source;
        var state = new ClashOfRimNetworkState(ledger: ledger); var token = Token(state, "receiver", "away");
        using var connection = new SqliteConnection("Data Source=" + Path.Combine(root, "events.sqlite")); connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"create trigger fail_gift before {phase} on events begin select raise(abort, 'gift'); end;"; command.ExecuteNonQuery();
        string before = Serialize(ledger); bool failed = false;
        try { Reject(state, Request(gift, token)); } catch (SqliteException) { failed = true; }
        Check(failed && Serialize(ledger) == before && Serialize(Open("sqlite", root)) == before
            && state.EventNotifications.GetVersion("sender") == 0, "failed endpoint leaked state or notification");
        command.CommandText = "drop trigger fail_gift;"; command.ExecuteNonQuery();
        Check(Reject(state, Request(gift, token)).ReturnEventCreated, "endpoint retry failed");
    });

    private static void ConcurrentRejections()
    {
        var state = new ClashOfRimNetworkState(); var scenario = new GiftRejectionCase(state.Ledger); var gift = scenario.Source; var token = Token(state, "receiver", "away");
        var results = new System.Collections.Concurrent.ConcurrentBag<RejectGiftResponse>();
        Parallel.For(0, 8, i => results.Add(Reject(state, Request(gift, token, "reason-" + i))));
        Check(results.All(r => r.Result.Accepted) && results.Count(r => r.ReturnEventCreated) == 1
            && results.Select(r => r.ReturnEventId).Distinct().Count() == 1 && state.Ledger.ListAll().Count == 2, "concurrent endpoint rejection diverged");
    }

    private static IAuthoritativeEventLedger Open(string storage, string root) => storage switch
    {
        "file" => new FileAuthoritativeEventLedger(Path.Combine(root, "events.json")),
        "sqlite" => new SqliteAuthoritativeEventLedger(Path.Combine(root, "events.sqlite")),
        _ => new InMemoryAuthoritativeEventLedger()
    };
}
