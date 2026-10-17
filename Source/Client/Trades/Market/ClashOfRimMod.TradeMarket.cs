using AIRsLight.ClashOfRim.Modules.Trades;
using AIRsLight.ClashOfRim.Modules.Synchronization;
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

        LocalMutationKind operation = LocalMutationKind.Trade;
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
        LocalMutationKind operation = LocalMutationKind.Trade;
        if (TryRejectBlockedByLocalAtomicMutation(out string blockedMessage, operation))
        {
            tradeStatus = blockedMessage;
            Messages.Message(blockedMessage, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        BeginLocalAtomicMutation(operation, ClashOfRimText.Key("ClashOfRim.Trade.StatusOrderPackagingSnapshot"));
        if (!snapshotPreparation.TryBuild(
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
        LocalMutationKind operation = LocalMutationKind.Trade;
        if (TryRejectBlockedByLocalAtomicMutation(out string blockedMessage, operation))
        {
            tradeStatus = blockedMessage;
            Messages.Message(blockedMessage, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        if (!TryBeginSnapshotUploadTransaction(out var uploadTransaction))
        {
            tradeStatus = ClashOfRimText.Key("ClashOfRim.SnapshotUpload.Busy");
            Messages.Message(tradeStatus, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        BeginLocalAtomicMutation(operation, ClashOfRimText.Key("ClashOfRim.Trade.StatusOrderSubmittingTransaction"));
        tradeStatus = ClashOfRimText.Key("ClashOfRim.Trade.StatusSubmittingOrder", draft.FeeSilver.Named("FEE"));
        ClientSessionWork.Run(async () =>
        {
            try
            {
                using var httpClient = new HttpClient();
                var client = new ClashOfRimModNetworkClient(
                    httpClient,
                    ClashOfRimClientNetworkContext.FromSettings(settings));
                await ClientSessionWork.Observe(UploadPawnPackagesForTradeAsync(
                    client,
                    draft.OfferedThings.Concat(draft.RequestedThings).ToList(),
                        idempotencyKey));
                ClashOfRimClientNetworkResult<ModEventCreationResponseDto> created =
                    await ClientSessionWork.Observe(client.CreateTradeOrderWithSnapshotAsync(
                        idempotencyKey,
                        draft.OfferedThings,
                        draft.RequestedThings,
                        draft.FeeSilver,
                        draft.AllowSelfPickup,
                        draft.AllowServerDropPod,
                        confirmedSnapshot,
                        confirmedPayload));
                using var publication = ClientSessionWork.EnterPublication();
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
                            snapshotPublication.PublishCurrentGame(
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
            when (ClientSessionWork.IsCurrent)
            {
                using var publication = ClientSessionWork.EnterPublication();
                tradeStatus = ClashOfRimText.Key("ClashOfRim.Trade.StatusOrderSubmitException", ex.GetType().Name.Named("TYPE"), ex.Message.Named("MESSAGE"));
                ShowUnconfirmedSnapshotFailure(
                    operation,
                    tradeStatus,
                    () => StartSubmitReservedManualTradeOrderWithSnapshot(draft, idempotencyKey, reservationMessage, confirmedSnapshot, confirmedPayload));
                Log.Warning("[ClashOfRim] Manual trade order creation failed: " + ex);
            }
            finally
            {
                uploadTransaction.Dispose();
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
        await PawnPackageHydration.HydrateThingPawnPackagesAsync(client, things);
        await PawnPackageHydration.HydrateThingStatePackagesAsync(client, things);
    }

    internal void StartRefreshTradeOrders(string scope = "Open") => _ = tradeMarket.RefreshAsync(scope);

    internal void StartLoadMoreTradeOrders(string scope = "Open") => _ = tradeMarket.RefreshAsync(scope, reset: false);

    internal void StartCancelTradeOrder(ModTradeOrderSummaryDto order) => _ = tradeMarket.CancelAsync(order, acceptedMemo: false);

    internal void StartCancelAcceptedTradeMemo(ModTradeOrderSummaryDto order) => _ = tradeMarket.CancelAsync(order, acceptedMemo: true);

    internal bool IsOwnOpenTradeOrder(ModTradeOrderSummaryDto order) => ClientTradeRules.IsOwnOpen(order, sessionLifecycle.ReadContext());

    internal bool CanActOnOpenTradeOrder(ModTradeOrderSummaryDto order) => ClientTradeRules.CanAct(order, sessionLifecycle.ReadContext());

    internal bool CanCancelAcceptedTradeMemo(ModTradeOrderSummaryDto order) => ClientTradeRules.CanCancelMemo(order, sessionLifecycle.ReadContext());

    internal Action BindTradeAction(Action action) => tradeMarket.Bind(action);

    internal void OpenTradeOrderDialog()
    {
        if (!tradeState.Enabled)
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
