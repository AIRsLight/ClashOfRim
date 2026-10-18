using AIRsLight.ClashOfRim.Network.Modules.Sessions;
using static AIRsLight.ClashOfRim.Network.Modules.Support.SupportReturns;
using static AIRsLight.ClashOfRim.Network.Modules.Support.SupportClosures;
using static AIRsLight.ClashOfRim.Network.Modules.Trades.TradeRules;
using static AIRsLight.ClashOfRim.Network.Modules.Events.EventProtocolMapper;
using static AIRsLight.ClashOfRim.Network.Modules.Transfers.ItemTransferMapper;
using AIRsLight.ClashOfRim.Network.Modules.Economy;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network.Plugins;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System.Globalization;

namespace AIRsLight.ClashOfRim.Network;

public static partial class ClashOfRimNetworkServer
{
    private static IResult CreateDiplomacyEvent(CreateDiplomacyEventRequest request, ClashOfRimNetworkState state)
    {
        ProtocolResponse? validation = ValidateDiplomacyCreateRequest(request);
        if (validation is not null)
        {
            return Results.Ok(new DiplomacyEventResponse(validation, null, null, null));
        }

        ProtocolResponse? Authorize()
        {
            if (state.SnapshotAuthorization.Authorize(request.AuthToken, request.Actor.UserId, request.Actor.ColonyId!) is { } failure)
                return failure;
            if (FindEventByIdempotencyKey(state.Ledger, request.IdempotencyKey) is not { } existing)
                return state.SnapshotAuthorization.RequireCurrent(request.Actor.UserId, request.Actor.ColonyId!, request.Actor.SnapshotId);
            // A key is global to the ledger; authenticate and bind a replay before recovery.
            return MatchesEndpoint(existing.Actor, request.Actor.UserId, request.Actor.ColonyId)
                && MatchesEndpoint(existing.Target, request.Target.UserId, request.Target.ColonyId)
                && existing.Type == ResolveDiplomacyEventType(request.Kind)
                && (!IsSupportRequestKind(request.Kind) || existing.Payload is ServerNotificationEventPayload notice
                    && notice.NotificationId == "support-request:" + request.IdempotencyKey)
                ? null : ProtocolResponse.Reject(ProtocolErrorCode.Conflict, T("Diplomacy.NotFoundOrInvisible"));
        }
        using var mutation = EnterRecoveredSnapshotMutation(state, Authorize);
        if (mutation.Rejection is { } denied)
            return Results.Ok(new DiplomacyEventResponse(denied, null, null, null));

        ServerEventType type = ResolveDiplomacyEventType(request.Kind);
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        DiplomacyRelationRecord? currentRelation = state.DiplomacyRelations.GetRelation(
            request.Actor.UserId,
            request.Actor.ColonyId,
            request.Target.UserId,
            request.Target.ColonyId);
        string currentRelationKind = currentRelation?.RelationKind ?? DiplomacyRelationRegistry.RelationNeutral;
        AuthoritativeEvent? existingDiplomacyEvent = FindEventByIdempotencyKey(state.Ledger, request.IdempotencyKey);
        if (existingDiplomacyEvent is not null)
        {
            return Results.Ok(new DiplomacyEventResponse(
                new ProtocolResponse(true, ProtocolErrorCode.DuplicateRequest, ServerLocalization.Text("Diplomacy.EventDuplicate")),
                existingDiplomacyEvent.EventId,
                null,
                currentRelationKind));
        }

        bool supportRequest = IsSupportRequestKind(request.Kind);
        ProtocolResponse? relationValidation = supportRequest
            ? ValidateSupportRequestDiplomacyTransition(state, request, currentRelationKind, nowUtc)
            : ValidateDiplomacyRelationTransition(
                type,
                currentRelationKind,
                currentRelation,
                nowUtc,
                state.ServerConfiguration.DiplomacyRelationChangeCooldown);
        if (relationValidation is not null)
        {
            return Results.Ok(new DiplomacyEventResponse(relationValidation, null, null, currentRelationKind));
        }

        bool targetOnline = state.OnlinePresence.IsUserOnline(request.Target.UserId);
        AuthoritativeEvent diplomacyEvent = AuthoritativeEventFactory.CreateForLedger(state.Ledger,
            type,
            ToEventParty(request.Actor),
            ToEventParty(request.Target),
            request.IdempotencyKey,
            targetOnline,
            supportRequest
                ? CreateSupportRequestPayload(request)
                : CreateDiplomacyPayload(type, request.IdempotencyKey, request.Message, request.ExpiresAtUtc),
            nowUtc);

        LedgerAppendResult append = state.Ledger.Append(diplomacyEvent);
        string? relationKind = type is ServerEventType.WarDeclaration or ServerEventType.AllianceCancellation
            ? PersistDiplomacyRelation(state, append.Event, accepted: true, nowUtc)
            : supportRequest ? currentRelationKind : null;
        SignalIfCreated(state, append, diplomacyEvent.Target.UserId);
        return Results.Ok(new DiplomacyEventResponse(
            append.Created
                ? ProtocolResponse.Ok(ServerLocalization.Text("Diplomacy.EventCreated"))
                : new ProtocolResponse(true, ProtocolErrorCode.DuplicateRequest, ServerLocalization.Text("Diplomacy.EventDuplicate")),
            append.Event.EventId,
            null,
            relationKind));
    }

