using static AIRsLight.ClashOfRim.Network.Modules.Transfers.ItemTransferMapper;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using Microsoft.AspNetCore.Http;

namespace AIRsLight.ClashOfRim.Network;

public static partial class ClashOfRimNetworkServer
{
    private static string? TradeFulfillmentMemoId(FulfillTradeOrderRequest request)
        => string.IsNullOrWhiteSpace(request.AcceptedMemoEventId)
            || ResolveRequestedTradeFulfillmentMode(request.FulfillmentMode) == TradeFulfillmentMode.ServerDropPod
                && request.AcceptedMemoEventId == "drop-pod-direct:" + request.TradeEventId
            ? null : request.AcceptedMemoEventId;

    private static void CommitTradeSnapshot(ClashOfRimNetworkState state, SaveSnapshotPackage package,
        SnapshotUploadResult upload, TradeSnapshotMutation mutation)
    {
        state.SnapshotCommits.CommitTrade(state.SnapshotStore, state.BankLoans, state.Ledger,
            package, upload.AcceptedSnapshot!, mutation,
            (snapshot, kind) => RunCommittedSnapshotPostUpload(state, snapshot, kind),
            user => state.EventNotifications.SignalUsers(new[] { user }),
            item => RecordAuthoritativeEventAchievements(state, item, item.CreatedAtUtc), upload.SnapshotUploadKind, state.MercenaryContracts, preparedStoragePayload: upload.PreparedStoragePayload);
    }

    private static IResult TradeOrderReplayResponse(ClashOfRimNetworkState state, CreateTradeOrderRequest request,
        string snapshotId, AuthoritativeEvent existing, string binding)
    {
        if (existing.Payload is not TradeEventPayload payload || payload.CreationRequestHash != binding
            || !TradeSnapshotReplayPolicy.Matches(existing, request.Owner.UserId, request.Owner.ColonyId, snapshotId, order: true))
            return Results.Ok(new EventCreationResponse(ProtocolResponse.Reject(ProtocolErrorCode.Conflict,
                "Trade idempotency key belongs to another operation."), null, ProtocolDeliverySemantics.OfflinePending));
        return Results.Ok(new EventCreationResponse(
            new ProtocolResponse(true, ProtocolErrorCode.DuplicateRequest, T("Trade.OrderTransactionDuplicate")),
            existing.EventId, ProtocolDeliverySemantics.OfflinePending, snapshotId,
            FindSnapshotNextLineageToken(state, request.Owner.UserId, request.Owner.ColonyId!, snapshotId)));
    }

    private static IResult TradeFulfillmentFailure(FulfillTradeOrderRequest request, ProtocolResponse failure)
        => Results.Ok(new FulfillTradeOrderResponse(failure, request.TradeEventId, request.AcceptedMemoEventId,
            null, false, Array.Empty<ThingReferenceDto>(), Array.Empty<string>(), string.Empty));

    private static IResult TradeFulfillmentReplayResponse(ClashOfRimNetworkState state, FulfillTradeOrderRequest request,
        string snapshotId, AuthoritativeEvent existing, string binding)
    {
        if (!TradeSnapshotReplayPolicy.Matches(existing, request.Acceptor.UserId, request.Acceptor.ColonyId, snapshotId, order: false)
            || existing.Payload is not TradeEventPayload payload || payload.CreationRequestHash != binding || payload.TradeId != request.TradeEventId
            || payload.AcceptedMemoEventId != TradeFulfillmentMemoId(request)
            || payload.FulfillmentMode != ResolveRequestedTradeFulfillmentMode(request.FulfillmentMode))
            return TradeFulfillmentFailure(request, ProtocolResponse.Reject(ProtocolErrorCode.Conflict,
                "Trade idempotency key belongs to another operation."));
        string fulfillmentKey = payload.AcceptedMemoEventId ?? request.Acceptor.UserId + ":" + request.Acceptor.ColonyId;
        return Results.Ok(new FulfillTradeOrderResponse(
            new ProtocolResponse(true, ProtocolErrorCode.DuplicateRequest, T("Trade.FulfillmentTransactionDuplicate")),
            payload.TradeId, payload.AcceptedMemoEventId, existing.EventId, false,
            payload.OfferedItems.Select(ToThingReferenceDto).ToList(), Array.Empty<string>(),
            state.Ledger.Find(payload.TradeId)?.Status.ToString() ?? existing.Status.ToString(),
            state.Ledger.FindByIdempotencyKey($"trade-completed-acceptor-delivery:{payload.TradeId}:{fulfillmentKey}")?.EventId,
            state.Ledger.FindByIdempotencyKey($"trade-completed-owner-delivery:{payload.TradeId}:{fulfillmentKey}")?.EventId,
            snapshotId, FindSnapshotNextLineageToken(state, request.Acceptor.UserId, request.Acceptor.ColonyId!, snapshotId)));
    }
}
