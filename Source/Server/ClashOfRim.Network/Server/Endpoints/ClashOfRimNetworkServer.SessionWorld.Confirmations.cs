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
        if (AuthorizeColony(
                state,
                request.AuthToken,
                request.UserId,
                request.ColonyId,
                request.EventId,
                authorizationScope: null,
                nowUtc) is { } authFailure)
        {
            return Results.Ok(new ConfirmEventApplicationResponse(
                authFailure,
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
        if (AuthorizeColony(
                state,
                request.AuthToken,
                request.UserId,
                request.ColonyId,
                authorizationEventId: null,
                authorizationScope: null,
                nowUtc) is { } authFailure)
        {
            return Results.Ok(new ConfirmEventApplicationsResponse(
                authFailure,
                appliedSnapshotId: null,
                applications: Array.Empty<ConfirmEventApplicationResultDto>()));
        }

        return Results.Ok(ReceiveConfirmationSnapshot(state, request.UserId, request.ColonyId, request.AuthToken,
            request.ConfirmedSnapshot, multipart.Payload,
            new SnapshotConfirmationRequest(request.BaseSnapshotId, request.Applications, false), nowUtc));
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

        using (var mutation = state.SnapshotMutations.Enter(() =>
            AuthorizeColony(state, request.AuthToken, request.UserId, request.ColonyId, request.EventId,
                authorizationScope: null, DateTimeOffset.UtcNow)))
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
}
