using System.Runtime.Serialization;
using System.Collections.Generic;

namespace AIRsLight.ClashOfRim.ClientNetwork;

[DataContract]
public sealed class ModEventReferenceDto
{
    [DataMember(Name = "eventId")]
    public string EventId { get; set; } = string.Empty;

    [DataMember(Name = "eventType")]
    public string EventType { get; set; } = string.Empty;

    [DataMember(Name = "status")]
    public string Status { get; set; } = string.Empty;

    [DataMember(Name = "deliverySemantics")]
    public int DeliverySemantics { get; set; }

    [DataMember(Name = "requiresSnapshotConfirmation")]
    public bool RequiresSnapshotConfirmation { get; set; }
}

[DataContract]
public sealed class ModEventQueueSummaryDto
{
    [DataMember(Name = "directlyProcessable")]
    public List<ModEventReferenceDto> DirectlyProcessable { get; set; } = new();

    [DataMember(Name = "waitingForUserChoice")]
    public List<ModEventReferenceDto> WaitingForUserChoice { get; set; } = new();

    [DataMember(Name = "deliveredUnconfirmed")]
    public List<ModEventReferenceDto> DeliveredUnconfirmed { get; set; } = new();

    [DataMember(Name = "conflicts")]
    public List<ModEventReferenceDto> Conflicts { get; set; } = new();

    [DataMember(Name = "rejected")]
    public List<ModEventReferenceDto> Rejected { get; set; } = new();
}

[DataContract]
public sealed class ModPullPendingEventsRequestDto
{
    [DataMember(Name = "userId")]
    public string UserId { get; set; } = string.Empty;

    [DataMember(Name = "colonyId")]
    public string ColonyId { get; set; } = string.Empty;

    [DataMember(Name = "currentSnapshotId")]
    public string CurrentSnapshotId { get; set; } = string.Empty;
}

[DataContract]
public sealed class ModPullPendingEventsResponseDto
{
    [DataMember(Name = "result")]
    public ModProtocolResponseDto? Result { get; set; }

    [DataMember(Name = "eventQueue")]
    public ModEventQueueSummaryDto? EventQueue { get; set; }
}

[DataContract]
public sealed class ModWaitForEventsRequestDto
{
    [DataMember(Name = "userId")]
    public string UserId { get; set; } = string.Empty;

    [DataMember(Name = "colonyId")]
    public string ColonyId { get; set; } = string.Empty;

    [DataMember(Name = "currentSnapshotId")]
    public string CurrentSnapshotId { get; set; } = string.Empty;

    [DataMember(Name = "knownNotificationVersion")]
    public long KnownNotificationVersion { get; set; }

    [DataMember(Name = "timeoutSeconds")]
    public int TimeoutSeconds { get; set; }
}

[DataContract]
public sealed class ModWaitForEventsResponseDto
{
    [DataMember(Name = "result")]
    public ModProtocolResponseDto? Result { get; set; }

    [DataMember(Name = "changed")]
    public bool Changed { get; set; }

    [DataMember(Name = "notificationVersion")]
    public long NotificationVersion { get; set; }

    [DataMember(Name = "eventQueue")]
    public ModEventQueueSummaryDto? EventQueue { get; set; }
}

[DataContract]
public sealed class ModPullEventDetailsRequestDto
{
    [DataMember(Name = "userId")]
    public string UserId { get; set; } = string.Empty;

    [DataMember(Name = "colonyId")]
    public string ColonyId { get; set; } = string.Empty;

    [DataMember(Name = "currentSnapshotId")]
    public string CurrentSnapshotId { get; set; } = string.Empty;

    [DataMember(Name = "eventIds")]
    public List<string> EventIds { get; set; } = new();
}

[DataContract]
public sealed class ModPullEventDetailsResponseDto
{
    [DataMember(Name = "result")]
    public ModProtocolResponseDto? Result { get; set; }

    [DataMember(Name = "events")]
    public List<ModEventDetailDto> Events { get; set; } = new();
}

[DataContract]
public sealed class ModRejectGiftRequestDto
{
    [DataMember(Name = "eventId")]
    public string EventId { get; set; } = string.Empty;

    [DataMember(Name = "userId")]
    public string UserId { get; set; } = string.Empty;

    [DataMember(Name = "colonyId")]
    public string ColonyId { get; set; } = string.Empty;

    [DataMember(Name = "currentSnapshotId")]
    public string CurrentSnapshotId { get; set; } = string.Empty;

    [DataMember(Name = "reason")]
    public string? Reason { get; set; }
}

[DataContract]
public sealed class ModRejectGiftResponseDto
{
    [DataMember(Name = "result")]
    public ModProtocolResponseDto? Result { get; set; }

    [DataMember(Name = "eventId")]
    public string? EventId { get; set; }

    [DataMember(Name = "returnEventId")]
    public string? ReturnEventId { get; set; }

    [DataMember(Name = "returnEventCreated")]
    public bool ReturnEventCreated { get; set; }
}

