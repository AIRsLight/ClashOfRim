using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using TestStore = SnapshotEndpointRecoveryPreparationTests.Store;

internal static partial class EconomicEndpointRecoveryPreparationTests
{
    public static void Run(Func<string, SaveSnapshotPackage> factory)
    {
        RunBindings(factory);
        foreach (string change in new[] { "normal-move", "forced-none", "forced-move", "forced-remove", "forced-peace", "forced-pvp" })
            With(factory, "gift", f => VerifyGiftTarget(f, change));
        foreach (string kind in new[] { "gift", "order", "self-delivery", "drop-pod" })
        {
            foreach (bool final in new[] { false, true })
            foreach (string change in new[] { "none", "logout", "replace", "revoke", "newer", "compete", "failure", "revoke-failure" })
                With(factory, kind, f => VerifyRecovery(f, final, change));
            With(factory, kind, VerifyConcurrent);
            With(factory, kind, VerifyPrincipal);
            foreach (string change in kind == "gift" ? new[] { "disabled" } : kind == "order"
                ? new[] { "disabled", "fee", "limit" } : kind == "self-delivery" ? new[] { "cancel", "memo" } : new[] { "cancel" })
                With(factory, kind, f => VerifyFinalValidation(f, change));
        }
    }

    private static void VerifyGiftTarget(Fixture f, string change)
    {
        f.ForcedGift = change.StartsWith("forced");
        f.State.UpdateServerConfiguration(new(pvpEnabled: true));
        f.State.DiplomacyRelations.SetRelationKind("userA", "colonyA", "userB", "colonyB", DiplomacyRelationRegistry.RelationHostile, null, DateTimeOffset.UtcNow);
        f.SetTarget("Map_old"); f.Store.Reset();
        Reply? reply = null; string? before = null;
        SnapshotPreparationGateProbe.Run(f.Gate, hook => f.Store.BeforePrepare = hook, () => reply = f.Call(), () =>
        {
            if (change.EndsWith("-move")) f.SetTarget("Map_new");
            if (change.EndsWith("remove")) f.Store.Inner.RemoveLatest("userB", "colonyB");
            if (change.EndsWith("peace")) f.State.DiplomacyRelations.SetRelationKind("userA", "colonyA", "userB", "colonyB", DiplomacyRelationRegistry.RelationNeutral, null, DateTimeOffset.UtcNow);
            if (change.EndsWith("pvp")) f.State.UpdateServerConfiguration(new(pvpEnabled: false));
            before = f.Evidence();
        });
        bool accepted = change.EndsWith("-move") || change.EndsWith("none");
        Check(reply!.Result.Accepted == accepted, "Gift target revalidation failed: " + change + " " + reply.Result.Message);
        if (accepted) Check(f.Store.Writes == 1 && f.State.Ledger.Find(reply.Id!)?.TargetContext?.MapUniqueId
            == (change.EndsWith("-move") ? "Map_new" : "Map_old"), "Gift retained its stale target map.");
        else Check(reply.Id is null && reply.Token is null && before == f.Evidence() && f.Store.Writes == 0, "Rejected forced gift changed state.");
        f.AssertReleased();
    }

