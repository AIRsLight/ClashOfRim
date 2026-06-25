using AIRsLight.ClashOfRim.Compatibility;
using System.Runtime.Serialization;
using System.Collections.Generic;

namespace AIRsLight.ClashOfRim.ClientNetwork;

[DataContract]
public sealed class ModProtocolResponseDto
{
    [DataMember(Name = "accepted")]
    public bool Accepted { get; set; }

    [DataMember(Name = "errorCode")]
    public int ErrorCode { get; set; }

    [DataMember(Name = "message")]
    public string? Message { get; set; }

}

[DataContract]
public sealed class ModLoginRequestDto
{
    [DataMember(Name = "protocolVersion")]
    public string ProtocolVersion { get; set; } = ClashOfRimVersion.ProtocolVersion;

    [DataMember(Name = "userId")]
    public string UserId { get; set; } = string.Empty;

    [DataMember(Name = "colonyId")]
    public string? ColonyId { get; set; }

    [DataMember(Name = "currentSnapshotId")]
    public string? CurrentSnapshotId { get; set; }

    [DataMember(Name = "compatibilityDigest")]
    public string CompatibilityDigest { get; set; } = string.Empty;

    [DataMember(Name = "steamAuthTicket")]
    public string? SteamAuthTicket { get; set; }

    [DataMember(Name = "password")]
    public string? Password { get; set; }

    [DataMember(Name = "compatibilityManifestJson")]
    public string? CompatibilityManifestJson { get; set; }

    [DataMember(Name = "compatibilityManifestId")]
    public string? CompatibilityManifestId { get; set; }

    [DataMember(Name = "compatibilityManifestSummaryJson")]
    public string? CompatibilityManifestSummaryJson { get; set; }
}

[DataContract]
public sealed class ModLoginResponseDto
{
    [DataMember(Name = "result")]
    public ModProtocolResponseDto? Result { get; set; }

    [DataMember(Name = "sessionId")]
    public string? SessionId { get; set; }

    [DataMember(Name = "authToken")]
    public string? AuthToken { get; set; }

    [DataMember(Name = "serverProtocolVersion")]
    public string? ServerProtocolVersion { get; set; }

    [DataMember(Name = "eventQueue")]
    public ModEventQueueSummaryDto? EventQueue { get; set; }

    [DataMember(Name = "worldMapMarkers")]
    public ModWorldMapMarkerDeliveryDto? WorldMapMarkers { get; set; }

    [DataMember(Name = "serverCompatibilityManifestJson")]
    public string? ServerCompatibilityManifestJson { get; set; }

    [DataMember(Name = "compatibilityIssues")]
    public List<ModCompatibilityIssueDto> CompatibilityIssues { get; set; } = new();

    [DataMember(Name = "canOverrideCompatibilityBaseline")]
    public bool CanOverrideCompatibilityBaseline { get; set; }

    [DataMember(Name = "isAdministrator")]
    public bool IsAdministrator { get; set; }

    [DataMember(Name = "authenticatedUserId")]
    public string? AuthenticatedUserId { get; set; }

    [DataMember(Name = "displayName")]
    public string? DisplayName { get; set; }

    [DataMember(Name = "requiresFullCompatibilityManifest")]
    public bool RequiresFullCompatibilityManifest { get; set; }

    [DataMember(Name = "requestedCompatibilityPackageIds")]
    public List<string> RequestedCompatibilityPackageIds { get; set; } = new();
}

[DataContract]
public sealed class ModCompatibilityIssueDto
{
    [DataMember(Name = "severity")]
    public string Severity { get; set; } = string.Empty;

    [DataMember(Name = "code")]
    public string Code { get; set; } = string.Empty;

    [DataMember(Name = "message")]
    public string Message { get; set; } = string.Empty;

    [DataMember(Name = "subject")]
    public string? Subject { get; set; }
}

[DataContract]
public sealed class ModOverrideCompatibilityBaselineRequestDto
{
    [DataMember(Name = "userId")]
    public string UserId { get; set; } = string.Empty;

    [DataMember(Name = "colonyId")]
    public string? ColonyId { get; set; }

    [DataMember(Name = "compatibilityManifestJson")]
    public string CompatibilityManifestJson { get; set; } = string.Empty;

