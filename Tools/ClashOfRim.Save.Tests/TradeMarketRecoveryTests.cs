using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Network.Plugins;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;

internal static class TradeMarketRecoveryTests
{
    public static void Run(Func<string, SaveSnapshotPackage> factory)
    {
        int count = 0; void Case(Action action) { action(); count++; }
        foreach (string invalid in new[] { "missing-token", "token", "user", "colony", "missing-user", "missing-colony", "missing-snapshot", "logout", "replace", "expired", "malformed", "empty", "null" })
            Case(() => Invalid(factory, invalid));
        foreach (string change in new[] { "none", "logout", "replace", "revoke", "expired", "failure", "revoke-failure", "compete", "disable", "extend", "terminal" })
            Case(() => DuringRecovery(factory, change));
        foreach (string change in new[] { "revoke", "replace", "disable", "terminal" }) Case(() => Published(factory, change));
        foreach (string scope in new[] { "Open", "Mine", "AcceptedByMe", "History" }) Case(() => Scopes(factory, scope));
        foreach (string kind in new[] { "expiration", "baseline" })
        {
            foreach (string change in new[] { "none", "failure", "compete" }) Case(() => Standalone(factory, kind, change));
            Case(() => Nested(factory, kind));
        }
        Case(() => Concurrent(factory)); Case(() => RecoveredGuard(factory));
        Console.WriteLine($"Trade market recovery scenarios passed: {count}");
    }

    private static void Invalid(Func<string, SaveSnapshotPackage> factory, string invalid)
    {
        using var e = new Env(factory); var f = e.F;
        if (invalid is "logout" or "replace") f.State.LoginSessions.EndUser("receiver");
        if (invalid == "replace") f.State.LoginSessions.TryCreate("receiver", "away", DateTimeOffset.UtcNow, out _);
        if (invalid == "expired") Expire(f);
        string before = f.Evidence(); long notifications = f.State.EventNotifications.GetVersion("seller");
        using var response = Call(e, invalid);
        if (invalid is "malformed" or "empty" or "null") Require(response.StatusCode == HttpStatusCode.BadRequest, "Malformed market request was not rejected.");
        else Require(!Body(response).Result.Accepted && Body(response).Orders.Count == 0, "Invalid market identity accepted or disclosed orders: " + invalid);
        f.AssertPending(before, "market/" + invalid); Require(f.State.EventNotifications.GetVersion("seller") == notifications, "Denied market query signalled a refund.");
    }

    private static void DuringRecovery(Func<string, SaveSnapshotPackage> factory, string change)
    {
        using var e = new Env(factory); var f = e.F; HttpResponseMessage? response = null; string? before = null;
        SnapshotPreparationGateProbe.Run(f.State.RaidSettlementSnapshotMutationGate, hook => f.Store.BeforePrepare = hook,
            () => response = Call(e), () =>
            {
                Change(e, change);
                if (change is "failure" or "revoke-failure") f.Store.FailPreparation = true;
                if (change == "compete") ClashOfRimNetworkServer.RecoverSnapshotCommits(f.State);
                before = f.Evidence();
            });
        using (response)
        {
            bool accepted = change is "none" or "compete" or "disable" or "extend" or "terminal";
            if (change == "failure") Require(response!.StatusCode == HttpStatusCode.InternalServerError, "Preparation failure was swallowed.");
            else Require(Body(response!).Result.Accepted == accepted, "Unexpected market recovery outcome: " + change);
            if (accepted)
            {
                f.AssertRecovered(); if (change is "none" or "compete") e.AssertExpired(); else e.AssertNoExpiration();
            }
            else
            {
                f.AssertPending(before!, "market/" + change); e.AssertNoExpiration();
                f.Store.FailPreparation = false; f.RenewToken(); using var retry = Call(e);
                Require(Body(retry).Result.Accepted, "Fresh market request could not retry."); e.AssertExpired(); f.AssertRecovered();
            }
        }
    }

    private static void Published(Func<string, SaveSnapshotPackage> factory, string change)
    {
        using var e = new Env(factory); e.Publication = () => Change(e, change);
        using var response = Call(e);
        bool denied = change is "revoke" or "replace";
        Require(Body(response).Result.Accepted == !denied && (!denied || Body(response).Orders.Count == 0), "Publication bypassed market reauthorization.");
        e.F.AssertRecovered(); e.AssertNoExpiration();
        e.Publication = null; e.F.RenewToken();
        if (denied) { using var retry = Call(e); Require(Body(retry).Result.Accepted, "Market retry after publication failed."); e.AssertExpired(); }
    }

