using AIRsLight.ClashOfRim.Compatibility;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network.Plugins;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AIRsLight.ClashOfRim.Network;

public static partial class ClashOfRimNetworkServer
{
    private static async Task<IResult> ConfirmEventApplication(HttpRequest httpRequest, ClashOfRimNetworkState state)
    {
        MultipartSnapshotRequest<ConfirmEventApplicationMetadataRequest>? multipart =
            await ReadMultipartSnapshotRequest<ConfirmEventApplicationMetadataRequest>(httpRequest);
        if (multipart is null || multipart.Request is null || multipart.Payload is null)
        {
            return Results.Ok(new ConfirmEventApplicationResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Events.ConfirmMissingPayload")),
                eventId: string.Empty,
                appliedSnapshotId: null,
                serverValidationResult: "MissingMultipartPayload"));
        }

        ConfirmEventApplicationMetadataRequest request = multipart.Request;
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        if (!IsAuthorizedForColony(
                state,
                request.AuthToken,
                request.UserId,
                request.ColonyId,
                request.EventId,
                authorizationScope: null,
                nowUtc,
                out string authFailure))
        {
            return Results.Ok(new ConfirmEventApplicationResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, authFailure),
                request.EventId,
                appliedSnapshotId: null,
                serverValidationResult: "AuthFailed"));
        }

        if (TryRejectExpiredPendingConfirmationSnapshot(
                state,
                request.UserId,
                request.ColonyId,
                nowUtc,
                out ProtocolResponse? pendingConfirmationFailure))
        {
            return Results.Ok(new ConfirmEventApplicationResponse(
                pendingConfirmationFailure!,
                request.EventId,
                appliedSnapshotId: null,
                serverValidationResult: "PendingConfirmationExpired"));
        }

        bool playerRaidSettlement = IsPlayerRaidSettlementConfirmation(state, request.EventId, request.SourceEventId);
        SaveSnapshotPackage? originalRaidSnapshot = playerRaidSettlement
            ? ResolveOriginalRaidSnapshotForConfirmation(state, request.EventId)
            : ResolveOriginalRaidSnapshotForConfirmation(
                state,
                request.EventId,
                request.UserId,
                request.ColonyId,
                request.BaseSnapshotId);
        SnapshotUploadResult upload = ReceiveSnapshot(
            state,
            request.UserId,
            request.ColonyId,
            request.ConfirmedSnapshot.SnapshotId,
            request.ConfirmedSnapshot,
            multipart.Payload,
            nowUtc,
            storeAccepted: !playerRaidSettlement,
            validateGameplayContinuity: true,
            snapshotUploadKind: playerRaidSettlement
                ? SnapshotUploadKinds.RaidSettlementEvidence
                : request.ConfirmedSnapshot.SnapshotUploadKind,
            requiredRaidEventId: playerRaidSettlement ? request.EventId : null,
            allowRaidSettlementEvidenceSnapshotKind: playerRaidSettlement);

        if (!upload.Accepted || upload.AcceptedSnapshot == null)
        {
            return Results.Ok(new ConfirmEventApplicationResponse(
                ToProtocolResponse(upload),
                request.EventId,
                appliedSnapshotId: null,
                upload.Kind.ToString()));
        }

        RunSnapshotPostUploadProcessors(
            state,
            request.UserId,
            request.ColonyId,
            sessionId: null,
            upload,
            nowUtc,
            extraData: new SnapshotPostUploadExtraData(
                playerRaidSettlement
                    ? SnapshotUploadKinds.RaidSettlementEvidence
                    : upload.SnapshotUploadKind,
                null,
                null),
            registerPlayerColonySite: !playerRaidSettlement);
        ConfirmEventApplicationResultDto result = ConfirmEventApplicationAfterSnapshot(
            new ConfirmEventApplicationEntry(
                request.EventId,
                request.SourceEventId,
                request.ClientApplicationResult),
            request.UserId,
            request.ColonyId,
            request.BaseSnapshotId,
            upload.AcceptedSnapshot,
            originalRaidSnapshot,
            state,
            nowUtc,
            playerRaidSettlement ? multipart.Payload : null);

        return Results.Ok(new ConfirmEventApplicationResponse(
            result.Result,
            result.EventId,
            result.AppliedSnapshotId,
            result.ServerValidationResult,
            upload.AcceptedSnapshot.Envelope.NextLineageToken));
    }

    private static async Task<IResult> ConfirmEventApplications(HttpRequest httpRequest, ClashOfRimNetworkState state)
    {
        MultipartSnapshotRequest<ConfirmEventApplicationsMetadataRequest>? multipart =
            await ReadMultipartSnapshotRequest<ConfirmEventApplicationsMetadataRequest>(httpRequest);
        if (multipart is null || multipart.Request is null || multipart.Payload is null)
        {
            return Results.Ok(new ConfirmEventApplicationsResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Events.BatchConfirmMissingPayload")),
                appliedSnapshotId: null,
                applications: Array.Empty<ConfirmEventApplicationResultDto>()));
        }

        ConfirmEventApplicationsMetadataRequest request = multipart.Request;
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        if (!IsAuthorizedForColony(
                state,
                request.AuthToken,
                request.UserId,
                request.ColonyId,
                authorizationEventId: null,
                authorizationScope: null,
                nowUtc,
                out string authFailure))
        {
            return Results.Ok(new ConfirmEventApplicationsResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, authFailure),
                appliedSnapshotId: null,
                applications: Array.Empty<ConfirmEventApplicationResultDto>()));
        }

        if (TryRejectExpiredPendingConfirmationSnapshot(
                state,
                request.UserId,
                request.ColonyId,
                nowUtc,
                out ProtocolResponse? pendingConfirmationFailure))
        {
            IReadOnlyList<ConfirmEventApplicationResultDto> rejectedApplications =
                request.Applications.Select(application => new ConfirmEventApplicationResultDto(
                    application.EventId,
                    pendingConfirmationFailure!,
                    appliedSnapshotId: null,
                    "PendingConfirmationExpired")).ToList();

            return Results.Ok(new ConfirmEventApplicationsResponse(
                pendingConfirmationFailure!,
                appliedSnapshotId: null,
                rejectedApplications));
        }

        SnapshotUploadResult upload = ReceiveSnapshot(
            state,
            request.UserId,
            request.ColonyId,
            request.ConfirmedSnapshot.SnapshotId,
            request.ConfirmedSnapshot,
            multipart.Payload,
            nowUtc);

        if (!upload.Accepted || upload.AcceptedSnapshot == null)
        {
            IReadOnlyList<ConfirmEventApplicationResultDto> rejectedApplications =
                request.Applications.Select(application => new ConfirmEventApplicationResultDto(
                    application.EventId,
                    ToProtocolResponse(upload),
                    appliedSnapshotId: null,
                    upload.Kind.ToString())).ToList();

            return Results.Ok(new ConfirmEventApplicationsResponse(
                ToProtocolResponse(upload),
                appliedSnapshotId: null,
                rejectedApplications));
        }

        RunSnapshotPostUploadProcessors(
            state,
            request.UserId,
            request.ColonyId,
            sessionId: null,
            upload,
            nowUtc);
        var results = new List<ConfirmEventApplicationResultDto>();
        foreach (ConfirmEventApplicationEntry application in request.Applications)
        {
            results.Add(ConfirmEventApplicationAfterSnapshot(
                application,
                request.UserId,
                request.ColonyId,
                request.BaseSnapshotId,
                upload.AcceptedSnapshot,
                originalRaidSnapshot: null,
                state,
                nowUtc));
        }

        return Results.Ok(new ConfirmEventApplicationsResponse(
            ProtocolResponse.Ok(T("Events.BatchApplicationConfirmed")),
            upload.AcceptedSnapshot.Identity.SnapshotId,
            results,
            upload.AcceptedSnapshot.Envelope.NextLineageToken));
    }

    private static ConfirmEventApplicationResultDto ConfirmEventApplicationAfterSnapshot(
        ConfirmEventApplicationEntry application,
        string userId,
        string colonyId,
        string baseSnapshotId,
        LatestSnapshotRecord acceptedSnapshot,
        SaveSnapshotPackage? originalRaidSnapshot,
        ClashOfRimNetworkState state,
        DateTimeOffset nowUtc,
        byte[]? acceptedSnapshotPayload = null)
    {
        AuthoritativeEvent? ledgerEvent = state.Ledger.Find(application.EventId);
        if (ledgerEvent is not null
            && ledgerEvent.Type is ServerEventType.Gift or ServerEventType.GiftReturn
            && ledgerEvent.Payload is GiftEventPayload)
        {
            var giftConsumer = new GiftApplicationConfirmationConsumer(state.Ledger);
            GiftApplicationConfirmationResult giftResult = giftConsumer.Consume(
                new GiftApplicationConfirmationRequest(
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
            && ledgerEvent.Type == ServerEventType.Raid
            && ledgerEvent.Payload is RaidEventPayload { OpponentKind: RaidOpponentKind.Player, AttackerLoss: null } raidPayload
            && string.IsNullOrWhiteSpace(application.SourceEventId))
        {
            return ConfirmPlayerRaidSettlementAfterSnapshot(
                application,
                userId,
                colonyId,
                acceptedSnapshot,
                originalRaidSnapshot,
                ledgerEvent,
                raidPayload,
                state,
                nowUtc,
                acceptedSnapshotPayload);
        }

        if (ledgerEvent is not null
            && ledgerEvent.Type == ServerEventType.Trade
            && ledgerEvent.Payload is TradeEventPayload { Stage: TradeStage.SelfDeliveryExchange or TradeStage.ServerDropPodExchange } tradePayload)
        {
            return ConfirmTradeExchangeAfterSnapshot(
                application,
                userId,
                colonyId,
                baseSnapshotId,
                acceptedSnapshot,
                ledgerEvent,
                tradePayload,
                state,
                nowUtc);
        }

        if (ledgerEvent is not null
            && ledgerEvent.Type == ServerEventType.SupportPawn
            && ledgerEvent.Payload is SupportPawnEventPayload supportPayload)
        {
            return ConfirmSupportPawnAfterSnapshot(
                application,
                userId,
                colonyId,
                baseSnapshotId,
                acceptedSnapshot,
                ledgerEvent,
                supportPayload,
                state,
                nowUtc);
        }

        if (string.IsNullOrWhiteSpace(application.SourceEventId))
        {
            return new ConfirmEventApplicationResultDto(
                application.EventId,
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Events.MissingSourceEvent")),
                appliedSnapshotId: null,
                "MissingSourceEventId");
        }

        var consumer = new RaidAttackerLossConfirmationConsumer(state.Ledger);
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

    private static ConfirmEventApplicationResultDto ConfirmSupportPawnAfterSnapshot(
        ConfirmEventApplicationEntry application,
        string userId,
        string colonyId,
        string baseSnapshotId,
        LatestSnapshotRecord acceptedSnapshot,
        AuthoritativeEvent supportEvent,
        SupportPawnEventPayload supportPayload,
        ClashOfRimNetworkState state,
        DateTimeOffset nowUtc)
    {
        if (supportEvent.Status == ServerEventStatus.AppliedToSnapshot)
        {
            return new ConfirmEventApplicationResultDto(
                application.EventId,
                new ProtocolResponse(true, ProtocolErrorCode.DuplicateRequest, T("Support.ArrivalAlreadyConfirmed")),
                supportEvent.AppliedSnapshotId,
                "AlreadyApplied");
        }

        if (!IsVisibleParty(supportEvent.Target, userId, colonyId))
        {
            state.Ledger.ReportApplicationResult(
                supportEvent.EventId,
                EventApplicationResultKind.SnapshotBaseMismatch,
                T("Support.ArrivalTargetMismatch"),
                nextRetryAtUtc: null);
            return new ConfirmEventApplicationResultDto(
                application.EventId,
                ProtocolResponse.Reject(ProtocolErrorCode.EventNotFound, T("Support.EventNotFoundOrInvisible")),
                appliedSnapshotId: null,
                "TargetIdentityMismatch");
        }

        if (supportEvent.Status is ServerEventStatus.RejectedByTarget
            or ServerEventStatus.Cancelled
            or ServerEventStatus.Failed)
        {
            state.Ledger.ReportApplicationResult(
                supportEvent.EventId,
                EventApplicationResultKind.NeedsManualReview,
                T("Support.ArrivalAlreadyEnded"),
                nextRetryAtUtc: null);
            return new ConfirmEventApplicationResultDto(
                application.EventId,
                ProtocolResponse.Reject(ProtocolErrorCode.Conflict, T("Support.ArrivalAlreadyEnded")),
                appliedSnapshotId: null,
                supportEvent.Status.ToString());
        }

        SnapshotIdentity identity = acceptedSnapshot.Identity;
        if (!string.Equals(identity.OwnerId, userId, StringComparison.Ordinal)
            || !string.Equals(identity.ColonyId, colonyId, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(identity.SnapshotId))
        {
            state.Ledger.ReportApplicationResult(
                supportEvent.EventId,
                EventApplicationResultKind.SnapshotBaseMismatch,
                T("Support.ArrivalSnapshotIdentityMismatch"),
                nextRetryAtUtc: null);
            return new ConfirmEventApplicationResultDto(
                application.EventId,
                ProtocolResponse.Reject(ProtocolErrorCode.IdentityMismatch, T("Support.ArrivalSnapshotIdentityMismatch")),
                appliedSnapshotId: null,
                "SnapshotIdentityMismatch");
        }

        if (!IsSupportPawnAppliedClientResult(application.ClientApplicationResult, supportPayload))
        {
            state.Ledger.ReportApplicationResult(
                supportEvent.EventId,
                EventApplicationResultKind.NeedsManualReview,
                T("Support.ArrivalClientResultMissing"),
                nextRetryAtUtc: null);
            return new ConfirmEventApplicationResultDto(
                application.EventId,
                ProtocolResponse.Reject(ProtocolErrorCode.Conflict, T("Support.ArrivalClientResultMissing")),
                appliedSnapshotId: null,
                "NotArrived");
        }

        AuthoritativeEvent delivered = string.IsNullOrWhiteSpace(supportEvent.DeliveredToSnapshotId)
            ? state.Ledger.MarkDelivered(supportEvent.EventId, baseSnapshotId, nowUtc)
            : supportEvent;
        AuthoritativeEvent applied = state.Ledger.MarkApplied(
            delivered.EventId,
            identity.SnapshotId!,
            nowUtc);
        state.Ledger.ReportApplicationResult(
            supportEvent.EventId,
            EventApplicationResultKind.Applied,
            failureReason: null,
            nextRetryAtUtc: null);

        return new ConfirmEventApplicationResultDto(
            application.EventId,
            ProtocolResponse.Ok(supportPayload.ReturnToSender ? T("Support.ReturnSnapshotConfirmed") : T("Support.ArrivalSnapshotConfirmed")),
            applied.AppliedSnapshotId,
            supportPayload.ReturnToSender ? "ReturnToSenderApplied" : "Arrived");
    }

    private static bool IsSupportPawnAppliedClientResult(
        string clientApplicationResult,
        SupportPawnEventPayload supportPayload)
    {
        if (supportPayload.ReturnToSender)
        {
            return string.Equals(clientApplicationResult, "SupportPawnReturnCaravanCreated", StringComparison.Ordinal);
        }

        return string.Equals(clientApplicationResult, "SupportPawnArrived", StringComparison.Ordinal);
    }

    private static ConfirmEventApplicationResultDto ConfirmTradeExchangeAfterSnapshot(
        ConfirmEventApplicationEntry application,
        string userId,
        string colonyId,
        string baseSnapshotId,
        LatestSnapshotRecord acceptedSnapshot,
        AuthoritativeEvent exchangeEvent,
        TradeEventPayload exchangePayload,
        ClashOfRimNetworkState state,
        DateTimeOffset nowUtc)
    {
        if (exchangeEvent.Status == ServerEventStatus.AppliedToSnapshot)
        {
            return new ConfirmEventApplicationResultDto(
                application.EventId,
                new ProtocolResponse(true, ProtocolErrorCode.DuplicateRequest, T("Trade.ExchangeAlreadyConfirmed")),
                exchangeEvent.AppliedSnapshotId,
                "AlreadyApplied");
        }

        if (!IsVisibleParty(exchangeEvent.Actor, userId, colonyId))
        {
            state.Ledger.ReportApplicationResult(
                exchangeEvent.EventId,
                EventApplicationResultKind.SnapshotBaseMismatch,
                T("Trade.ExchangeActorMismatchLedger"),
                nextRetryAtUtc: null);
            return new ConfirmEventApplicationResultDto(
                application.EventId,
                ProtocolResponse.Reject(ProtocolErrorCode.SnapshotMismatch, T("Trade.ExchangeActorMismatch")),
                appliedSnapshotId: null,
                "AcceptorIdentityMismatch");
        }

        if (string.IsNullOrWhiteSpace(application.SourceEventId)
            || !string.Equals(application.SourceEventId, exchangePayload.TradeId, StringComparison.Ordinal))
        {
            state.Ledger.ReportApplicationResult(
                exchangeEvent.EventId,
                EventApplicationResultKind.SnapshotBaseMismatch,
                T("Trade.ExchangeSourceMismatch"),
                nextRetryAtUtc: null);
            return new ConfirmEventApplicationResultDto(
                application.EventId,
                ProtocolResponse.Reject(ProtocolErrorCode.SnapshotMismatch, T("Trade.ExchangeSourceMismatch")),
                appliedSnapshotId: null,
                "SourceTradeMismatch");
        }

        SnapshotIdentity identity = acceptedSnapshot.Identity;
        if (!string.Equals(identity.OwnerId, userId, StringComparison.Ordinal)
            || !string.Equals(identity.ColonyId, colonyId, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(identity.SnapshotId))
        {
            state.Ledger.ReportApplicationResult(
                exchangeEvent.EventId,
                EventApplicationResultKind.SnapshotBaseMismatch,
                T("Trade.ExchangeSnapshotIdentityMismatch"),
                nextRetryAtUtc: null);
            return new ConfirmEventApplicationResultDto(
                application.EventId,
                ProtocolResponse.Reject(ProtocolErrorCode.IdentityMismatch, T("Trade.ExchangeSnapshotIdentityMismatch")),
                appliedSnapshotId: null,
                "SnapshotIdentityMismatch");
        }

        if (!TradeExchangeBaseSnapshotMatches(exchangePayload, baseSnapshotId)
            || !string.Equals(acceptedSnapshot.Envelope.PreviousSnapshotId, baseSnapshotId, StringComparison.Ordinal))
        {
            state.Ledger.ReportApplicationResult(
                exchangeEvent.EventId,
                EventApplicationResultKind.SnapshotBaseMismatch,
                T("Trade.ExchangeBaseSnapshotMismatch"),
                nextRetryAtUtc: null);
            return new ConfirmEventApplicationResultDto(
                application.EventId,
                ProtocolResponse.Reject(ProtocolErrorCode.SnapshotMismatch, T("Trade.ExchangeBaseSnapshotMismatch")),
                appliedSnapshotId: null,
                "SnapshotBaseMismatch");
        }

        AuthoritativeEvent delivered = string.IsNullOrWhiteSpace(exchangeEvent.DeliveredToSnapshotId)
            ? state.Ledger.MarkDelivered(exchangeEvent.EventId, baseSnapshotId, nowUtc)
            : exchangeEvent;
        AuthoritativeEvent applied = state.Ledger.MarkApplied(
            delivered.EventId,
            identity.SnapshotId!,
            nowUtc);
        state.Ledger.ReportApplicationResult(
            exchangeEvent.EventId,
            EventApplicationResultKind.Applied,
            failureReason: null,
            nextRetryAtUtc: null);
        RecordAuthoritativeEventAchievements(state, applied, nowUtc);

        return new ConfirmEventApplicationResultDto(
            application.EventId,
            ProtocolResponse.Ok(T("Trade.ExchangeSnapshotConfirmed")),
            applied.AppliedSnapshotId,
            exchangePayload.Stage.ToString());
    }

    private static bool TradeExchangeBaseSnapshotMatches(TradeEventPayload payload, string baseSnapshotId)
    {
        if (string.IsNullOrWhiteSpace(baseSnapshotId))
        {
            return false;
        }

        IReadOnlyList<EventThingReference> deliveredByAcceptor = payload.RequestedItems;
        return deliveredByAcceptor.Count > 0
            && deliveredByAcceptor.All(thing =>
                string.Equals(thing.SourceSnapshotId, baseSnapshotId, StringComparison.Ordinal));
    }

    private static IResult ReportEventApplicationFailure(
        ReportEventApplicationFailureRequest request,
        ClashOfRimNetworkState state)
    {
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey)
            || string.IsNullOrWhiteSpace(request.EventId)
            || string.IsNullOrWhiteSpace(request.UserId)
            || string.IsNullOrWhiteSpace(request.ColonyId)
            || string.IsNullOrWhiteSpace(request.CurrentSnapshotId)
            || string.IsNullOrWhiteSpace(request.Reason))
        {
            return Results.Ok(new ReportEventApplicationFailureResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Events.ReportFailureMissingFields")),
                request.EventId ?? string.Empty,
                string.Empty,
                affectedEventCount: 0));
        }

        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        if (!IsAuthorizedForColony(
                state,
                request.AuthToken,
                request.UserId,
                request.ColonyId,
                request.EventId,
                authorizationScope: null,
                nowUtc,
                out string authFailure))
        {
            return Results.Ok(new ReportEventApplicationFailureResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, authFailure),
                request.EventId,
                string.Empty,
                affectedEventCount: 0));
        }

        AuthoritativeEvent? ledgerEvent = state.Ledger.Find(request.EventId);
        if (ledgerEvent is null || !IsVisibleTo(ledgerEvent, request.UserId, request.ColonyId))
        {
            return Results.Ok(new ReportEventApplicationFailureResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.EventNotFound, T("Events.NotFoundOrNotVisible")),
                request.EventId,
                string.Empty,
                affectedEventCount: 0));
        }

        if (ledgerEvent.Status == ServerEventStatus.Failed)
        {
            return Results.Ok(new ReportEventApplicationFailureResponse(
                new ProtocolResponse(true, ProtocolErrorCode.DuplicateRequest, T("Events.AlreadyFailed")),
                ledgerEvent.EventId,
                ServerEventStatus.Failed.ToString(),
                affectedEventCount: 0));
        }

        IReadOnlyCollection<AuthoritativeEvent> affected = FailEventApplicationGraph(
            state,
            ledgerEvent,
            request.SourceEventId,
            request.Reason,
            nowUtc);
        state.EventNotifications.SignalUsers(affected
            .SelectMany(evt => new[] { evt.Actor.UserId, evt.Target.UserId })
            .Where(userId => !string.IsNullOrWhiteSpace(userId))
            .Distinct(StringComparer.Ordinal));

        AuthoritativeEvent? updatedRoot = state.Ledger.Find(ledgerEvent.EventId);
        return Results.Ok(new ReportEventApplicationFailureResponse(
            ProtocolResponse.Ok(T("Events.FailureRecorded")),
            ledgerEvent.EventId,
            updatedRoot?.Status.ToString() ?? ServerEventStatus.Failed.ToString(),
            affected.Count));
    }

    private static IReadOnlyCollection<AuthoritativeEvent> FailEventApplicationGraph(
        ClashOfRimNetworkState state,
        AuthoritativeEvent rootEvent,
        string? sourceEventId,
        string reason,
        DateTimeOffset nowUtc)
    {
        var affected = new Dictionary<string, AuthoritativeEvent>(StringComparer.Ordinal);

        void MarkFailed(AuthoritativeEvent? ledgerEvent)
        {
            if (ledgerEvent is null || affected.ContainsKey(ledgerEvent.EventId))
            {
                return;
            }

            AuthoritativeEvent failed = ledgerEvent.Status == ServerEventStatus.Failed
                ? ledgerEvent
                : state.Ledger.ChangeStatus(ledgerEvent.EventId, ServerEventStatus.Failed);
            affected[failed.EventId] = failed;
        }

        if (TryResolveTradeOrderIdFromDelivery(state, rootEvent, out string? tradeOrderId))
        {
            CancelTradeApplicationAfterLandingFailure(state, tradeOrderId!, rootEvent, reason, nowUtc, affected);
        }
        else if (rootEvent.Type == ServerEventType.Trade
            && rootEvent.Payload is TradeEventPayload tradePayload
            && tradePayload.Stage is TradeStage.SelfDeliveryExchange or TradeStage.ServerDropPodExchange)
        {
            CancelTradeApplicationAfterLandingFailure(state, tradePayload.TradeId, rootEvent, reason, nowUtc, affected);
        }
        else
        {
            MarkFailed(rootEvent);

            if (!string.IsNullOrWhiteSpace(sourceEventId))
            {
                MarkFailed(state.Ledger.Find(sourceEventId!));
            }
        }

        AppendEventApplicationFailureNotifications(state, rootEvent, affected.Values.ToList(), reason, nowUtc);
        return affected.Values.ToList();
    }

    private static void CancelTradeApplicationAfterLandingFailure(
        ClashOfRimNetworkState state,
        string tradeOrderId,
        AuthoritativeEvent rootEvent,
        string reason,
        DateTimeOffset nowUtc,
        IDictionary<string, AuthoritativeEvent> affected)
    {
        void Mark(AuthoritativeEvent? ledgerEvent, ServerEventStatus status)
        {
            if (ledgerEvent is null || affected.ContainsKey(ledgerEvent.EventId))
            {
                return;
            }

            AuthoritativeEvent updated = ledgerEvent.Status == status
                ? ledgerEvent
                : state.Ledger.ChangeStatus(ledgerEvent.EventId, status);
            affected[updated.EventId] = updated;
        }

        AuthoritativeEvent? tradeOrder = state.Ledger.Find(tradeOrderId);
        Mark(tradeOrder, ServerEventStatus.Cancelled);

        foreach (AuthoritativeEvent related in FindTradeApplicationGraph(state, tradeOrderId))
        {
            if (string.Equals(related.EventId, tradeOrderId, StringComparison.Ordinal))
            {
                continue;
            }

            if (related.Type == ServerEventType.Trade
                && related.Payload is TradeEventPayload { Stage: TradeStage.AcceptedMemo })
            {
                Mark(related, ServerEventStatus.Cancelled);
            }
            else
            {
                Mark(related, ServerEventStatus.Failed);
            }
        }

        if (tradeOrder?.Payload is TradeEventPayload orderPayload)
        {
            LedgerAppendResult returnAppend = AppendTradeItemDeliveryEvent(
                state,
                $"trade-application-failed-owner-return:{tradeOrder.EventId}:{rootEvent.EventId}",
                new EventParty("server"),
                tradeOrder.Actor,
                orderPayload.OfferedItems,
                GiftEventPurpose.TradeApplicationFailedOwnerReturn,
                tradeOrder.TargetContext,
                nowUtc);
            affected[returnAppend.Event.EventId] = returnAppend.Event;
        }
    }

    private static IReadOnlyList<AuthoritativeEvent> FindTradeApplicationGraph(
        ClashOfRimNetworkState state,
        string tradeOrderId)
    {
        return state.Ledger.ListByType(ServerEventType.Trade)
            .Concat(state.Ledger.ListByType(ServerEventType.GiftReturn))
            .Where(ledgerEvent =>
                string.Equals(ledgerEvent.EventId, tradeOrderId, StringComparison.Ordinal)
                || IsTradeEventForOrder(ledgerEvent, tradeOrderId)
                || IsTradeDeliveryEventForOrder(ledgerEvent, tradeOrderId))
            .ToList();
    }

    private static bool IsTradeEventForOrder(AuthoritativeEvent ledgerEvent, string tradeOrderId)
    {
        return ledgerEvent.Type == ServerEventType.Trade
            && ledgerEvent.Payload is TradeEventPayload payload
            && string.Equals(payload.TradeId, tradeOrderId, StringComparison.Ordinal);
    }

    private static bool IsTradeDeliveryEventForOrder(AuthoritativeEvent ledgerEvent, string tradeOrderId)
    {
        return ledgerEvent.Type == ServerEventType.GiftReturn
            && IsTradeCompletedDeliveryIdempotencyKey(ledgerEvent.IdempotencyKey, tradeOrderId);
    }

    private static bool TryResolveTradeOrderIdFromDelivery(
        ClashOfRimNetworkState state,
        AuthoritativeEvent ledgerEvent,
        out string? tradeOrderId)
    {
        tradeOrderId = null;
        if (ledgerEvent.Type != ServerEventType.GiftReturn
            || IsTradeApplicationFailedOwnerReturnEvent(ledgerEvent))
        {
            return false;
        }

        if (!TryParseTradeOrderIdFromCompletedDeliveryIdempotencyKey(ledgerEvent.IdempotencyKey, out string? parsedTradeOrderId))
        {
            return false;
        }

        AuthoritativeEvent? tradeOrder = state.Ledger.Find(parsedTradeOrderId!);
        if (tradeOrder is null
            || tradeOrder.Type != ServerEventType.Trade
            || tradeOrder.Payload is not TradeEventPayload { Stage: TradeStage.MarketOrder })
        {
            return false;
        }

        tradeOrderId = tradeOrder.EventId;
        return true;
    }

    private static bool IsTradeApplicationFailedOwnerReturnEvent(AuthoritativeEvent ledgerEvent)
    {
        return ledgerEvent.Type == ServerEventType.GiftReturn
            && (ledgerEvent.IdempotencyKey.StartsWith("trade-application-failed-owner-return:", StringComparison.Ordinal)
                || ledgerEvent.Payload is GiftEventPayload { Purpose: GiftEventPurpose.TradeApplicationFailedOwnerReturn });
    }

    private static bool IsTradeCompletedDeliveryIdempotencyKey(string idempotencyKey, string tradeOrderId)
    {
        return IsTradeCompletedDeliveryIdempotencyKey(idempotencyKey, tradeOrderId, "trade-completed-owner-delivery:")
            || IsTradeCompletedDeliveryIdempotencyKey(idempotencyKey, tradeOrderId, "trade-completed-acceptor-delivery:");
    }

    private static bool TryParseTradeOrderIdFromCompletedDeliveryIdempotencyKey(
        string idempotencyKey,
        out string? tradeOrderId)
    {
        tradeOrderId = null;
        return TryParseTradeOrderIdFromCompletedDeliveryIdempotencyKey(idempotencyKey, "trade-completed-owner-delivery:", out tradeOrderId)
            || TryParseTradeOrderIdFromCompletedDeliveryIdempotencyKey(idempotencyKey, "trade-completed-acceptor-delivery:", out tradeOrderId);
    }

    private static bool TryParseTradeOrderIdFromCompletedDeliveryIdempotencyKey(
        string idempotencyKey,
        string prefix,
        out string? tradeOrderId)
    {
        tradeOrderId = null;
        if (string.IsNullOrWhiteSpace(idempotencyKey)
            || !idempotencyKey.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        string rest = idempotencyKey[prefix.Length..];
        int typeSeparator = rest.IndexOf(':');
        if (typeSeparator <= 0)
        {
            return false;
        }

        int idEnd = rest.IndexOf(':', typeSeparator + 1);
        if (idEnd <= typeSeparator + 1)
        {
            return false;
        }

        tradeOrderId = rest[..idEnd];
        return true;
    }

    private static bool IsTradeCompletedDeliveryIdempotencyKey(
        string idempotencyKey,
        string tradeOrderId,
        string prefix)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey)
            || string.IsNullOrWhiteSpace(tradeOrderId)
            || !idempotencyKey.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        string rest = idempotencyKey[prefix.Length..];
        return rest.StartsWith(tradeOrderId + ":", StringComparison.Ordinal);
    }

    private static void AppendEventApplicationFailureNotifications(
        ClashOfRimNetworkState state,
        AuthoritativeEvent rootEvent,
        IReadOnlyCollection<AuthoritativeEvent> affected,
        string reason,
        DateTimeOffset nowUtc)
    {
        IReadOnlyList<EventParty> targets = affected
            .SelectMany(evt => new[] { evt.Actor, evt.Target })
            .Where(target => !string.IsNullOrWhiteSpace(target.UserId)
                && !string.Equals(target.UserId, "server", StringComparison.OrdinalIgnoreCase))
            .GroupBy(target => target.UserId + "\n" + target.ColonyId, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();

        foreach (EventParty target in targets)
        {
            string notificationId = $"event-application-failed:{rootEvent.EventId}:{target.UserId}:{target.ColonyId}";
            AuthoritativeEvent notification = AuthoritativeEventFactory.Create(
                ServerEventType.ServerNotification,
                new EventParty("server"),
                target,
                notificationId,
                state.OnlinePresence.IsUserOnline(target.UserId),
                new ServerNotificationEventPayload(
                    notificationId,
                    T("Events.ApplicationFailedTitle"),
                    T("Events.ApplicationFailedMessage", ("EVENT", rootEvent.EventId), ("REASON", reason)),
                    ServerNotificationSeverity.Warning,
                    FromAdministrator: false),
                nowUtc,
                rootEvent.TargetContext);
            LogEventAppend(state, state.Ledger.Append(notification), "event-application-failed-notification");
        }
    }

    private static SaveSnapshotPackage? ResolveOriginalRaidSnapshotForConfirmation(
        ClashOfRimNetworkState state,
        string eventId)
    {
        if (state.SnapshotStore is not IColonySnapshotPackageStore packageStore
            || string.IsNullOrWhiteSpace(eventId))
        {
            return null;
        }

        AuthoritativeEvent? ledgerEvent = state.Ledger.Find(eventId);
        if (ledgerEvent?.Type != ServerEventType.Raid
            || ledgerEvent.Payload is not RaidEventPayload { OpponentKind: RaidOpponentKind.Player } payload
            || string.IsNullOrWhiteSpace(payload.DefenderSnapshotId))
        {
            return null;
        }

        SaveSnapshotPackage? package = packageStore.GetLatestPackage(
            ledgerEvent.Target.UserId,
            ledgerEvent.Target.ColonyId ?? string.Empty);
        if (package is null
            || !string.Equals(package.Envelope.Identity.SnapshotId, payload.DefenderSnapshotId, StringComparison.Ordinal))
        {
            return null;
        }

        return package;
    }

    private static SaveSnapshotPackage? ResolveOriginalRaidSnapshotForConfirmation(
        ClashOfRimNetworkState state,
        string eventId,
        string userId,
        string colonyId,
        string baseSnapshotId)
    {
        if (state.SnapshotStore is not IColonySnapshotPackageStore packageStore
            || string.IsNullOrWhiteSpace(eventId)
            || string.IsNullOrWhiteSpace(userId)
            || string.IsNullOrWhiteSpace(colonyId)
            || string.IsNullOrWhiteSpace(baseSnapshotId))
        {
            return null;
        }

        AuthoritativeEvent? ledgerEvent = state.Ledger.Find(eventId);
        if (ledgerEvent?.Type != ServerEventType.Raid
            || ledgerEvent.Payload is not RaidEventPayload { OpponentKind: RaidOpponentKind.Player } payload
            || !IsVisibleParty(ledgerEvent.Target, userId, colonyId)
            || !string.Equals(payload.DefenderSnapshotId, baseSnapshotId, StringComparison.Ordinal))
        {
            return null;
        }

        SaveSnapshotPackage? package = packageStore.GetLatestPackage(userId, colonyId);
        if (package is null
            || !string.Equals(package.Envelope.Identity.SnapshotId, baseSnapshotId, StringComparison.Ordinal))
        {
            return null;
        }

        return package;
    }

    private static bool IsPlayerRaidSettlementConfirmation(
        ClashOfRimNetworkState state,
        string eventId,
        string? sourceEventId)
    {
        if (!string.IsNullOrWhiteSpace(sourceEventId))
        {
            return false;
        }

        AuthoritativeEvent? ledgerEvent = state.Ledger.Find(eventId);
        return ledgerEvent?.Type == ServerEventType.Raid
            && ledgerEvent.Payload is RaidEventPayload { OpponentKind: RaidOpponentKind.Player, AttackerLoss: null };
    }

    private static string BuildRaidSettlementSnapshotId(string colonyId, string raidEventId, DateTimeOffset nowUtc)
    {
        string safeColony = SanitizeSnapshotIdPart(colonyId);
        string safeRaid = SanitizeSnapshotIdPart(raidEventId);
        if (safeRaid.Length > 24)
        {
            safeRaid = safeRaid[..24];
        }

        return $"{safeColony}-raid-{nowUtc:yyyyMMddHHmmss}-{safeRaid}";
    }

    private static string SanitizeSnapshotIdPart(string value)
    {
        char[] chars = (value ?? string.Empty).Trim().ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            char c = chars[i];
            if (!char.IsLetterOrDigit(c) && c != '-' && c != '_')
            {
                chars[i] = '-';
            }
        }

        string sanitized = new(chars);
        return string.IsNullOrWhiteSpace(sanitized) ? "snapshot" : sanitized;
    }

    private static ConfirmEventApplicationResultDto ConfirmPlayerRaidSettlementAfterSnapshot(
        ConfirmEventApplicationEntry application,
        string userId,
        string colonyId,
        LatestSnapshotRecord acceptedSnapshot,
        SaveSnapshotPackage? originalRaidSnapshot,
        AuthoritativeEvent sourceRaid,
        RaidEventPayload raidPayload,
        ClashOfRimNetworkState state,
        DateTimeOffset nowUtc,
        byte[]? acceptedSnapshotPayload)
    {
        if (sourceRaid.Status == ServerEventStatus.AppliedToSnapshot)
        {
            return new ConfirmEventApplicationResultDto(
                application.EventId,
                new ProtocolResponse(true, ProtocolErrorCode.DuplicateRequest, T("Raid.SettlementAlreadyConfirmed")),
                sourceRaid.AppliedSnapshotId,
                "AlreadyApplied");
        }

        if (!IsVisibleParty(sourceRaid.Actor, userId, colonyId))
        {
            AuthoritativeEvent changed = state.Ledger.ReportApplicationResult(
                sourceRaid.EventId,
                EventApplicationResultKind.SnapshotBaseMismatch,
                T("Raid.SettlementAttackerIdentityMismatchLedger"),
                nextRetryAtUtc: null);
            return new ConfirmEventApplicationResultDto(
                application.EventId,
                ProtocolResponse.Reject(ProtocolErrorCode.SnapshotMismatch, T("Raid.SettlementAttackerIdentityMismatch")),
                appliedSnapshotId: null,
                "AttackerIdentityMismatch");
        }

        if (string.IsNullOrWhiteSpace(raidPayload.DefenderSnapshotId))
        {
            AuthoritativeEvent changed = state.Ledger.ReportApplicationResult(
                sourceRaid.EventId,
                EventApplicationResultKind.SnapshotBaseMismatch,
                T("Raid.SettlementBaseSnapshotMismatch"),
                nextRetryAtUtc: null);
            return new ConfirmEventApplicationResultDto(
                application.EventId,
                ProtocolResponse.Reject(ProtocolErrorCode.SnapshotMismatch, T("Raid.SettlementBaseSnapshotMismatch")),
                appliedSnapshotId: null,
                "BaseSnapshotMismatch");
        }

        if (originalRaidSnapshot is null)
        {
            AuthoritativeEvent changed = state.Ledger.ReportApplicationResult(
                sourceRaid.EventId,
                EventApplicationResultKind.NeedsManualReview,
                T("Raid.SettlementMissingOriginalSnapshot"),
                nextRetryAtUtc: null);
            return new ConfirmEventApplicationResultDto(
                application.EventId,
                ProtocolResponse.Reject(ProtocolErrorCode.Conflict, T("Raid.SettlementMissingOriginalSnapshot")),
                appliedSnapshotId: null,
                "MissingOriginalRaidSnapshot");
        }

        var returnedSnapshot = new SaveSnapshotPackage(
            acceptedSnapshot.Envelope,
            Payload: acceptedSnapshotPayload ?? Array.Empty<byte>(),
            acceptedSnapshot.Index);
        string targetMapId = sourceRaid.TargetContext?.MapUniqueId ?? string.Empty;
        string returnedMapId = ResolveRaidSettlementEvidenceMapId(acceptedSnapshot.Index, targetMapId, sourceRaid.EventId);
        RaidSettlementReturnResult settlement = RaidSettlementReturnProcessor.Process(
            new RaidSettlementReturnRequest(
                sourceRaid.EventId,
                originalRaidSnapshot.Envelope.Identity,
                originalRaidSnapshot,
                returnedSnapshot,
                targetMapId,
                LossRatio: state.ServerConfiguration.RaidSettlementLossRatio,
                PackableBuildingDefNames: state.AdminBaseline.Current?.PackableBuildingDefNames,
                BuildingMaxHitPointsByDefName: state.AdminBaseline.Current?.EstimatedSettlementMaxHitPointsByDefName,
                StuffHitPointFactorByDefName: state.AdminBaseline.Current?.StuffHitPointFactorByDefName,
                StuffHitPointOffsetByDefName: state.AdminBaseline.Current?.StuffHitPointOffsetByDefName,
                MinimumRemainingHitPointsRatio: state.ServerConfiguration.RaidSettlementMinimumRemainingHitPointsRatio,
                IgnoredThingDefNames: state.Plugins.ActiveIgnoredRaidSettlementThingDefNames(state.CompatibilityBaseline.Current),
                ReturnedMapUniqueId: returnedMapId,
                BuildingHitPointsLossRatio: state.ServerConfiguration.RaidSettlementBuildingHitPointsLossRatio,
                TrapDefNames: state.AdminBaseline.Current?.ApprovedTrapDefNames));
        if (settlement.Accepted)
        {
            if (state.SnapshotStore is not IColonySnapshotPackageStore packageStore)
            {
                return new ConfirmEventApplicationResultDto(
                    application.EventId,
                    ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, T("Raid.SettlementSnapshotEditUnsupported")),
                    appliedSnapshotId: null,
                    "SnapshotPackageStoreUnavailable");
            }

            string defenderColonyId = string.IsNullOrWhiteSpace(sourceRaid.Target.ColonyId)
                ? colonyId
                : sourceRaid.Target.ColonyId!;
            string editedSnapshotId = BuildRaidSettlementSnapshotId(defenderColonyId, sourceRaid.EventId, nowUtc);
            SaveSnapshotPackage editedPackage;
            try
            {
                editedPackage = RaidSettlementSnapshotEditor.ApplySettlementLosses(
                    originalRaidSnapshot,
                    settlement,
                    editedSnapshotId,
                    nowUtc,
                    state.Plugins.ActiveRaidSettlementSnapshotEditorExtensions(state.CompatibilityBaseline.Current));
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or System.Xml.XmlException)
            {
                state.Ledger.ReportApplicationResult(
                    sourceRaid.EventId,
                    EventApplicationResultKind.NeedsManualReview,
                    T("Raid.SettlementSnapshotEditFailed", ("MESSAGE", ex.Message)),
                    nextRetryAtUtc: null);
                return new ConfirmEventApplicationResultDto(
                    application.EventId,
                    ProtocolResponse.Reject(ProtocolErrorCode.Conflict, T("Raid.SettlementSnapshotEditFailed", ("MESSAGE", ex.Message))),
                    appliedSnapshotId: null,
                    "DefenderSnapshotEditFailed");
            }

            if (!string.Equals(editedPackage.Envelope.Identity.OwnerId, sourceRaid.Target.UserId, StringComparison.Ordinal)
                || !string.Equals(editedPackage.Envelope.Identity.ColonyId, defenderColonyId, StringComparison.Ordinal))
            {
                state.Ledger.ReportApplicationResult(
                    sourceRaid.EventId,
                    EventApplicationResultKind.NeedsManualReview,
                    T("Raid.SettlementSnapshotIdentityMismatch"),
                    nextRetryAtUtc: null);
                return new ConfirmEventApplicationResultDto(
                    application.EventId,
                    ProtocolResponse.Reject(ProtocolErrorCode.IdentityMismatch, T("Raid.SettlementSnapshotIdentityMismatch")),
                    appliedSnapshotId: null,
                    "DefenderSnapshotIdentityMismatch");
            }

            RaidSettlementLedgerRecordResult record;
            lock (state.RaidSettlementSnapshotMutationGate)
            {
                packageStore.StoreLatest(returnedSnapshot, returnedSnapshot.Index, nowUtc);
                RecordLatestSnapshotReference(
                    state,
                    userId,
                    colonyId,
                    new LatestSnapshotRecord(returnedSnapshot.Envelope.Identity, returnedSnapshot.Envelope, returnedSnapshot.Index, nowUtc),
                    nowUtc);
                packageStore.StoreLatest(editedPackage, editedPackage.Index, nowUtc);
                RecordLatestSnapshotReference(
                    state,
                    sourceRaid.Target.UserId,
                    defenderColonyId,
                    new LatestSnapshotRecord(editedPackage.Envelope.Identity, editedPackage.Envelope, editedPackage.Index, nowUtc),
                    nowUtc);
                record = RaidSettlementLedgerRecorder.Record(
                    state.Ledger,
                    sourceRaid.EventId,
                    settlement,
                    nowUtc,
                    state.OnlinePresence.IsUserOnline(sourceRaid.Target.UserId));
            }
            state.RuntimeLogger.LogInformation(
                "Stored raid settlement evidence snapshot as attacker latest: raid={RaidEventId} attacker={UserId}/{ColonyId} snapshot={SnapshotId}",
                sourceRaid.EventId,
                userId,
                colonyId,
                returnedSnapshot.Envelope.Identity.SnapshotId);
            if (record.Kind is not (RaidSettlementLedgerRecordResultKind.SettlementEventCreated
                or RaidSettlementLedgerRecordResultKind.SettlementEventAlreadyExists))
            {
                return new ConfirmEventApplicationResultDto(
                    application.EventId,
                    ProtocolResponse.Reject(ProtocolErrorCode.Conflict, record.FailureReason ?? T("Raid.SettlementRejected")),
                    appliedSnapshotId: null,
                    record.Kind + ":" + settlement.Kind);
            }

            if (string.IsNullOrWhiteSpace(sourceRaid.DeliveredToSnapshotId))
            {
                state.Ledger.MarkDelivered(
                    sourceRaid.EventId,
                    originalRaidSnapshot.Envelope.Identity.SnapshotId ?? raidPayload.DefenderSnapshotId,
                    nowUtc);
            }

            state.Ledger.MarkApplied(
                sourceRaid.EventId,
                editedPackage.Envelope.Identity.SnapshotId ?? editedSnapshotId,
                nowUtc);
            state.Ledger.ReportApplicationResult(
                sourceRaid.EventId,
                EventApplicationResultKind.Applied,
                failureReason: null,
                nextRetryAtUtc: null);
            if (record.SettlementEvent is not null)
            {
                LogEventAppend(
                    state,
                    new LedgerAppendResult(
                        record.SettlementEvent,
                        record.Kind == RaidSettlementLedgerRecordResultKind.SettlementEventCreated),
                    "raid-settlement");
                state.EventNotifications.SignalUser(record.SettlementEvent.Target.UserId);
            }

            return new ConfirmEventApplicationResultDto(
                application.EventId,
                ProtocolResponse.Ok(T("Raid.SettlementApplied")),
                returnedSnapshot.Envelope.Identity.SnapshotId,
                record.Kind.ToString());
        }

        RaidSettlementLedgerRecordResult rejectedRecord = RaidSettlementLedgerRecorder.Record(
            state.Ledger,
            sourceRaid.EventId,
            settlement,
            nowUtc,
            state.OnlinePresence.IsUserOnline(sourceRaid.Target.UserId));
        if (rejectedRecord.SettlementEvent is not null)
        {
            LogEventAppend(
                state,
                new LedgerAppendResult(
                    rejectedRecord.SettlementEvent,
                    rejectedRecord.Kind == RaidSettlementLedgerRecordResultKind.SettlementEventCreated),
                "raid-settlement-rejected");
        }
        return new ConfirmEventApplicationResultDto(
            application.EventId,
            ProtocolResponse.Reject(ProtocolErrorCode.Conflict, rejectedRecord.FailureReason ?? T("Raid.SettlementRejected")),
            appliedSnapshotId: null,
            rejectedRecord.Kind + ":" + settlement.Kind);
    }

    private static string ResolveRaidSettlementEvidenceMapId(SaveSnapshotIndex evidenceIndex, string targetMapId, string? raidEventId)
    {
        HashSet<string> raidBattleWorldObjectIds = evidenceIndex.WorldObjects
            .Where(worldObject => string.Equals(worldObject.Def, "ClashOfRim_RemoteRaidBattleMapParent", StringComparison.Ordinal))
            .Where(worldObject => string.IsNullOrWhiteSpace(raidEventId)
                || string.Equals(worldObject.ClashOfRimRelatedEventId, raidEventId, StringComparison.Ordinal))
            .Select(worldObject => worldObject.UniqueLoadId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .ToHashSet(StringComparer.Ordinal);
        string? raidBattleMapId = evidenceIndex.Maps
            .Where(map => !string.IsNullOrWhiteSpace(map.UniqueId))
            .FirstOrDefault(map => !string.IsNullOrWhiteSpace(map.ParentWorldObjectId)
                && raidBattleWorldObjectIds.Contains(map.ParentWorldObjectId!))
            ?.UniqueId;
        if (!string.IsNullOrWhiteSpace(raidBattleMapId))
        {
            return raidBattleMapId!;
        }

        string normalizedTargetMapId = NormalizeMapId(targetMapId);
        if (!string.IsNullOrWhiteSpace(normalizedTargetMapId)
            && evidenceIndex.Maps.Any(map => string.Equals(map.UniqueId, normalizedTargetMapId, StringComparison.Ordinal)))
        {
            return normalizedTargetMapId;
        }

        return normalizedTargetMapId;
    }

    private static string NormalizeMapId(string? mapId)
    {
        if (string.IsNullOrWhiteSpace(mapId))
        {
            return string.Empty;
        }

        string trimmed = mapId!.Trim();
        return trimmed.StartsWith("Map_", StringComparison.Ordinal)
            ? trimmed["Map_".Length..]
            : trimmed;
    }
}
