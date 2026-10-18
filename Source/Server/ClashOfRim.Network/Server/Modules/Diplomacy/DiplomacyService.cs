using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Network.Modules.Authorization;
using AIRsLight.ClashOfRim.Network.Modules.Snapshots;
using AIRsLight.ClashOfRim.Network.Modules.Events;
using Microsoft.Extensions.Logging;
using static AIRsLight.ClashOfRim.Network.Modules.Diplomacy.DiplomacyPolicy;
using static AIRsLight.ClashOfRim.Network.Modules.Events.EventProtocolMapper;

namespace AIRsLight.ClashOfRim.Network.Modules.Diplomacy;

internal sealed class DiplomacyService(IAuthoritativeEventLedger ledger, ColonySnapshotAuthorization authorization,
    SnapshotMutationCoordinator mutations, DiplomacyRelationRegistry relations, OnlinePresenceRegistry presence,
    EventNotificationHub notifications, Func<ClashOfRimServerConfiguration> configuration, Func<ILogger> logger)
{
    internal DiplomacyEventResponse Create(CreateDiplomacyEventRequest request)
    {
        ProtocolResponse? validation = ValidateDiplomacyCreateRequest(request);
        if (validation is not null)
        {
            return new DiplomacyEventResponse(validation, null, null, null);
        }

        ProtocolResponse? Authorize()
        {
            if (authorization.Authorize(request.AuthToken, request.Actor.UserId, request.Actor.ColonyId!) is { } failure)
                return failure;
            if (ledger.FindByIdempotencyKey(request.IdempotencyKey) is not { } existing)
                return authorization.RequireCurrent(request.Actor.UserId, request.Actor.ColonyId!, request.Actor.SnapshotId);
            // A key is global to the ledger; authenticate and bind a replay before recovery.
            return MatchesEndpoint(existing.Actor, request.Actor.UserId, request.Actor.ColonyId)
                && MatchesEndpoint(existing.Target, request.Target.UserId, request.Target.ColonyId)
                && existing.Type == ResolveDiplomacyEventType(request.Kind)
                && (!IsSupportRequestKind(request.Kind) || existing.Payload is ServerNotificationEventPayload notice
                    && notice.NotificationId == SupportRequestPrefix + request.IdempotencyKey)
                ? null : ProtocolResponse.Reject(ProtocolErrorCode.Conflict, ServerText.Format("Diplomacy.NotFoundOrInvisible"));
        }
        using var mutation = mutations.Enter(Authorize);
        if (mutation.Rejection is { } denied)
            return new DiplomacyEventResponse(denied, null, null, null);

        ServerEventType type = ResolveDiplomacyEventType(request.Kind);
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        DiplomacyRelationRecord? currentRelation = relations.GetRelation(
            request.Actor.UserId,
            request.Actor.ColonyId,
            request.Target.UserId,
            request.Target.ColonyId);
        string currentRelationKind = currentRelation?.RelationKind ?? DiplomacyRelationRegistry.RelationNeutral;
        AuthoritativeEvent? existingDiplomacyEvent = ledger.FindByIdempotencyKey(request.IdempotencyKey);
        if (existingDiplomacyEvent is not null)
        {
            return new DiplomacyEventResponse(
                new ProtocolResponse(true, ProtocolErrorCode.DuplicateRequest, ServerLocalization.Text("Diplomacy.EventDuplicate")),
                existingDiplomacyEvent.EventId,
                null,
                currentRelationKind);
        }

        bool supportRequest = IsSupportRequestKind(request.Kind);
        ProtocolResponse? relationValidation = supportRequest
            ? ValidateSupportRequestDiplomacyTransition(request, currentRelationKind, nowUtc)
            : ValidateDiplomacyRelationTransition(
                type,
                currentRelationKind,
                currentRelation,
                nowUtc,
                configuration().DiplomacyRelationChangeCooldown);
        if (relationValidation is not null)
        {
            return new DiplomacyEventResponse(relationValidation, null, null, currentRelationKind);
        }

        bool targetOnline = presence.IsUserOnline(request.Target.UserId);
        AuthoritativeEvent diplomacyEvent = AuthoritativeEventFactory.CreateForLedger(ledger,
            type,
            ToEventParty(request.Actor),
            ToEventParty(request.Target),
            request.IdempotencyKey,
            targetOnline,
            supportRequest
                ? CreateSupportRequestPayload(request)
                : CreateDiplomacyPayload(type, request.IdempotencyKey, request.Message, request.ExpiresAtUtc),
            nowUtc);

        LedgerAppendResult append = ledger.Append(diplomacyEvent);
        string? relationKind = type is ServerEventType.WarDeclaration or ServerEventType.AllianceCancellation
            ? PersistDiplomacyRelation(append.Event, accepted: true, nowUtc)
            : supportRequest ? currentRelationKind : null;
        SignalIfCreated(append, diplomacyEvent.Target.UserId);
        return new DiplomacyEventResponse(
            append.Created
                ? ProtocolResponse.Ok(ServerLocalization.Text("Diplomacy.EventCreated"))
                : new ProtocolResponse(true, ProtocolErrorCode.DuplicateRequest, ServerLocalization.Text("Diplomacy.EventDuplicate")),
            append.Event.EventId,
            null,
            relationKind);
    }

    internal DiplomacyEventResponse Respond(RespondDiplomacyEventRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.EventId)
            || string.IsNullOrWhiteSpace(request.UserId)
            || string.IsNullOrWhiteSpace(request.ColonyId)
            || string.IsNullOrWhiteSpace(request.CurrentSnapshotId))
        {
            return new DiplomacyEventResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, ServerText.Format("Diplomacy.ResponseMissingFields")),
                request.EventId,
                null,
                null);
        }

        ProtocolResponse? Authorize()
        {
            if (authorization.Authorize(request.AuthToken, request.UserId, request.ColonyId) is { } failure)
                return failure;
            var existing = ledger.Find(request.EventId);
            if (existing is null || !IsDiplomacyEventType(existing.Type)
                || !EventVisibility.MatchesParty(existing.Target, request.UserId, request.ColonyId))
                return ProtocolResponse.Reject(ProtocolErrorCode.EventNotFound, ServerText.Format("Diplomacy.NotFoundOrInvisible"));
            return existing.Status is ServerEventStatus.AppliedToSnapshot or ServerEventStatus.RejectedByTarget ? null
                : authorization.RequireCurrent(request.UserId, request.ColonyId, request.CurrentSnapshotId);
        }
        using var mutation = mutations.Enter(Authorize);
        if (mutation.Rejection is { } denied)
            return new DiplomacyEventResponse(denied, request.EventId, null, null);

        AuthoritativeEvent diplomacyEvent = ledger.Find(request.EventId)!;

        if (diplomacyEvent.Status is ServerEventStatus.AppliedToSnapshot or ServerEventStatus.RejectedByTarget)
        {
            return new DiplomacyEventResponse(
                new ProtocolResponse(true, ProtocolErrorCode.DuplicateRequest, ServerLocalization.Text("Diplomacy.EventProcessed")),
                diplomacyEvent.EventId,
                null,
                relations.GetRelationKind(
                    diplomacyEvent.Actor.UserId,
                    diplomacyEvent.Actor.ColonyId,
                    diplomacyEvent.Target.UserId,
                    diplomacyEvent.Target.ColonyId));
        }

        if (!request.Accepted && diplomacyEvent.RejectionPolicy != EventRejectionPolicy.RejectableByTarget)
        {
            return new DiplomacyEventResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, ServerText.Format("Diplomacy.NotRejectable")),
                diplomacyEvent.EventId,
                null,
                null);
        }

        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        if (request.Accepted && diplomacyEvent.Type is ServerEventType.AllianceRequest or ServerEventType.PeaceRequest)
        {
            DiplomacyRelationRecord? currentRelation = relations.GetRelation(
                diplomacyEvent.Actor.UserId,
                diplomacyEvent.Actor.ColonyId,
                diplomacyEvent.Target.UserId,
                diplomacyEvent.Target.ColonyId);
            string currentRelationKind = currentRelation?.RelationKind ?? DiplomacyRelationRegistry.RelationNeutral;
            ProtocolResponse? relationValidation = ValidateDiplomacyRelationTransition(
                diplomacyEvent.Type,
                currentRelationKind,
                currentRelation,
                nowUtc,
                configuration().DiplomacyRelationChangeCooldown);
            if (relationValidation is not null)
            {
                return new DiplomacyEventResponse(
                    relationValidation,
                    diplomacyEvent.EventId,
                    null,
                    currentRelationKind);
            }
        }

        AuthoritativeEvent changed = request.Accepted
            ? ledger.MarkAccepted(diplomacyEvent.EventId, nowUtc, request.Reason)
            : ledger.MarkRejected(diplomacyEvent.EventId, nowUtc, request.Reason);
        AuthoritativeEvent notification = CreateDiplomacyResponseNotification(
            changed,
            request.Accepted,
            presence.IsUserOnline(changed.Actor.UserId),
            nowUtc);
        notification = AuthoritativeEventFactory.ResolvePersistedIdentity(ledger, notification);
        LedgerAppendResult notificationAppend = ledger.Append(notification);
        string? relationKind = request.Accepted && changed.Type is ServerEventType.AllianceRequest or ServerEventType.PeaceRequest
            ? PersistDiplomacyRelation(changed, accepted: true, nowUtc)
            : null;
        SignalIfCreated(notificationAppend, notification.Target.UserId);

        return new DiplomacyEventResponse(
            ProtocolResponse.Ok(request.Accepted ? ServerLocalization.Text("Diplomacy.Accepted") : ServerLocalization.Text("Diplomacy.Rejected")),
            changed.EventId,
            notificationAppend.Event.EventId,
            relationKind);
    }

    private ProtocolResponse? ValidateSupportRequestDiplomacyTransition(
        CreateDiplomacyEventRequest request,
        string currentRelationKind,
        DateTimeOffset nowUtc)
    {
        if (!string.Equals(currentRelationKind, DiplomacyRelationRegistry.RelationAlly, StringComparison.Ordinal))
        {
            return ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, ServerText.Format("Diplomacy.SupportRequestRequiresAlly"));
        }

        AuthoritativeEvent? latest = FindLatestSupportRequestEvent(
            ledger,
            request.Actor.UserId,
            request.Actor.ColonyId,
            request.Target.UserId,
            request.Target.ColonyId);
        return ValidateSupportRequestCooldown(latest?.CreatedAtUtc, nowUtc, configuration().DiplomacySupportRequestCooldown);
    }

    private static AuthoritativeEvent? FindLatestSupportRequestEvent(
        IAuthoritativeEventLedger ledger,
        string userA,
        string? colonyA,
        string userB,
        string? colonyB)
    {
        return ledger.ListByTypeForActor(ServerEventType.ServerNotification, userA, colonyA)
            .Where(IsSupportRequestNotification)
            .Where(evt => MatchesEndpoint(evt.Actor, userA, colonyA)
                && MatchesEndpoint(evt.Target, userB, colonyB))
            .OrderByDescending(evt => evt.CreatedAtUtc)
            .FirstOrDefault();
    }

    private static bool IsSupportRequestNotification(AuthoritativeEvent evt)
    {
        return evt.Type == ServerEventType.ServerNotification
            && evt.Payload is ServerNotificationEventPayload payload
            && payload.NotificationId.StartsWith(SupportRequestPrefix, StringComparison.Ordinal);
    }

    private string? PersistDiplomacyRelation(
        AuthoritativeEvent diplomacyEvent,
        bool accepted,
        DateTimeOffset nowUtc)
    {
        string? relationKind = ResolveDiplomacyRelationKind(diplomacyEvent.Type, accepted);
        if (relationKind is null)
        {
            return null;
        }

        DiplomacyRelationRecord record = relations.SetRelationKind(
            diplomacyEvent.Actor.UserId,
            diplomacyEvent.Actor.ColonyId,
            diplomacyEvent.Target.UserId,
            diplomacyEvent.Target.ColonyId,
            relationKind,
            diplomacyEvent.EventId,
            nowUtc);
        return record.RelationKind;
    }

    private void SignalIfCreated(
        LedgerAppendResult append,
        string targetUserId)
    {
        EventDiagnostics.LogAppend(logger(), append, "signal-target");
        if (append.Created && !string.IsNullOrWhiteSpace(targetUserId))
        {
            notifications.SignalUser(targetUserId);
        }
    }
}
