using AIRsLight.ClashOfRim.Modules.Trades;
using AIRsLight.ClashOfRim.Modules.Events;
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
using AIRsLight.ClashOfRim.ThirdPartyCompatibility;
using AIRsLight.ClashOfRim.Trades;
using AIRsLight.ClashOfRim.WorldObjects;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace AIRsLight.ClashOfRim;

public sealed partial class ClashOfRimMod
{
    internal void ConfirmTradeAcceptorWillReceiveGoods(ModTradeOrderSummaryDto order, Action confirmedAction)
    {
        Action guarded = tradeMarket.Bind(confirmedAction);
        if (TradeAcceptorReceivesNoGoods(order))
        {
            Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                ClashOfRimText.Key("ClashOfRim.Trade.ConfirmNoOfferedThings"),
                guarded));
            return;
        }

        guarded();
    }

    private static bool TradeAcceptorReceivesNoGoods(ModTradeOrderSummaryDto order)
    {
        if (order.OfferedThings is null || order.OfferedThings.Count == 0)
        {
            return true;
        }

        return order.OfferedThings.All(thing => thing.StackCount <= 0);
    }

    internal void StartAcceptTradeOrder(ModTradeOrderSummaryDto order, bool postagePaidByAcceptor) => _ = tradeMarket.AcceptAsync(order, postagePaidByAcceptor);

    internal IReadOnlyList<ModTradeOrderSummaryDto> FindFulfillableTradeOrdersForCaravan(Caravan caravan)
    {
        if (!settings.IsConfigured || string.IsNullOrWhiteSpace(settings.CurrentSnapshotId))
        {
            return Array.Empty<ModTradeOrderSummaryDto>();
        }

        int tile = caravan.Tile;
        return tradeState.CopyOrders().Where(order => CanFulfillTradeOrderAtTile(order, tile)).ToList();
    }

    internal IReadOnlyList<ModWorldMapMarkerDto> FindReinforceableTargetsForCaravan(Caravan caravan)
    {
        if (!settings.IsConfigured || string.IsNullOrWhiteSpace(settings.CurrentSnapshotId))
        {
            return Array.Empty<ModWorldMapMarkerDto>();
        }

        int tile = caravan.Tile;
        lock (eventStateLock)
        {
            return lastWorldMapMarkers
                .Where(marker => marker.CanReinforce)
                .Where(marker => marker.Tile == tile)
                .Where(marker => !string.Equals(marker.OwnerUserId, settings.UserId, StringComparison.Ordinal)
                    || !string.Equals(marker.OwnerColonyId, settings.ColonyId, StringComparison.Ordinal))
                .Where(marker => !string.IsNullOrWhiteSpace(marker.OwnerUserId))
                .Where(marker => !string.IsNullOrWhiteSpace(marker.OwnerColonyId))
                .Where(marker => !string.IsNullOrWhiteSpace(marker.SnapshotId))
                .Where(marker => !string.IsNullOrWhiteSpace(marker.MapId))
                .OrderBy(marker => marker.OwnerUserId, StringComparer.Ordinal)
                .ThenBy(marker => marker.OwnerColonyId, StringComparer.Ordinal)
                .ToList();
        }
    }

    internal IReadOnlyList<ModWorldMapMarkerDto> FindRaidableTargetsForCaravan(Caravan caravan)
    {
        if (!pvpEnabled
            || !settings.IsConfigured
            || string.IsNullOrWhiteSpace(settings.CurrentSnapshotId))
        {
            return Array.Empty<ModWorldMapMarkerDto>();
        }

        int tile = caravan.Tile;
        lock (eventStateLock)
        {
            return lastWorldMapMarkers
                .Where(marker => marker.CanRaid)
                .Where(marker => marker.Tile == tile)
                .Where(marker => !string.Equals(marker.OwnerUserId, settings.UserId, StringComparison.Ordinal)
                    || !string.Equals(marker.OwnerColonyId, settings.ColonyId, StringComparison.Ordinal))
                .Where(marker => !string.IsNullOrWhiteSpace(marker.OwnerUserId))
                .Where(marker => !string.IsNullOrWhiteSpace(marker.OwnerColonyId))
                .Where(marker => !string.IsNullOrWhiteSpace(marker.SnapshotId))
                .Where(marker => !string.IsNullOrWhiteSpace(marker.MapId))
                .Where(marker => !string.IsNullOrWhiteSpace(marker.WorldObjectId))
                .OrderBy(marker => marker.OwnerUserId, StringComparer.Ordinal)
                .ThenBy(marker => marker.OwnerColonyId, StringComparer.Ordinal)
                .ToList();
        }
    }

    // UI compatibility entry points until M11; gift modules own policy and execution.
    internal IReadOnlyList<ModWorldMapMarkerDto> FindGiftDeliverableTargetsForCaravan(Caravan caravan)
        => giftActions.FindTargets(caravan, forced: false);
    internal IReadOnlyList<ModWorldMapMarkerDto> FindForcedGiftDeliverableTargetsForCaravan(Caravan caravan)
        => giftActions.FindTargets(caravan, forced: true);
    internal void OpenCaravanGiftDeliveryMenu(Caravan caravan, IReadOnlyList<ModWorldMapMarkerDto> targets, bool forcedDelivery)
        => giftActions.OpenMenu(caravan, targets, forcedDelivery);
    internal void StartCreateGiftFromCaravan(Caravan caravan, ModWorldMapMarkerDto target,
        IReadOnlyList<TradeOfferSelection> selections, bool forcedDelivery, string? message)
        => giftActions.SendCaravan(caravan, ClientGiftActions.Target(target), selections, forcedDelivery, message);
    internal void StartCreateGiftFromTransportPods(string transporterKey, string targetUserId, string targetColonyId,
        string targetSnapshotId, string targetMapId, string targetWorldObjectId, PlanetTile targetTile,
        string targetLabel, IReadOnlyList<ActiveTransporterInfo> transporters, bool forcedDelivery)
        => giftActions.SendTransportPods(transporterKey, targetUserId, targetColonyId, targetSnapshotId, targetMapId,
            targetWorldObjectId, targetTile, targetLabel, transporters, forcedDelivery);
    internal bool CanStartTransportPodGiftArrival() => giftActions.CanStartArrival;

    internal void OpenCaravanTradeFulfillmentMenu(
        Caravan caravan,
        IReadOnlyList<ModTradeOrderSummaryDto> orders)
    {
        if (orders.Count == 0)
        {
            tradeStatus = ClashOfRimText.Key("ClashOfRim.Trade.StatusNoFulfillableCaravanOrders");
            return;
        }

        List<ModThingReferenceDto> deliveredThings = TradeCaravanFulfillmentUtility.BuildDeliveredThingReferences(
            caravan,
            settings.UserId,
            settings.ColonyId,
            settings.CurrentSnapshotId).ToList();
        var options = new List<FloatMenuOption>();
        foreach (ModTradeOrderSummaryDto order in orders)
        {
            string label = BuildTradeFulfillmentOptionLabel(order);
            if (!TradeCaravanFulfillmentUtility.Satisfies(order.RequestedThings, deliveredThings, out IReadOnlyList<string> missing))
            {
                options.Add(new FloatMenuOption(label + ClashOfRimText.Key("ClashOfRim.Trade.CaravanMissingSuffix"), null)
                {
                    Disabled = true,
                    tooltip = string.Join("\n", missing.Take(5))
                });
                continue;
            }

            ModTradeOrderSummaryDto captured = order;
            options.Add(new FloatMenuOption(label, () =>
            {
                string summary = ClashOfRimText.Key("ClashOfRim.Trade.ConfirmCaravanFulfillTitle")
                    + "\n\n"
                    + ClashOfRimText.Key("ClashOfRim.Trade.DeliverLine", TradeUiUtility.FormatThingList(captured.RequestedThings, asRequirement: true).Named("THINGS"))
                    + "\n"
                    + ClashOfRimText.Key("ClashOfRim.Trade.ReceiveLine", TradeUiUtility.FormatThingList(captured.OfferedThings, asRequirement: false).Named("THINGS"));
                Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                    summary,
                    () => ConfirmTradeAcceptorWillReceiveGoods(
                        captured,
                        () => StartFulfillTradeOrderFromCaravan(caravan, captured))));
            }));
        }

        Find.WindowStack.Add(new FloatMenu(options));
    }

    private void StartFulfillTradeOrderFromCaravan(Caravan caravan, ModTradeOrderSummaryDto order)
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

        if (!CanFulfillTradeOrderAtTile(order, caravan.Tile))
        {
            tradeStatus = ClashOfRimText.Key("ClashOfRim.Trade.StatusCaravanNotAtOwnerTile");
            return;
        }

        if (string.IsNullOrWhiteSpace(order.ViewerAcceptedMemoEventId))
        {
            tradeStatus = ClashOfRimText.Key("ClashOfRim.Trade.StatusMissingAcceptMemo");
            return;
        }

        string baseSnapshotId = settings.CurrentSnapshotId;
        string idempotencyKey = $"trade-fulfill:{settings.UserId}:{order.EventId}:{order.ViewerAcceptedMemoEventId}:{caravan.GetUniqueLoadID()}:{DateTime.UtcNow.Ticks}";
        LocalMutationKind operation = LocalMutationKind.Trade;
        if (!TryBeginManualSynchronization(out var manualOperation, out failureReason))
        {
            tradeStatus = failureReason;
            return;
        }
        BeginLocalAtomicMutation(operation, ClashOfRimText.Key("ClashOfRim.Trade.StatusCaravanFulfillPreparing"));
        tradeStatus = ClashOfRimText.Key("ClashOfRim.Trade.StatusSubmittingCaravanFulfill", order.EventId.Named("EVENTID"));
        ClientSessionWork.Run(async () =>
        {
            try
            {
                using var httpClient = new HttpClient();
                var client = new ClashOfRimModNetworkClient(
                    httpClient,
                    ClashOfRimClientNetworkContext.FromSettings(settings));
                await ClientSessionWork.Observe(HydratePawnPackagesForTradeAsync(client, order.OfferedThings));
                using var publication = ClientSessionWork.EnterPublication();
                manualOperation.QueueContinuation(EnqueueClashOfRimMainThreadAction, continuation =>
                {
                    if (!TradeCaravanFulfillmentUtility.ApplyExchangeToCaravan(
                            caravan,
                            order.RequestedThings,
                            order.OfferedThings,
                            settings.UserId,
                            settings.ColonyId,
                            settings.CurrentSnapshotId,
                            out IReadOnlyList<ModThingReferenceDto> deliveredThings,
                            out string applyMessage))
                    {
                        tradeStatus = ClashOfRimText.Key("ClashOfRim.Trade.StatusFulfillLocalApplyFailed", applyMessage.Named("MESSAGE"));
                        PawnFlowFailurePolicy.LogFailure(
                            "trade-self-delivery-local-apply",
                            order.EventId,
                            "TradeFulfillment",
                            "LocalExchangeApplyFailed",
                            applyMessage,
                            willReportToServer: false);
                        ClearLocalAtomicMutation();
                        return;
                    }

                    tradeStatus = ClashOfRimText.Key(
                        "ClashOfRim.Trade.StatusFulfillCompletedUploading",
                        order.EventId.Named("EVENTID"),
                        applyMessage.Named("MESSAGE"));
                    Messages.Message(ClashOfRimText.Key("ClashOfRim.Trade.FulfillCompletedMessage"), MessageTypeDefOf.PositiveEvent, historical: false);
                    StartSubmitAppliedTradeFulfillment(
                        tradeEventId: order.EventId,
                        acceptedMemoEventId: order.ViewerAcceptedMemoEventId!,
                        deliveredThings,
                        idempotencyKey,
                        fulfillmentMode: "SelfDelivery",
                        baseSnapshotId,
                        reservationMessage: applyMessage,
                        continuation);
                });
            }
            catch (Exception ex)
            when (ClientSessionWork.IsCurrent)
            {
                using var publication = ClientSessionWork.EnterPublication();
                tradeStatus = ClashOfRimText.Key("ClashOfRim.Trade.StatusFulfillException", ex.GetType().Name.Named("TYPE"), ex.Message.Named("MESSAGE"));
                ClearLocalAtomicMutation();
                Log.Warning("[ClashOfRim] Trade self-delivery fulfillment failed: " + ex);
            }
            finally
            {
                manualOperation.Dispose();
            }
        });
    }

    internal void StartFulfillTradeOrderByDropPod(ModTradeOrderSummaryDto order)
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
            Messages.Message(tradeStatus, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        Map? map = Find.CurrentMap;
        if (map is null)
        {
            tradeStatus = ClashOfRimText.Key("ClashOfRim.Trade.StatusNoMapReserve");
            Messages.Message(tradeStatus, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        if (string.IsNullOrWhiteSpace(order.EventId))
        {
            tradeStatus = ClashOfRimText.Key("ClashOfRim.Trade.StatusMissingOrderEventId");
            Messages.Message(tradeStatus, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        int postageSilver = order.ServerDropPodPostage?.Reachable == true
            ? order.ServerDropPodPostage.PostageSilver ?? -1
            : -1;
        if (postageSilver < 0)
        {
            tradeStatus = ClashOfRimText.Key("ClashOfRim.Trade.StatusDropPodUnreachable");
            Messages.Message(tradeStatus, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        LocalMutationKind operation = LocalMutationKind.Trade;
        BeginLocalAtomicMutation(operation, ClashOfRimText.Key("ClashOfRim.Trade.StatusDropPodReserving"));
        if (!TradeMapFulfillmentUtility.TryReserveDropPodDelivery(
                map,
                order.RequestedThings,
                postageSilver,
                settings.UserId,
                settings.ColonyId,
                settings.CurrentSnapshotId,
                out IReadOnlyList<ModThingReferenceDto> deliveredThings,
                out string reservationMessage))
        {
            ClearLocalAtomicMutation();
            tradeStatus = reservationMessage;
            Messages.Message(tradeStatus, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        string baseSnapshotId = settings.CurrentSnapshotId;
        string idempotencyKey = $"trade-drop-pod:{settings.UserId}:{order.EventId}:{settings.CurrentSnapshotId}:{DateTime.UtcNow.Ticks}";
        string acceptedMemoEventId = order.ViewerAcceptedMemoEventId ?? string.Empty;
        StartSubmitAppliedTradeFulfillment(
            order.EventId,
            acceptedMemoEventId,
            deliveredThings,
            idempotencyKey,
            fulfillmentMode: "ServerDropPod",
            baseSnapshotId,
            reservationMessage);
    }

    private void StartSubmitAppliedTradeFulfillment(
        string tradeEventId,
        string acceptedMemoEventId,
        IReadOnlyList<ModThingReferenceDto> deliveredThings,
        string idempotencyKey,
        string fulfillmentMode,
        string baseSnapshotId,
        string reservationMessage,
        ClientSynchronizationCoordinator.Lease? continuation = null)
    {
        LocalMutationKind operation = LocalMutationKind.Trade;
        if (TryRejectBlockedByLocalAtomicMutation(out string blockedMessage, operation))
        {
            tradeStatus = blockedMessage;
            Messages.Message(blockedMessage, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        BeginLocalAtomicMutation(operation, ClashOfRimText.Key("ClashOfRim.Trade.StatusFulfillPackagingSnapshot"));
        if (!snapshotPreparation.TryBuild(
                settings.UserId,
                settings.ColonyId,
                out ModSnapshotPackageBuildResult build,
                out string buildFailureReason))
        {
            tradeStatus = ClashOfRimText.Key("ClashOfRim.Trade.StatusFulfillSnapshotBuildFailed", buildFailureReason.Named("REASON"));
            ShowUnconfirmedSnapshotFailure(
                operation,
                tradeStatus,
                () => StartSubmitAppliedTradeFulfillment(
                    tradeEventId,
                    acceptedMemoEventId,
                    deliveredThings,
                    idempotencyKey,
                    fulfillmentMode,
                    baseSnapshotId,
                    reservationMessage));
            return;
        }

        StartSubmitAppliedTradeFulfillmentWithSnapshot(
            tradeEventId,
            acceptedMemoEventId,
            deliveredThings,
            idempotencyKey,
            fulfillmentMode,
            baseSnapshotId,
            reservationMessage,
            build.Package!,
            build.Payload!,
            continuation);
    }

    private void StartSubmitAppliedTradeFulfillmentWithSnapshot(
        string tradeEventId,
        string acceptedMemoEventId,
        IReadOnlyList<ModThingReferenceDto> deliveredThings,
        string idempotencyKey,
        string fulfillmentMode,
        string baseSnapshotId,
        string reservationMessage,
        ModSnapshotPackageMetadataDto confirmedSnapshot,
        byte[] confirmedPayload,
        ClientSynchronizationCoordinator.Lease? continuation = null)
    {
        LocalMutationKind operation = LocalMutationKind.Trade;
        if (TryRejectBlockedByLocalAtomicMutation(out string blockedMessage, operation))
        {
            tradeStatus = blockedMessage;
            Messages.Message(blockedMessage, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        if (!synchronization.TryAcquireSnapshot(out var uploadTransaction, continuation))
        {
            tradeStatus = ClashOfRimText.Key("ClashOfRim.SnapshotUpload.Busy");
            Messages.Message(tradeStatus, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        BeginLocalAtomicMutation(operation, ClashOfRimText.Key("ClashOfRim.Trade.StatusFulfillSubmittingTransaction"));
        tradeStatus = ClashOfRimText.Key(
            "ClashOfRim.Trade.StatusSubmittingDropPodFulfill",
            tradeEventId.Named("EVENTID"),
            reservationMessage.Named("MESSAGE"));
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
                    deliveredThings,
                    $"trade-drop-pod:{settings.UserId}:{tradeEventId}:{DateTime.UtcNow.Ticks}"));
                ClashOfRimClientNetworkResult<ModFulfillTradeOrderResponseDto> result =
                    await ClientSessionWork.Observe(client.FulfillTradeOrderWithSnapshotAsync(
                        idempotencyKey,
                        tradeEventId,
                        acceptedMemoEventId,
                        deliveredThings,
                        fulfillmentMode,
                        confirmedSnapshot,
                        confirmedPayload));

                using var publication = ClientSessionWork.EnterPublication();
                if (!result.Success || result.Response is null)
                {
                    tradeStatus = ClashOfRimText.Key("ClashOfRim.Trade.StatusFulfillFailed", result.ErrorCode.Named("CODE"), result.Message.Named("MESSAGE"));
                    ShowUnconfirmedSnapshotFailure(
                        operation,
                        tradeStatus,
                        () => StartSubmitAppliedTradeFulfillmentWithSnapshot(tradeEventId, acceptedMemoEventId, deliveredThings, idempotencyKey, fulfillmentMode, baseSnapshotId, reservationMessage, confirmedSnapshot, confirmedPayload));
                    return;
                }

                ModFulfillTradeOrderResponseDto response = result.Response;
                if (response.Result is not null && !response.Result.Accepted)
                {
                    string missing = response.MissingRequirements.Count == 0
                        ? string.Empty
                        : ClashOfRimText.Key("ClashOfRim.Trade.MissingSuffix", string.Join("；", response.MissingRequirements.Take(5)).Named("MISSING"));
                    tradeStatus = ClashOfRimText.Key(
                        "ClashOfRim.Trade.StatusFulfillRejected",
                        response.Result.ErrorCode.Named("CODE"),
                        response.Result.Message.Named("MESSAGE"),
                        missing.Named("MISSING"));
                    Log.Warning("[ClashOfRim][TradeDropPod] server rejected after local reservation. Retry the same fulfillment request or abandon the local state.");
                    ShowUnconfirmedSnapshotFailure(
                        operation,
                        tradeStatus,
                        () => StartSubmitAppliedTradeFulfillmentWithSnapshot(tradeEventId, acceptedMemoEventId, deliveredThings, idempotencyKey, fulfillmentMode, baseSnapshotId, reservationMessage, confirmedSnapshot, confirmedPayload));
                    return;
                }

                ClashOfRimGameComponent.EnqueueMainThreadAction(() =>
                {
                    if (!string.IsNullOrWhiteSpace(response.AppliedSnapshotId))
                    {
                        snapshotPublication.PublishCurrentGame(
                            response.AppliedSnapshotId!,
                            response.NextLineageToken);
                    }

                    MarkFulfilledTradeOrderLocally(tradeEventId);
                    tradeStatus = ClashOfRimText.Key(
                        "ClashOfRim.Trade.StatusDropPodFulfillCreated",
                        response.ExchangeEventId.Named("EVENTID"),
                        response.AcceptorDeliveryEventId.Named("ACCEPTOR"),
                        response.OwnerDeliveryEventId.Named("OWNER"));
                    CompleteLocalAtomicMutation();
                    CloseUnconfirmedSnapshotFailureWindow();
                    StartAutomaticEventRefresh(ClashOfRimText.Key("ClashOfRim.Trade.ReasonFulfillment"));
                });
            }
            catch (Exception ex)
            when (ClientSessionWork.IsCurrent)
            {
                using var publication = ClientSessionWork.EnterPublication();
                tradeStatus = ClashOfRimText.Key("ClashOfRim.Trade.StatusFulfillException", ex.GetType().Name.Named("TYPE"), ex.Message.Named("MESSAGE"));
                ShowUnconfirmedSnapshotFailure(
                    operation,
                    tradeStatus,
                    () => StartSubmitAppliedTradeFulfillmentWithSnapshot(tradeEventId, acceptedMemoEventId, deliveredThings, idempotencyKey, fulfillmentMode, baseSnapshotId, reservationMessage, confirmedSnapshot, confirmedPayload));
                Log.Warning("[ClashOfRim] Trade drop-pod fulfillment failed: " + ex);
            }
            finally
            {
                uploadTransaction.Dispose();
            }
        });
    }

    private void StartTradeExchangeConfirmationAsync(string exchangeEventId, string? tradeEventId, string baseSnapshotId)
    {
        _ = eventConfirmations.TradeAsync(exchangeEventId, tradeEventId, baseSnapshotId);
    }

    private void MarkFulfilledTradeOrderLocally(string tradeEventId) => tradeState.Fulfilled(tradeEventId);

    private bool CanFulfillTradeOrderAtTile(ModTradeOrderSummaryDto order, int caravanTile)
    {
        return order.ViewerHasAccepted
            && order.AllowSelfPickup
            && IsOpenTradeStatus(order.Status)
            && !string.IsNullOrWhiteSpace(order.ViewerAcceptedMemoEventId)
            && ResolveTradeOrderTile(order) == caravanTile;
    }

    private int? ResolveTradeOrderTile(ModTradeOrderSummaryDto order)
    {
        if (order.TargetContext?.Tile.HasValue == true)
        {
            return order.TargetContext.Tile.Value;
        }

        if (order.Owner is null || string.IsNullOrWhiteSpace(order.Owner.UserId))
        {
            return null;
        }

        lock (eventStateLock)
        {
            return lastWorldMapMarkers
                .Where(marker => marker.CanTrade)
                .Where(marker => string.Equals(marker.OwnerUserId, order.Owner.UserId, StringComparison.Ordinal))
                .Where(marker => string.IsNullOrWhiteSpace(order.Owner.ColonyId)
                    || string.Equals(marker.OwnerColonyId, order.Owner.ColonyId, StringComparison.Ordinal))
                .OrderBy(marker => marker.Tile)
                .Select(marker => (int?)marker.Tile)
                .FirstOrDefault();
        }
    }

    private static bool IsOpenTradeStatus(string? status) => string.IsNullOrWhiteSpace(status) || ClientTradeRules.IsOpen(status);

    private static string BuildTradeFulfillmentOptionLabel(ModTradeOrderSummaryDto order)
    {
        string owner = order.Owner is null
            ? ClashOfRimText.Key("ClashOfRim.Trade.UnknownOwner")
            : $"{order.Owner.UserId}/{order.Owner.ColonyId}";
        return ClashOfRimText.Key(
            "ClashOfRim.Trade.CaravanFulfillOption",
            owner.Named("OWNER"),
            TradeUiUtility.FormatThingList(order.RequestedThings, asRequirement: true).Named("DELIVER"),
            TradeUiUtility.FormatThingList(order.OfferedThings, asRequirement: false).Named("RECEIVE"));
    }

    private static string FormatSupportTargetLabel(ModWorldMapMarkerDto marker)
        => ClientWorldTargetPresentation.Label(marker);

    internal string FormatWorldMapTargetLabel(ModWorldMapMarkerDto marker)
    {
        return FormatSupportTargetLabel(marker);
    }

}
