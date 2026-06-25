using System.Runtime.Serialization;
using System.Collections.Generic;

namespace AIRsLight.ClashOfRim.ClientNetwork;

[DataContract]
public sealed class ModCreateDiplomacyEventRequestDto
{
    [DataMember(Name = "idempotencyKey")]
    public string IdempotencyKey { get; set; } = string.Empty;

    [DataMember(Name = "actor")]
    public ModProtocolIdentityDto? Actor { get; set; }

    [DataMember(Name = "target")]
    public ModProtocolIdentityDto? Target { get; set; }

    [DataMember(Name = "kind")]
    public string Kind { get; set; } = string.Empty;

    [DataMember(Name = "message")]
    public string? Message { get; set; }

    [DataMember(Name = "expiresAtUtc")]
    public string? ExpiresAtUtc { get; set; }
}

[DataContract]
public sealed class ModRespondDiplomacyEventRequestDto
{
    [DataMember(Name = "eventId")]
    public string EventId { get; set; } = string.Empty;

    [DataMember(Name = "userId")]
    public string UserId { get; set; } = string.Empty;

    [DataMember(Name = "colonyId")]
    public string ColonyId { get; set; } = string.Empty;

    [DataMember(Name = "currentSnapshotId")]
    public string CurrentSnapshotId { get; set; } = string.Empty;

    [DataMember(Name = "accepted")]
    public bool Accepted { get; set; }

    [DataMember(Name = "reason")]
    public string? Reason { get; set; }
}

[DataContract]
public sealed class ModDiplomacyEventResponseDto
{
    [DataMember(Name = "result")]
    public ModProtocolResponseDto? Result { get; set; }

    [DataMember(Name = "eventId")]
    public string? EventId { get; set; }

    [DataMember(Name = "notificationEventId")]
    public string? NotificationEventId { get; set; }

    [DataMember(Name = "relationKind")]
    public string? RelationKind { get; set; }
}