[DataContract]
public sealed class ModRejectSupportPawnRequestDto
{
    [DataMember(Name = "eventId")]
    public string EventId { get; set; } = string.Empty;

    [DataMember(Name = "userId")]
    public string UserId { get; set; } = string.Empty;

    [DataMember(Name = "colonyId")]
    public string ColonyId { get; set; } = string.Empty;

    [DataMember(Name = "currentSnapshotId")]
    public string CurrentSnapshotId { get; set; } = string.Empty;

    [DataMember(Name = "reason")]
    public string? Reason { get; set; }
}

[DataContract]
public sealed class ModRejectSupportPawnResponseDto
{
    [DataMember(Name = "result")]
    public ModProtocolResponseDto? Result { get; set; }

    [DataMember(Name = "eventId")]
    public string? EventId { get; set; }

    [DataMember(Name = "returnEventId")]
    public string? ReturnEventId { get; set; }

    [DataMember(Name = "returnEventCreated")]
    public bool ReturnEventCreated { get; set; }
}

[DataContract]
public sealed class ModFinishSupportPawnRequestDto
{
    [DataMember(Name = "idempotencyKey")]
    public string IdempotencyKey { get; set; } = string.Empty;

    [DataMember(Name = "eventId")]
    public string EventId { get; set; } = string.Empty;

    [DataMember(Name = "userId")]
    public string UserId { get; set; } = string.Empty;

    [DataMember(Name = "colonyId")]
    public string ColonyId { get; set; } = string.Empty;

    [DataMember(Name = "currentSnapshotId")]
    public string CurrentSnapshotId { get; set; } = string.Empty;

    [DataMember(Name = "finishReason")]
    public string FinishReason { get; set; } = string.Empty;

    [DataMember(Name = "pawnGlobalKey")]
    public string PawnGlobalKey { get; set; } = string.Empty;

    [DataMember(Name = "pawnName")]
    public string? PawnName { get; set; }

    [DataMember(Name = "pawnDead")]
    public bool PawnDead { get; set; }

    [DataMember(Name = "pawnPackage")]
    public ModPawnExchangePackageDto? PawnPackage { get; set; }
}

[DataContract]
public sealed class ModFinishSupportPawnResponseDto
{
    [DataMember(Name = "result")]
    public ModProtocolResponseDto? Result { get; set; }

    [DataMember(Name = "eventId")]
    public string? EventId { get; set; }

    [DataMember(Name = "returnEventId")]
    public string? ReturnEventId { get; set; }

    [DataMember(Name = "notificationEventId")]
    public string? NotificationEventId { get; set; }

    [DataMember(Name = "created")]
    public bool Created { get; set; }
}

[DataContract]
public sealed class ModEventCreationResponseDto
{
    [DataMember(Name = "result")]
    public ModProtocolResponseDto? Result { get; set; }

    [DataMember(Name = "eventId")]
    public string? EventId { get; set; }

    [DataMember(Name = "deliverySemantics")]
    public int DeliverySemantics { get; set; }

    [DataMember(Name = "appliedSnapshotId")]
    public string? AppliedSnapshotId { get; set; }

    [DataMember(Name = "nextLineageToken")]
    public string? NextLineageToken { get; set; }

    [DataMember(Name = "raidStartedAtUtc")]
    public string? RaidStartedAtUtc { get; set; }

    [DataMember(Name = "raidDeadlineUtc")]
    public string? RaidDeadlineUtc { get; set; }

    [DataMember(Name = "raidFinalDeadlineUtc")]
    public string? RaidFinalDeadlineUtc { get; set; }
}

[DataContract]
public sealed class ModEventTargetContextDto
{
    [DataMember(Name = "worldObjectId")]
    public string? WorldObjectId { get; set; }

    [DataMember(Name = "mapUniqueId")]
    public string? MapUniqueId { get; set; }

    [DataMember(Name = "tile")]
    public int? Tile { get; set; }

    [DataMember(Name = "landingMode")]
    public string LandingMode { get; set; } = string.Empty;
}

[DataContract]
public sealed class ModEventDetailDto
{
    [DataMember(Name = "eventId")]
    public string EventId { get; set; } = string.Empty;

    [DataMember(Name = "eventType")]
    public string EventType { get; set; } = string.Empty;

    [DataMember(Name = "status")]
    public string Status { get; set; } = string.Empty;

    [DataMember(Name = "actor")]
    public ModProtocolIdentityDto? Actor { get; set; }

    [DataMember(Name = "target")]
    public ModProtocolIdentityDto? Target { get; set; }

    [DataMember(Name = "targetContext")]
    public ModEventTargetContextDto? TargetContext { get; set; }

    [DataMember(Name = "payloadType")]
    public string PayloadType { get; set; } = string.Empty;

    [DataMember(Name = "payloadSummary")]
    public string PayloadSummary { get; set; } = string.Empty;
}

[DataContract]
public sealed class ModConfirmEventApplicationRequestDto
{
    [DataMember(Name = "idempotencyKey")]
    public string IdempotencyKey { get; set; } = string.Empty;

