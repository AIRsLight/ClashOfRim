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
using AIRsLight.ClashOfRim.WorldObjects;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace AIRsLight.ClashOfRim;

public sealed partial class ClashOfRimMod
{
    internal void ConfirmTradeAcceptorWillReceiveGoods(ModTradeOrderSummaryDto order, Action confirmedAction)
    {
        if (TradeAcceptorReceivesNoGoods(order))
        {
            Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                ClashOfRimText.Key("ClashOfRim.Trade.ConfirmNoOfferedThings"),
                confirmedAction));
            return;
        }

        confirmedAction();
    }

    private static bool TradeAcceptorReceivesNoGoods(ModTradeOrderSummaryDto order)
    {
        if (order.OfferedThings is null || order.OfferedThings.Count == 0)
        {
            return true;
        }

        return order.OfferedThings.All(thing => thing.StackCount <= 0);
    }

    internal void StartAcceptTradeOrder(ModTradeOrderSummaryDto order, bool postagePaidByAcceptor)
    {
        if (!CanRunManualSync(out string failureReason))
        {
            tradeStatus = failureReason;
            return;
        }

        if (string.IsNullOrWhiteSpace(order.EventId))
        {
            tradeStatus = ClashOfRimText.Key("ClashOfRim.Trade.StatusMissingOrderEventId");
            return;
        }

        manualSyncInProgress = true;
        tradeStatus = ClashOfRimText.Key("ClashOfRim.Trade.StatusAcceptingOrder", order.EventId.Named("EVENTID"));
        Task.Run(async () =>
        {
            try
            {
                using var httpClient = new HttpClient();
                var client = new ClashOfRimModNetworkClient(
                    httpClient,
                    ClashOfRimClientNetworkContext.FromSettings(settings));
                ClashOfRimClientNetworkResult<ModAcceptTradeOrderResponseDto> result =
                    await client.AcceptTradeOrderAsync(
                        $"trade-accept:{settings.UserId}:{order.EventId}:{DateTime.UtcNow.Ticks}",
                        order.EventId,
                        postagePaidByAcceptor);
                if (!result.Success || result.Response is null)
                {
                    tradeStatus = ClashOfRimText.Key("ClashOfRim.Trade.StatusAcceptFailed", result.ErrorCode.Named("CODE"), result.Message.Named("MESSAGE"));
                    return;
                }

                if (result.Response.Result is not null && !result.Response.Result.Accepted)
                {
                    tradeStatus = ClashOfRimText.Key("ClashOfRim.Trade.StatusAcceptRejected", result.Response.Result.ErrorCode.Named("CODE"), result.Response.Result.Message.Named("MESSAGE"));
                    return;
                }

                tradeStatus = result.Response.MemoCreated
                    ? ClashOfRimText.Key("ClashOfRim.Trade.StatusAcceptedOrder", result.Response.TradeEventId.Named("EVENTID"), result.Response.MemoEventId.Named("MEMOID"))
                    : ClashOfRimText.Key("ClashOfRim.Trade.StatusAcceptMemoExists", result.Response.MemoEventId.Named("MEMOID"));
                MarkTradeOrderAcceptedLocally(order.EventId, result.Response.MemoEventId);
                await RefreshEventsAfterTradeAccept(client);
            }
            catch (Exception ex)
            {
                tradeStatus = ClashOfRimText.Key("ClashOfRim.Trade.StatusAcceptException", ex.GetType().Name.Named("TYPE"), ex.Message.Named("MESSAGE"));
                Log.Warning("[ClashOfRim] Trade accept failed: " + ex);
            }
            finally
            {
                manualSyncInProgress = false;
            }
        });
    }

    private void MarkTradeOrderAcceptedLocally(string tradeEventId, string? memoEventId)
    {
        if (string.IsNullOrWhiteSpace(tradeEventId))
        {
            return;
        }

        lock (eventStateLock)
        {
            ModTradeOrderSummaryDto? cached = lastTradeOrders.FirstOrDefault(order =>
                string.Equals(order.EventId, tradeEventId, StringComparison.Ordinal));
            if (cached is null)
            {
                return;
            }

            cached.ViewerHasAccepted = true;
            if (!string.IsNullOrWhiteSpace(memoEventId))
            {
                cached.ViewerAcceptedMemoEventId = memoEventId;
            }
        }
    }

    internal IReadOnlyList<ModTradeOrderSummaryDto> FindFulfillableTradeOrdersForCaravan(Caravan caravan)
    {
        if (!settings.IsConfigured || string.IsNullOrWhiteSpace(settings.CurrentSnapshotId))
        {
            return Array.Empty<ModTradeOrderSummaryDto>();
        }

        int tile = caravan.Tile;
        lock (eventStateLock)
        {
            return lastTradeOrders
                .Where(order => CanFulfillTradeOrderAtTile(order, tile))
                .ToList();
        }
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

    internal IReadOnlyList<ModWorldMapMarkerDto> FindGiftDeliverableTargetsForCaravan(Caravan caravan)
    {
        if (!giftsEnabled
            || !settings.IsConfigured
            || string.IsNullOrWhiteSpace(settings.CurrentSnapshotId))
        {
            return Array.Empty<ModWorldMapMarkerDto>();
        }

        int tile = caravan.Tile;
        lock (eventStateLock)
        {
            return lastWorldMapMarkers
                .Where(marker => IsValidGiftDeliveryTarget(marker, requireWorldObjectId: false))
                .Where(marker => marker.Tile == tile)
                .OrderBy(marker => marker.OwnerUserId, StringComparer.Ordinal)
                .ThenBy(marker => marker.OwnerColonyId, StringComparer.Ordinal)
                .ToList();
        }
    }

    internal IReadOnlyList<ModWorldMapMarkerDto> FindForcedGiftDeliverableTargetsForCaravan(Caravan caravan)
    {
        if (!giftsEnabled
            || !pvpEnabled
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
                .Where(marker => IsValidGiftDeliveryTarget(marker, requireWorldObjectId: false))
                .Where(marker => marker.Tile == tile)
                .OrderBy(marker => marker.OwnerUserId, StringComparer.Ordinal)
                .ThenBy(marker => marker.OwnerColonyId, StringComparer.Ordinal)
                .ToList();
        }
    }

    private bool IsValidGiftDeliveryTarget(ModWorldMapMarkerDto marker, bool requireWorldObjectId)
    {
        if (!string.Equals(marker.Kind, "TradeableColony", StringComparison.Ordinal))
        {
            return false;
        }

        if (string.Equals(marker.OwnerUserId, settings.UserId, StringComparison.Ordinal)
            && string.Equals(marker.OwnerColonyId, settings.ColonyId, StringComparison.Ordinal))
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(marker.OwnerUserId)
            && !string.IsNullOrWhiteSpace(marker.OwnerColonyId)
            && !string.IsNullOrWhiteSpace(marker.SnapshotId)
            && !string.IsNullOrWhiteSpace(marker.MapId)
            && (!requireWorldObjectId || !string.IsNullOrWhiteSpace(marker.WorldObjectId));
    }

    internal void OpenCaravanGiftDeliveryMenu(
        Caravan caravan,
        IReadOnlyList<ModWorldMapMarkerDto> targets,
        bool forcedDelivery)
    {
        if (targets.Count == 0)
        {
            giftProcessingStatus = ClashOfRimText.Key("ClashOfRim.GiftDelivery.StatusNoTargets");
            return;
        }

        var options = new List<FloatMenuOption>();
        foreach (ModWorldMapMarkerDto target in targets)
        {
            ModWorldMapMarkerDto captured = target;
            options.Add(new FloatMenuOption(
                ClashOfRimText.Key("ClashOfRim.GiftDelivery.TargetOption", FormatWorldMapTargetLabel(captured).Named("TARGET")),
                () => Find.WindowStack.Add(new GiftDeliveryDialogWindow(
                    this,
                    caravan,
                    captured,
                    normalAvailable: !forcedDelivery,
                    forcedAvailable: forcedDelivery,
                    startForcedDelivery: forcedDelivery))));
        }

        Find.WindowStack.Add(new FloatMenu(options));
    }

    internal void StartCreateGiftFromCaravan(
        Caravan caravan,
        ModWorldMapMarkerDto target,
        IReadOnlyList<TradeOfferSelection> selections,
        bool forcedDelivery,
        string? message)
    {
        if (!giftsEnabled)
        {
            giftProcessingStatus = ClashOfRimText.Key("ClashOfRim.GiftDelivery.Disabled");
            Messages.Message(giftProcessingStatus, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        if (forcedDelivery && !pvpEnabled)
        {
            giftProcessingStatus = ClashOfRimText.Key("ClashOfRim.Raid.PvpDisabled");
            Messages.Message(giftProcessingStatus, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        if (TryRejectBlockedByLocalAtomicMutation(out string atomicMessage))
        {
            giftProcessingStatus = atomicMessage;
            Messages.Message(atomicMessage, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        if (!CanRunManualSync(out string failureReason))
        {
            giftProcessingStatus = failureReason;
            return;
        }

        if (target.Tile != caravan.Tile)
        {
            giftProcessingStatus = ClashOfRimText.Key("ClashOfRim.GiftDelivery.StatusTargetTileMismatch");
            return;
        }

        if (string.IsNullOrWhiteSpace(target.OwnerUserId)
            || string.IsNullOrWhiteSpace(target.OwnerColonyId)
            || string.IsNullOrWhiteSpace(target.MapId))
        {
            giftProcessingStatus = ClashOfRimText.Key("ClashOfRim.GiftDelivery.StatusTargetIncomplete");
            return;
        }

        if (selections.Count == 0)
        {
            giftProcessingStatus = ClashOfRimText.Key("ClashOfRim.GiftDelivery.StatusNoSelectedItems");
            return;
        }

        IReadOnlyList<ModThingReferenceDto> things = selections
            .Select(selection => TradeCaravanFulfillmentUtility.BuildThingReference(
                selection.Thing,
                caravan,
                settings.UserId,
                settings.ColonyId,
                settings.CurrentSnapshotId,
                selection.Count))
            .ToList();
        BeginLocalAtomicMutation(
            ClashOfRimText.Key("ClashOfRim.GiftDelivery.OperationSend"),
            ClashOfRimText.Key("ClashOfRim.GiftDelivery.StatusReserving"));
        if (!TradeCaravanFulfillmentUtility.RemoveSelectedThings(caravan, selections, out string removeMessage))
        {
            ClearLocalAtomicMutation();
            giftProcessingStatus = ClashOfRimText.Key("ClashOfRim.GiftDelivery.StatusLocalRemoveFailed", removeMessage.Named("MESSAGE"));
            return;
        }

        var targetContext = new ModEventTargetContextDto
        {
            WorldObjectId = target.WorldObjectId,
            MapUniqueId = target.MapId,
            Tile = target.Tile,
            LandingMode = "MapEdge"
        };
        string idempotencyKey = $"gift-caravan:{settings.UserId}:{settings.CurrentSnapshotId}:{caravan.GetUniqueLoadID()}:{target.OwnerUserId}:{target.OwnerColonyId}:{DateTime.UtcNow.Ticks}";
        StartSubmitRemovedCaravanGift(target, things, targetContext, forcedDelivery, message, idempotencyKey, removeMessage);
    }

    private void StartSubmitRemovedCaravanGift(
        ModWorldMapMarkerDto target,
        IReadOnlyList<ModThingReferenceDto> things,
        ModEventTargetContextDto targetContext,
        bool forcedDelivery,
        string? message,
        string idempotencyKey,
        string removeMessage)
    {
        string operation = ClashOfRimText.Key("ClashOfRim.GiftDelivery.OperationSend");
        if (TryRejectBlockedByDifferentLocalAtomicMutation(operation, out string blockedMessage))
        {
            giftProcessingStatus = blockedMessage;
            Messages.Message(blockedMessage, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        BeginLocalAtomicMutation(operation, ClashOfRimText.Key("ClashOfRim.GiftDelivery.StatusPackagingSnapshot"));
        if (!TryBuildCurrentGameSnapshotPackage(
                settings.UserId,
                settings.ColonyId,
                out ModSnapshotPackageBuildResult build,
                out string buildFailureReason))
        {
            giftProcessingStatus = ClashOfRimText.Key("ClashOfRim.GiftDelivery.StatusSnapshotBuildFailed", buildFailureReason.Named("REASON"));
            ShowUnconfirmedSnapshotFailure(
                operation,
                giftProcessingStatus,
                () => StartSubmitRemovedCaravanGift(target, things, targetContext, forcedDelivery, message, idempotencyKey, removeMessage));
            return;
        }

        StartSubmitRemovedCaravanGiftWithSnapshot(
            target,
            things,
            targetContext,
            forcedDelivery,
            message,
            idempotencyKey,
            removeMessage,
            build.Package!,
            build.Payload!);
    }

    private void StartSubmitRemovedCaravanGiftWithSnapshot(
        ModWorldMapMarkerDto target,
        IReadOnlyList<ModThingReferenceDto> things,
        ModEventTargetContextDto targetContext,
        bool forcedDelivery,
        string? message,
        string idempotencyKey,
        string removeMessage,
        ModSnapshotPackageMetadataDto confirmedSnapshot,
        byte[] confirmedPayload)
    {
        string operation = ClashOfRimText.Key("ClashOfRim.GiftDelivery.OperationSend");
        if (TryRejectBlockedByDifferentLocalAtomicMutation(operation, out string blockedMessage))
        {
            giftProcessingStatus = blockedMessage;
            Messages.Message(blockedMessage, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        if (!TryBeginSnapshotUploadTransaction())
        {
            giftProcessingStatus = ClashOfRimText.Key("ClashOfRim.SnapshotUpload.Busy");
            Messages.Message(giftProcessingStatus, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        BeginLocalAtomicMutation(operation, ClashOfRimText.Key("ClashOfRim.GiftDelivery.StatusSubmittingTransaction"));
        giftProcessingStatus = ClashOfRimText.Key(
            forcedDelivery ? "ClashOfRim.GiftDelivery.StatusSubmittingForced" : "ClashOfRim.GiftDelivery.StatusSubmittingNormal",
            FormatWorldMapTargetLabel(target).Named("TARGET"));
        Task.Run(async () =>
        {
            try
            {
                using var httpClient = new HttpClient();
                var client = new ClashOfRimModNetworkClient(
                    httpClient,
                    ClashOfRimClientNetworkContext.FromSettings(settings));
                ClashOfRimClientNetworkResult<ModEventCreationResponseDto> result = await client.CreateGiftWithSnapshotAsync(
                    idempotencyKey,
                    target.OwnerUserId,
                    target.OwnerColonyId ?? string.Empty,
                    null,
                    things,
                    string.IsNullOrWhiteSpace(message) ? null : message,
                    targetContext,
                    forcedDelivery ? "Forced" : null,
                    confirmedSnapshot,
                    confirmedPayload);

                if (!result.Success || result.Response is null)
                {
                    giftProcessingStatus = ClashOfRimText.Key("ClashOfRim.GiftDelivery.StatusCreateFailed", result.ErrorCode.Named("CODE"), result.Message.Named("MESSAGE"));
                    ShowUnconfirmedSnapshotFailure(
                        operation,
                        giftProcessingStatus,
                        () => StartSubmitRemovedCaravanGiftWithSnapshot(target, things, targetContext, forcedDelivery, message, idempotencyKey, removeMessage, confirmedSnapshot, confirmedPayload));
                    return;
                }

                ModProtocolResponseDto? response = result.Response.Result;
                if (response is not null && !response.Accepted)
                {
                    giftProcessingStatus = ClashOfRimText.Key("ClashOfRim.GiftDelivery.StatusCreateRejected", response.ErrorCode.Named("CODE"), response.Message.Named("MESSAGE"));
                    ShowUnconfirmedSnapshotFailure(
                        operation,
                        giftProcessingStatus,
                        () => StartSubmitRemovedCaravanGiftWithSnapshot(target, things, targetContext, forcedDelivery, message, idempotencyKey, removeMessage, confirmedSnapshot, confirmedPayload));
                    return;
                }

                ClashOfRimGameComponent.EnqueueMainThreadAction(() =>
                {
                    if (!string.IsNullOrWhiteSpace(result.Response.AppliedSnapshotId))
                    {
                        PersistAcceptedSnapshotLineage(
                            result.Response.AppliedSnapshotId!,
                            result.Response.NextLineageToken);
                    }

                    giftProcessingStatus = ClashOfRimText.Key(
                        forcedDelivery ? "ClashOfRim.GiftDelivery.StatusForcedCreated" : "ClashOfRim.GiftDelivery.StatusNormalCreated",
                        result.Response.EventId.Named("EVENTID"),
                        removeMessage.Named("MESSAGE"));
                    Messages.Message(giftProcessingStatus, forcedDelivery ? MessageTypeDefOf.NegativeEvent : MessageTypeDefOf.PositiveEvent, historical: false);
                    CompleteLocalAtomicMutation();
                    CloseUnconfirmedSnapshotFailureWindow();
                });
            }
            catch (Exception ex)
            {
                giftProcessingStatus = ClashOfRimText.Key("ClashOfRim.GiftDelivery.StatusCreateException", ex.GetType().Name.Named("TYPE"), ex.Message.Named("MESSAGE"));
                ShowUnconfirmedSnapshotFailure(
                    operation,
                    giftProcessingStatus,
                    () => StartSubmitRemovedCaravanGiftWithSnapshot(target, things, targetContext, forcedDelivery, message, idempotencyKey, removeMessage, confirmedSnapshot, confirmedPayload));
                Log.Warning("[ClashOfRim] Caravan gift delivery failed: " + ex);
            }
            finally
            {
                EndSnapshotUploadTransaction();
            }
        });
    }

    internal void StartCreateGiftFromTransportPods(
        string transporterKey,
        string targetUserId,
        string targetColonyId,
        string targetSnapshotId,
        string targetMapId,
        string targetWorldObjectId,
        PlanetTile targetTile,
        string targetLabel,
        IReadOnlyList<ModThingReferenceDto> things,
        bool forcedDelivery)
    {
        if (!giftsEnabled)
        {
            giftProcessingStatus = ClashOfRimText.Key("ClashOfRim.GiftDelivery.Disabled");
            Messages.Message(giftProcessingStatus, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        if (forcedDelivery && !pvpEnabled)
        {
            giftProcessingStatus = ClashOfRimText.Key("ClashOfRim.Raid.PvpDisabled");
            Messages.Message(giftProcessingStatus, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        if (!CanRunManualSync(out string failureReason))
        {
            giftProcessingStatus = failureReason;
            return;
        }

        if (string.IsNullOrWhiteSpace(targetUserId)
            || string.IsNullOrWhiteSpace(targetColonyId)
            || string.IsNullOrWhiteSpace(targetMapId))
        {
            giftProcessingStatus = ClashOfRimText.Key("ClashOfRim.GiftDelivery.StatusTargetIncomplete");
            return;
        }

        if (things.Count == 0)
        {
            giftProcessingStatus = ClashOfRimText.Key("ClashOfRim.GiftDelivery.StatusNoSelectedItems");
            return;
        }

        var targetContext = new ModEventTargetContextDto
        {
            WorldObjectId = targetWorldObjectId,
            MapUniqueId = targetMapId,
            Tile = targetTile,
            LandingMode = "DropPod"
        };
        string idempotencyKey = $"gift-transport-pods:{settings.UserId}:{settings.CurrentSnapshotId}:{transporterKey}";
        manualSyncInProgress = true;
        giftProcessingStatus = ClashOfRimText.Key(
            forcedDelivery ? "ClashOfRim.GiftDelivery.StatusSubmittingForced" : "ClashOfRim.GiftDelivery.StatusSubmittingNormal",
            (string.IsNullOrWhiteSpace(targetLabel) ? targetUserId : targetLabel).Named("TARGET"));
        Task.Run(async () =>
        {
            try
            {
                using var httpClient = new HttpClient();
                var client = new ClashOfRimModNetworkClient(
                    httpClient,
                    ClashOfRimClientNetworkContext.FromSettings(settings));
                ClashOfRimClientNetworkResult<ModEventCreationResponseDto> result = await client.CreateGiftAsync(
                    idempotencyKey,
                    targetUserId,
                    targetColonyId,
                    null,
                    things,
                    null,
                    targetContext,
                    forcedDelivery ? "Forced" : null);

                if (!result.Success || result.Response is null)
                {
                    giftProcessingStatus = ClashOfRimText.Key("ClashOfRim.GiftDelivery.StatusCreateFailed", result.ErrorCode.Named("CODE"), result.Message.Named("MESSAGE"));
                    return;
                }

                ModProtocolResponseDto? response = result.Response.Result;
                if (response is not null && !response.Accepted)
                {
                    giftProcessingStatus = ClashOfRimText.Key("ClashOfRim.GiftDelivery.StatusCreateRejected", response.ErrorCode.Named("CODE"), response.Message.Named("MESSAGE"));
                    return;
                }

                giftProcessingStatus = ClashOfRimText.Key(
                    forcedDelivery ? "ClashOfRim.GiftDelivery.StatusForcedDropPodCreated" : "ClashOfRim.GiftDelivery.StatusDropPodCreated",
                    (result.Response.EventId ?? string.Empty).Named("EVENTID"));
                StartAutomaticEventRefresh(ClashOfRimText.Key("ClashOfRim.GiftDelivery.ReasonTransportPodGift"));
            }
            catch (Exception ex)
            {
                giftProcessingStatus = ClashOfRimText.Key("ClashOfRim.GiftDelivery.StatusCreateException", ex.GetType().Name.Named("TYPE"), ex.Message.Named("MESSAGE"));
                Log.Warning("[ClashOfRim] Gift transport-pod delivery failed: " + ex);
            }
            finally
            {
                manualSyncInProgress = false;
            }
        });
    }

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
        string operation = ClashOfRimText.Key("ClashOfRim.SnapshotConfirmationFailure.OperationTrade");
        BeginLocalAtomicMutation(operation, ClashOfRimText.Key("ClashOfRim.Trade.StatusCaravanFulfillPreparing"));
        manualSyncInProgress = true;
        tradeStatus = ClashOfRimText.Key("ClashOfRim.Trade.StatusSubmittingCaravanFulfill", order.EventId.Named("EVENTID"));
        Task.Run(async () =>
        {
            try
            {
                using var httpClient = new HttpClient();
                var client = new ClashOfRimModNetworkClient(
                    httpClient,
                    ClashOfRimClientNetworkContext.FromSettings(settings));
                await HydratePawnPackagesForTradeAsync(client, order.OfferedThings);
                ClashOfRimGameComponent.EnqueueMainThreadAction(() =>
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
                        reservationMessage: applyMessage);
                });
            }
            catch (Exception ex)
            {
                tradeStatus = ClashOfRimText.Key("ClashOfRim.Trade.StatusFulfillException", ex.GetType().Name.Named("TYPE"), ex.Message.Named("MESSAGE"));
                ClearLocalAtomicMutation();
                Log.Warning("[ClashOfRim] Trade self-delivery fulfillment failed: " + ex);
            }
            finally
            {
                manualSyncInProgress = false;
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

        string operation = ClashOfRimText.Key("ClashOfRim.SnapshotConfirmationFailure.OperationTrade");
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
        string reservationMessage)
    {
        string operation = ClashOfRimText.Key("ClashOfRim.SnapshotConfirmationFailure.OperationTrade");
        if (TryRejectBlockedByDifferentLocalAtomicMutation(operation, out string blockedMessage))
        {
            tradeStatus = blockedMessage;
            Messages.Message(blockedMessage, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        BeginLocalAtomicMutation(operation, ClashOfRimText.Key("ClashOfRim.Trade.StatusFulfillPackagingSnapshot"));
        if (!TryBuildCurrentGameSnapshotPackage(
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
            build.Payload!);
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

        BeginLocalAtomicMutation(operation, ClashOfRimText.Key("ClashOfRim.Trade.StatusFulfillSubmittingTransaction"));
        tradeStatus = ClashOfRimText.Key(
            "ClashOfRim.Trade.StatusSubmittingDropPodFulfill",
            tradeEventId.Named("EVENTID"),
            reservationMessage.Named("MESSAGE"));
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
                    deliveredThings,
                    $"trade-drop-pod:{settings.UserId}:{tradeEventId}:{DateTime.UtcNow.Ticks}");
                ClashOfRimClientNetworkResult<ModFulfillTradeOrderResponseDto> result =
                    await client.FulfillTradeOrderWithSnapshotAsync(
                        idempotencyKey,
                        tradeEventId,
                        acceptedMemoEventId,
                        deliveredThings,
                        fulfillmentMode,
                        confirmedSnapshot,
                        confirmedPayload);

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
                        PersistAcceptedSnapshotLineage(
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
            {
                tradeStatus = ClashOfRimText.Key("ClashOfRim.Trade.StatusFulfillException", ex.GetType().Name.Named("TYPE"), ex.Message.Named("MESSAGE"));
                ShowUnconfirmedSnapshotFailure(
                    operation,
                    tradeStatus,
                    () => StartSubmitAppliedTradeFulfillmentWithSnapshot(tradeEventId, acceptedMemoEventId, deliveredThings, idempotencyKey, fulfillmentMode, baseSnapshotId, reservationMessage, confirmedSnapshot, confirmedPayload));
                Log.Warning("[ClashOfRim] Trade drop-pod fulfillment failed: " + ex);
            }
            finally
            {
                EndSnapshotUploadTransaction();
            }
        });
    }

    private void StartTradeExchangeConfirmationAsync(string exchangeEventId, string? tradeEventId, string baseSnapshotId)
    {
        StartConfirmEventApplicationSnapshot(new EventApplicationSnapshotConfirmationRequest
        {
            EventId = exchangeEventId,
            SourceEventId = tradeEventId,
            BaseSnapshotId = baseSnapshotId,
            ClientApplicationResult = "SelfDeliveryExchangeApplied",
            Operation = ClashOfRimText.Key("ClashOfRim.SnapshotConfirmationFailure.OperationTrade"),
            IdempotencyPrefix = "trade-confirm",
            UploadingStatus = ClashOfRimText.Key(
                "ClashOfRim.Trade.StatusConfirmUploading",
                exchangeEventId.Named("EVENTID")),
            EmptyEventMessage = ClashOfRimText.Key(
                "ClashOfRim.Trade.StatusConfirmFailed",
                "MissingEventId".Named("CODE"),
                string.Empty.Named("MESSAGE")),
            RetryAction = () => StartTradeExchangeConfirmationAsync(exchangeEventId, tradeEventId, baseSnapshotId),
            SetStatus = value => tradeStatus = value,
            BuildSnapshotBuildFailedStatus = buildFailureReason => ClashOfRimText.Key(
                "ClashOfRim.Trade.StatusConfirmBuildFailed",
                "SaveToMemoryFailed".Named("CODE"),
                buildFailureReason.Named("MESSAGE")),
            BuildRequestFailedStatus = result => ClashOfRimText.Key(
                "ClashOfRim.Trade.StatusConfirmFailed",
                result.ErrorCode.Named("CODE"),
                result.Message.Named("MESSAGE")),
            BuildRejectedStatus = (serverResult, response) => ClashOfRimText.Key(
                "ClashOfRim.Trade.StatusConfirmRejected",
                serverResult.ErrorCode.Named("CODE"),
                serverResult.Message.Named("MESSAGE"),
                response.ServerValidationResult.Named("VALIDATION")),
            BuildExceptionStatus = ex => ClashOfRimText.Key(
                "ClashOfRim.Trade.StatusConfirmException",
                ex.GetType().Name.Named("TYPE"),
                ex.Message.Named("MESSAGE")),
            OnAcceptedOnMainThread = response =>
            {
                tradeStatus = ClashOfRimText.Key(
                    "ClashOfRim.Trade.StatusConfirmSucceeded",
                    exchangeEventId.Named("EVENTID"),
                    response.AppliedSnapshotId.Named("SNAPSHOT"));
            }
        });
    }

    private void MarkFulfilledTradeOrderLocally(string tradeEventId)
    {
        lock (eventStateLock)
        {
            ModTradeOrderSummaryDto? cached = lastTradeOrders.FirstOrDefault(order =>
                string.Equals(order.EventId, tradeEventId, StringComparison.Ordinal));
            if (cached is not null)
            {
                cached.Status = "AppliedToSnapshot";
            }
        }
    }

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

    private static bool IsOpenTradeStatus(string? status)
    {
        return string.IsNullOrWhiteSpace(status)
            || string.Equals(status, "PendingOfflineDelivery", StringComparison.Ordinal)
            || string.Equals(status, "ReadyForImmediateDelivery", StringComparison.Ordinal)
            || string.Equals(status, "Recorded", StringComparison.Ordinal);
    }

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
    {
        string label = string.IsNullOrWhiteSpace(marker.Label)
            ? $"{marker.OwnerUserId}/{marker.OwnerColonyId}"
            : marker.Label!;
        return ClashOfRimText.Key(
            "ClashOfRim.Support.TargetLabel",
            label.Named("LABEL"),
            marker.OwnerUserId.Named("USER"),
            (marker.OwnerColonyId ?? "").Named("COLONY"),
            marker.Tile.Named("TILE"));
    }

    internal string FormatWorldMapTargetLabel(ModWorldMapMarkerDto marker)
    {
        return FormatSupportTargetLabel(marker);
    }

    private async Task RefreshEventsAfterTradeAccept(ClashOfRimModNetworkClient client)
    {
        ClashOfRimClientNetworkResult<ModPullPendingEventsResponseDto> queue = await client.PullPendingEventsAsync();
        if (!queue.Success || queue.Response is null || queue.Response.Result?.Accepted == false)
        {
            return;
        }

        eventQueueStatus = FormatEventQueue(queue.Response.EventQueue);
        CaptureEventIds(queue.Response.EventQueue);
    }

}
