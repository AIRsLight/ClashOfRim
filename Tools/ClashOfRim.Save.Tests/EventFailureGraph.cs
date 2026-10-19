using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Protocol;
using static SnapshotTestAssertions;

internal sealed class EventFailureGraph
{
    public static readonly DateTimeOffset Original = DateTimeOffset.UnixEpoch.AddDays(2);
    public static IReadOnlyList<string> Kinds { get; } = Array.AsReadOnly(new[] { "gift", "support", "return", "loss", "trade", "self" });
    public string Kind { get; }
    public AuthoritativeEvent Root { get; }

    public EventFailureGraph(IAuthoritativeEventLedger ledger, string kind, string suffix = "")
    {
        Kind = kind;
        Root = Seed(ledger, kind, suffix);
    }

    private static AuthoritativeEvent Seed(IAuthoritativeEventLedger ledger, string kind, string suffix = "")
    {
        var seller = new EventParty("seller", "seller-colony"); var buyer = new EventParty("buyer", "buyer-colony");
        AuthoritativeEvent Create(ServerEventType type, string key, LedgerEventPayload payload, EventParty? actor = null, EventParty? target = null) =>
            AuthoritativeEventFactory.Create(type, actor ?? seller, target ?? buyer, key, true, payload, Original)
                // Keep this graph as pre-upgrade data; failure handling must preserve its old links.
                with { EventId = AuthoritativeEventFactory.BuildLegacyEventId(type, key), Status = ServerEventStatus.DeliveredToClient };
        AuthoritativeEvent root;
        if (kind is "trade" or "self")
        {
            var offered = new[] { new EventThingReference("steel", "Steel", 10, "seller-base") };
            var paid = new[] { new EventThingReference("silver", "Silver", 20, "buyer-base") };
            ledger.Append(Create(ServerEventType.Trade, "order", new TradeEventPayload("order", TradeStage.MarketOrder, offered, paid, 0), seller, new("server"))
                with { Status = ServerEventStatus.AppliedToSnapshot });
            ledger.Append(Create(ServerEventType.Trade, "exchange", new TradeEventPayload("trade:order",
                kind == "self" ? TradeStage.SelfDeliveryExchange : TradeStage.ServerDropPodExchange, offered, paid, 0), buyer, seller)
                with { Status = ServerEventStatus.AppliedToSnapshot, AppliedSnapshotId = "buyer-paid", AppliedAtUtc = Original,
                    LastApplicationResult = EventApplicationResultKind.Applied });
            var observer = new EventParty("observer", "observer-colony");
            ledger.Append(Create(ServerEventType.Trade, "memo", new TradeEventPayload("trade:order", TradeStage.AcceptedMemo, offered, paid, 0,
                AcceptedByUserId: "observer"), observer, observer));
            root = Create(ServerEventType.ItemDelivery, "trade-completed-acceptor-delivery:trade:order:buyer:buyer-colony",
                new ItemDeliveryEventPayload(offered, null, Purpose: ItemDeliveryPurpose.TradeCompletedAcceptorDelivery));
            var ownerDelivery = Create(ServerEventType.ItemDelivery, "trade-completed-owner-delivery:trade:order:buyer:buyer-colony",
                new ItemDeliveryEventPayload(paid, null, Purpose: ItemDeliveryPurpose.TradeCompletedOwnerDelivery), buyer, seller);
            if (kind == "self") root = ownerDelivery;
            else ledger.Append(ownerDelivery);
        }
        else if (kind == "loss")
            root = Create(ServerEventType.Raid, "loss" + suffix, new RaidEventPayload("base", null, null, null, null,
                AttackerLoss: new("parent-" + suffix, "base", ["pawn:lost"], [], "lost")));
        else if (kind is "support" or "return")
            root = Create(ServerEventType.SupportPawn, kind + suffix, new SupportPawnEventPayload("pawn:ally", "base", "Ally", true, null,
                ReturnToSender: kind == "return", SourceEventId: kind == "return" ? "parent-" + suffix : null));
        else root = Create(ServerEventType.ItemDelivery, "gift" + suffix, new ItemDeliveryEventPayload([], "gift"));
        if (kind is "loss" or "return")
            ledger.Append(Create(ServerEventType.SupportPawn, "parent-key-" + suffix, new SupportPawnEventPayload("pawn:ally", "base", "Ally", true, null), buyer, seller)
                with { EventId = "parent-" + suffix, Status = ServerEventStatus.AppliedToSnapshot, AppliedSnapshotId = "original", AppliedAtUtc = Original });
        ledger.Append(root);
        if (ledger.Find("unrelated") is null)
            ledger.Append(Create(ServerEventType.ItemDelivery, "unrelated", new ItemDeliveryEventPayload([], "other"), new("other"), new("third")) with { EventId = "unrelated" });
        return ledger.Find(root.EventId)!;
    }

    public void AssertCompleted(IAuthoritativeEventLedger ledger)
    {
        var root = Root; string kind = Kind;
        Check(ledger.Find(root.EventId)!.Status == (kind == "self" ? ServerEventStatus.Conflict : ServerEventStatus.Failed), "root failure missing");
        Check(ledger.Find("unrelated")!.Status == ServerEventStatus.DeliveredToClient, "unrelated event changed");
        if (kind is "trade" or "self")
        {
            Check(ledger.Find("trade:exchange")!.Status == ServerEventStatus.AppliedToSnapshot
                && ledger.Find("trade:exchange")!.AppliedAtUtc == Original, "applied exchange evidence overwritten");
            Check(ledger.Find("trade:order")!.Status == (kind == "self" ? ServerEventStatus.Conflict : ServerEventStatus.Cancelled), "order closure missing");
            Check(ledger.ListByType(ServerEventType.ItemDelivery).Count(item => ((ItemDeliveryEventPayload)item.Payload).IsTradeReturn)
                == (kind == "self" ? 0 : 2), "refund lost or duplicated");
        }
        Check(ledger.ListByType(ServerEventType.ServerNotification).Count == (kind is "trade" or "self" ? 3 : 2), "notification recipient missing");
    }

}
