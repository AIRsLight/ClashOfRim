using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Network.Modules.Authorization;
using static AIRsLight.ClashOfRim.Network.Modules.Trades.TradeRules;
using static AIRsLight.ClashOfRim.Network.Modules.Events.EventProtocolMapper;
using static AIRsLight.ClashOfRim.Network.Modules.Transfers.ItemTransferMapper;
using static AIRsLight.ClashOfRim.Network.ServerText;

namespace AIRsLight.ClashOfRim.Network.Modules.Trades;

internal sealed class TradeMarket(IAuthoritativeEventLedger ledger, SnapshotRequestAuthorization requests,
    TradeLifecycle lifecycle, TradeDelivery delivery, Func<ClashOfRimServerConfiguration> configuration)
{
    internal ListTradeOrdersResponse ListTradeOrders(ListTradeOrdersRequest request)
    {
        ListTradeOrdersResponse Reject(ProtocolResponse failure) => new ListTradeOrdersResponse(
            failure, Array.Empty<TradeOrderSummaryDto>(), configuration().TradeMarketplaceEnabled);
        if (string.IsNullOrWhiteSpace(request.UserId)
            || string.IsNullOrWhiteSpace(request.ColonyId)
            || string.IsNullOrWhiteSpace(request.CurrentSnapshotId))
        {
            return Reject(ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, Format("Trade.MarketRequestMissingFields")));
        }

        using (var mutation = requests.EnterColony(request.AuthToken, request.UserId, request.ColonyId))
        {
            if (mutation.Rejection is { } rejection) return Reject(rejection);
            lifecycle.ApplyRecoveredTradeOrderExpirations(DateTimeOffset.UtcNow);
            return BuildTradeOrdersResponse(request);
        }

    }

    private ListTradeOrdersResponse BuildTradeOrdersResponse(ListTradeOrdersRequest request)
    {
        IReadOnlyList<AuthoritativeEvent> events = ledger.ListByType(ServerEventType.Trade);
        IReadOnlyDictionary<string, AuthoritativeEvent> viewerMemoByTradeId = BuildLatestActiveViewerTradeMemoByTradeId(
            events,
            request.UserId);
        IReadOnlySet<string> viewerInvolvedTradeIds = BuildViewerInvolvedTradeIds(events, request.UserId);
        string scope = string.IsNullOrWhiteSpace(request.Scope)
            ? "Open"
            : request.Scope.Trim();

        int offset = Math.Max(0, request.Offset);
        int limit = request.Limit <= 0 ? 10 : Math.Clamp(request.Limit, 1, 50);
        List<AuthoritativeEvent> visibleOrderEvents = events
            .Where(ledgerEvent => IsTradeOrderVisibleInScope(
                ledgerEvent,
                request.UserId,
                scope,
                viewerMemoByTradeId.ContainsKey(ledgerEvent.EventId),
                viewerInvolvedTradeIds.Contains(ledgerEvent.EventId)))
            .OrderByDescending(ledgerEvent => ledgerEvent.CreatedAtUtc)
            .ThenBy(ledgerEvent => ledgerEvent.EventId, StringComparer.Ordinal)
            .ToList();

        List<AuthoritativeEvent> pageEvents = visibleOrderEvents
            .Skip(offset)
            .Take(limit)
            .ToList();
        HashSet<string> pageTradeIds = pageEvents
            .Select(ledgerEvent => ledgerEvent.EventId)
            .ToHashSet(StringComparer.Ordinal);
        IReadOnlyDictionary<string, int> acceptedMemoCountByTradeId = BuildActiveMemoCountByTradeId(events, pageTradeIds);
        IReadOnlyDictionary<string, ProtocolIdentity> counterpartyByTradeId = BuildTradeCounterpartyByTradeId(events, pageTradeIds);
        List<TradeOrderSummaryDto> page = pageEvents
            .Select(ledgerEvent => ToTradeOrderSummary(
                ledgerEvent,
                acceptedMemoCountByTradeId.TryGetValue(ledgerEvent.EventId, out int memoCount) ? memoCount : 0,
                request.UserId,
                request.ColonyId,
                request.CurrentSnapshotId,
                viewerMemoByTradeId.TryGetValue(ledgerEvent.EventId, out AuthoritativeEvent? viewerMemo)
                    ? viewerMemo
                    : null,
                counterpartyByTradeId.TryGetValue(ledgerEvent.EventId, out ProtocolIdentity? counterparty)
                    ? counterparty
                    : null))
            .ToList();

        return new ListTradeOrdersResponse(
            ProtocolResponse.Ok(ServerLocalization.Text("Trade.MarketReturned")),
            page,
            configuration().TradeMarketplaceEnabled,
            visibleOrderEvents.Count,
            offset,
            limit,
            offset + page.Count < visibleOrderEvents.Count);
    }

    private static IReadOnlyDictionary<string, AuthoritativeEvent> BuildLatestActiveViewerTradeMemoByTradeId(
        IReadOnlyList<AuthoritativeEvent> events,
        string viewerUserId)
    {
        var result = new Dictionary<string, AuthoritativeEvent>(StringComparer.Ordinal);
        foreach (AuthoritativeEvent ledgerEvent in events)
        {
            if (!IsTradeAcceptanceMemoForViewer(ledgerEvent, viewerUserId, includeTerminal: false)
                || ledgerEvent.Payload is not TradeEventPayload payload)
            {
                continue;
            }

            if (!result.TryGetValue(payload.TradeId, out AuthoritativeEvent? current)
                || ledgerEvent.CreatedAtUtc > current.CreatedAtUtc)
            {
                result[payload.TradeId] = ledgerEvent;
            }

        }

        return result;
    }

    private static IReadOnlySet<string> BuildViewerInvolvedTradeIds(
        IReadOnlyList<AuthoritativeEvent> events,
        string viewerUserId)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(viewerUserId))
        {
            return result;
        }

        foreach (AuthoritativeEvent ledgerEvent in events)
        {
            if (ledgerEvent.Type != ServerEventType.Trade
                || ledgerEvent.Payload is not TradeEventPayload payload
                || string.IsNullOrWhiteSpace(payload.TradeId))
            {
                continue;
            }

            if ((payload.Stage == TradeStage.AcceptedMemo
                    && string.Equals(payload.AcceptedByUserId, viewerUserId, StringComparison.Ordinal)
                    && ledgerEvent.Status is not ServerEventStatus.Failed
                        and not ServerEventStatus.RejectedByTarget
                        and not ServerEventStatus.Conflict)
                || (payload.Stage is TradeStage.SelfDeliveryExchange or TradeStage.ServerDropPodExchange
                    && (string.Equals(payload.AcceptedByUserId, viewerUserId, StringComparison.Ordinal)
                        || string.Equals(ledgerEvent.Actor.UserId, viewerUserId, StringComparison.Ordinal))))
            {
                result.Add(payload.TradeId);
            }

        }

        return result;
    }

    private static IReadOnlyDictionary<string, int> BuildActiveMemoCountByTradeId(
        IReadOnlyList<AuthoritativeEvent> events,
        IReadOnlySet<string> tradeIds)
    {
        var result = new Dictionary<string, int>(tradeIds.Count, StringComparer.Ordinal);
        if (tradeIds.Count == 0)
        {
            return result;
        }

        foreach (AuthoritativeEvent ledgerEvent in events)
        {
            if (!IsActiveTradeAcceptanceMemo(ledgerEvent)
                || ledgerEvent.Payload is not TradeEventPayload payload
                || !tradeIds.Contains(payload.TradeId))
            {
                continue;
            }

            result[payload.TradeId] = result.TryGetValue(payload.TradeId, out int count)
                ? count + 1
                : 1;
        }

        return result;
    }

    private TradeOrderSummaryDto ToTradeOrderSummary(
        AuthoritativeEvent ledgerEvent,
        int acceptedMemoCount,
        string viewerUserId,
        string viewerColonyId,
        string viewerSnapshotId,
        AuthoritativeEvent? viewerMemo = null,
        ProtocolIdentity? counterparty = null)
    {
        TradeEventPayload payload = (TradeEventPayload)ledgerEvent.Payload;
        return new TradeOrderSummaryDto(
            ledgerEvent.EventId,
            new ProtocolIdentity(
                ledgerEvent.Actor.UserId,
                ledgerEvent.Actor.ColonyId,
                snapshotId: null),
            payload.OfferedItems.Select(ToThingReferenceDto).ToList(),
            payload.RequestedItems.Select(ToThingReferenceDto).ToList(),
            payload.FeeSilver,
            payload.FulfillmentMode is TradeFulfillmentMode.SelfDelivery or TradeFulfillmentMode.Unspecified,
            payload.FulfillmentMode is TradeFulfillmentMode.ServerDropPod or TradeFulfillmentMode.Unspecified,
            acceptedMemoCount,
            ledgerEvent.CreatedAtUtc,
            ledgerEvent.Status.ToString(),
            viewerMemo is not null,
            viewerMemo?.EventId,
            delivery.BuildTradePostageQuote(ledgerEvent, viewerUserId, viewerColonyId, viewerSnapshotId),
            ToEventTargetContextDto(ledgerEvent.TargetContext),
            ledgerEvent.CreatedAtUtc + configuration().TradeOrderExpiration,
            counterparty);
    }

    private static IReadOnlyDictionary<string, ProtocolIdentity> BuildTradeCounterpartyByTradeId(
        IReadOnlyList<AuthoritativeEvent> events,
        IReadOnlySet<string> tradeIds)
    {
        var result = new Dictionary<string, (DateTimeOffset CreatedAtUtc, ProtocolIdentity Identity)>(StringComparer.Ordinal);
        foreach (AuthoritativeEvent ledgerEvent in events)
        {
            if (ledgerEvent.Type != ServerEventType.Trade
                || ledgerEvent.Payload is not TradeEventPayload payload
                || !tradeIds.Contains(payload.TradeId)
                || payload.Stage is not (TradeStage.SelfDeliveryExchange or TradeStage.ServerDropPodExchange)
                || ledgerEvent.Status is ServerEventStatus.Cancelled or ServerEventStatus.Failed or ServerEventStatus.Conflict)
            {
                continue;
            }

            AddLatestTradeCounterparty(result, payload.TradeId, ledgerEvent.Actor, ledgerEvent.CreatedAtUtc);
        }

        foreach (AuthoritativeEvent ledgerEvent in events)
        {
            if (ledgerEvent.Type != ServerEventType.Trade
                || ledgerEvent.Payload is not TradeEventPayload payload
                || !tradeIds.Contains(payload.TradeId)
                || result.ContainsKey(payload.TradeId)
                || payload.Stage != TradeStage.AcceptedMemo
                || ledgerEvent.Status is ServerEventStatus.Cancelled or ServerEventStatus.Failed or ServerEventStatus.RejectedByTarget or ServerEventStatus.Conflict)
            {
                continue;
            }

            AddLatestTradeCounterparty(result, payload.TradeId, ledgerEvent.Actor, ledgerEvent.CreatedAtUtc);
        }

        return result.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.Identity,
            StringComparer.Ordinal);
    }

    private static void AddLatestTradeCounterparty(
        Dictionary<string, (DateTimeOffset CreatedAtUtc, ProtocolIdentity Identity)> result,
        string tradeId,
        EventParty party,
        DateTimeOffset createdAtUtc)
    {
        if (string.IsNullOrWhiteSpace(tradeId) || string.IsNullOrWhiteSpace(party.UserId))
        {
            return;
        }

        if (!result.TryGetValue(tradeId, out (DateTimeOffset CreatedAtUtc, ProtocolIdentity Identity) current)
            || createdAtUtc > current.CreatedAtUtc)
        {
            result[tradeId] = (
                createdAtUtc,
                new ProtocolIdentity(party.UserId, party.ColonyId, snapshotId: null));
        }

    }

}
