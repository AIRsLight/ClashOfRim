using AIRsLight.ClashOfRim.Network.Modules.Support;
using static AIRsLight.ClashOfRim.Network.Modules.Trades.TradeFailureEvidence;
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

        bool playerRaidSettlement = state.RaidEvidenceConfirmation.Handles(request.EventId, request.SourceEventId);
        if (!playerRaidSettlement)
        {
            var response = ReceiveConfirmationSnapshot(state, request.UserId, request.ColonyId, request.AuthToken,
                request.ConfirmedSnapshot, multipart.Payload, new SnapshotConfirmationRequest(request.BaseSnapshotId,
                    [new ConfirmEventApplicationEntry(request.EventId, request.SourceEventId, request.ClientApplicationResult)], true), nowUtc);
            var result = response.Applications.FirstOrDefault();
            return Results.Ok(new ConfirmEventApplicationResponse(result?.Result ?? response.Result, request.EventId,
                result?.AppliedSnapshotId, result?.ServerValidationResult ?? "InvalidConfirmation", response.NextLineageToken));
        }
        return Results.Ok(state.RaidEvidenceConfirmation.Confirm(request, multipart.Payload,
            httpRequest.Headers[MultipartSnapshotAuthentication.HeaderName].FirstOrDefault(), nowUtc));
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

        return Results.Ok(ReceiveConfirmationSnapshot(state, request.UserId, request.ColonyId, request.AuthToken,
            request.ConfirmedSnapshot, multipart.Payload,
            new SnapshotConfirmationRequest(request.BaseSnapshotId, request.Applications, false), nowUtc));
    }
    private static ConfirmEventApplicationResultDto ConfirmEventApplicationAfterSnapshot(
        ConfirmEventApplicationEntry application,
        string userId,
        string colonyId,
        string baseSnapshotId,
        LatestSnapshotRecord acceptedSnapshot,
        ClashOfRimNetworkState state,
        DateTimeOffset nowUtc)
        => EvaluateEventApplication(application, userId, colonyId, baseSnapshotId, acceptedSnapshot, state.Ledger, nowUtc,
            item => RecordAuthoritativeEventAchievements(state, item, item.AppliedAtUtc ?? nowUtc));
    private static ConfirmEventApplicationResultDto EvaluateEventApplication(
        ConfirmEventApplicationEntry application,
        string userId,
        string colonyId,
        string baseSnapshotId,
        LatestSnapshotRecord acceptedSnapshot,
        IAuthoritativeEventLedger ledger,
        DateTimeOffset nowUtc, Action<AuthoritativeEvent>? recordTrade = null)
    {
        AuthoritativeEvent? ledgerEvent = ledger.Find(application.EventId);
        if (ledgerEvent is not null && !IsVisibleParty(ledgerEvent.Type == ServerEventType.Trade ? ledgerEvent.Actor : ledgerEvent.Target, userId, colonyId))
            return new ConfirmEventApplicationResultDto(application.EventId,
                ProtocolResponse.Reject(ProtocolErrorCode.EventNotFound, T("Events.NotFoundOrNotVisible")), null, "TargetIdentityMismatch");
        if (ledgerEvent?.Status is ServerEventStatus.Cancelled or ServerEventStatus.Failed or ServerEventStatus.RejectedByTarget)
            return new ConfirmEventApplicationResultDto(application.EventId,
                ProtocolResponse.Reject(ProtocolErrorCode.Conflict, T("Events.ConfirmationAlreadyEnded")), null, ledgerEvent.Status.ToString());
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
            return ConfirmTradeExchangeAfterSnapshot(
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
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Events.MissingSourceEvent")),
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

    private static ConfirmEventApplicationResultDto ConfirmTradeExchangeAfterSnapshot(
        ConfirmEventApplicationEntry application,
        string userId,
        string colonyId,
        string baseSnapshotId,
        LatestSnapshotRecord acceptedSnapshot,
        AuthoritativeEvent exchangeEvent,
        TradeEventPayload exchangePayload,
        IAuthoritativeEventLedger ledger,
        DateTimeOffset nowUtc, Action<AuthoritativeEvent>? recordTrade = null)
    {
        if (!IsVisibleParty(exchangeEvent.Actor, userId, colonyId))
        {
            return new ConfirmEventApplicationResultDto(
                application.EventId,
                ProtocolResponse.Reject(ProtocolErrorCode.SnapshotMismatch, T("Trade.ExchangeActorMismatch")),
                appliedSnapshotId: null,
                "AcceptorIdentityMismatch");
        }

        if (string.IsNullOrWhiteSpace(application.SourceEventId)
            || !string.Equals(application.SourceEventId, exchangePayload.TradeId, StringComparison.Ordinal))
        {
            if (exchangeEvent.Status != ServerEventStatus.AppliedToSnapshot)
                ledger.ReportApplicationResult(
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

        if (exchangeEvent.Status == ServerEventStatus.AppliedToSnapshot)
        {
            AuthoritativeEvent repaired = SnapshotApplicationCommit.Apply(ledger, exchangeEvent,
                baseSnapshotId, acceptedSnapshot.Identity.SnapshotId!, nowUtc);
            if (!string.IsNullOrWhiteSpace(repaired.AppliedSnapshotId) && repaired.AppliedAtUtc is { } appliedAtUtc)
                recordTrade?.Invoke(repaired);
            return new ConfirmEventApplicationResultDto(
                application.EventId,
                new ProtocolResponse(true, ProtocolErrorCode.DuplicateRequest, T("Trade.ExchangeAlreadyConfirmed")),
                repaired.AppliedSnapshotId,
                "AlreadyApplied");
        }

        SnapshotIdentity identity = acceptedSnapshot.Identity;
        if (!string.Equals(identity.OwnerId, userId, StringComparison.Ordinal)
            || !string.Equals(identity.ColonyId, colonyId, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(identity.SnapshotId))
        {
            ledger.ReportApplicationResult(
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
            ledger.ReportApplicationResult(
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

        AuthoritativeEvent applied = SnapshotApplicationCommit.Apply(ledger, exchangeEvent,
            baseSnapshotId, identity.SnapshotId!, nowUtc);
        recordTrade?.Invoke(applied);

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

        using (var mutation = EnterRecoveredSnapshotMutation(state, () =>
            IsAuthorizedForColony(state, request.AuthToken, request.UserId, request.ColonyId, request.EventId,
                authorizationScope: null, DateTimeOffset.UtcNow, out string failure)
                ? null : ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, failure)))
        {
            if (mutation.Rejection is { } rejection)
                return Results.Ok(new ReportEventApplicationFailureResponse(rejection, request.EventId, string.Empty, 0));
            DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
            AuthoritativeEvent? ledgerEvent = state.Ledger.Find(request.EventId);
            if (ledgerEvent is null || !IsVisibleParty(FailureReportingParty(ledgerEvent), request.UserId, request.ColonyId))
            {
                return Results.Ok(new ReportEventApplicationFailureResponse(
                    ProtocolResponse.Reject(ProtocolErrorCode.EventNotFound, T("Events.NotFoundOrNotVisible")),
                    request.EventId,
                    string.Empty,
                    affectedEventCount: 0));
            }

            ProtocolResponse? reportValidation = ValidateFailureReport(state, ledgerEvent, request.SourceEventId);
            if (reportValidation is not null)
                return Results.Ok(new ReportEventApplicationFailureResponse(reportValidation, ledgerEvent.EventId,
                    ledgerEvent.Status.ToString(), 0));

            IReadOnlyList<AuthoritativeEvent> notifications = Modules.Events.EventFailureNotifications.FindFailureNotifications(state.Ledger, ledgerEvent);
            if (ledgerEvent.Status == ServerEventStatus.Failed
                || ledgerEvent.Status == ServerEventStatus.Conflict && notifications.Count > 0)
            {
                // Wake durable notification recipients and root parties without recalculating refunds or failure reasons.
                SignalFailureReport(state, ledgerEvent, notifications);
                return Results.Ok(new ReportEventApplicationFailureResponse(
                    new ProtocolResponse(true, ProtocolErrorCode.DuplicateRequest, T("Events.AlreadyFailed")),
                    ledgerEvent.EventId,
                    ledgerEvent.Status.ToString(),
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
    }

    private static IReadOnlyCollection<AuthoritativeEvent> FailEventApplicationGraph(
        ClashOfRimNetworkState state,
        AuthoritativeEvent rootEvent,
        string? sourceEventId,
        string reason,
        DateTimeOffset nowUtc)
    {
        if (ValidateFailureReport(state, rootEvent, sourceEventId) is { } invalid)
            throw new InvalidOperationException(invalid.Message);
        var before = state.Ledger.ListAll().ToDictionary(item => item.EventId, StringComparer.Ordinal);
        var affected = new Dictionary<string, AuthoritativeEvent>(StringComparer.Ordinal);

        void MarkFailed(AuthoritativeEvent? ledgerEvent)
        {
            if (ledgerEvent is null || affected.ContainsKey(ledgerEvent.EventId))
            {
                return;
            }

            AuthoritativeEvent failed = ledgerEvent.Status == ServerEventStatus.Failed
                ? ledgerEvent
                : ledgerEvent with { Status = ServerEventStatus.Failed };
            affected[failed.EventId] = failed;
        }

        if (TryResolveTradeOrderIdFromDelivery(state.Ledger, rootEvent, out string? tradeOrderId))
        {
            state.TradeLifecycle.CancelTradeApplicationAfterLandingFailure(tradeOrderId!, rootEvent, nowUtc, affected);
        }
        else if (rootEvent.Type == ServerEventType.Trade
            && rootEvent.Payload is TradeEventPayload tradePayload
            && tradePayload.Stage is TradeStage.SelfDeliveryExchange or TradeStage.ServerDropPodExchange)
        {
            state.TradeLifecycle.CancelTradeApplicationAfterLandingFailure(tradePayload.TradeId, rootEvent, nowUtc, affected);
        }
        else
        {
            MarkFailed(rootEvent);
            // A support return or raid loss references an earlier operation, not another failed delivery.
            // The optional client source is an assertion only; it never authorizes closing the parent.
        }

        var events = affected.Values.Where(item => !before.ContainsKey(item.EventId)).ToList();
        events.AddRange(CreateEventApplicationFailureNotifications(state, rootEvent, affected.Values.ToList(), reason, nowUtc));
        var changes = affected.Values.Where(item => before.ContainsKey(item.EventId))
            .Select(item => new LedgerStatusChange(item.EventId, before[item.EventId].Status, item.Status,
                ExpectedEvent: before[item.EventId])).ToArray();
        state.Ledger.ApplyBatch(new LedgerBatch(events, changes));
        return affected.Values.ToList();
    }

    private static IEnumerable<AuthoritativeEvent> CreateEventApplicationFailureNotifications(
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
            if (state.Ledger.FindByIdempotencyKey(notificationId) is { } existing)
            {
                Modules.Events.EventFailureNotifications.ValidateFailureNotification(rootEvent, existing);
                yield return existing;
                continue;
            }
            AuthoritativeEvent notification = AuthoritativeEventFactory.CreateForLedger(state.Ledger,
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
                    FromAdministrator: false, RelatedEventId: rootEvent.EventId, RelatedEventType: rootEvent.Type),
                nowUtc,
                rootEvent.TargetContext);
            yield return notification;
        }
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

}
