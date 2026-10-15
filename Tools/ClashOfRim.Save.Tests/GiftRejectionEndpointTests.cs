using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Protocol;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;

internal static class GiftRejectionEndpointTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.UnixEpoch.AddDays(4);
    public static void Run()
    {
        SnapshotEndpointTestClient.LoadLocalization();
        Guards();
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
        var state = new ClashOfRimNetworkState(); var gift = Seed(state.Ledger);
        string token = Token(state, "receiver", "away");
        string ownerToken = Token(state, "sender", "home");
        var valid = Request(gift, token);
        string original = Snapshot(state.Ledger);
        foreach (var bad in new[] { Request(gift, null), Request(gift, "invalid"), Request(gift, ownerToken),
            new RejectGiftRequest(gift.EventId, "receiver", "other-colony", "base", null, token) })
            Require(!Reject(state, bad).Result.Accepted && Snapshot(state.Ledger) == original, "unauthenticated/impersonated rejection changed the gift");
        var invisible = new RejectGiftRequest(gift.EventId, "sender", "home", "base", null, ownerToken);
        Require(Reject(state, invisible).Result.ErrorCode == ProtocolErrorCode.EventNotFound, "authenticated sender could reject receiver's gift");
        state.LoginSessions.EndUser("receiver");
        Require(!Reject(state, valid).Result.Accepted, "expired session accepted");
        token = Token(state, "receiver", "away"); valid = Request(gift, token);
        foreach (var status in new[] { ServerEventStatus.AppliedToSnapshot, ServerEventStatus.Cancelled, ServerEventStatus.Failed })
        {
            state.Ledger.ChangeStatus(gift.EventId, status); original = Snapshot(state.Ledger);
            Require(Reject(state, valid).Result.ErrorCode == ProtocolErrorCode.Conflict && Snapshot(state.Ledger) == original,
                "closed gift accepted: " + status);
        }
        var forced = Gift("forced") with { RejectionPolicy = EventRejectionPolicy.NotRejectable };
        state.Ledger.Append(forced);
        Require(!Reject(state, Request(forced, token)).Result.Accepted, "forced gift was rejected");
        var trade = Gift("not-gift") with { Payload = new ItemDeliveryEventPayload([], null, Purpose: ItemDeliveryPurpose.TradeCancelledOwnerReturn) };
        state.Ledger.Append(trade);
        Require(!Reject(state, Request(trade, token)).Result.Accepted, "non-gift accepted");
        Require(state.EventNotifications.GetVersion("sender") == 0 && state.EventNotifications.GetVersion("receiver") == 0,
            "invalid requests notified players");
    }

    private static void InterruptedNotification(string storage, bool reopen) => WithRoot(root =>
    {
        var ledger = Open(storage, root); var gift = Seed(ledger);
        var proxy = DispatchProxy.Create<IAuthoritativeEventLedger, InterruptedLedger>();
        var interceptor = (InterruptedLedger)proxy; interceptor.Inner = ledger;
        interceptor.AfterReject = () => throw new IOException("committed before notification");
        var state = new ClashOfRimNetworkState(ledger: proxy);
        string token = Token(state, "receiver", "away");
        bool failed = false;
        try { Reject(state, Request(gift, token)); } catch (IOException) { failed = true; }
        Require(failed && ledger.Find(gift.EventId)!.Status == ServerEventStatus.RejectedByTarget && ledger.ListAll().Count == 2,
            "post-commit interruption not exercised");
        Require(state.EventNotifications.GetVersion("sender") == 0, "interruption followed notification");
        var returned = ledger.FindByIdempotencyKey(gift.IdempotencyKey + ":return")!;
        ledger.MarkDelivered(returned.EventId, "base", At); ledger.MarkApplied(returned.EventId, "home", At);
        string before = Snapshot(ledger);
        interceptor.AfterReject = null;
        if (reopen)
        {
            state = new ClashOfRimNetworkState(ledger: storage == "memory" ? ledger : Open(storage, root));
            token = Token(state, "receiver", "away");
        }
        // Duplicate requests must not bypass authentication either.
        Require(!Reject(state, Request(gift, null)).Result.Accepted && state.EventNotifications.GetVersion("sender") == 0,
            "duplicate bypassed authentication");
        var result = Reject(state, Request(gift, token, "different retry reason"));
        Require(result.Result.Accepted && result.Result.ErrorCode == ProtocolErrorCode.DuplicateRequest
            && result.ReturnEventId == returned.EventId && !result.ReturnEventCreated && Snapshot(state.Ledger) == before,
            "retry changed first decision, return identity or consumption");
        Require(state.EventNotifications.GetVersion("sender") == 1 && state.EventNotifications.GetVersion("receiver") == 1,
            "retry lost source or return notification");
    });

    private static void ConflictingReturn(string storage, string conflict) => WithRoot(root =>
    {
        var ledger = Open(storage, root); var gift = Seed(ledger);
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
        string before = Snapshot(ledger);
        Require(Reject(state, Request(gift, token)).Result.ErrorCode == ProtocolErrorCode.Conflict && Snapshot(ledger) == before,
            "duplicate accepted a conflicting return: " + conflict);
        Require(state.EventNotifications.GetVersion("sender") == 0 && state.EventNotifications.GetVersion("stranger") == 0,
            "conflicting return woke a recipient");
    });

    private static void LegacyRepair(string storage) => WithRoot(root =>
    {
        var ledger = Open(storage, root); var gift = Seed(ledger);
        ledger.MarkRejected(gift.EventId, At, "first");
        var state = new ClashOfRimNetworkState(ledger: ledger); var token = Token(state, "receiver", "away");
        var result = Reject(state, Request(gift, token, "later"));
        Require(result.Result.Accepted && result.ReturnEventCreated && ledger.Find(gift.EventId)!.DecisionReason == "first"
            && ledger.Find(gift.EventId)!.DecisionAtUtc == At, "old partial rejection could not be repaired");
    });

    private static void SqlRollback(string phase) => WithRoot(root =>
    {
        var ledger = Open("sqlite", root); var gift = Seed(ledger);
        var state = new ClashOfRimNetworkState(ledger: ledger); var token = Token(state, "receiver", "away");
        using var connection = new SqliteConnection("Data Source=" + Path.Combine(root, "events.sqlite")); connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"create trigger fail_gift before {phase} on events begin select raise(abort, 'gift'); end;"; command.ExecuteNonQuery();
        string before = Snapshot(ledger); bool failed = false;
        try { Reject(state, Request(gift, token)); } catch (SqliteException) { failed = true; }
        Require(failed && Snapshot(ledger) == before && Snapshot(Open("sqlite", root)) == before
            && state.EventNotifications.GetVersion("sender") == 0, "failed endpoint leaked state or notification");
        command.CommandText = "drop trigger fail_gift;"; command.ExecuteNonQuery();
        Require(Reject(state, Request(gift, token)).ReturnEventCreated, "endpoint retry failed");
    });

    private static void ConcurrentRejections()
    {
        var state = new ClashOfRimNetworkState(); var gift = Seed(state.Ledger); var token = Token(state, "receiver", "away");
        var results = new System.Collections.Concurrent.ConcurrentBag<RejectGiftResponse>();
        Parallel.For(0, 8, i => results.Add(Reject(state, Request(gift, token, "reason-" + i))));
        Require(results.All(r => r.Result.Accepted) && results.Count(r => r.ReturnEventCreated) == 1
            && results.Select(r => r.ReturnEventId).Distinct().Count() == 1 && state.Ledger.ListAll().Count == 2, "concurrent endpoint rejection diverged");
    }

    private static AuthoritativeEvent Gift(string key = "gift") => AuthoritativeEventFactory.Create(ServerEventType.ItemDelivery,
        new("sender", "home"), new("receiver", "away"), key, false,
        new ItemDeliveryEventPayload([new("steel", "Steel", 10, "source")], "gift"), At);
    private static AuthoritativeEvent Seed(IAuthoritativeEventLedger ledger) { var gift = Gift(); ledger.Append(gift); return gift; }
    private static RejectGiftRequest Request(AuthoritativeEvent gift, string? token, string reason = "first") =>
        new(gift.EventId, "receiver", "away", "base", reason, token);
    private static string Token(ClashOfRimNetworkState state, string user, string colony)
    {
        var now = DateTimeOffset.UtcNow; state.LoginSessions.TryCreate(user, colony, now, out var session);
        return state.AuthTokens.Issue(user, user, colony, session, now);
    }
    private static RejectGiftResponse Reject(ClashOfRimNetworkState state, RejectGiftRequest request)
    {
        try { return (RejectGiftResponse)((IValueHttpResult)typeof(ClashOfRimNetworkServer).GetMethod("RejectGift", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [request, state])!).Value!; }
        catch (TargetInvocationException ex) when (ex.InnerException is not null) { ExceptionDispatchInfo.Capture(ex.InnerException).Throw(); throw; }
    }
    private static IAuthoritativeEventLedger Open(string storage, string root) => storage switch
    {
        "file" => new FileAuthoritativeEventLedger(Path.Combine(root, "events.json")),
        "sqlite" => new SqliteAuthoritativeEventLedger(Path.Combine(root, "events.sqlite")),
        _ => new InMemoryAuthoritativeEventLedger()
    };
    private static string Snapshot(IAuthoritativeEventLedger ledger) => JsonSerializer.Serialize(ledger.ListAll());
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    private static void WithRoot(Action<string> action)
    {
        string root = Path.Combine(Path.GetTempPath(), "cor-gift-endpoint-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try { action(root); } finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, recursive: true); }
    }
    public class InterruptedLedger : DispatchProxy
    {
        public IAuthoritativeEventLedger Inner = null!;
        public Action? AfterReject;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            object? result;
            try { result = method!.Invoke(Inner, args); }
            catch (TargetInvocationException ex) when (ex.InnerException is not null) { ExceptionDispatchInfo.Capture(ex.InnerException).Throw(); throw; }
            if (method!.Name == nameof(IAuthoritativeEventLedger.RejectGiftAndCreateReturn)) AfterReject?.Invoke();
            return result;
        }
    }
}