    [DataMember(Name = "eventId")]
    public string EventId { get; set; } = string.Empty;

    [DataMember(Name = "sourceEventId")]
    public string? SourceEventId { get; set; }

    [DataMember(Name = "userId")]
    public string UserId { get; set; } = string.Empty;

    [DataMember(Name = "colonyId")]
    public string ColonyId { get; set; } = string.Empty;

    [DataMember(Name = "baseSnapshotId")]
    public string BaseSnapshotId { get; set; } = string.Empty;

    [DataMember(Name = "confirmedSnapshot")]
    public ModSnapshotPackageMetadataDto? ConfirmedSnapshot { get; set; }

    [DataMember(Name = "clientApplicationResult")]
    public string ClientApplicationResult { get; set; } = string.Empty;

    [DataMember(Name = "authToken")]
    public string? AuthToken { get; set; }
}

[DataContract]
public sealed class ModConfirmEventApplicationResponseDto
{
    [DataMember(Name = "result")]
    public ModProtocolResponseDto? Result { get; set; }

    [DataMember(Name = "eventId")]
    public string? EventId { get; set; }

    [DataMember(Name = "appliedSnapshotId")]
    public string? AppliedSnapshotId { get; set; }

    [DataMember(Name = "serverValidationResult")]
    public string? ServerValidationResult { get; set; }

    [DataMember(Name = "nextLineageToken")]
    public string? NextLineageToken { get; set; }
}

[DataContract]
public sealed class ModConfirmEventApplicationEntryDto
{
    [DataMember(Name = "eventId")]
    public string EventId { get; set; } = string.Empty;

    [DataMember(Name = "sourceEventId")]
    public string? SourceEventId { get; set; }

    [DataMember(Name = "clientApplicationResult")]
    public string ClientApplicationResult { get; set; } = string.Empty;
}

[DataContract]
public sealed class ModConfirmEventApplicationsRequestDto
{
    [DataMember(Name = "idempotencyKey")]
    public string IdempotencyKey { get; set; } = string.Empty;

    [DataMember(Name = "userId")]
    public string UserId { get; set; } = string.Empty;

    [DataMember(Name = "colonyId")]
    public string ColonyId { get; set; } = string.Empty;

    [DataMember(Name = "baseSnapshotId")]
    public string BaseSnapshotId { get; set; } = string.Empty;

    [DataMember(Name = "confirmedSnapshot")]
    public ModSnapshotPackageMetadataDto? ConfirmedSnapshot { get; set; }

    [DataMember(Name = "applications")]
    public List<ModConfirmEventApplicationEntryDto> Applications { get; set; } = new();

    [DataMember(Name = "authToken")]
    public string? AuthToken { get; set; }
}

[DataContract]
public sealed class ModConfirmEventApplicationResultDto
{
    [DataMember(Name = "eventId")]
    public string? EventId { get; set; }

    [DataMember(Name = "result")]
    public ModProtocolResponseDto? Result { get; set; }

    [DataMember(Name = "appliedSnapshotId")]
    public string? AppliedSnapshotId { get; set; }

    [DataMember(Name = "serverValidationResult")]
    public string? ServerValidationResult { get; set; }
}

[DataContract]
public sealed class ModConfirmEventApplicationsResponseDto
{
    [DataMember(Name = "result")]
    public ModProtocolResponseDto? Result { get; set; }

    [DataMember(Name = "appliedSnapshotId")]
    public string? AppliedSnapshotId { get; set; }

    [DataMember(Name = "applications")]
    public List<ModConfirmEventApplicationResultDto> Applications { get; set; } = new();

    [DataMember(Name = "nextLineageToken")]
    public string? NextLineageToken { get; set; }
}

[DataContract]
public sealed class ModReportEventApplicationFailureRequestDto
{
    [DataMember(Name = "idempotencyKey")]
    public string IdempotencyKey { get; set; } = string.Empty;

    [DataMember(Name = "eventId")]
    public string EventId { get; set; } = string.Empty;

    [DataMember(Name = "sourceEventId")]
    public string? SourceEventId { get; set; }

    [DataMember(Name = "userId")]
    public string UserId { get; set; } = string.Empty;

    [DataMember(Name = "colonyId")]
    public string ColonyId { get; set; } = string.Empty;

    [DataMember(Name = "currentSnapshotId")]
    public string CurrentSnapshotId { get; set; } = string.Empty;

    [DataMember(Name = "reason")]
    public string Reason { get; set; } = string.Empty;

    [DataMember(Name = "authToken")]
    public string? AuthToken { get; set; }
}

[DataContract]
public sealed class ModReportEventApplicationFailureResponseDto
{
    [DataMember(Name = "result")]
    public ModProtocolResponseDto? Result { get; set; }

    [DataMember(Name = "eventId")]
    public string EventId { get; set; } = string.Empty;

    [DataMember(Name = "terminalStatus")]
    public string TerminalStatus { get; set; } = string.Empty;

    [DataMember(Name = "affectedEventCount")]
    public int AffectedEventCount { get; set; }
}
