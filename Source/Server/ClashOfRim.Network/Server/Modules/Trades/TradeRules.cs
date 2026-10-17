using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Network.Modules.Events;
using System.Text.Json;
using static AIRsLight.ClashOfRim.Network.ServerText;

namespace AIRsLight.ClashOfRim.Network.Modules.Trades;

internal static class TradeRules
{
    internal enum TradeCancellationReason { Owner, Expired, BaselineChanged }

    internal static EventTargetContext? ResolveTradeCompletedOwnerDeliveryTargetContext(
        AuthoritativeEvent tradeOrder,
        TradeFulfillmentMode fulfillmentMode)
    {
        if (tradeOrder.TargetContext is null)
        {
            return null;
        }

        EventLandingMode landingMode = fulfillmentMode == TradeFulfillmentMode.ServerDropPod
            ? EventLandingMode.DropPod
            : EventLandingMode.MapEdge;
        return tradeOrder.TargetContext with { LandingMode = landingMode };
    }

    internal static IReadOnlyList<string> FindUnavailableTradeThingDefs(
        IEnumerable<EventThingReference> things,
        AdminBaselineSnapshot? baseline,
        ISet<string> validThingDefs)
    {
        var unavailable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (EventThingReference thing in things.Where(ShouldValidateTradeThingDef))
        {
            string? effectiveDef = string.IsNullOrWhiteSpace(thing.MinifiedInnerDefName)
                ? thing.Def
                : thing.MinifiedInnerDefName;
            if (!string.IsNullOrWhiteSpace(effectiveDef) && !validThingDefs.Contains(effectiveDef!))
            {
                unavailable.Add(effectiveDef!);
            }

            if (!string.IsNullOrWhiteSpace(thing.MinifiedInnerDefName)
                && baseline is not null
                && !baseline.IsApprovedPackableBuilding(thing.MinifiedInnerDefName))
            {
                unavailable.Add(thing.MinifiedInnerDefName!);
            }

        }

        return unavailable
            .OrderBy(defName => defName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    internal static bool ContainsConcreteThingPackage(IReadOnlyList<ThingReferenceDto>? things)
    {
        return things is not null
            && things.Any(thing => thing is not null
                && (thing.ThingPackage is not null || !string.IsNullOrWhiteSpace(thing.ThingPackageId)));
    }

    internal static HashSet<string> BuildValidTradeThingDefSet(
        AdminBaselineSnapshot? baseline,
        ClashOfRimServerConfiguration configuration)
    {
        var validThingDefs = new HashSet<string>(
            baseline?.StandardMarketValuePerThing.Keys ?? Array.Empty<string>(),
            StringComparer.OrdinalIgnoreCase);
        foreach (string defName in configuration.TradeFeePolicy.FixedFeePerThing.Keys)
        {
            if (!string.IsNullOrWhiteSpace(defName))
            {
                validThingDefs.Add(defName);
            }

        }

        if (baseline is not null)
        {
            foreach (PackableBuildingDto building in baseline.PackableBuildings)
            {
                if (!string.IsNullOrWhiteSpace(building.DefName))
                {
                    validThingDefs.Add(building.DefName);
                }

            }

        }

        return validThingDefs;
    }

    internal static bool ShouldValidateTradeThingDef(EventThingReference thing)
    {
        return !string.IsNullOrWhiteSpace(thing.Def)
            && thing.PawnPackage is null
            && string.IsNullOrWhiteSpace(thing.PawnPackageId);
    }

    internal static bool IsTradeOrderVisibleInScope(
        AuthoritativeEvent ledgerEvent,
        string viewerUserId,
        string scope,
        bool viewerHasAccepted,
        bool viewerWasInvolved)
    {
        if (ledgerEvent.Type != ServerEventType.Trade
            || ledgerEvent.Payload is not TradeEventPayload payload
            || payload.Stage != TradeStage.MarketOrder)
        {
            return false;
        }

        bool isOwner = string.Equals(ledgerEvent.Actor.UserId, viewerUserId, StringComparison.Ordinal);
        bool isOpen = IsOpenMarketTradeOrderForOwner(ledgerEvent);
        return scope.Equals("AcceptedByMe", StringComparison.OrdinalIgnoreCase)
            ? viewerHasAccepted && isOpen
            : scope.Equals("Mine", StringComparison.OrdinalIgnoreCase)
                ? isOwner && isOpen
                : scope.Equals("History", StringComparison.OrdinalIgnoreCase)
                    ? !isOpen && (viewerWasInvolved || isOwner)
                    : IsOpenMarketTradeOrder(ledgerEvent, viewerUserId);
    }

    internal static bool IsOpenMarketTradeOrder(AuthoritativeEvent item, string viewerUserId) =>
        !string.Equals(item.Actor.UserId, viewerUserId, StringComparison.Ordinal) && IsOpenMarketTradeOrderForOwner(item);

    internal static bool IsOpenMarketTradeOrderForOwner(AuthoritativeEvent ledgerEvent)
    {
        return ledgerEvent.Type == ServerEventType.Trade
            && ledgerEvent.Payload is TradeEventPayload payload
            && payload.Stage == TradeStage.MarketOrder
            && ledgerEvent.Status is ServerEventStatus.PendingOfflineDelivery
                or ServerEventStatus.ReadyForImmediateDelivery
                or ServerEventStatus.Recorded;
    }

    internal static bool IsActiveTradeAcceptanceMemo(AuthoritativeEvent ledgerEvent)
    {
        return ledgerEvent.Type == ServerEventType.Trade
            && ledgerEvent.Payload is TradeEventPayload payload
            && payload.Stage == TradeStage.AcceptedMemo
            && ledgerEvent.Status is not ServerEventStatus.Cancelled
                and not ServerEventStatus.Failed
                and not ServerEventStatus.RejectedByTarget
                and not ServerEventStatus.AppliedToSnapshot
                and not ServerEventStatus.Conflict;
    }

    internal static bool IsTradeAcceptanceMemoForViewer(
        AuthoritativeEvent ledgerEvent,
        string viewerUserId,
        bool includeTerminal)
    {
        if (ledgerEvent.Type != ServerEventType.Trade
            || ledgerEvent.Payload is not TradeEventPayload payload
            || payload.Stage != TradeStage.AcceptedMemo
            || !string.Equals(payload.AcceptedByUserId, viewerUserId, StringComparison.Ordinal))
        {
            return false;
        }

        return includeTerminal || IsActiveTradeAcceptanceMemo(ledgerEvent);
    }

    internal static bool IsMatchingActiveTradeAcceptanceMemo(
        AuthoritativeEvent ledgerEvent,
        string tradeEventId,
        string acceptorUserId,
        string? acceptorColonyId)
    {
        return IsActiveTradeAcceptanceMemo(ledgerEvent)
            && ledgerEvent.Payload is TradeEventPayload payload
            && string.Equals(payload.TradeId, tradeEventId, StringComparison.Ordinal)
            && string.Equals(payload.AcceptedByUserId, acceptorUserId, StringComparison.Ordinal)
            && string.Equals(ledgerEvent.Actor.UserId, acceptorUserId, StringComparison.Ordinal)
            && (string.IsNullOrWhiteSpace(acceptorColonyId)
                || string.Equals(ledgerEvent.Actor.ColonyId, acceptorColonyId, StringComparison.Ordinal));
    }

    internal static IReadOnlyList<AuthoritativeEvent> FindActiveTradeAcceptanceMemos(
        IAuthoritativeEventLedger ledger,
        string tradeEventId)
    {
        return ledger.ListByType(ServerEventType.Trade)
            .Where(ledgerEvent =>
                IsActiveTradeAcceptanceMemo(ledgerEvent)
                && ledgerEvent.Payload is TradeEventPayload payload
                && string.Equals(payload.TradeId, tradeEventId, StringComparison.Ordinal))
            .ToList();
    }

    internal static ProtocolResponse? ValidateTradeFulfillmentRequest(FulfillTradeOrderRequest request)
    {
        TradeFulfillmentMode fulfillmentMode = ResolveRequestedTradeFulfillmentMode(request.FulfillmentMode);
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey)
            || string.IsNullOrWhiteSpace(request.TradeEventId)
            || (fulfillmentMode == TradeFulfillmentMode.SelfDelivery && string.IsNullOrWhiteSpace(request.AcceptedMemoEventId))
            || request.Acceptor is null
            || string.IsNullOrWhiteSpace(request.Acceptor.UserId)
            || string.IsNullOrWhiteSpace(request.Acceptor.ColonyId)
            || string.IsNullOrWhiteSpace(request.Acceptor.SnapshotId)
            || request.DeliveredThings is null)
        {
            return ProtocolResponse.Reject(
                ProtocolErrorCode.ValidationFailed,
                Format("Trade.FulfillmentMissingFields"));
        }

        foreach (ThingReferenceDto thing in request.DeliveredThings)
        {
            if (string.IsNullOrWhiteSpace(thing.GlobalKey)
                || string.IsNullOrWhiteSpace(thing.DefName)
                || thing.StackCount <= 0)
            {
                return ProtocolResponse.Reject(
                    ProtocolErrorCode.ValidationFailed,
                    Format("Trade.FulfillmentInvalidDeliveredThing"));
            }

        }

        return null;
    }