    [DataMember(Name = "steamAuthTicket")]
    public string? SteamAuthTicket { get; set; }

    [DataMember(Name = "password")]
    public string? Password { get; set; }
}

[DataContract]
public sealed class ModChangeOfflinePasswordRequestDto
{
    [DataMember(Name = "userId")]
    public string UserId { get; set; } = string.Empty;

    [DataMember(Name = "colonyId")]
    public string ColonyId { get; set; } = string.Empty;

    [DataMember(Name = "authToken")]
    public string? AuthToken { get; set; }

    [DataMember(Name = "currentPassword")]
    public string? CurrentPassword { get; set; }

    [DataMember(Name = "newPassword")]
    public string? NewPassword { get; set; }
}

[DataContract]
public sealed class ModChangeOfflinePasswordResponseDto
{
    [DataMember(Name = "result")]
    public ModProtocolResponseDto? Result { get; set; }
}

[DataContract]
public sealed class ModOverrideCompatibilityBaselineResponseDto
{
    [DataMember(Name = "result")]
    public ModProtocolResponseDto? Result { get; set; }

    [DataMember(Name = "serverCompatibilityManifestJson")]
    public string? ServerCompatibilityManifestJson { get; set; }

    [DataMember(Name = "updatedAtUtc")]
    public string? UpdatedAtUtc { get; set; }
}

[DataContract]
public sealed class ModMaintainPresenceRequestDto
{
    [DataMember(Name = "userId")]
    public string UserId { get; set; } = string.Empty;

    [DataMember(Name = "colonyId")]
    public string ColonyId { get; set; } = string.Empty;

    [DataMember(Name = "currentSnapshotId")]
    public string? CurrentSnapshotId { get; set; }

    [DataMember(Name = "sessionId")]
    public string? SessionId { get; set; }
}

[DataContract]
public sealed class ModMaintainPresenceResponseDto
{
    [DataMember(Name = "result")]
    public ModProtocolResponseDto? Result { get; set; }

    [DataMember(Name = "userId")]
    public string UserId { get; set; } = string.Empty;

    [DataMember(Name = "online")]
    public bool Online { get; set; }
}

[DataContract]
public sealed class ModLogoutRequestDto
{
    [DataMember(Name = "userId")]
    public string UserId { get; set; } = string.Empty;

    [DataMember(Name = "colonyId")]
    public string ColonyId { get; set; } = string.Empty;

    [DataMember(Name = "sessionId")]
    public string? SessionId { get; set; }
}

[DataContract]
public sealed class ModLogoutResponseDto
{
    [DataMember(Name = "result")]
    public ModProtocolResponseDto? Result { get; set; }

    [DataMember(Name = "userId")]
    public string UserId { get; set; } = string.Empty;

    [DataMember(Name = "disconnected")]
    public bool Disconnected { get; set; }
}

[DataContract]
public sealed class ModListPlayersRequestDto
{
    [DataMember(Name = "userId")]
    public string UserId { get; set; } = string.Empty;

    [DataMember(Name = "colonyId")]
    public string ColonyId { get; set; } = string.Empty;

    [DataMember(Name = "currentSnapshotId")]
    public string? CurrentSnapshotId { get; set; }
}

[DataContract]
public sealed class ModPlayerSummaryDto
{
    [DataMember(Name = "userId")]
    public string UserId { get; set; } = string.Empty;

    [DataMember(Name = "colonyId")]
    public string ColonyId { get; set; } = string.Empty;

    [DataMember(Name = "currentSnapshotId")]
    public string? CurrentSnapshotId { get; set; }

    [DataMember(Name = "online")]
    public bool Online { get; set; }

    [DataMember(Name = "displayName")]
    public string? DisplayName { get; set; }

    [DataMember(Name = "relationKind")]
    public string? RelationKind { get; set; }

    [DataMember(Name = "latestSnapshotWealth")]
    public int? LatestSnapshotWealth { get; set; }
}

[DataContract]
public sealed class ModListPlayersResponseDto
{
    [DataMember(Name = "result")]
    public ModProtocolResponseDto? Result { get; set; }

    [DataMember(Name = "players")]
    public List<ModPlayerSummaryDto> Players { get; set; } = new();
}