    private static IResult RespondDiplomacyEvent(RespondDiplomacyEventRequest request, ClashOfRimNetworkState state)
    {
        if (string.IsNullOrWhiteSpace(request.EventId)
            || string.IsNullOrWhiteSpace(request.UserId)
            || string.IsNullOrWhiteSpace(request.ColonyId)
            || string.IsNullOrWhiteSpace(request.CurrentSnapshotId))
        {
            return Results.Ok(new DiplomacyEventResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Diplomacy.ResponseMissingFields")),
                request.EventId,
                null,
                null));
        }

        ProtocolResponse? Authorize()
        {
            if (state.SnapshotAuthorization.Authorize(request.AuthToken, request.UserId, request.ColonyId) is { } failure)
                return failure;
            var existing = state.Ledger.Find(request.EventId);
            if (existing is null || !IsDiplomacyEventType(existing.Type)
                || !IsVisibleParty(existing.Target, request.UserId, request.ColonyId))
                return ProtocolResponse.Reject(ProtocolErrorCode.EventNotFound, T("Diplomacy.NotFoundOrInvisible"));
            return existing.Status is ServerEventStatus.AppliedToSnapshot or ServerEventStatus.RejectedByTarget ? null
                : state.SnapshotAuthorization.RequireCurrent(request.UserId, request.ColonyId, request.CurrentSnapshotId);
        }
        using var mutation = EnterRecoveredSnapshotMutation(state, Authorize);
        if (mutation.Rejection is { } denied)
            return Results.Ok(new DiplomacyEventResponse(denied, request.EventId, null, null));

        AuthoritativeEvent diplomacyEvent = state.Ledger.Find(request.EventId)!;

        if (diplomacyEvent.Status is ServerEventStatus.AppliedToSnapshot or ServerEventStatus.RejectedByTarget)
        {
            return Results.Ok(new DiplomacyEventResponse(
                new ProtocolResponse(true, ProtocolErrorCode.DuplicateRequest, ServerLocalization.Text("Diplomacy.EventProcessed")),
                diplomacyEvent.EventId,
                null,
                ResolveDiplomacyRelationKind(
                    state,
                    diplomacyEvent.Actor.UserId,
                    diplomacyEvent.Actor.ColonyId,
                    diplomacyEvent.Target.UserId,
                    diplomacyEvent.Target.ColonyId)));
        }

