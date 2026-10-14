using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Protocol;
using Microsoft.Data.Sqlite;
using System.Reflection;

internal static class TradeClosureTests
{
    public static void Run()
    {
        SnapshotEndpointTestClient.LoadLocalization();
        VerifyLegacyReturnRecovery();
        foreach (string scenario in new[] { "cancel", "expire", "baseline", "failure", "legacy-fulfill" })
        foreach (string phase in new[] { "insert", "update" })
        {
            string root = Path.Combine(Path.GetTempPath(), "cor-trade-close-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                string database = Path.Combine(root, "server.sqlite");
                ServerDatabaseMigrator.Migrate(database);
                ClashOfRimNetworkState Open() => new(ledger: new SqliteAuthoritativeEventLedger(database),
                    serverConfiguration: new ClashOfRimServerConfiguration(tradeOrderExpiration: scenario == "expire" ? TimeSpan.FromMinutes(1) : TimeSpan.Zero));
                var state = Open();
                var (order, delivery) = Seed(state, scenario == "failure");
                var before = state.Ledger.ListAll().ToDictionary(item => item.EventId, item => item.Status);
                Execute(database, $"create trigger fail_close before {phase} on events begin select raise(abort, 'injected closure failure'); end;");
                bool failed = false;
                try { Close(state, scenario, order, delivery); }
                catch (TargetInvocationException ex) when (ex.InnerException is SqliteException) { failed = true; }
                Require(failed, "Closure failure was not injected.");
                state = Open();
                Require(state.Ledger.ListAll().Count == before.Count && before.All(pair => state.Ledger.Find(pair.Key)!.Status == pair.Value),
                    scenario + " failed between closing the order and publishing returns/notifications.");
                Execute(database, "drop trigger fail_close;");
                Close(state, scenario, order, delivery);
                state = Open();
                Require(state.Ledger.Find(order.EventId)!.Status == (scenario == "legacy-fulfill" ? ServerEventStatus.AppliedToSnapshot : ServerEventStatus.Cancelled), "Order was not closed.");
                var returns = state.Ledger.ListByType(ServerEventType.ItemDelivery).Where(item => item.Payload is ItemDeliveryEventPayload p && p.IsTradeReturn).ToArray();
                Require(returns.Length == (scenario == "failure" ? 2 : scenario == "legacy-fulfill" ? 0 : 1), "Escrow returns were lost or duplicated.");
                if (scenario == "legacy-fulfill") Require(state.Ledger.ListByType(ServerEventType.ItemDelivery).Count == 2, "Legacy fulfillment lost a delivery.");
                if (scenario == "failure")
                    Require(returns.Single(item => item.Target.UserId == "buyer").Payload is ItemDeliveryEventPayload
                        { Purpose: ItemDeliveryPurpose.TradeApplicationFailedAcceptorReturn } p && p.Items.Single().StackCount == 20,
                        "Confirmed buyer payment was not returned.");
                int count = state.Ledger.ListAll().Count;
                Close(state, scenario, order, delivery);
                Require(state.Ledger.ListAll().Count == count, "Closure retry duplicated events.");
            }
            finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, recursive: true); }
        }
        foreach (bool selfDelivery in new[] { false, true })
        {
            var state = new ClashOfRimNetworkState();
            var (order, delivery) = Seed(state, true, selfDelivery);
            AuthoritativeEvent ownerDelivery = state.Ledger.ListByType(ServerEventType.ItemDelivery).Single(item => item.Target.UserId == "seller");
            if (!selfDelivery)
            {
                state.Ledger.MarkDelivered(ownerDelivery.EventId, "before", DateTimeOffset.UtcNow);
                state.Ledger.MarkApplied(ownerDelivery.EventId, "after", DateTimeOffset.UtcNow);
            }
            Close(state, "failure", order, delivery);
            Require(state.Ledger.Find(order.EventId)!.Status == ServerEventStatus.Conflict
                && !state.Ledger.ListByType(ServerEventType.ItemDelivery).Any(item => ((ItemDeliveryEventPayload)item.Payload).IsTradeReturn),
                "Partially applied trade must not automatically mint escrow returns.");
            if (!selfDelivery) Require(state.Ledger.Find(ownerDelivery.EventId)!.Status == ServerEventStatus.AppliedToSnapshot,
                "Failure handling reset a consumed delivery.");
        }
    }

    private static void VerifyLegacyReturnRecovery()
    {
        string root = Path.Combine(Path.GetTempPath(), "cor-legacy-trade-return-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string database = Path.Combine(root, "server.sqlite");
            ServerDatabaseMigrator.Migrate(database);
            var state = new ClashOfRimNetworkState(ledger: new SqliteAuthoritativeEventLedger(database), serverConfiguration: new ClashOfRimServerConfiguration(tradeOrderExpiration: TimeSpan.Zero));
            var (order, _) = Seed(state, false, legacyOrder: true);
            string key = "trade-cancelled-owner-return:" + order.EventId;
            var returned = AuthoritativeEventFactory.Create(ServerEventType.ItemDelivery, new("server"), order.Actor,
                key, false, new ItemDeliveryEventPayload(((TradeEventPayload)order.Payload).OfferedItems, null,
                    Purpose: ItemDeliveryPurpose.TradeCancelledOwnerReturn), DateTimeOffset.UnixEpoch)
                with { EventId = AuthoritativeEventFactory.BuildLegacyEventId(ServerEventType.ItemDelivery, key) };
            // A pre-upgrade partial closure already returned the escrow, but left the order open.
            state.Ledger.Append(returned);
            state.Ledger.MarkDelivered(returned.EventId, "before", DateTimeOffset.UnixEpoch);
            state.Ledger.MarkApplied(returned.EventId, "after", DateTimeOffset.UnixEpoch);
            var consumed = state.Ledger.Find(returned.EventId)!;
            state = new ClashOfRimNetworkState(ledger: new SqliteAuthoritativeEventLedger(database), serverConfiguration: new ClashOfRimServerConfiguration(tradeOrderExpiration: TimeSpan.Zero));
            Close(state, "cancel", order, null);
            Require(state.Ledger.Find(order.EventId)!.Status == ServerEventStatus.Cancelled
                && state.Ledger.ListByType(ServerEventType.ItemDelivery).Count == 1
                && System.Text.Json.JsonSerializer.Serialize(state.Ledger.Find(returned.EventId)) == System.Text.Json.JsonSerializer.Serialize(consumed)
                && state.Ledger.Find(AuthoritativeEventFactory.BuildEventId(ServerEventType.ItemDelivery, key)) is null,
                "upgraded cancellation lost the legacy return or reset its consumption");
            Close(state, "cancel", order, null);
            Require(state.Ledger.ListByType(ServerEventType.ItemDelivery).Count == 1, "repeated legacy cancellation returned escrow twice");
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, recursive: true); }
    }

    private static (AuthoritativeEvent Order, AuthoritativeEvent? Delivery) Seed(ClashOfRimNetworkState state, bool fulfilled, bool selfDelivery = false, bool legacyOrder = false)
    {
        var seller = new EventParty("seller", "seller-colony");
        var buyer = new EventParty("buyer", "buyer-colony");
        var offered = new[] { new EventThingReference("steel", "Steel", 10, "seller-snapshot") };
        var paid = new[] { new EventThingReference("silver", "Silver", 20, "buyer-snapshot") };
        var order = AuthoritativeEventFactory.Create(ServerEventType.Trade, seller, new EventParty("server"), "close-order", false,
            new TradeEventPayload("close-order", TradeStage.MarketOrder, offered, paid, 0), DateTimeOffset.UnixEpoch);
        if (legacyOrder) order = order with { EventId = AuthoritativeEventFactory.BuildLegacyEventId(order.Type, order.IdempotencyKey) };
        if (fulfilled) order = order with { Status = ServerEventStatus.AppliedToSnapshot };
        state.Ledger.Append(order);
        state.Ledger.Append(AuthoritativeEventFactory.Create(ServerEventType.Trade, buyer, buyer, "memo", false,
            new TradeEventPayload(order.EventId, TradeStage.AcceptedMemo, offered, paid, 0, AcceptedByUserId: "buyer"), DateTimeOffset.UnixEpoch)
            with { Status = fulfilled ? ServerEventStatus.AppliedToSnapshot : ServerEventStatus.Recorded });
        if (!fulfilled) return (order, null);
        state.Ledger.Append(AuthoritativeEventFactory.Create(ServerEventType.Trade, buyer, seller, "exchange", false,
            new TradeEventPayload(order.EventId, selfDelivery ? TradeStage.SelfDeliveryExchange : TradeStage.ServerDropPodExchange,
                offered, paid, 0, AcceptedByUserId: "buyer"), DateTimeOffset.UnixEpoch)
            with { Status = ServerEventStatus.AppliedToSnapshot, AppliedSnapshotId = "buyer-snapshot" });
        AuthoritativeEvent Delivery(bool toOwner) => AuthoritativeEventFactory.Create(ServerEventType.ItemDelivery,
            toOwner ? buyer : seller, toOwner ? seller : buyer,
            (toOwner ? "trade-completed-owner-delivery:" : "trade-completed-acceptor-delivery:") + order.EventId + ":buyer:buyer-colony", false,
            new ItemDeliveryEventPayload(toOwner ? paid : offered, null, Purpose: toOwner ? ItemDeliveryPurpose.TradeCompletedOwnerDelivery : ItemDeliveryPurpose.TradeCompletedAcceptorDelivery),
            DateTimeOffset.UnixEpoch);
        AuthoritativeEvent ownerDelivery = Delivery(true);
        state.Ledger.Append(ownerDelivery);
        if (selfDelivery) return (order, ownerDelivery);
        AuthoritativeEvent target = Delivery(false);
        state.Ledger.Append(target);
        return (order, target);
    }

    private static void Close(ClashOfRimNetworkState state, string scenario, AuthoritativeEvent order, AuthoritativeEvent? delivery)
    {
        object? Invoke(string name, params object?[] args) => typeof(ClashOfRimNetworkServer)
            .GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, args);
        switch (scenario)
        {
            case "cancel": Invoke("CancelTradeOrder", new CloseTradeOrderRequest("cancel", order.EventId,
                new ProtocolIdentity("seller", "seller-colony", "seller-snapshot"), null), state); break;
            case "expire": Invoke("ApplyTradeOrderExpirations", state, DateTimeOffset.UtcNow); break;
            case "baseline": Invoke("ApplyTradeOrderBaselineInvalidations", state,
                new AdminBaselineSnapshot("admin", "admin-colony", DateTimeOffset.UtcNow, new Dictionary<string, float> { ["Silver"] = 1 },
                    Array.Empty<TrapClassificationDto>()), DateTimeOffset.UtcNow); break;
            case "failure":
                AuthoritativeEvent current = state.Ledger.Find(delivery!.EventId)!;
                // The public endpoint returns DuplicateRequest once the durable failure batch is present.
                if (current.Status == ServerEventStatus.Failed) return;
                Invoke("FailEventApplicationGraph", state, current, null, "delivery failed", DateTimeOffset.UtcNow); break;
            case "legacy-fulfill": Invoke("FulfillTradeOrder", new FulfillTradeOrderRequest("fulfill", order.EventId, "",
                new ProtocolIdentity("buyer", "buyer-colony", "buyer-snapshot"), new[] { new ThingReferenceDto("paid", "Silver", 20) }, "ServerDropPod"), state); break;
        }
    }

    private static void Execute(string database, string sql)
    {
        using var connection = new SqliteConnection("Data Source=" + database);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
}
