using System.Globalization;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Network.Modules.Diplomacy;

internal static class DiplomacyPolicy
{
    internal static ProtocolResponse? ValidateDiplomacyCreateRequest(CreateDiplomacyEventRequest request)
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
                ServerText.Format("Diplomacy.CreateMissingFields"));
        }

        if (!IsDiplomacyKind(request.Kind))
        {
            return ProtocolResponse.Reject(
                ProtocolErrorCode.ValidationFailed,
                ServerText.Format("Diplomacy.UnknownKind"));
        }

        if (string.Equals(request.Actor.UserId, request.Target.UserId, StringComparison.Ordinal)
            && string.Equals(request.Actor.ColonyId, request.Target.ColonyId, StringComparison.Ordinal))
        {
            return ProtocolResponse.Reject(
                ProtocolErrorCode.ValidationFailed,
                ServerText.Format("Diplomacy.CannotTargetSelf"));
        }

        return null;
    }

    internal static ProtocolResponse? ValidateDiplomacyRelationTransition(
        ServerEventType type,
        string currentRelationKind,
        DiplomacyRelationRecord? currentRelation,
        DateTimeOffset nowUtc,
        TimeSpan cooldown)
    {
        ProtocolResponse? transitionValidation = type switch
        {
            ServerEventType.AllianceRequest when !string.Equals(currentRelationKind, DiplomacyRelationRegistry.RelationNeutral, StringComparison.Ordinal) =>
                ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, ServerText.Format("Diplomacy.AllianceRequiresNeutral")),
            ServerEventType.AllianceCancellation when !string.Equals(currentRelationKind, DiplomacyRelationRegistry.RelationAlly, StringComparison.Ordinal) =>
                ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, ServerText.Format("Diplomacy.CancelAllianceRequiresAlly")),
            ServerEventType.WarDeclaration when string.Equals(currentRelationKind, DiplomacyRelationRegistry.RelationAlly, StringComparison.Ordinal) =>
                ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, ServerText.Format("Diplomacy.CannotDeclareWarOnAlly")),
            ServerEventType.WarDeclaration when string.Equals(currentRelationKind, DiplomacyRelationRegistry.RelationHostile, StringComparison.Ordinal) =>
                ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, ServerText.Format("Diplomacy.AlreadyHostile")),
            ServerEventType.PeaceRequest when !string.Equals(currentRelationKind, DiplomacyRelationRegistry.RelationHostile, StringComparison.Ordinal) =>
                ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, ServerText.Format("Diplomacy.PeaceRequiresHostile")),
            _ => null
        };
        if (transitionValidation is not null)
        {
            return transitionValidation;
        }

        return ValidateDiplomacyRelationCooldown(currentRelation, nowUtc, cooldown);
    }

    internal static ProtocolResponse? ValidateDiplomacyRelationCooldown(
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
            ? ServerText.Format("Diplomacy.TimeHours", ("VALUE", Math.Ceiling(remaining.TotalHours).ToString("0", CultureInfo.InvariantCulture)))
            : ServerText.Format("Diplomacy.TimeMinutes", ("VALUE", Math.Ceiling(remaining.TotalMinutes).ToString("0", CultureInfo.InvariantCulture)));
        return ProtocolResponse.Reject(
            ProtocolErrorCode.ServerRejected,
            ServerText.Format("Diplomacy.RelationCooldown", ("REMAINING", remainingText)));
    }

    internal static ProtocolResponse? ValidateSupportRequestCooldown(DateTimeOffset? latestAtUtc, DateTimeOffset nowUtc, TimeSpan cooldown)
    {
        if (latestAtUtc is null || cooldown <= TimeSpan.Zero) return null;
        DateTimeOffset availableAtUtc = latestAtUtc.Value + cooldown;
        if (availableAtUtc <= nowUtc) return null;
        TimeSpan remaining = availableAtUtc - nowUtc;
        string remainingText = remaining.TotalMinutes >= 1
            ? ServerText.Format("Diplomacy.TimeMinutes", ("VALUE", Math.Ceiling(remaining.TotalMinutes).ToString("0", CultureInfo.InvariantCulture)))
            : ServerText.Format("Diplomacy.TimeSeconds", ("VALUE", Math.Ceiling(remaining.TotalSeconds).ToString("0", CultureInfo.InvariantCulture)));
        return ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected,
            ServerText.Format("Diplomacy.SupportRequestCooldown", ("REMAINING", remainingText)));
    }

    internal const string SupportRequestKind = "SupportRequest";
    internal const string SupportRequestPrefix = "support-request:";
    private static readonly IReadOnlyDictionary<string, ServerEventType> Kinds = new Dictionary<string, ServerEventType>(StringComparer.OrdinalIgnoreCase)
    {
        ["AllianceRequest"] = ServerEventType.AllianceRequest,
        ["AllianceCancellation"] = ServerEventType.AllianceCancellation,
        ["WarDeclaration"] = ServerEventType.WarDeclaration,
        ["PeaceRequest"] = ServerEventType.PeaceRequest,
        [SupportRequestKind] = ServerEventType.ServerNotification
    };
    internal static bool IsDiplomacyKind(string? kind) => kind is not null && Kinds.ContainsKey(kind);
    internal static bool IsSupportRequestKind(string? kind) => string.Equals(kind, SupportRequestKind, StringComparison.OrdinalIgnoreCase);
    internal static ServerEventType ResolveDiplomacyEventType(string kind) => Kinds[kind];

    internal static LedgerEventPayload CreateDiplomacyPayload(
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
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, ServerText.Format("Diplomacy.NotDiplomacyEventType"))
        };
    }

    internal static ServerNotificationEventPayload CreateSupportRequestPayload(CreateDiplomacyEventRequest request)
    {
        string message = string.IsNullOrWhiteSpace(request.Message)
            ? ServerText.Format("Diplomacy.SupportRequestDefaultMessage", ("USER", request.Actor.UserId))
            : request.Message!;
        string fullMessage = ServerText.Format(
            "Diplomacy.SupportRequestMessage",
            ("USER", request.Actor.UserId),
            ("COLONY", request.Actor.ColonyId),
            ("MESSAGE", message));
        return new ServerNotificationEventPayload(
            SupportRequestPrefix + request.IdempotencyKey,
            ServerText.Format("Diplomacy.SupportRequestTitle"),
            fullMessage,
            ServerNotificationSeverity.Info,
            FromAdministrator: false,
            AdministratorUserId: null,
            OnlineOnly: false);
    }

    internal static string? ResolveDiplomacyRelationKind(ServerEventType type, bool accepted)
    {
        if (!accepted)
        {
            return null;
        }

        return type switch
        {
            ServerEventType.AllianceRequest => DiplomacyRelationRegistry.RelationAlly,
            ServerEventType.AllianceCancellation => DiplomacyRelationRegistry.RelationNeutral,
            ServerEventType.WarDeclaration => DiplomacyRelationRegistry.RelationHostile,
            ServerEventType.PeaceRequest => DiplomacyRelationRegistry.RelationNeutral,
            _ => null
        };
    }

    internal static string ResponseNotificationKey(string sourceEventId, bool accepted) => $"diplomacy-response:{sourceEventId}:{accepted}";

    internal static bool IsSupportRequestNotification(AuthoritativeEvent item) => item.Type == ServerEventType.ServerNotification
        && item.Payload is ServerNotificationEventPayload payload
        && payload.NotificationId == SupportRequestPrefix + item.IdempotencyKey;

    internal static AuthoritativeEvent CreateDiplomacyResponseNotification(
        AuthoritativeEvent diplomacyEvent,
        bool accepted,
        bool originalActorOnline,
        DateTimeOffset createdAtUtc)
    {
        string action = accepted ? ServerText.Format("Diplomacy.ActionAccepted") : ServerText.Format("Diplomacy.ActionRejected");
        string title = diplomacyEvent.Type switch
        {
            ServerEventType.AllianceRequest => ServerText.Format("Diplomacy.AllianceRequestRespondedTitle", ("ACTION", action)),
            ServerEventType.AllianceCancellation => ServerText.Format("Diplomacy.AllianceCancellationConfirmedTitle"),
            ServerEventType.PeaceRequest => ServerText.Format("Diplomacy.PeaceRequestRespondedTitle", ("ACTION", action)),
            ServerEventType.WarDeclaration => ServerText.Format("Diplomacy.WarDeclarationConfirmedTitle"),
            _ => ServerText.Format("Diplomacy.EventProcessedTitle")
        };
        string message = ServerText.Format(
            "Diplomacy.ResponseNotificationMessage",
            ("USER", diplomacyEvent.Target.UserId),
            ("COLONY", diplomacyEvent.Target.ColonyId),
            ("ACTION", action),
            ("EVENT", diplomacyEvent.EventId));
        string notificationId = ResponseNotificationKey(diplomacyEvent.EventId, accepted);
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

    internal static bool MatchesEndpoint(EventParty party, string userId, string? colonyId)
    {
        return string.Equals(party.UserId, userId, StringComparison.Ordinal)
            && string.Equals(party.ColonyId ?? string.Empty, colonyId ?? string.Empty, StringComparison.Ordinal);
    }
}
