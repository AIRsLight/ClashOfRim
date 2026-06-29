using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.ClientSnapshots;
using AIRsLight.ClashOfRim.Gifts;
using AIRsLight.ClashOfRim.Pawns;
using AIRsLight.ClashOfRim.Trades;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace AIRsLight.ClashOfRim;

public sealed partial class ClashOfRimMod
{
    internal void StartCreateManualTradeOrder(TradeOrderDraft draft)
    {
        if (TryRejectBlockedByLocalAtomicMutation(out string atomicMessage))
        {
            tradeStatus = atomicMessage;
            Messages.Message(atomicMessage, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        if (!CanRunManualSync(out string failureReason))
        {
            tradeStatus = failureReason;
            return;
        }

        if (draft.OfferedThings.Count == 0 && draft.RequestedThings.Count == 0)
        {
            tradeStatus = ClashOfRimText.Key("ClashOfRim.Trade.StatusDraftMissingThings");
            return;
        }

        if (!ValidateTradeDraftInventory(draft, out string inventoryFailure))
        {
            tradeStatus = inventoryFailure;
            return;
        }

        string operation = ClashOfRimText.Key("ClashOfRim.SnapshotConfirmationFailure.OperationTrade");
        BeginLocalAtomicMutation(operation, ClashOfRimText.Key("ClashOfRim.Trade.StatusOrderReserving"));
        if (!ApplyTradeDraftLocalReservation(draft, out string reservationMessage))
        {
            ClearLocalAtomicMutation();
            tradeStatus = ClashOfRimText.Key("ClashOfRim.Trade.StatusOrderCreatedReservationFailed", reservationMessage.Named("MESSAGE"));
            return;
        }

        string ticks = DateTime.UtcNow.Ticks.ToString();
        string idempotencyKey = $"manual-trade:{settings.UserId}:{ticks}";
        StartSubmitReservedManualTradeOrder(draft, idempotencyKey, reservationMessage);
    }

    private void StartSubmitReservedManualTradeOrder(TradeOrderDraft draft, string idempotencyKey, string reservationMessage)
    {
        string operation = ClashOfRimText.Key("ClashOfRim.SnapshotConfirmationFailure.OperationTrade");
        if (TryRejectBlockedByDifferentLocalAtomicMutation(operation, out string blockedMessage))
        {
            tradeStatus = blockedMessage;
            Messages.Message(blockedMessage, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        BeginLocalAtomicMutation(operation, ClashOfRimText.Key("ClashOfRim.Trade.StatusOrderPackagingSnapshot"));
        if (!TryBuildCurrentGameSnapshotPackage(
                settings.UserId,
                settings.ColonyId,
                out ModSnapshotPackageBuildResult build,
                out string buildFailureReason))
        {
            tradeStatus = ClashOfRimText.Key("ClashOfRim.Trade.StatusOrderSnapshotBuildFailed", buildFailureReason.Named("REASON"));
            ShowUnconfirmedSnapshotFailure(
                operation,
                tradeStatus,
                () => StartSubmitReservedManualTradeOrder(draft, idempotencyKey, reservationMessage));
            return;
        }

        StartSubmitReservedManualTradeOrderWithSnapshot(
            draft,
            idempotencyKey,
            reservationMessage,
            build.Package!,
            build.Payload!);
    }

    private void StartSubmitReservedManualTradeOrderWithSnapshot(
        TradeOrderDraft draft,
        string idempotencyKey,
        string reservationMessage,
        ModSnapshotPackageMetadataDto confirmedSnapshot,
        byte[] confirmedPayload)
    {
        string operation = ClashOfRimText.Key("ClashOfRim.SnapshotConfirmationFailure.OperationTrade");
        if (TryRejectBlockedByDifferentLocalAtomicMutation(operation, out string blockedMessage))
        {
            tradeStatus = blockedMessage;
            Messages.Message(blockedMessage, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        if (!TryBeginSnapshotUploadTransaction())
        {
            tradeStatus = ClashOfRimText.Key("ClashOfRim.SnapshotUpload.Busy");
            Messages.Message(tradeStatus, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        BeginLocalAtomicMutation(operation, ClashOfRimText.Key("ClashOfRim.Trade.StatusOrderSubmittingTransaction"));
        tradeStatus = ClashOfRimText.Key("ClashOfRim.Trade.StatusSubmittingOrder", draft.FeeSilver.Named("FEE"));
        Task.Run(async () =>
        {
            try
            {
                using var httpClient = new HttpClient();
                var client = new ClashOfRimModNetworkClient(
                    httpClient,
                    ClashOfRimClientNetworkContext.FromSettings(settings));
                await UploadPawnPackagesForTradeAsync(
                    client,
                    draft.OfferedThings.Concat(draft.RequestedThings).ToList(),
                        idempotencyKey);
                ClashOfRimClientNetworkResult<ModEventCreationResponseDto> created =
                    await client.CreateTradeOrderWithSnapshotAsync(
                        idempotencyKey,
                        draft.OfferedThings,
                        draft.RequestedThings,
                        draft.FeeSilver,
                        draft.AllowSelfPickup,
                        draft.AllowServerDropPod,
                        confirmedSnapshot,
                        confirmedPayload);
                tradeStatus = FormatCreationResult(created, ClashOfRimText.Key("ClashOfRim.Trade.OrderName"));
                ModEventCreationResponseDto? response = created.Response;
                if (created.Success
                    && response is not null
                    && response.Result?.Accepted != false
                    && !string.IsNullOrWhiteSpace(response.EventId))
                {
                    string eventId = response.EventId!;
                    ClashOfRimGameComponent.EnqueueMainThreadAction(() =>
                    {
                        if (!string.IsNullOrWhiteSpace(response.AppliedSnapshotId))
                        {
                            PersistAcceptedSnapshotLineage(
                                response.AppliedSnapshotId!,
                                response.NextLineageToken);
                        }

                        tradeStatus = ClashOfRimText.Key("ClashOfRim.Trade.StatusOrderCreatedReserved", eventId.Named("EVENTID"), reservationMessage.Named("MESSAGE"));
                        CompleteLocalAtomicMutation();
                        CloseUnconfirmedSnapshotFailureWindow();
                    });
                }
                else if (!created.Success || response is null || response.Result?.Accepted == false)
                {
                    ModProtocolResponseDto? rejected = response?.Result;
                    string message = created.Success && response is not null
                        ? ClashOfRimText.Key(
                            "ClashOfRim.Trade.StatusCreateRejected",
                            (rejected?.ErrorCode.ToString() ?? string.Empty).Named("CODE"),
                            (rejected?.Message ?? string.Empty).Named("MESSAGE"))
                        : ClashOfRimText.Key(
                            "ClashOfRim.Trade.StatusOrderSubmitException",
                            (created.ErrorCode ?? string.Empty).Named("TYPE"),
                            (created.Message ?? string.Empty).Named("MESSAGE"));
                    tradeStatus = message;
                    ShowUnconfirmedSnapshotFailure(
                        operation,
                        message,
                        () => StartSubmitReservedManualTradeOrderWithSnapshot(draft, idempotencyKey, reservationMessage, confirmedSnapshot, confirmedPayload));
                }
            }
            catch (Exception ex)
            {
                tradeStatus = ClashOfRimText.Key("ClashOfRim.Trade.StatusOrderSubmitException", ex.GetType().Name.Named("TYPE"), ex.Message.Named("MESSAGE"));
                ShowUnconfirmedSnapshotFailure(
                    operation,
                    tradeStatus,
                    () => StartSubmitReservedManualTradeOrderWithSnapshot(draft, idempotencyKey, reservationMessage, confirmedSnapshot, confirmedPayload));
                Log.Warning("[ClashOfRim] Manual trade order creation failed: " + ex);
            }
            finally
            {
                EndSnapshotUploadTransaction();
            }
        });
    }

    private bool ValidateTradeDraftInventory(TradeOrderDraft draft, out string failureReason)
    {
        Map? map = Find.CurrentMap;
        if (map is null)
        {
            failureReason = ClashOfRimText.Key("ClashOfRim.Trade.StatusNoMapReserve");
            return false;
        }

        HashSet<Thing> accessibleItems = new(TradeInventoryUtility.AccessibleMapItems(map, draft.RequireTradeBeaconRange));
        foreach (TradeOfferSelection selection in draft.LocalOfferedSelections)
        {
            if (selection.Thing.Destroyed || selection.Thing.Map != map || selection.Count <= 0)
            {
                failureReason = ClashOfRimText.Key("ClashOfRim.Trade.StatusTradeThingInvalid");
                return false;
            }

            if (selection.Thing is not Pawn
                && !accessibleItems.Contains(selection.Thing))
            {
                failureReason = ClashOfRimText.Key(
                    "ClashOfRim.Trade.StatusTradeThingOutsideBeacon",
                    selection.Thing.LabelCap.ToString().Named("THING"));
                return false;
            }

            if (selection.Thing.stackCount < selection.Count)
            {
                failureReason = ClashOfRimText.Key(
                    "ClashOfRim.Trade.StatusTradeThingInsufficient",
                    selection.Thing.LabelCap.ToString().Named("THING"),
                    selection.Count.Named("NEEDED"),
                    selection.Thing.stackCount.Named("CURRENT"));
                return false;
            }
        }

        int availableSilver = CountAvailableFeeSilver(map, draft.LocalOfferedSelections, draft.RequireTradeBeaconRange);
        if (availableSilver < draft.FeeSilver)
        {
            failureReason = ClashOfRimText.Key(
                "ClashOfRim.Trade.StatusFeeSilverInsufficient",
                draft.FeeSilver.Named("NEEDED"),
                availableSilver.Named("AVAILABLE"));
            return false;
        }

        failureReason = string.Empty;
        return true;
    }

    private bool ApplyTradeDraftLocalReservation(TradeOrderDraft draft, out string message)
    {
        if (!ValidateTradeDraftInventory(draft, out message))
        {
            return false;
        }

        Map map = Find.CurrentMap!;
        try
        {
            foreach (TradeOfferSelection selection in draft.LocalOfferedSelections)
            {
                RemoveThingCount(selection.Thing, selection.Count);
            }

            int remainingFee = draft.FeeSilver;
            if (remainingFee > 0)
            {
                foreach (Thing silver in FeeSilverCandidates(map, draft.RequireTradeBeaconRange)
                             .Where(thing => thing.def == ThingDefOf.Silver)
                             .OrderBy(thing => thing.stackCount)
                             .ToList())
                {
                    if (remainingFee <= 0)
                    {
                        break;
                    }

                    int take = Math.Min(remainingFee, Math.Max(0, silver.stackCount));
                    if (take <= 0)
                    {
                        continue;
                    }

                    RemoveThingCount(silver, take);
                    remainingFee -= take;
                }
            }

            if (remainingFee > 0)
            {
                message = ClashOfRimText.Key("ClashOfRim.Trade.StatusFeeReservationIncomplete", remainingFee.Named("REMAINING"));
                return false;
            }

            message = ClashOfRimText.Key(
                "ClashOfRim.Trade.StatusReservedLocalThings",
                draft.LocalOfferedSelections.Count.Named("THINGCOUNT"),
                draft.FeeSilver.Named("FEE"));
            return true;
        }
        catch (Exception ex)
        {
            message = $"{ex.GetType().Name} {ex.Message}";
            Log.Warning("[ClashOfRim][Trade] local reservation exception: " + ex);
            return false;
        }
    }

    private static int CountAvailableFeeSilver(Map map, IReadOnlyList<TradeOfferSelection> offeredSelections, bool requireTradeBeaconRange)
    {
        return FeeSilverCandidates(map, requireTradeBeaconRange)
            .Where(thing => thing.def == ThingDefOf.Silver)
            .Sum(thing =>
            {
                TradeOfferSelection? selection = offeredSelections.FirstOrDefault(entry => ReferenceEquals(entry.Thing, thing));
                return Math.Max(0, thing.stackCount - (selection?.Count ?? 0));
            });
    }

    private static IEnumerable<Thing> FeeSilverCandidates(Map map, bool requireTradeBeaconRange)
    {
        return TradeInventoryUtility.AccessibleMapItems(map, requireTradeBeaconRange);
    }

    private static void RemoveThingCount(Thing thing, int count)
    {
        if (thing is Pawn pawn)
        {
            TradePawnUtility.ApplySoldPawnEffectsAndRemove(pawn);
            return;
        }

        int clamped = Math.Min(count, Math.Max(0, thing.stackCount));
        if (clamped <= 0)
        {
            return;
        }

        Thing removed = thing.SplitOff(clamped);
        if (!removed.Destroyed)
        {
            removed.Destroy(DestroyMode.Vanish);
        }
    }

    private static async Task UploadPawnPackagesForTradeAsync(
        ClashOfRimModNetworkClient client,
        IReadOnlyList<ModThingReferenceDto> things,
        string idempotencyPrefix)
    {
        await PawnPackageTransferService.StoreThingPawnPackagesAsync(client, things, idempotencyPrefix);
        await PawnPackageTransferService.StoreThingStatePackagesAsync(client, things, idempotencyPrefix);
    }

    private static async Task HydratePawnPackagesForTradeAsync(
        ClashOfRimModNetworkClient client,
        IReadOnlyList<ModThingReferenceDto> things)
    {
        await PawnPackageTransferService.HydrateThingPawnPackagesAsync(client, things);
        await PawnPackageTransferService.HydrateThingStatePackagesAsync(client, things);
    }

    internal void StartRefreshTradeOrders(string scope = "Open")
    {
        StartLoadTradeOrdersPage(scope, reset: true);
    }

    internal void StartLoadMoreTradeOrders(string scope = "Open")
    {
        StartLoadTradeOrdersPage(scope, reset: false);
    }

    internal void StartCancelTradeOrder(ModTradeOrderSummaryDto order)
    {
        if (order is null || string.IsNullOrWhiteSpace(order.EventId))
        {
            tradeStatus = ClashOfRimText.Key("ClashOfRim.Trade.StatusMissingOrderEventId");
            return;
        }

        if (!IsOwnOpenTradeOrder(order))
        {
            tradeStatus = ClashOfRimText.Key("ClashOfRim.Trade.StatusCancelNotAllowed");
            return;
        }

        if (!CanRunManualSync(out string failureReason))
        {
            tradeStatus = failureReason;
            return;
        }

        tradeStatus = ClashOfRimText.Key("ClashOfRim.Trade.StatusCancellingOrder");
        string tradeEventId = order.EventId;
        string scope = tradeOrdersScope;
        Task.Run(async () =>
        {
            try
            {
                using var httpClient = new HttpClient();
                var client = new ClashOfRimModNetworkClient(
                    httpClient,
                    ClashOfRimClientNetworkContext.FromSettings(settings));
                ClashOfRimClientNetworkResult<ModCloseTradeOrderResponseDto> result =
                    await client.CancelTradeOrderAsync(
                        "trade-cancel:" + tradeEventId + ":" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        tradeEventId,
                        "OwnerCancelled");
                if (!result.Success || result.Response is null)
                {
                    tradeStatus = ClashOfRimText.Key(
                        "ClashOfRim.Trade.StatusCancelFailed",
                        result.ErrorCode.Named("CODE"),
                        result.Message.Named("MESSAGE"));
                    return;
                }

                if (result.Response.Result is not null && !result.Response.Result.Accepted)
                {
                    tradeStatus = ClashOfRimText.Key(
                        "ClashOfRim.Trade.StatusCancelRejected",
                        result.Response.Result.ErrorCode.Named("CODE"),
                        result.Response.Result.Message.Named("MESSAGE"));
                    return;
                }

                lock (eventStateLock)
                {
                    lastTradeOrders.RemoveAll(cached => string.Equals(cached.EventId, tradeEventId, StringComparison.Ordinal));
                    tradeOrdersSnapshotVersion++;
                }

                tradeStatus = ClashOfRimText.Key(
                    "ClashOfRim.Trade.StatusCancelSucceeded",
                    result.Response.NotifiedAcceptorCount.Named("COUNT"));
                StartRefreshTradeOrders(scope);
            }
            catch (Exception ex)
            {
                tradeStatus = ClashOfRimText.Key(
                    "ClashOfRim.Trade.StatusCancelException",
                    ex.GetType().Name.Named("TYPE"),
                    ex.Message.Named("MESSAGE"));
                Log.Warning("[ClashOfRim] Trade order cancel failed: " + ex);
            }
        });
    }

    internal void StartCancelAcceptedTradeMemo(ModTradeOrderSummaryDto order)
    {
        if (order is null || string.IsNullOrWhiteSpace(order.EventId))
        {
            tradeStatus = ClashOfRimText.Key("ClashOfRim.Trade.StatusMissingOrderEventId");
            return;
        }

        if (!CanCancelAcceptedTradeMemo(order))
        {
            tradeStatus = ClashOfRimText.Key("ClashOfRim.Trade.StatusCancelAcceptedMemoNotAllowed");
            return;
        }

        if (!CanRunManualSync(out string failureReason))
        {
            tradeStatus = failureReason;
            return;
        }

        tradeStatus = ClashOfRimText.Key("ClashOfRim.Trade.StatusCancellingAcceptedMemo");
        string tradeEventId = order.EventId;
        string scope = tradeOrdersScope;
        Task.Run(async () =>
        {
            try
            {
                using var httpClient = new HttpClient();
                var client = new ClashOfRimModNetworkClient(
                    httpClient,
                    ClashOfRimClientNetworkContext.FromSettings(settings));
                ClashOfRimClientNetworkResult<ModCloseTradeOrderResponseDto> result =
                    await client.CancelTradeOrderAsync(
                        "trade-cancel-memo:" + settings.UserId + ":" + tradeEventId + ":" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        tradeEventId,
                        "AcceptorCancelledMemo");
                if (!result.Success || result.Response is null)
                {
                    tradeStatus = ClashOfRimText.Key(
                        "ClashOfRim.Trade.StatusCancelAcceptedMemoFailed",
                        result.ErrorCode.Named("CODE"),
                        result.Message.Named("MESSAGE"));
                    return;
                }

                if (result.Response.Result is not null && !result.Response.Result.Accepted)
                {
                    tradeStatus = ClashOfRimText.Key(
                        "ClashOfRim.Trade.StatusCancelAcceptedMemoRejected",
                        result.Response.Result.ErrorCode.Named("CODE"),
                        result.Response.Result.Message.Named("MESSAGE"));
                    return;
                }

                MarkTradeAcceptedMemoCancelledLocally(tradeEventId, scope);
                tradeStatus = ClashOfRimText.Key("ClashOfRim.Trade.StatusCancelAcceptedMemoSucceeded");
                StartRefreshTradeOrders(scope);
            }
            catch (Exception ex)
            {
                tradeStatus = ClashOfRimText.Key(
                    "ClashOfRim.Trade.StatusCancelAcceptedMemoException",
                    ex.GetType().Name.Named("TYPE"),
                    ex.Message.Named("MESSAGE"));
                Log.Warning("[ClashOfRim] Trade accepted memo cancel failed: " + ex);
            }
        });
    }

    private void MarkTradeAcceptedMemoCancelledLocally(string tradeEventId, string scope)
    {
        lock (eventStateLock)
        {
            ModTradeOrderSummaryDto? cached = lastTradeOrders.FirstOrDefault(order =>
                string.Equals(order.EventId, tradeEventId, StringComparison.Ordinal));
            if (cached is null)
            {
                return;
            }

            cached.ViewerHasAccepted = false;
            cached.ViewerAcceptedMemoEventId = null;
            cached.AcceptedMemoCount = Math.Max(0, cached.AcceptedMemoCount - 1);
            if (string.Equals(scope, "AcceptedByMe", StringComparison.Ordinal))
            {
                lastTradeOrders.Remove(cached);
            }

            tradeOrdersSnapshotVersion++;
        }
    }

    internal bool IsOwnOpenTradeOrder(ModTradeOrderSummaryDto order)
    {
        return string.Equals(order.Owner?.UserId, settings.UserId, StringComparison.Ordinal)
            && string.Equals(order.Owner?.ColonyId, settings.ColonyId, StringComparison.Ordinal)
            && IsOpenTradeOrderStatus(order.Status);
    }

    internal bool CanActOnOpenTradeOrder(ModTradeOrderSummaryDto order)
    {
        return !string.Equals(order.Owner?.UserId, settings.UserId, StringComparison.Ordinal)
            && IsOpenTradeOrderStatus(order.Status);
    }

    internal bool CanCancelAcceptedTradeMemo(ModTradeOrderSummaryDto order)
    {
        return CanActOnOpenTradeOrder(order)
            && order.ViewerHasAccepted
            && !string.IsNullOrWhiteSpace(order.ViewerAcceptedMemoEventId);
    }

    private static bool IsOpenTradeOrderStatus(string? status)
    {
        return string.Equals(status, "PendingOfflineDelivery", StringComparison.Ordinal)
            || string.Equals(status, "ReadyForImmediateDelivery", StringComparison.Ordinal)
            || string.Equals(status, "Recorded", StringComparison.Ordinal);
    }

    private void StartLoadTradeOrdersPage(string scope, bool reset)
    {
        if (!CanRunManualSync(out string failureReason))
        {
            tradeStatus = failureReason;
            return;
        }

        if (tradeOrdersPageLoadInProgress)
        {
            return;
        }

        int offset;
        lock (eventStateLock)
        {
            if (!reset
                && (!tradeOrdersHasMore || !string.Equals(tradeOrdersScope, scope, StringComparison.Ordinal)))
            {
                return;
            }

            if (reset)
            {
                tradeOrdersScope = scope;
                tradeOrdersHasMore = false;
                tradeOrdersTotalCount = 0;
                lastTradeOrders.Clear();
                tradeOrdersSnapshotVersion++;
            }

            offset = reset ? 0 : lastTradeOrders.Count;
        }

        tradeOrdersPageLoadInProgress = true;
        tradeStatus = reset
            ? ClashOfRimText.Key("ClashOfRim.Trade.StatusRefreshingMarket")
            : ClashOfRimText.Key("ClashOfRim.Trade.StatusLoadingMoreMarket");
        Task.Run(async () =>
        {
            try
            {
                using var httpClient = new HttpClient();
                var client = new ClashOfRimModNetworkClient(
                    httpClient,
                    ClashOfRimClientNetworkContext.FromSettings(settings));
                int pageSize = TradeOrdersPageSizeForScope(scope);
                ClashOfRimClientNetworkResult<ModListTradeOrdersResponseDto> result =
                    await client.ListTradeOrdersAsync(scope, offset, pageSize);
                if (!result.Success || result.Response is null)
                {
                    tradeStatus = ClashOfRimText.Key("ClashOfRim.Trade.StatusMarketRefreshFailed", result.ErrorCode.Named("CODE"), result.Message.Named("MESSAGE"));
                    return;
                }

                if (result.Response.Result is not null && !result.Response.Result.Accepted)
                {
                    tradeStatus = ClashOfRimText.Key("ClashOfRim.Trade.StatusMarketRejected", result.Response.Result.ErrorCode.Named("CODE"), result.Response.Result.Message.Named("MESSAGE"));
                    return;
                }

                ModListTradeOrdersResponseDto response = result.Response;
                IReadOnlyList<ModTradeOrderSummaryDto> pageOrders =
                    response.Orders ?? new List<ModTradeOrderSummaryDto>();
                lock (eventStateLock)
                {
                    tradeMarketplaceEnabled = response.TradeMarketplaceEnabled;
                    tradeOrdersScope = scope;
                    tradeOrdersHasMore = response.HasMore;
                    tradeOrdersTotalCount = Math.Max(0, response.TotalCount);
                    if (reset)
                    {
                        lastTradeOrders.Clear();
                    }

                    tradeOrdersSnapshotVersion++;
                }

                tradeStatus = response.TradeMarketplaceEnabled
                    ? pageOrders.Count > 0
                        ? ClashOfRimText.Key("ClashOfRim.Trade.LoadingMore")
                        : FormatPagedTradeOrdersStatus()
                    : ClashOfRimText.Key("ClashOfRim.Trade.Disabled");
                if (!response.TradeMarketplaceEnabled)
                {
                    return;
                }

                foreach (ModTradeOrderSummaryDto order in pageOrders)
                {
                    await HydrateTradeOrderPawnPackagesAsync(client, new[] { order });
                    ModTradeOrderSummaryDto capturedOrder = order;
                    ClashOfRimGameComponent.EnqueueMainThreadAction(() =>
                        TradeUiUtility.PreparePawnPreviewsForTradeOrders(new[] { capturedOrder }));
                    UpsertLoadedTradeOrder(scope, capturedOrder);
                    tradeStatus = FormatPagedTradeOrdersStatus();
                }
            }
            catch (Exception ex)
            {
                tradeStatus = ClashOfRimText.Key("ClashOfRim.Trade.StatusMarketException", ex.GetType().Name.Named("TYPE"), ex.Message.Named("MESSAGE"));
                Log.Warning("[ClashOfRim] Trade market refresh failed: " + ex);
            }
            finally
            {
                tradeOrdersPageLoadInProgress = false;
            }
        });
    }

    private static int TradeOrdersPageSizeForScope(string? scope)
    {
        return string.Equals(scope, "History", StringComparison.Ordinal)
            ? TradeOrdersHistoryPageSize
            : TradeOrdersPageSize;
    }

    private void UpsertLoadedTradeOrder(string scope, ModTradeOrderSummaryDto order)
    {
        lock (eventStateLock)
        {
            if (!string.Equals(tradeOrdersScope, scope, StringComparison.Ordinal))
            {
                return;
            }

            int existingIndex = lastTradeOrders.FindIndex(existing => string.Equals(existing.EventId, order.EventId, StringComparison.Ordinal));
            if (existingIndex >= 0)
            {
                lastTradeOrders[existingIndex] = order;
            }
            else
            {
                lastTradeOrders.Add(order);
            }

            tradeOrdersSnapshotVersion++;
        }
    }

    private string FormatPagedTradeOrdersStatus()
    {
        lock (eventStateLock)
        {
            if (lastTradeOrders.Count == 0)
            {
                return ClashOfRimText.Key("ClashOfRim.Trade.NoAcceptableOrders");
            }

            return ClashOfRimText.Key(
                "ClashOfRim.Trade.PageStatus",
                lastTradeOrders.Count.Named("LOADED"),
                Math.Max(tradeOrdersTotalCount, lastTradeOrders.Count).Named("TOTAL"));
        }
    }

    private static async Task HydrateTradeOrderPawnPackagesAsync(
        ClashOfRimModNetworkClient client,
        IReadOnlyList<ModTradeOrderSummaryDto>? orders)
    {
        if (orders is null || orders.Count == 0)
        {
            return;
        }

        foreach (ModTradeOrderSummaryDto order in orders)
        {
            await TryHydrateTradeOrderPawnPackagesForPreviewAsync(client, order.OfferedThings, order.EventId);
            await TryHydrateTradeOrderPawnPackagesForPreviewAsync(client, order.RequestedThings, order.EventId);
        }
    }

    private static async Task TryHydrateTradeOrderPawnPackagesForPreviewAsync(
        ClashOfRimModNetworkClient client,
        IReadOnlyList<ModThingReferenceDto>? things,
        string? eventId)
    {
        if (things is null || things.Count == 0)
        {
            return;
        }

        try
        {
            IReadOnlyList<ModThingReferenceDto> representatives =
                TradeUiUtility.BuildDisplayGroupRepresentatives(things);
            await HydratePawnPackagesForTradeAsync(client, representatives);
        }
        catch (Exception ex)
        {
            Log.Warning("[ClashOfRim] Failed to hydrate trade pawn preview packages for order "
                + (eventId ?? "<unknown>")
                + ": "
                + ex.Message);
        }
    }

    internal void OpenTradeOrderDialog()
    {
        if (!tradeMarketplaceEnabled)
        {
            Messages.Message(ClashOfRimText.Key("ClashOfRim.Trade.Disabled"), MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        Map? map = Find.CurrentMap;
        if (map is null)
        {
            tradeStatus = ClashOfRimText.Key("ClashOfRim.Trade.StatusNoMapCreate");
            return;
        }

        if (!settings.IsConfigured || string.IsNullOrWhiteSpace(settings.CurrentSnapshotId))
        {
            tradeStatus = ClashOfRimText.Key("ClashOfRim.Trade.StatusConfigMissingCreate");
            return;
        }

        Find.WindowStack.Add(new TradeOrderDialogWindow(this, map));
    }

    internal async Task<ClashOfRimClientNetworkResult<ModTradeOrderFeeQuoteResponseDto>> QuoteTradeOrderFeeAsync(
        IReadOnlyList<ModThingReferenceDto> offeredThings,
        IReadOnlyList<ModThingReferenceDto> requestedThings)
    {
        using var httpClient = new HttpClient();
        var client = new ClashOfRimModNetworkClient(
            httpClient,
            ClashOfRimClientNetworkContext.FromSettings(settings));
        return await client.QuoteTradeOrderFeeAsync(offeredThings, requestedThings);
    }

    internal void OpenTradeMarketWindow()
    {
        if (!settings.IsConfigured || string.IsNullOrWhiteSpace(settings.CurrentSnapshotId))
        {
            tradeStatus = ClashOfRimText.Key("ClashOfRim.Trade.StatusConfigMissingMarket");
            return;
        }

        Find.WindowStack.Add(new TradeMarketWindow(this));
        StartRefreshTradeOrders();
    }
}
