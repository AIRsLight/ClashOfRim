using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using AIRsLight.ClashOfRim.Network.Modules.Support;
using AIRsLight.ClashOfRim.Network.Modules.Trades;

namespace AIRsLight.ClashOfRim.Network.Modules.Events;

// Evaluates against the supplied ledger, including the private staging ledger used by confirmation plans.
internal static class EventApplicationConfirmation
{
    internal static ConfirmEventApplicationResultDto Evaluate(
        ConfirmEventApplicationEntry application,
        string userId,
        string colonyId,
        string baseSnapshotId,
        LatestSnapshotRecord acceptedSnapshot,
        IAuthoritativeEventLedger ledger,
        DateTimeOffset nowUtc, Action<AuthoritativeEvent>? recordTrade = null)
    {
        AuthoritativeEvent? ledgerEvent = ledger.Find(application.EventId);
        if (ledgerEvent is not null && !EventVisibility.MatchesParty(ledgerEvent.Type == ServerEventType.Trade ? ledgerEvent.Actor : ledgerEvent.Target, userId, colonyId))
            return new ConfirmEventApplicationResultDto(application.EventId,
                ProtocolResponse.Reject(ProtocolErrorCode.EventNotFound, ServerText.Format("Events.NotFoundOrNotVisible")), null, "TargetIdentityMismatch");
        if (ledgerEvent?.Status is ServerEventStatus.Cancelled or ServerEventStatus.Failed or ServerEventStatus.RejectedByTarget)
            return new ConfirmEventApplicationResultDto(application.EventId,
                ProtocolResponse.Reject(ProtocolErrorCode.Conflict, ServerText.Format("Events.ConfirmationAlreadyEnded")), null, ledgerEvent.Status.ToString());
        if (ledgerEvent is not null
            && ledgerEvent.Type == ServerEventType.ItemDelivery
            && ledgerEvent.Payload is ItemDeliveryEventPayload)
        {
            var giftConsumer = new ItemDeliveryApplicationConfirmationConsumer(ledger);
            ItemDeliveryApplicationConfirmationResult giftResult = giftConsumer.Consume(
                new ItemDeliveryApplicationConfirmationRequest(
                    application.EventId,
                    userId,
                    colonyId,
                    baseSnapshotId,
                    acceptedSnapshot,
                    application.ClientApplicationResult),
                nowUtc);

            return new ConfirmEventApplicationResultDto(
                application.EventId,
                ToProtocolResponse(giftResult),
                giftResult.AppliedSnapshotId,
                giftResult.Kind.ToString());
        }

        if (ledgerEvent is not null
            && ledgerEvent.Type == ServerEventType.Trade
            && ledgerEvent.Payload is TradeEventPayload { Stage: TradeStage.SelfDeliveryExchange or TradeStage.ServerDropPodExchange } tradePayload)
        {
            return TradeApplicationConfirmation.Confirm(
                application,
                userId,
                colonyId,
                baseSnapshotId,
                acceptedSnapshot,
                ledgerEvent,
                tradePayload,
                ledger,
                nowUtc, recordTrade);
        }

        if (ledgerEvent is not null
            && ledgerEvent.Type == ServerEventType.SupportPawn
            && ledgerEvent.Payload is SupportPawnEventPayload supportPayload)
        {
            return SupportApplicationConfirmation.Confirm(
                application,
                userId,
                colonyId,
                baseSnapshotId,
                acceptedSnapshot,
                ledgerEvent,
                supportPayload,
                ledger,
                nowUtc);
        }

        if (string.IsNullOrWhiteSpace(application.SourceEventId))
        {
            return new ConfirmEventApplicationResultDto(
                application.EventId,
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, ServerText.Format("Events.MissingSourceEvent")),
                appliedSnapshotId: null,
                "MissingSourceEventId");
        }

        var consumer = new RaidAttackerLossConfirmationConsumer(ledger);
        RaidAttackerLossConfirmationResult result = consumer.Consume(
            new RaidAttackerLossConfirmationRequest(
                application.EventId,
                application.SourceEventId,
                userId,
                colonyId,
                baseSnapshotId,
                acceptedSnapshot),
            nowUtc);

        return new ConfirmEventApplicationResultDto(
            application.EventId,
            ToProtocolResponse(result),
            result.AppliedSnapshotId,
            result.Kind.ToString());
    }

    private static ProtocolResponse ToProtocolResponse(RaidAttackerLossConfirmationResult result)
    {
        if (result.Accepted)
        {
            return ProtocolResponse.Ok(ServerLocalization.Text("Event.ApplicationConfirmed"));
        }

        return ProtocolResponse.Reject(
            result.Kind == RaidAttackerLossConfirmationResultKind.LossNotReflected
                ? ProtocolErrorCode.LossNotReflected
                : ProtocolErrorCode.Conflict,
            result.FailureReason ?? ServerText.Format("Events.ApplicationConfirmationRejected"));
    }

    private static ProtocolResponse ToProtocolResponse(ItemDeliveryApplicationConfirmationResult result)
    {
        if (result.Accepted)
        {
            return ProtocolResponse.Ok(ServerLocalization.Text("Gift.ApplicationConfirmed"));
        }

        ProtocolErrorCode errorCode = result.Kind switch
        {
            ItemDeliveryApplicationConfirmationResultKind.EventNotFound => ProtocolErrorCode.EventNotFound,
            ItemDeliveryApplicationConfirmationResultKind.NotTarget => ProtocolErrorCode.EventNotFound,
            ItemDeliveryApplicationConfirmationResultKind.SnapshotIdentityMismatch => ProtocolErrorCode.IdentityMismatch,
            ItemDeliveryApplicationConfirmationResultKind.SnapshotBaseMismatch => ProtocolErrorCode.SnapshotMismatch,
            ItemDeliveryApplicationConfirmationResultKind.NotDelivered => ProtocolErrorCode.SnapshotMismatch,
            ItemDeliveryApplicationConfirmationResultKind.RejectedByTarget => ProtocolErrorCode.Conflict,
            ItemDeliveryApplicationConfirmationResultKind.NotAnchored => ProtocolErrorCode.Conflict,
            _ => ProtocolErrorCode.ValidationFailed
        };

        return ProtocolResponse.Reject(
            errorCode,
            result.FailureReason ?? ServerText.Format("Gift.ApplicationConfirmationRejected"));
    }
}
