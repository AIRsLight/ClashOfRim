using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;

internal static class EconomicSnapshotReplayRaceTests
{
    private static readonly AsyncLocal<int> Attempt = new();
    private static readonly ProtocolIdentity Actor = new("userA", "colonyA", "before");
    private const int Count = 12;

    public static void Run(Func<string, SaveSnapshotPackage> fixture)
    {
        SnapshotEndpointTestClient.LoadLocalization();
        foreach (string kind in new[] { "gift", "order", "self-delivery", "drop-pod" })
        {
            foreach (bool sameKey in new[] { true, false }) RunScenario(fixture, kind, sameKey);
            RunScenario(fixture, kind, sameKey: true, revokeSession: true);
        }
    }

    private static void RunScenario(Func<string, SaveSnapshotPackage> fixture, string kind, bool sameKey, bool revokeSession = false)
    {
        var ledger = DispatchProxy.Create<IAuthoritativeEventLedger, ObservedLedger>();
        var observed = (ObservedLedger)ledger;
        observed.Inner = new InMemoryAuthoritativeEventLedger();
        var state = new ClashOfRimNetworkState(ledger: ledger, serverConfiguration: new ClashOfRimServerConfiguration(
            tradeFeePolicy: new TradeFeePolicy(standardMarketValuePerThing: new Dictionary<string, float> { ["Silver"] = 1 })));
        var package = fixture("race-snapshot");
        string? orderId = null, memoId = null;
        if (kind is "self-delivery" or "drop-pod")
        {
            var order = AuthoritativeEventFactory.Create(ServerEventType.Trade, new EventParty("userB", "colonyB"),
                new EventParty("server"), "order", false, new TradeEventPayload("order", TradeStage.MarketOrder,
                    [new EventThingReference("offer", "Silver", 10, "seller-snapshot")],
                    [new EventThingReference("request", "Silver", 20, "seller-snapshot")], 0), DateTimeOffset.UtcNow);
            ledger.Append(order); orderId = order.EventId;
            if (kind == "self-delivery")
            {
                var memo = AuthoritativeEventFactory.Create(ServerEventType.Trade, new EventParty("userA", "colonyA"),
                    new EventParty("userA", "colonyA"), "memo", false, ((TradeEventPayload)order.Payload) with
                    { TradeId = order.EventId, Stage = TradeStage.AcceptedMemo, AcceptedByUserId = "userA" }, DateTimeOffset.UtcNow)
                    with { Status = ServerEventStatus.Recorded };
                ledger.Append(memo); memoId = memo.EventId;
            }
        }
        string method = kind == "gift" ? "CreateGiftWithSnapshot" : kind == "order" ? "CreateTradeOrderWithSnapshot" : "FulfillTradeOrderWithSnapshot";
        object Request(int attempt) => kind switch
        {
            "gift" => new CreateGiftWithSnapshotRequest(new CreateGiftRequest(Key(attempt), Actor,
                new ProtocolIdentity("userB", "colonyB", null), [new ThingReferenceDto("silver", "Silver", 100)], "race"), ProtocolDtoMapper.ToMetadataDto(package)),
            "order" => new CreateTradeOrderWithSnapshotRequest(new CreateTradeOrderRequest(Key(attempt), Actor,
                [], [new ThingReferenceDto("want", "Silver", 10)], 100, true, true), ProtocolDtoMapper.ToMetadataDto(package)),
            _ => new FulfillTradeOrderWithSnapshotRequest(new FulfillTradeOrderRequest(Key(attempt), orderId!, memoId ?? string.Empty,
                Actor, [new ThingReferenceDto("paid", "Silver", 20)], kind == "self-delivery" ? "SelfDelivery" : "ServerDropPod"), ProtocolDtoMapper.ToMetadataDto(package))
        };
        string Key(int attempt) => sameKey ? "race" : "race-" + attempt;
        var now = DateTimeOffset.UtcNow;
        state.LoginSessions.TryCreate(Actor.UserId, Actor.ColonyId!, now, out string session);
        string token = state.AuthTokens.Issue(Actor.UserId, Actor.UserId, Actor.ColonyId!, session, now);
        async Task<Reply> Call(int attempt)
        {
            if (kind is "gift" or "order")
            {
                var response = await SnapshotEndpointTestClient.Invoke<EventCreationResponse>(method, state, _ => Request(attempt), package.Payload, Actor, token);
                return new(response.Result, response.EventId, response.AppliedSnapshotId, response.NextLineageToken);
            }
            var fulfillment = await SnapshotEndpointTestClient.Invoke<FulfillTradeOrderResponse>(method, state, _ => Request(attempt), package.Payload, Actor, token);
            return new(fulfillment.Result, fulfillment.ExchangeEventId, fulfillment.AppliedSnapshotId, fulfillment.NextLineageToken,
                fulfillment.OwnerDeliveryEventId, fulfillment.AcceptorDeliveryEventId, fulfillment.ExchangeCreated);
        }

        // Hold the real preparation limiter until all requests have passed their first key lookup.
        // No production test hook: requests run the actual multipart endpoint and snapshot receiver.
        var slots = (SemaphoreSlim)typeof(ClashOfRimNetworkServer).Assembly
            .GetType("AIRsLight.ClashOfRim.Network.SnapshotPreparationService")!
            .GetField("Slots", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        int held = 0;
        while (slots.Wait(0)) held++;
        Check(held > 0, "preparation limiter unexpectedly busy");
        var results = new Reply?[Count]; var errors = new Exception?[Count];
        var threads = Enumerable.Range(1, Count).Select(i => new Thread(() =>
        {
            Attempt.Value = i;
            try { results[i - 1] = Call(i).GetAwaiter().GetResult(); }
            catch (Exception ex) { errors[i - 1] = ex; }
        }) { IsBackground = true }).ToArray();
        try
        {
            foreach (var thread in threads) thread.Start();
            Check(SpinWait.SpinUntil(() => observed.Lookups.Count == Count || errors.Any(e => e != null) || results.Any(r => r != null), TimeSpan.FromSeconds(10))
                && observed.Lookups.Count == Count && results.All(r => r is null) && errors.All(e => e is null),
                "requests did not reach concurrent preparation: " + JsonSerializer.Serialize(results) + string.Join(";", errors.Select(e => e?.Message)));
            if (revokeSession) state.LoginSessions.EndUser(Actor.UserId);
            slots.Release(held); held = 0;
            foreach (var thread in threads) Check(thread.Join(TimeSpan.FromSeconds(30)), "snapshot request did not finish");
        }
        finally
        {
            if (held > 0) slots.Release(held);
            foreach (var thread in threads) if (thread.IsAlive) thread.Join(TimeSpan.FromSeconds(30));
        }
        if (errors.FirstOrDefault(e => e is not null) is { } error) throw new Exception("snapshot request failed", error);
        if (revokeSession)
        {
            Check(results.All(r => r!.Result.ErrorCode == ProtocolErrorCode.SessionUnavailable && r.EventId is null && r.SnapshotId is null)
                && state.SnapshotStore.ListLatest().Count == 0 && ledger.ListAll().Count == (kind == "self-delivery" ? 2 : kind == "drop-pod" ? 1 : 0)
                && state.EventNotifications.GetVersion("userA") == 0 && state.EventNotifications.GetVersion("userB") == 0,
                "revoked session committed or replayed a prepared operation: " + kind);
            return;
        }
        var accepted = results.Where(r => r!.Result.Accepted).Select(r => r!).ToArray();
        Check(accepted.Length == (sameKey ? Count : 1)
            && accepted.Count(r => r.Result.ErrorCode != ProtocolErrorCode.DuplicateRequest) == 1,
            $"Concurrent economic replay rejected or duplicated: {kind}, sameKey={sameKey}: " + JsonSerializer.Serialize(results));
        var winner = accepted.Single(r => r.Result.ErrorCode != ProtocolErrorCode.DuplicateRequest);
        Check(accepted.All(r => r.EventId == winner.EventId && r.SnapshotId == winner.SnapshotId && r.Lineage == winner.Lineage
            && r.OwnerDelivery == winner.OwnerDelivery && r.BuyerDelivery == winner.BuyerDelivery), "concurrent replies disagree on the committed operation");
        int expectedDeliveries = kind == "gift" || kind == "self-delivery" ? 1 : kind == "drop-pod" ? 2 : 0;
        Check(state.SnapshotStore.ListLatest().Count == 1 && ledger.ListByType(ServerEventType.ItemDelivery).Count == expectedDeliveries,
            "snapshot or deliveries were duplicated");
        if (kind is "self-delivery" or "drop-pod")
            Check(accepted.Count(r => r.ExchangeCreated) == 1 && ledger.Find(orderId!)!.Status == ServerEventStatus.AppliedToSnapshot,
                "fulfillment replies created more than one exchange or left the order open");
        string before = JsonSerializer.Serialize(ledger.ListAll()) + JsonSerializer.Serialize(state.SnapshotStore.ListLatest());
        int winningAttempt = sameKey ? 1 : Array.FindIndex(results, r => r!.Result.Accepted) + 1;
        var repeated = Call(winningAttempt).GetAwaiter().GetResult();
        Check(repeated.Result.ErrorCode == ProtocolErrorCode.DuplicateRequest && repeated.EventId == winner.EventId
            && before == JsonSerializer.Serialize(ledger.ListAll()) + JsonSerializer.Serialize(state.SnapshotStore.ListLatest()),
            "later replay changed the committed snapshot or ledger");
    }

    private sealed record Reply(ProtocolResponse Result, string? EventId, string? SnapshotId, string? Lineage,
        string? OwnerDelivery = null, string? BuyerDelivery = null, bool ExchangeCreated = false);
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

    public class ObservedLedger : DispatchProxy
    {
        public IAuthoritativeEventLedger Inner = null!;
        public ConcurrentDictionary<int, bool> Lookups { get; } = new();
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            try
            {
                var value = targetMethod!.Invoke(Inner, args);
                if (targetMethod.Name == nameof(IAuthoritativeEventLedger.FindByIdempotencyKey) && Attempt.Value > 0
                    && args![0] is string key && key.StartsWith("race", StringComparison.Ordinal)) Lookups.TryAdd(Attempt.Value, true);
                return value;
            }
            catch (TargetInvocationException ex) when (ex.InnerException is not null) { ExceptionDispatchInfo.Capture(ex.InnerException).Throw(); throw; }
        }
    }
}