    private static void Scopes(Func<string, SaveSnapshotPackage> factory, string scope)
    {
        using var e = new Env(factory);
        if (scope != "History") e.F.State.UpdateServerConfiguration(new(tradeOrderExpiration: TimeSpan.Zero));
        using var response = e.F.Post(ProtocolMessageKind.ListTradeOrders, new ListTradeOrdersRequest("receiver", "away", "base", scope, -4, 0, e.F.Token));
        var result = Body(response);
        Require(result.Result.Accepted && result.Offset == 0 && result.Limit == 10 && result.TotalCount == (scope == "Mine" ? 0 : 1)
            && result.Orders.Count == result.TotalCount && !result.HasMore, "Authenticated market scope or pagination changed: " + scope);
        using var emptyPage = e.F.Post(ProtocolMessageKind.ListTradeOrders, new ListTradeOrdersRequest("receiver", "away", "base", scope, 2, 100, e.F.Token));
        var page = Body(emptyPage);
        Require(page.Result.Accepted && page.Orders.Count == 0 && page.Offset == 2 && page.Limit == 50 && page.TotalCount == result.TotalCount, "Market page boundaries changed.");
    }

    private static void Standalone(Func<string, SaveSnapshotPackage> factory, string kind, string change)
    {
        using var e = new Env(factory); var f = e.F; Exception? failure = null;
        SnapshotPreparationGateProbe.Run(f.State.RaidSettlementSnapshotMutationGate, hook => f.Store.BeforePrepare = hook,
            () => { try { Maintain(e, kind); } catch (IOException ex) { failure = ex; } }, () =>
            {
                if (change == "failure") f.Store.FailPreparation = true;
                if (change == "compete") ClashOfRimNetworkServer.RecoverSnapshotCommits(f.State);
            });
        Require((failure is not null) == (change == "failure"), "Standalone maintenance recovery failed unexpectedly.");
        if (failure is not null) { e.AssertNoExpiration(); f.Store.FailPreparation = false; Maintain(e, kind); }
        e.AssertExpired(kind == "baseline"); f.AssertRecovered();
    }
    private static void Nested(Func<string, SaveSnapshotPackage> factory, string kind)
    {
        using var e = new Env(factory); var f = e.F; bool published = false;
        e.Publication = () => { Require(Monitor.IsEntered(f.State.RaidSettlementSnapshotMutationGate), "Nested maintenance released its caller's gate."); published = true; };
        using (f.State.EnterSnapshotMutation())
        {
            Maintain(e, kind);
            Require(Monitor.IsEntered(f.State.RaidSettlementSnapshotMutationGate), "Nested maintenance consumed caller ownership.");
        }
        Require(published && f.Store.RawWrites == 1, "Nested maintenance skipped pending recovery or changed the nested storage path."); e.AssertExpired(kind == "baseline"); f.AssertRecovered();
    }
    private static void RecoveredGuard(Func<string, SaveSnapshotPackage> factory)
    {
        using var e = new Env(factory); string before = e.F.Evidence(); bool rejected = false;
        try { Invoke("ApplyRecoveredTradeOrderExpirations", e.F.State, DateTimeOffset.UtcNow); } catch (InvalidOperationException) { rejected = true; }
        Require(rejected && before == e.F.Evidence(), "Recovered expiration helper accepted an unowned mutation scope.");
    }
    private static void Concurrent(Func<string, SaveSnapshotPackage> factory)
    {
        using var e = new Env(factory);
        Task.WaitAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => { using var result = Call(e); Require(Body(result).Result.Accepted, "Concurrent market query failed."); })).ToArray());
        e.AssertExpired(); e.F.AssertRecovered();
        Require(e.F.State.EventNotifications.GetVersion("seller") == 1, "Concurrent queries duplicated the expiration wakeup.");
    }
    private static void Maintain(Env e, string kind)
    {
        if (kind == "expiration") Invoke("ApplyTradeOrderExpirations", e.F.State, DateTimeOffset.UtcNow);
        else Invoke("ApplyTradeOrderBaselineInvalidations", e.F.State,
            new AdminBaselineSnapshot("admin", "colony", DateTimeOffset.UnixEpoch, new Dictionary<string, float> { ["Silver"] = 1 }, []), DateTimeOffset.UtcNow);
    }
    private static void Invoke(string name, params object[] args)
    {
        try { typeof(ClashOfRimNetworkServer).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, args); }
        catch (TargetInvocationException ex) when (ex.InnerException is not null) { ExceptionDispatchInfo.Capture(ex.InnerException).Throw(); throw; }
    }
    private static void Change(Env e, string change)
    {
        var f = e.F;
        if (change is "logout" or "replace") f.State.LoginSessions.EndUser("receiver");
        if (change == "replace") f.State.LoginSessions.TryCreate("receiver", "away", DateTimeOffset.UtcNow, out _);
        if (change is "revoke" or "revoke-failure") f.State.AuthTokens.RevokeForUser("receiver");
        if (change == "expired") Expire(f);
        if (change == "disable") f.State.UpdateServerConfiguration(new(tradeOrderExpiration: TimeSpan.Zero));
        if (change == "extend") f.State.UpdateServerConfiguration(new(tradeOrderExpiration: TimeSpan.FromDays(1)));
        if (change == "terminal") f.State.Ledger.ChangeStatus(e.Order.EventId, ServerEventStatus.Failed);
    }
    private static void Expire(HttpSnapshotRecoveryTests.Fixture f)
    {
        f.State.LoginSessions.EndUser("receiver"); f.State.LoginSessions.TryCreate("receiver", "away", DateTimeOffset.UtcNow.AddHours(-1), out var session);
        f.Token = f.State.AuthTokens.Issue("receiver", "receiver", "away", session, DateTimeOffset.UtcNow);
    }
    private static HttpResponseMessage Call(Env e, string invalid = "")
    {
        if (invalid is "malformed" or "empty" or "null") return e.F.Client.PostAsync(ProtocolContractManifest.Find(ProtocolMessageKind.ListTradeOrders).Route,
            new StringContent(invalid == "malformed" ? "{\"" : invalid == "null" ? "null" : "", Encoding.UTF8, "application/json")).GetAwaiter().GetResult();
        return e.F.Post(ProtocolMessageKind.ListTradeOrders, new ListTradeOrdersRequest(
            invalid == "user" ? "intruder" : invalid == "missing-user" ? "" : "receiver",
            invalid == "colony" ? "other" : invalid == "missing-colony" ? "" : "away", invalid == "missing-snapshot" ? "" : "base", "History",
            authToken: invalid == "missing-token" ? null : invalid == "token" ? "invalid" : e.F.Token));
    }
    private static ListTradeOrdersResponse Body(HttpResponseMessage response)
    { response.EnsureSuccessStatusCode(); return JsonSerializer.Deserialize<ListTradeOrdersResponse>(response.Content.ReadAsStringAsync().GetAwaiter().GetResult(), new JsonSerializerOptions(JsonSerializerDefaults.Web))!; }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    private sealed class Env : IDisposable
    {
        public readonly HttpSnapshotRecoveryTests.Fixture F; public readonly AuthoritativeEvent Order; public Action? Publication;
        public Env(Func<string, SaveSnapshotPackage> factory)
        {
            var plugins = new ServerPluginRegistry().WithBuiltInPlugins([new("test.market-recovery", "test", "1", "test", "", [])
                { SnapshotPostUploadProcessors = [new PublicationProbe(() => Publication?.Invoke())] }]);
            F = new(factory, plugins); F.Start(); F.State.UpdateServerConfiguration(new(tradeOrderExpiration: TimeSpan.FromHours(1)));
            Order = AuthoritativeEventFactory.Create(ServerEventType.Trade, new("seller", "seller-colony"), new("server"), "market-order", false,
                new TradeEventPayload("market-order", TradeStage.MarketOrder, [new("steel", "Steel", 10)], [new("silver", "Silver", 20)], 0), DateTimeOffset.UtcNow.AddHours(-2));
            F.State.Ledger.Append(Order); var p = (TradeEventPayload)Order.Payload;
            F.State.Ledger.Append(AuthoritativeEventFactory.Create(ServerEventType.Trade, new("receiver", "away"), new("receiver", "away"), "market-memo", false,
                new TradeEventPayload(Order.EventId, TradeStage.AcceptedMemo, p.OfferedItems, p.RequestedItems, 0, AcceptedByUserId: "receiver"), DateTimeOffset.UtcNow)
                with { Status = ServerEventStatus.Recorded });
            F.Seed();
        }
        public void AssertExpired(bool baseline = false)
        {
            Require(F.State.Ledger.Find(Order.EventId)!.Status == ServerEventStatus.Cancelled && F.State.Ledger.FindByIdempotencyKey("market-memo")!.Status == ServerEventStatus.Cancelled
                && F.State.Ledger.ListByType(ServerEventType.ItemDelivery).Count(item => item.Payload is ItemDeliveryEventPayload payload
                    && payload.Purpose == (baseline ? ItemDeliveryPurpose.TradeBaselineChangedOwnerReturn : ItemDeliveryPurpose.TradeExpiredOwnerReturn)) == 1
                && F.State.Ledger.ListByType(ServerEventType.ServerNotification).Count(item => item.IdempotencyKey.StartsWith(baseline ? "trade-baseline-invalidated:" : "trade-expired:", StringComparison.Ordinal)) == 2,
                "Maintenance lost or duplicated the order closure, refund or participant notifications.");
        }
        public void AssertNoExpiration() => Require(F.State.Ledger.Find(Order.EventId)!.Status != ServerEventStatus.Cancelled
            && F.State.Ledger.ListByType(ServerEventType.ItemDelivery).All(item => item.Payload is not ItemDeliveryEventPayload { Purpose: ItemDeliveryPurpose.TradeExpiredOwnerReturn }), "Denied or obsolete expiration changed the order.");
        public void Dispose() => F.Dispose();
    }
    private sealed class PublicationProbe(Action callback) : IInlineSnapshotPostUploadProcessor
    {
        public string Id => "test.market-recovery"; public int Order => 350;
        public SnapshotPostUploadStage Stage => SnapshotPostUploadStage.EventReconciliation;
        public SnapshotPostUploadFailureMode FailureMode => SnapshotPostUploadFailureMode.AbortPipeline;
        public SnapshotPostUploadExecutionMode ExecutionMode => SnapshotPostUploadExecutionMode.Inline;
        public bool Supports(SnapshotPostUploadKind kind) => true;
        public void Process(SnapshotPostUploadContext context) => callback();
    }
}