    internal static ProtocolResponse? ValidateTradeAcceptanceRequest(AcceptTradeOrderRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey)
            || string.IsNullOrWhiteSpace(request.TradeEventId)
            || request.Acceptor is null
            || string.IsNullOrWhiteSpace(request.Acceptor.UserId)
            || string.IsNullOrWhiteSpace(request.Acceptor.ColonyId)
            || string.IsNullOrWhiteSpace(request.Acceptor.SnapshotId))
        {
            return ProtocolResponse.Reject(
                ProtocolErrorCode.ValidationFailed,
                Format("Trade.AcceptanceMissingFields"));
        }

        return null;
    }

    internal static ProtocolResponse? ValidateTradeCloseRequest(CloseTradeOrderRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey)
            || string.IsNullOrWhiteSpace(request.TradeEventId)
            || request.Owner is null
            || string.IsNullOrWhiteSpace(request.Owner.UserId)
            || string.IsNullOrWhiteSpace(request.Owner.ColonyId)
            || string.IsNullOrWhiteSpace(request.Owner.SnapshotId))
        {
            return ProtocolResponse.Reject(
                ProtocolErrorCode.ValidationFailed,
                Format("Trade.CloseMissingFields"));
        }

        return null;
    }

    internal static TradeFulfillmentMode ResolveTradeFulfillmentMode(CreateTradeOrderRequest request)
    {
        if (request.AllowSelfPickup && request.AllowServerDropPod)
        {
            return TradeFulfillmentMode.Unspecified;
        }

        if (request.AllowServerDropPod)
        {
            return TradeFulfillmentMode.ServerDropPod;
        }

        return request.AllowSelfPickup
            ? TradeFulfillmentMode.SelfDelivery
            : TradeFulfillmentMode.Unspecified;
    }

    internal static TradeFulfillmentMode ResolveRequestedTradeFulfillmentMode(string? fulfillmentMode)
    {
        return Enum.TryParse(fulfillmentMode, ignoreCase: true, out TradeFulfillmentMode parsed)
            ? parsed
            : TradeFulfillmentMode.Unspecified;
    }

    internal static AuthoritativeEvent[] FindActiveAcceptanceCandidates(IAuthoritativeEventLedger ledger, string orderId, ProtocolIdentity identity) =>
        ledger.ListForUser(identity.UserId).Where(item =>
            item.Payload is TradeEventPayload { Stage: TradeStage.AcceptedMemo } payload && payload.TradeId == orderId
            && item.Status is not (ServerEventStatus.Cancelled or ServerEventStatus.Failed or ServerEventStatus.RejectedByTarget
                or ServerEventStatus.AppliedToSnapshot or ServerEventStatus.Conflict)
            && (EventVisibility.MatchesParty(item.Actor, identity.UserId, identity.ColonyId!)
                || EventVisibility.MatchesParty(item.Target, identity.UserId, identity.ColonyId!))).ToArray();

    internal static bool MatchesTradeAcceptance(AuthoritativeEvent memo, AuthoritativeEvent order, AcceptTradeOrderRequest request)
    {
        bool SameParty(EventParty party) => party.UserId == request.Acceptor.UserId
            && party.ColonyId == request.Acceptor.ColonyId && string.IsNullOrWhiteSpace(party.FactionId);
        if (!IsActiveTradeAcceptanceMemo(memo) || !SameParty(memo.Actor) || !SameParty(memo.Target)
            || memo.Payload is not TradeEventPayload payload || order.Payload is not TradeEventPayload original)
            return false;
        var expected = new TradeEventPayload(order.EventId, TradeStage.AcceptedMemo, original.OfferedItems, original.RequestedItems,
            original.FeeSilver, AcceptedByUserId: request.Acceptor.UserId, PostagePaidByAcceptor: request.PostagePaidByAcceptor);
        return SameTradeEvidence(payload, expected)
            && SameTradeEvidence(memo.TargetContext, order.TargetContext);
    }

    internal static bool TryGetCompletedTradeParticipants(IAuthoritativeEventLedger ledger, AuthoritativeEvent order, out IReadOnlyList<string> participants)
    {
        participants = [];
        if (order.Status != ServerEventStatus.AppliedToSnapshot || order.Payload is not TradeEventPayload original) return false;
        var related = ledger.ListAll().Where(item => item.Payload is TradeEventPayload p && p.TradeId == order.EventId).ToArray();
        var exchanges = related.Where(item => item.Payload is TradeEventPayload
            { Stage: TradeStage.SelfDeliveryExchange or TradeStage.ServerDropPodExchange }).ToArray();
        if (exchanges.Length != 1) return false;
        var exchange = exchanges[0]; var payload = (TradeEventPayload)exchange.Payload;
        bool dropPod = payload.Stage == TradeStage.ServerDropPodExchange;
        if (exchange.Type != ServerEventType.Trade || exchange.Status != ServerEventStatus.AppliedToSnapshot
            || string.IsNullOrWhiteSpace(exchange.AppliedSnapshotId) || exchange.AppliedAtUtc is null
            || string.IsNullOrWhiteSpace(exchange.DeliveredToSnapshotId) || exchange.DeliveredAtUtc is null
            || exchange.LastApplicationResult != EventApplicationResultKind.Applied
            || exchange.Target != order.Actor || exchange.Actor.UserId == order.Actor.UserId
            || string.IsNullOrWhiteSpace(exchange.Actor.UserId) || string.IsNullOrWhiteSpace(exchange.Actor.ColonyId)
            || payload.AcceptedByUserId != exchange.Actor.UserId || payload.FeeSilver != original.FeeSilver
            || payload.FulfillmentMode != (dropPod ? TradeFulfillmentMode.ServerDropPod : TradeFulfillmentMode.SelfDelivery)
            || original.FulfillmentMode != TradeFulfillmentMode.Unspecified && original.FulfillmentMode != payload.FulfillmentMode
            || payload.PostagePaidByAcceptor != dropPod || !SameTradeEvidence(payload.OfferedItems, original.OfferedItems)
            || !SameTradeEvidence(exchange.TargetContext, order.TargetContext)
            || payload.RequestedItems.Any(item => item.SourceSnapshotId != exchange.AppliedSnapshotId)) return false;

        var memos = related.Where(item => item.Payload is TradeEventPayload { Stage: TradeStage.AcceptedMemo }).ToArray();
        if ((!dropPod || payload.AcceptedMemoEventId is not null) && !memos.Any(memo => memo.EventId == payload.AcceptedMemoEventId)) return false;
        foreach (var memo in memos)
        {
            var memoPayload = (TradeEventPayload)memo.Payload;
            bool selected = memo.EventId == payload.AcceptedMemoEventId;
            if (memo.Status != (selected ? ServerEventStatus.AppliedToSnapshot : ServerEventStatus.Cancelled)
                || selected && memo.Actor != exchange.Actor || memo.Actor.UserId == order.Actor.UserId
                || !MatchesTradeAcceptance(memo with { Status = ServerEventStatus.Recorded }, order,
                    new AcceptTradeOrderRequest(memo.IdempotencyKey, order.EventId,
                        new(memo.Actor.UserId, memo.Actor.ColonyId, "committed"), memoPayload.PostagePaidByAcceptor))) return false;
        }

        string suffix = payload.AcceptedMemoEventId ?? exchange.Actor.UserId + ":" + exchange.Actor.ColonyId;
        var ownerDelivery = ledger.FindByIdempotencyKey($"trade-completed-owner-delivery:{order.EventId}:{suffix}");
        var buyerDelivery = ledger.FindByIdempotencyKey($"trade-completed-acceptor-delivery:{order.EventId}:{suffix}");
        if (!MatchesTradeCompletionDelivery(ownerDelivery, exchange.Actor, order.Actor, payload.RequestedItems,
                ItemDeliveryPurpose.TradeCompletedOwnerDelivery)
            || !SameTradeEvidence(ownerDelivery!.TargetContext, ResolveTradeCompletedOwnerDeliveryTargetContext(order, payload.FulfillmentMode))
            || (dropPod ? !MatchesTradeCompletionDelivery(buyerDelivery, order.Actor, exchange.Actor, payload.OfferedItems,
                    ItemDeliveryPurpose.TradeCompletedAcceptorDelivery) : buyerDelivery is not null)) return false;
        participants = memos.Select(memo => memo.Actor.UserId).Append(exchange.Actor.UserId).Distinct(StringComparer.Ordinal).ToArray();
        return true;
    }

    internal static bool MatchesTradeCompletionDelivery(AuthoritativeEvent? delivery, EventParty actor, EventParty target,
        IReadOnlyList<EventThingReference> items, ItemDeliveryPurpose purpose) => delivery is not null
        && delivery.Type == ServerEventType.ItemDelivery && delivery.Actor == actor && delivery.Target == target
        && delivery.Status is not (ServerEventStatus.Cancelled or ServerEventStatus.Failed or ServerEventStatus.Conflict or ServerEventStatus.RejectedByTarget)
        && delivery.TargetDecision != TargetEventDecision.Rejected
        && delivery.Payload is ItemDeliveryEventPayload { Message: null, DeliveryKind: null } payload
        && payload.Purpose == purpose && SameTradeEvidence(payload.Items, items);

    internal static bool SameTradeEvidence<T>(T left, T right) => JsonSerializer.Serialize(left) == JsonSerializer.Serialize(right);
    internal static string? TradeFulfillmentMemoId(FulfillTradeOrderRequest request)
        => string.IsNullOrWhiteSpace(request.AcceptedMemoEventId)
            || ResolveRequestedTradeFulfillmentMode(request.FulfillmentMode) == TradeFulfillmentMode.ServerDropPod
                && request.AcceptedMemoEventId == "drop-pod-direct:" + request.TradeEventId
            ? null : request.AcceptedMemoEventId;

    internal static (string Prefix, ItemDeliveryPurpose Purpose) TradeCancellationReturn(TradeCancellationReason reason) => reason switch
    {
        TradeCancellationReason.Expired => ("trade-expired-owner-return:", ItemDeliveryPurpose.TradeExpiredOwnerReturn),
        TradeCancellationReason.BaselineChanged => ("trade-baseline-invalidated-owner-return:", ItemDeliveryPurpose.TradeBaselineChangedOwnerReturn),
        _ => ("trade-cancelled-owner-return:", ItemDeliveryPurpose.TradeCancelledOwnerReturn)
    };

}