    private static void VerifyRecovery(Fixture f, bool final, string change)
    {
        // Capture a real HTTP intent; final-boundary cases inject that exact business decision during upload preparation.
        var original = f.Seed();
        if (final) File.Delete(Directory.GetFiles(f.Journal, "*.json").Single());
        f.Open(); f.Store.Reset();
        Reply? reply = null; Exception? failure = null; string? before = null; int prepared = 0;
        void Change()
        {
            prepared++;
            if (change is "logout" or "replace") f.State.LoginSessions.EndUser("userA");
            if (change == "replace") f.State.LoginSessions.TryCreate("userA", "colonyA", DateTimeOffset.UtcNow, out _);
            if (change is "revoke" or "revoke-failure") f.State.AuthTokens.RevokeForUser("userA");
            if (change == "newer")
            {
                var newer = f.Package with { Envelope = f.Package.Envelope with { Identity = new("userA", "colonyA", "newer"), NextLineageToken = "newer" } };
                f.Store.Inner.StoreLatest(newer, newer.Index, DateTimeOffset.UtcNow);
            }
            if (change == "compete") ClashOfRimNetworkServer.RecoverSnapshotCommits(f.State);
            if (change is "failure" or "revoke-failure") f.Store.FailPreparation = true;
            before = f.Evidence();
        }
        SnapshotPreparationGateProbe.Run(f.Gate, hook => f.Store.BeforePrepare = hook,
            () => { try { reply = f.Call(); } catch (Exception ex) { failure = ex; } }, () =>
            {
                Check(f.Store.Writes == 0, "Recovery published during preparation.");
                if (!final) Change();
                else
                {
                    f.SeedDirect(original); f.Store.Reset();
                    f.Store.BeforePrepare = () =>
                    {
                        f.Store.BeforePrepare = null;
                        Check(!Monitor.IsEntered(f.Gate), "Final recovery prepared under the mutation gate.");
                        Task.Run(() => { using (f.State.EnterSnapshotMutation()) Change(); }).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                    };
                }
            });
        Check(prepared == 1, "Economic endpoint did not prepare recovery at its commit boundary: " + f.Kind);
        bool denied = change is "logout" or "replace" or "revoke" or "revoke-failure";
        bool failed = change is "newer" or "failure";
        Check((failure is not null) == failed, "Unexpected recovery failure: " + f.Kind + "/" + final + "/" + change + " " + failure);
        if (denied) Check(reply is { Result.Accepted: false, Id: null, Snapshot: null, Token: null, Owner: null, Buyer: null }
            && before == f.Evidence() && f.Store.Writes == 0 && f.State.SnapshotCommits.HasPending, "Revoked request recovered or disclosed economic state.");
        else if (failed) Check(before == f.Evidence() && f.Store.Writes == 0 && f.State.SnapshotCommits.HasPending, "Failed recovery changed economic state.");
        else
        {
            f.AssertReplay(reply!, original);
            Check(f.Store.Writes == 1 && f.Store.PreparedWrites == (change == "compete" ? 0 : 1), "Recovery did not reuse prepared publication.");
        }
        f.AssertReleased();
    }

    private static void VerifyConcurrent(Fixture f)
    {
        var original = f.Seed(); f.Open(); f.Store.Reset();
        var replies = ServiceSnapshotReplayRaceTests.Race(_ => Task.FromResult(f.Call()), () => { });
        foreach (var reply in replies) f.AssertReplay(reply, original);
        Check(f.Store.Writes == 1 && f.Store.PreparedWrites == 1, "Concurrent recovery published more than once.");
        f.AssertReleased();
    }

    private static void VerifyPrincipal(Fixture f)
    {
        f.Seed(); f.Open(); f.Store.Reset(); string before = f.Evidence();
        f.Store.BeforePrepare = () => throw new Exception("Mismatched principal started recovery.");
        Check(!f.Call(wrongPrincipal: true).Result.Accepted && before == f.Evidence() && f.Store.Writes == 0,
            "Mismatched principal changed economic state.");
        f.AssertReleased();
    }

    private static void VerifyFinalValidation(Fixture f, string change)
    {
        f.Store.Reset(); string? before = null; Reply? reply = null;
        SnapshotPreparationGateProbe.Run(f.Gate, hook => f.Store.BeforePrepare = hook,
            () => reply = f.Call(), () => { f.ChangeTerms(change); before = f.Evidence(); });
        Check(reply is { Result.Accepted: false, Id: null, Snapshot: null, Token: null }
            && before == f.Evidence() && f.Store.Writes == 0 && !f.State.SnapshotCommits.HasPending,
            "Economic commit accepted changed terms: " + f.Kind + "/" + change);
        f.AssertReleased();
    }

    private static void With(Func<string, SaveSnapshotPackage> factory, string kind, Action<Fixture> test)
    {
        string root = Path.Combine(Path.GetTempPath(), "cor-economic-recovery-" + Guid.NewGuid().ToString("N"));
        try { test(new(root, factory, kind)); }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private sealed record Reply(ProtocolResponse Result, string? Id, string? Snapshot, string? Token,
        string? Owner = null, string? Buyer = null, bool Created = false);

    private sealed partial class Fixture
    {
        private static readonly ProtocolIdentity Actor = new("userA", "colonyA", "before");
        private readonly string root, database;
        private string token = "", orderId = "", memoId = "";
        public string Kind { get; }
        public bool ForcedGift;
        public string Journal => Path.Combine(root, "journal");
        public SaveSnapshotPackage Package { get; }
        public ClashOfRimNetworkState State = null!;
        public TestStore Store = null!;
        public object Gate => State.RaidSettlementSnapshotMutationGate;
        public Fixture(string root, Func<string, SaveSnapshotPackage> factory, string kind)
        {
            this.root = root; Kind = kind; database = Path.Combine(root, "server.sqlite");
            ServerDatabaseMigrator.Migrate(database); Open(); Store.Gate = null;
            Package = MercenaryCommitCrashTests.PrepareEndpoint(State, factory, Actor, "paid");
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
    }
}