        if (!request.Accepted && diplomacyEvent.RejectionPolicy != EventRejectionPolicy.RejectableByTarget)
        {
            return Results.Ok(new DiplomacyEventResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, T("Diplomacy.NotRejectable")),
                diplomacyEvent.EventId,
                null,
                null));
        }

        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        if (request.Accepted && diplomacyEvent.Type is ServerEventType.AllianceRequest or ServerEventType.PeaceRequest)
        {
            DiplomacyRelationRecord? currentRelation = state.DiplomacyRelations.GetRelation(
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
                state.ServerConfiguration.DiplomacyRelationChangeCooldown);
            if (relationValidation is not null)
            {
                return Results.Ok(new DiplomacyEventResponse(
                    relationValidation,
                    diplomacyEvent.EventId,
                    null,
                    currentRelationKind));
            }
        }

        AuthoritativeEvent changed = request.Accepted
            ? state.Ledger.MarkAccepted(diplomacyEvent.EventId, nowUtc, request.Reason)
            : state.Ledger.MarkRejected(diplomacyEvent.EventId, nowUtc, request.Reason);
        AuthoritativeEvent notification = CreateDiplomacyResponseNotification(
            changed,
            request.Accepted,
            state.OnlinePresence.IsUserOnline(changed.Actor.UserId),
            nowUtc);
        notification = AuthoritativeEventFactory.ResolvePersistedIdentity(state.Ledger, notification);
        LedgerAppendResult notificationAppend = state.Ledger.Append(notification);
        string? relationKind = request.Accepted && changed.Type is ServerEventType.AllianceRequest or ServerEventType.PeaceRequest
            ? PersistDiplomacyRelation(state, changed, accepted: true, nowUtc)
            : null;
        SignalIfCreated(state, notificationAppend, notification.Target.UserId);

        return Results.Ok(new DiplomacyEventResponse(
            ProtocolResponse.Ok(request.Accepted ? ServerLocalization.Text("Diplomacy.Accepted") : ServerLocalization.Text("Diplomacy.Rejected")),
            changed.EventId,
            notificationAppend.Event.EventId,
            relationKind));
    }

    private static ProtocolResponse? ValidateDiplomacyCreateRequest(CreateDiplomacyEventRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey)
            || request.Actor is null
            || request.Target is null
            || string.IsNullOrWhiteSpace(request.Actor.UserId)
            || string.IsNullOrWhiteSpace(request.Actor.ColonyId)
            || string.IsNullOrWhiteSpace(request.Actor.SnapshotId)
            || string.IsNullOrWhiteSpace(request.Target.UserId)
            || string.IsNullOrWhiteSpace(request.Target.ColonyId)
            || string.IsNullOrWhiteSpace(request.Kind))
        {
            return ProtocolResponse.Reject(
                ProtocolErrorCode.ValidationFailed,
                T("Diplomacy.CreateMissingFields"));
        }

        if (!IsDiplomacyKind(request.Kind))
        {
            return ProtocolResponse.Reject(
                ProtocolErrorCode.ValidationFailed,
                T("Diplomacy.UnknownKind"));
        }

        if (string.Equals(request.Actor.UserId, request.Target.UserId, StringComparison.Ordinal)
            && string.Equals(request.Actor.ColonyId, request.Target.ColonyId, StringComparison.Ordinal))
        {
            return ProtocolResponse.Reject(
                ProtocolErrorCode.ValidationFailed,
                T("Diplomacy.CannotTargetSelf"));
        }

        return null;
    }

    private static ProtocolResponse? ValidateDiplomacyRelationTransition(
        ServerEventType type,
        string currentRelationKind,
        DiplomacyRelationRecord? currentRelation,
        DateTimeOffset nowUtc,
        TimeSpan cooldown)
    {
        ProtocolResponse? transitionValidation = type switch
        {
            ServerEventType.AllianceRequest when !string.Equals(currentRelationKind, "Neutral", StringComparison.Ordinal) =>
                ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, T("Diplomacy.AllianceRequiresNeutral")),
            ServerEventType.AllianceCancellation when !string.Equals(currentRelationKind, "Ally", StringComparison.Ordinal) =>
                ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, T("Diplomacy.CancelAllianceRequiresAlly")),
            ServerEventType.WarDeclaration when string.Equals(currentRelationKind, "Ally", StringComparison.Ordinal) =>
                ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, T("Diplomacy.CannotDeclareWarOnAlly")),
            ServerEventType.WarDeclaration when string.Equals(currentRelationKind, "Hostile", StringComparison.Ordinal) =>
                ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, T("Diplomacy.AlreadyHostile")),
            ServerEventType.PeaceRequest when !string.Equals(currentRelationKind, "Hostile", StringComparison.Ordinal) =>
                ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, T("Diplomacy.PeaceRequiresHostile")),
            _ => null
        };
        if (transitionValidation is not null)
        {
            return transitionValidation;
        }

        return ValidateDiplomacyRelationCooldown(currentRelation, nowUtc, cooldown);
    }

    private static ProtocolResponse? ValidateDiplomacyRelationCooldown(
        DiplomacyRelationRecord? currentRelation,
        DateTimeOffset nowUtc,
        TimeSpan cooldown)
    {
        if (currentRelation is null || cooldown <= TimeSpan.Zero)
        {
            return null;
        }

        DateTimeOffset availableAtUtc = currentRelation.UpdatedAtUtc + cooldown;
        if (availableAtUtc <= nowUtc)
        {
            return null;
        }

        TimeSpan remaining = availableAtUtc - nowUtc;
        string remainingText = remaining.TotalHours >= 1
            ? T("Diplomacy.TimeHours", ("VALUE", Math.Ceiling(remaining.TotalHours).ToString("0", CultureInfo.InvariantCulture)))
            : T("Diplomacy.TimeMinutes", ("VALUE", Math.Ceiling(remaining.TotalMinutes).ToString("0", CultureInfo.InvariantCulture)));
        return ProtocolResponse.Reject(
            ProtocolErrorCode.ServerRejected,
            T("Diplomacy.RelationCooldown", ("REMAINING", remainingText)));
    }

    private static ProtocolResponse? ValidateSupportRequestDiplomacyTransition(
        ClashOfRimNetworkState state,
        CreateDiplomacyEventRequest request,
        string currentRelationKind,
        DateTimeOffset nowUtc)
    {
        if (!string.Equals(currentRelationKind, DiplomacyRelationRegistry.RelationAlly, StringComparison.Ordinal))
        {
            return ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, T("Diplomacy.SupportRequestRequiresAlly"));
        }

        AuthoritativeEvent? latest = FindLatestSupportRequestEvent(
            state.Ledger,
            request.Actor.UserId,
            request.Actor.ColonyId,
            request.Target.UserId,
            request.Target.ColonyId);
        if (latest is null || state.ServerConfiguration.DiplomacySupportRequestCooldown <= TimeSpan.Zero)
        {
            return null;
        }

        DateTimeOffset availableAtUtc = latest.CreatedAtUtc + state.ServerConfiguration.DiplomacySupportRequestCooldown;
        if (availableAtUtc <= nowUtc)
        {
            return null;
        }

        TimeSpan remaining = availableAtUtc - nowUtc;
        string remainingText = remaining.TotalMinutes >= 1
            ? T("Diplomacy.TimeMinutes", ("VALUE", Math.Ceiling(remaining.TotalMinutes).ToString("0", CultureInfo.InvariantCulture)))
            : T("Diplomacy.TimeSeconds", ("VALUE", Math.Ceiling(remaining.TotalSeconds).ToString("0", CultureInfo.InvariantCulture)));
        return ProtocolResponse.Reject(
            ProtocolErrorCode.ServerRejected,
            T("Diplomacy.SupportRequestCooldown", ("REMAINING", remainingText)));
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
            && payload.NotificationId.StartsWith("support-request:", StringComparison.Ordinal);
    }

    private static bool MatchesEndpoint(EventParty party, string userId, string? colonyId)
    {
        return string.Equals(party.UserId, userId, StringComparison.Ordinal)
            && string.Equals(party.ColonyId ?? string.Empty, colonyId ?? string.Empty, StringComparison.Ordinal);
    }

    private static AuthoritativeEvent? FindEventByIdempotencyKey(
        IAuthoritativeEventLedger ledger,
        string idempotencyKey)
    {
        return string.IsNullOrWhiteSpace(idempotencyKey)
            ? null
            : ledger.FindByIdempotencyKey(idempotencyKey);
    }

    private static bool IsDiplomacyKind(string? kind)
    {
        return string.Equals(kind, "AllianceRequest", StringComparison.OrdinalIgnoreCase)
            || string.Equals(kind, "AllianceCancellation", StringComparison.OrdinalIgnoreCase)
            || string.Equals(kind, "SupportRequest", StringComparison.OrdinalIgnoreCase)
            || string.Equals(kind, "WarDeclaration", StringComparison.OrdinalIgnoreCase)
            || string.Equals(kind, "PeaceRequest", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSupportRequestKind(string? kind)
    {
        return string.Equals(kind, "SupportRequest", StringComparison.OrdinalIgnoreCase);
    }

    private static ServerEventType ResolveDiplomacyEventType(string kind)
    {
        if (string.Equals(kind, "AllianceRequest", StringComparison.OrdinalIgnoreCase))
        {
            return ServerEventType.AllianceRequest;
        }

        if (string.Equals(kind, "AllianceCancellation", StringComparison.OrdinalIgnoreCase))
        {
            return ServerEventType.AllianceCancellation;
        }

        if (IsSupportRequestKind(kind))
        {
            return ServerEventType.ServerNotification;
        }

        if (string.Equals(kind, "PeaceRequest", StringComparison.OrdinalIgnoreCase))
        {
            return ServerEventType.PeaceRequest;
        }

        return ServerEventType.WarDeclaration;
    }

    private static LedgerEventPayload CreateDiplomacyPayload(
        ServerEventType type,
        string id,
        string? message,
        DateTimeOffset? expiresAtUtc)
    {
        return type switch
        {
            ServerEventType.AllianceRequest => new AllianceRequestEventPayload(id, message, expiresAtUtc),
            ServerEventType.AllianceCancellation => new AllianceCancellationEventPayload(id, message),
            ServerEventType.PeaceRequest => new PeaceRequestEventPayload(id, message, expiresAtUtc),
            ServerEventType.WarDeclaration => new WarDeclarationEventPayload(id, message),
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, T("Diplomacy.NotDiplomacyEventType"))
        };
    }

    private static ServerNotificationEventPayload CreateSupportRequestPayload(CreateDiplomacyEventRequest request)
    {
        string message = string.IsNullOrWhiteSpace(request.Message)
            ? T("Diplomacy.SupportRequestDefaultMessage", ("USER", request.Actor.UserId))
            : request.Message!;
        string fullMessage = T(
            "Diplomacy.SupportRequestMessage",
            ("USER", request.Actor.UserId),
            ("COLONY", request.Actor.ColonyId),
            ("MESSAGE", message));
        return new ServerNotificationEventPayload(
            "support-request:" + request.IdempotencyKey,
            T("Diplomacy.SupportRequestTitle"),
            fullMessage,
            ServerNotificationSeverity.Info,
            FromAdministrator: false,
            AdministratorUserId: null,
            OnlineOnly: false);
    }

    private static bool IsDiplomacyEventType(ServerEventType type)
    {
        return type is ServerEventType.AllianceRequest
            or ServerEventType.AllianceCancellation
            or ServerEventType.WarDeclaration
            or ServerEventType.PeaceRequest;
    }

    private static string? ResolveDiplomacyRelationKind(ServerEventType type, bool accepted)
    {
        if (!accepted)
        {
            return null;
        }

        return type switch
        {
            ServerEventType.AllianceRequest => "Ally",
            ServerEventType.AllianceCancellation => "Neutral",
            ServerEventType.WarDeclaration => "Hostile",
            ServerEventType.PeaceRequest => "Neutral",
            _ => null
        };
    }

    private static string? PersistDiplomacyRelation(
        ClashOfRimNetworkState state,
        AuthoritativeEvent diplomacyEvent,
        bool accepted,
        DateTimeOffset nowUtc)
    {
        string? relationKind = ResolveDiplomacyRelationKind(diplomacyEvent.Type, accepted);
        if (relationKind is null)
        {
            return null;
        }

        DiplomacyRelationRecord record = state.DiplomacyRelations.SetRelationKind(
            diplomacyEvent.Actor.UserId,
            diplomacyEvent.Actor.ColonyId,
            diplomacyEvent.Target.UserId,
            diplomacyEvent.Target.ColonyId,
            relationKind,
            diplomacyEvent.EventId,
            nowUtc);
        return record.RelationKind;
    }

    private static AuthoritativeEvent CreateDiplomacyResponseNotification(
        AuthoritativeEvent diplomacyEvent,
        bool accepted,
        bool originalActorOnline,
        DateTimeOffset createdAtUtc)
    {
        string action = accepted ? T("Diplomacy.ActionAccepted") : T("Diplomacy.ActionRejected");
        string title = diplomacyEvent.Type switch
        {
            ServerEventType.AllianceRequest => T("Diplomacy.AllianceRequestRespondedTitle", ("ACTION", action)),
            ServerEventType.AllianceCancellation => T("Diplomacy.AllianceCancellationConfirmedTitle"),
            ServerEventType.PeaceRequest => T("Diplomacy.PeaceRequestRespondedTitle", ("ACTION", action)),
            ServerEventType.WarDeclaration => T("Diplomacy.WarDeclarationConfirmedTitle"),
            _ => T("Diplomacy.EventProcessedTitle")
        };
        string message = T(
            "Diplomacy.ResponseNotificationMessage",
            ("USER", diplomacyEvent.Target.UserId),
            ("COLONY", diplomacyEvent.Target.ColonyId),
            ("ACTION", action),
            ("EVENT", diplomacyEvent.EventId));
        string notificationId = $"diplomacy-response:{diplomacyEvent.EventId}:{accepted}";
        return AuthoritativeEventFactory.Create(
            ServerEventType.ServerNotification,
            new EventParty("server"),
            diplomacyEvent.Actor,
            notificationId,
            originalActorOnline,
            new ServerNotificationEventPayload(
                notificationId,
                title,
                message,
                accepted ? ServerNotificationSeverity.Info : ServerNotificationSeverity.Warning,
                FromAdministrator: false,
                RelatedEventId: diplomacyEvent.EventId,
                RelatedEventType: diplomacyEvent.Type,
                RelatedUserId: diplomacyEvent.Target.UserId,
                RelatedColonyId: diplomacyEvent.Target.ColonyId,
                RelatedAccepted: accepted),
            createdAtUtc);
    }

    private static void SignalIfCreated(
        ClashOfRimNetworkState state,
        LedgerAppendResult append,
        string targetUserId)
    {
        LogEventAppend(state, append, "signal-target");
        if (append.Created && !string.IsNullOrWhiteSpace(targetUserId))
        {
            state.EventNotifications.SignalUser(targetUserId);
        }
    }

    private static void SignalWorldConfigurationChanged(ClashOfRimNetworkState state, params string?[] additionalUserIds)
    {
        IEnumerable<string> knownUsers = state.Players.List().Select(player => player.UserId);
        IEnumerable<string> additionalUsers = additionalUserIds
            .Where(userId => !string.IsNullOrWhiteSpace(userId))
            .Select(userId => userId!);
        state.WorldConfigurationNotifications.SignalUsers(knownUsers.Concat(additionalUsers));
    }

    private static void RunClientLifecycleHooks(
        ClashOfRimNetworkState state,
        ClientLifecycleEvent lifecycleEvent)
    {
        foreach (ClientLifecycleHook hook in ClientLifecycleHooks)
        {
            hook(state, lifecycleEvent);
        }
    }

    private static void ReconcilePendingConfirmationsOnClientLifecycle(
        ClashOfRimNetworkState state,
        ClientLifecycleEvent lifecycleEvent)
    {
        // Abandonment reconciles and closes the colony in one recovered boundary below.
        if (lifecycleEvent.Kind == ClientLifecycleEventKind.ColonyAbandoned
            || string.IsNullOrWhiteSpace(lifecycleEvent.UserId)
            || string.IsNullOrWhiteSpace(lifecycleEvent.ColonyId))
        {
            return;
        }

        ReconcilePendingConfirmationsForColony(
            state,
            lifecycleEvent.UserId,
            lifecycleEvent.ColonyId,
            lifecycleEvent.OccurredAtUtc,
            forceCancel: lifecycleEvent.Kind is ClientLifecycleEventKind.LoggedIn
                or ClientLifecycleEventKind.InitialWorldSessionPrepared);
    }

    private static PendingConfirmationReconciliationResult ReconcilePendingConfirmationsForColony(
        ClashOfRimNetworkState state,
        string userId,
        string colonyId,
        DateTimeOffset nowUtc,
        bool forceCancel)
    {
        using var mutation = EnterRecoveredSnapshotMutation(state);
        return ReconcilePendingConfirmationsLocked(state, userId, colonyId, nowUtc, forceCancel);
    }

    private static PendingConfirmationReconciliationResult ReconcilePendingConfirmationsLocked(
        ClashOfRimNetworkState state, string userId, string colonyId, DateTimeOffset nowUtc, bool forceCancel)
        => state.PendingConfirmations.Reconcile(userId, colonyId, nowUtc, forceCancel);

    private static bool TryRejectExpiredPendingConfirmationSnapshot(
        ClashOfRimNetworkState state,
        string userId,
        string colonyId,
        DateTimeOffset nowUtc,
        out ProtocolResponse? rejection)
    {
        PendingConfirmationReconciliationResult result = ReconcilePendingConfirmationsForColony(
            state,
            userId,
            colonyId,
            nowUtc,
            forceCancel: false);
        rejection = PendingConfirmationRejection(result);
        return rejection is not null;
    }

    private static ProtocolResponse? PendingConfirmationRejection(PendingConfirmationReconciliationResult result)
        => PendingConfirmationReconciler.Rejection(result);

    private static void ReconcileAbandonedColonyOnClientLifecycle(
        ClashOfRimNetworkState state,
        ClientLifecycleEvent lifecycleEvent)
    {
        if (lifecycleEvent.Kind != ClientLifecycleEventKind.ColonyAbandoned
            || string.IsNullOrWhiteSpace(lifecycleEvent.UserId)
            || string.IsNullOrWhiteSpace(lifecycleEvent.ColonyId))
        {
            return;
        }

        using var mutation = EnterRecoveredSnapshotMutation(state);
        ReconcilePendingConfirmationsLocked(state, lifecycleEvent.UserId, lifecycleEvent.ColonyId,
            lifecycleEvent.OccurredAtUtc, forceCancel: false);
        ReconcileAbandonedColonyLocked(state, lifecycleEvent);
    }

    private static void ReconcileAbandonedColonyLocked(
        ClashOfRimNetworkState state, ClientLifecycleEvent lifecycleEvent)
    {
        string userId = lifecycleEvent.UserId;
        string colonyId = lifecycleEvent.ColonyId;

        state.RaidPreparations.RemoveForColony(userId, colonyId);
        state.RaidProtectionActivations.RemoveForColony(userId, colonyId);
        state.MercenaryContracts.CloseForColony(userId, colonyId);

        LedgerBatch batch = BuildAbandonedColonyEventClosure(state.Ledger, userId, colonyId, out HashSet<string> usersToSignal);
        if (batch.StatusChanges.Count > 0) state.Ledger.ApplyBatch(batch);
        if (usersToSignal.Count > 0) state.EventNotifications.SignalUsers(usersToSignal);
    }

    // Build the complete graph before changing any status. An order and its
    // acceptance memos must not be split by a persistence failure.
    private static LedgerBatch BuildAbandonedColonyEventClosure(
        IAuthoritativeEventLedger ledger, string userId, string colonyId, out HashSet<string> usersToSignal)
    {
        var users = new HashSet<string>(StringComparer.Ordinal);
        var changes = new Dictionary<string, LedgerStatusChange>(StringComparer.Ordinal);
        void Close(AuthoritativeEvent item, ServerEventStatus status)
        {
            changes[item.EventId] = new LedgerStatusChange(item.EventId, item.Status, status);
            AddSignalUser(users, item.Actor.UserId);
            AddSignalUser(users, item.Target.UserId);
        }

        foreach (AuthoritativeEvent item in ledger.ListAll())
        {
            if (IsTerminalForAbandonedColonyCleanup(item.Status) || IsColonyTombstoneEvent(item, userId, colonyId)) continue;
            bool actorMatches = IsVisibleParty(item.Actor, userId, colonyId);
            if (!actorMatches && !IsVisibleParty(item.Target, userId, colonyId)) continue;

            if (item.Type == ServerEventType.Trade && actorMatches
                && item.Payload is TradeEventPayload { Stage: TradeStage.MarketOrder })
            {
                foreach (AuthoritativeEvent memo in FindActiveTradeAcceptanceMemos(ledger, item.EventId))
                    Close(memo, ServerEventStatus.Cancelled);
            }
            Close(item, item.Type == ServerEventType.Raid && actorMatches ? ServerEventStatus.Failed : ServerEventStatus.Cancelled);
        }
        usersToSignal = users;
        return new LedgerBatch(Array.Empty<AuthoritativeEvent>(), changes.Values.ToArray());
    }

    private static bool IsTerminalForAbandonedColonyCleanup(ServerEventStatus status)
    {
        return status is ServerEventStatus.AppliedToSnapshot
            or ServerEventStatus.RejectedByTarget
            or ServerEventStatus.Conflict
            or ServerEventStatus.Cancelled
            or ServerEventStatus.Failed;
    }

    private static bool IsColonyTombstoneEvent(AuthoritativeEvent ledgerEvent, string userId, string colonyId)
    {
        return ledgerEvent.Payload is ServerNotificationEventPayload payload
            && string.Equals(payload.NotificationId, BuildColonyTombstoneNotificationId(userId, colonyId), StringComparison.Ordinal);
    }

    private static void AddSignalUser(HashSet<string> users, string? userId)
    {
        if (!string.IsNullOrWhiteSpace(userId) && !string.Equals(userId, "server", StringComparison.Ordinal))
        {
            users.Add(userId!);
        }
    }

    private delegate void ClientLifecycleHook(
        ClashOfRimNetworkState state,
        ClientLifecycleEvent lifecycleEvent);


}
