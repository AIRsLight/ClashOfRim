using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using static SnapshotTestAssertions;

internal sealed class EconomicEndpointFixture
{
    private static readonly ProtocolIdentity Actor = new("userA", "colonyA", "before");
    private readonly string root, database;
    private string token = "", orderId = "", memoId = "";
    public string Kind { get; }
    public bool ForcedGift { get; set; }
    public string Journal => Path.Combine(root, "journal");
    public SaveSnapshotPackage Package { get; }
    public ClashOfRimNetworkState State { get; private set; } = null!;
    public SnapshotRecoveryTestStore Store { get; private set; } = null!;
    public object Gate => State.RaidSettlementSnapshotMutationGate;
    private EconomicEndpointFixture(string root, Func<string, SaveSnapshotPackage> factory, string kind)
    {
        this.root = root; Kind = kind; database = Path.Combine(root, "server.sqlite");
        ServerDatabaseMigrator.Migrate(database); Open(); Store.Gate = null;
        Package = SnapshotEndpointPreparation.Prepare(State.SnapshotStore, factory, Actor, "paid");
        Store.Gate = Gate;
        if (kind is "self-delivery" or "drop-pod")
        {
            var order = CreateOrder(new("userB", "colonyB"), "order", "seller-snapshot");
            State.Ledger.Append(order); orderId = order.EventId;
            if (kind == "self-delivery")
            {
                var memo = AuthoritativeEventFactory.Create(ServerEventType.Trade, new("userA", "colonyA"), new("userA", "colonyA"),
                    "memo", false, ((TradeEventPayload)order.Payload) with { TradeId = orderId, Stage = TradeStage.AcceptedMemo,
                        AcceptedByUserId = "userA" }, DateTimeOffset.UtcNow) with { Status = ServerEventStatus.Recorded };
                State.Ledger.Append(memo); memoId = memo.EventId;
            }
        }
    }
    private static AuthoritativeEvent CreateOrder(EventParty owner, string key, string snapshot)
        => AuthoritativeEventFactory.Create(ServerEventType.Trade, owner, new("server"), key, false,
            new TradeEventPayload(key, TradeStage.MarketOrder, [new("offer", "Silver", 10, snapshot)],
                [new("request", "Silver", 20, snapshot)], 0), DateTimeOffset.UtcNow);
    public void Open()
    {
        Store = new(Path.Combine(root, "snapshots"));
        State = new(snapshotStore: Store, snapshotCommits: new(Journal), ledger: new SqliteAuthoritativeEventLedger(database),
            playerRegistry: new PlayerRegistry(new SqlitePlayerRegistryStore(database), null),
            serverConfiguration: new(tradeFeePolicy: new(standardMarketValuePerThing: new Dictionary<string, float> { ["Silver"] = 1 })));
        Store.Gate = Gate;
        State.LoginSessions.TryCreate("userA", "colonyA", DateTimeOffset.UtcNow, out string session);
        token = State.AuthTokens.Issue("userA", "userA", "colonyA", session, DateTimeOffset.UtcNow);
    }
    public Reply Call(bool wrongPrincipal = false, Action<System.Text.Json.Nodes.JsonObject>? mutate = null, byte[]? bytes = null)
    {
        var actor = wrongPrincipal ? new ProtocolIdentity("other", "other-colony", "before") : Actor;
        var metadata = ProtocolDtoMapper.ToMetadataDto(Package);
        object request = Kind switch
        {
            "gift" => new CreateGiftWithSnapshotRequest(new CreateGiftRequest("operation", actor, new("userB", "colonyB", null),
                [new("silver", "Silver", 100)], "gift", deliveryKind: ForcedGift ? ItemDeliveryEventPayload.ForcedDeliveryKind : null), metadata),
            "order" => new CreateTradeOrderWithSnapshotRequest(new CreateTradeOrderRequest("operation", actor,
                [], [new("want", "Silver", 10)], 100, true, true), metadata),
            _ => new FulfillTradeOrderWithSnapshotRequest(new FulfillTradeOrderRequest("operation", orderId, memoId, actor,
                [new("paid", "Silver", 20)], Kind == "self-delivery" ? "SelfDelivery" : "ServerDropPod"), metadata)
        };
        if (mutate is not null)
        {
            var body = JsonSerializer.SerializeToNode(request)!.AsObject();
            mutate(body); request = body;
        }
        if (Kind is "gift" or "order")
        {
            var r = SnapshotEndpointTestClient.Invoke<EventCreationResponse>(Kind == "gift" ? "CreateGiftWithSnapshot" : "CreateTradeOrderWithSnapshot",
                State, _ => request, bytes ?? Package.Payload, Actor, token).GetAwaiter().GetResult();
            return new(r.Result, r.EventId, r.AppliedSnapshotId, r.NextLineageToken);
        }
        var result = SnapshotEndpointTestClient.Invoke<FulfillTradeOrderResponse>("FulfillTradeOrderWithSnapshot", State, _ => request,
            bytes ?? Package.Payload, Actor, token).GetAwaiter().GetResult();
        return new(result.Result, result.ExchangeEventId, result.AppliedSnapshotId, result.NextLineageToken,
            result.OwnerDeliveryEventId, result.AcceptorDeliveryEventId, result.ExchangeCreated);
    }
    public SnapshotCommitIntent Seed()
    {
        Store.FailPublication = true; bool failed = false;
        try { Call(); } catch (IOException) { failed = true; } finally { Store.FailPublication = false; }
        Check(failed && State.SnapshotCommits.HasPending, "Could not interrupt economic HTTP commit.");
        return JsonSerializer.Deserialize<SnapshotCommitIntent>(File.ReadAllText(Directory.GetFiles(Journal, "*.json").Single()))!;
    }
    public void SeedDirect(SnapshotCommitIntent intent)
    {
        Store.FailPublication = true;
        var package = intent.Package;
        var snapshot = new LatestSnapshotRecord(package.Envelope.Identity, package.Envelope, package.Index, intent.AcceptedAtUtc);
        try
        {
            if (intent.Gift is { } gift) State.SnapshotCommits.CommitGift(Store, State.BankLoans, State.Ledger, package, snapshot, gift, (_, _) => { }, _ => { });
            else State.SnapshotCommits.CommitTrade(Store, State.BankLoans, State.Ledger, package, snapshot, intent.Trade!, (_, _) => { }, _ => { }, _ => { });
        }
        catch (IOException) { }
        finally { Store.FailPublication = false; }
        Check(State.SnapshotCommits.HasPending, "Could not inject captured economic intent.");
    }
    public void AssertReplay(Reply reply, SnapshotCommitIntent original)
    {
        var events = original.Gift is { } gift ? new[] { gift } : original.Trade!.Batch.Events;
        var owner = events.FirstOrDefault(e => e.Payload is ItemDeliveryEventPayload { Purpose: ItemDeliveryPurpose.TradeCompletedOwnerDelivery });
        var buyer = events.FirstOrDefault(e => e.Payload is ItemDeliveryEventPayload { Purpose: ItemDeliveryPurpose.TradeCompletedAcceptorDelivery });
        Check(reply.Result.Accepted && reply.Result.ErrorCode == ProtocolErrorCode.DuplicateRequest && reply.Id == events[0].EventId
            && reply.Snapshot == "paid" && reply.Token == original.Package.Envelope.NextLineageToken && !string.IsNullOrEmpty(reply.Token)
            && reply.Owner == owner?.EventId && reply.Buyer == buyer?.EventId && !reply.Created && !State.SnapshotCommits.HasPending,
            "Economic recovery lost its original reply: " + Kind + " " + JsonSerializer.Serialize(reply));
        foreach (var item in events) Check(JsonSerializer.Serialize(State.Ledger.Find(item.EventId)) == JsonSerializer.Serialize(item), "Recovery changed a captured event.");
        if (original.Trade is { } trade) foreach (var change in trade.Batch.StatusChanges)
            Check(State.Ledger.Find(change.EventId)!.Status == change.Status, "Recovery did not finish its original ledger batch.");
        Check(State.Ledger.ListAll().Count == events.Count + (Kind == "self-delivery" ? 2 : Kind == "drop-pod" ? 1 : 0), "Recovery duplicated events.");
    }
    public void SetTarget(string map)
    {
        var target = Package with { Envelope = Package.Envelope with { Identity = new("userB", "colonyB", "target-" + map) },
            Index = Package.Index with { Maps = Package.Index.Maps.Select(m => m with { UniqueId = map }).ToArray() } };
        Store.Inner.StoreLatest(target, target.Index, DateTimeOffset.UtcNow);
    }
    public void ChangeTerms(string change)
    {
        if (change == "cancel") State.Ledger.ChangeStatus(orderId, ServerEventStatus.Cancelled);
        else if (change == "memo") State.Ledger.ChangeStatus(memoId, ServerEventStatus.Cancelled);
        else if (change == "disabled") State.UpdateServerConfiguration(new(giftsEnabled: false, tradeMarketplaceEnabled: false));
        else if (change == "fee") State.UpdateServerConfiguration(new(tradeFeePolicy: new(baseFeeRate: 100, feeStrategy: TradeFeeStrategies.HighestSide,
            standardMarketValuePerThing: new Dictionary<string, float> { ["Silver"] = 1 })));
        else
        {
            State.UpdateServerConfiguration(new(maxOpenTradeOrdersPerOwner: 1, tradeFeePolicy: new(standardMarketValuePerThing: new Dictionary<string, float> { ["Silver"] = 1 })));
            State.Ledger.Append(CreateOrder(new("userA", "colonyA"), "other", "before"));
        }
    }
    public string Evidence() => JsonSerializer.Serialize(new { snapshots = Store.ListLatest().Select(s => new { s.Envelope, s.AcceptedAtUtc }),
        events = State.Ledger.ListAll(), a = State.EventNotifications.GetVersion("userA"), b = State.EventNotifications.GetVersion("userB"),
        journal = Directory.GetFiles(Journal, "*.json").Order().Select(File.ReadAllText).ToArray() });
    public void AssertReleased() => Task.Run(() =>
    {
        Check(Monitor.TryEnter(Gate, TimeSpan.FromSeconds(3)), "Economic recovery leaked the mutation gate.");
        Monitor.Exit(Gate);
    }).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();

    public static void With(Func<string, SaveSnapshotPackage> factory, string kind, Action<EconomicEndpointFixture> test)
    {
        string root = Path.Combine(Path.GetTempPath(), "cor-economic-recovery-" + Guid.NewGuid().ToString("N"));
        try { test(new(root, factory, kind)); }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    public sealed record Reply(ProtocolResponse Result, string? Id, string? Snapshot, string? Token,
        string? Owner = null, string? Buyer = null, bool Created = false);

    public static AuthoritativeEvent WithHash(AuthoritativeEvent item, string? hash) => item with
    {
        Payload = item.Payload is ItemDeliveryEventPayload gift ? gift with { CreationRequestHash = hash }
            : ((TradeEventPayload)item.Payload) with { CreationRequestHash = hash }
    };

    public string? MainHash() => State.Ledger.FindByIdempotencyKey("operation")?.Payload switch
    {
        ItemDeliveryEventPayload gift => gift.CreationRequestHash,
        TradeEventPayload trade => trade.CreationRequestHash,
        _ => null
    };
    public void SetMainHash(string? hash)
    {
        var original = State.Ledger.FindByIdempotencyKey("operation")!;
        State.Ledger.ApplyBatch(new LedgerBatch([], [new(original.EventId, original.Status, original.Status,
            original.Payload, WithHash(original, hash).Payload, original)]));
    }
}
