using System;
using System.Collections.Generic;

namespace AIRsLight.ClashOfRim.Protocol;

public sealed class LoginRequest
{
    public LoginRequest(
        string protocolVersion,
        string userId,
        string colonyId,
        string? currentSnapshotId,
        string compatibilityDigest,
        string? steamAuthTicket = null,
        string? password = null,
        string? compatibilityManifestJson = null,
        string? compatibilityManifestId = null,
        string? compatibilityManifestSummaryJson = null)
    {
        ProtocolVersion = protocolVersion;
        UserId = userId;
        ColonyId = colonyId;
        CurrentSnapshotId = currentSnapshotId;
        CompatibilityDigest = compatibilityDigest;
        SteamAuthTicket = steamAuthTicket;
        Password = password;
        CompatibilityManifestJson = compatibilityManifestJson;
        CompatibilityManifestId = compatibilityManifestId;
        CompatibilityManifestSummaryJson = compatibilityManifestSummaryJson;
    }

    public string ProtocolVersion { get; }

    public string UserId { get; }

    public string ColonyId { get; }

    public string? CurrentSnapshotId { get; }

    public string CompatibilityDigest { get; }

    public string? SteamAuthTicket { get; }

    public string? Password { get; }

    public string? CompatibilityManifestJson { get; }

    public string? CompatibilityManifestId { get; }

    public string? CompatibilityManifestSummaryJson { get; }
}

public sealed class LoginResponse
{
    public LoginResponse(
        ProtocolResponse result,
        string? sessionId,
        string serverProtocolVersion,
        EventQueueSummaryDto? eventQueue,
        WorldMapMarkerDeliveryDto? worldMapMarkers,
        IReadOnlyList<ServerNotificationDto> notifications,
        string? authToken = null,
        string? serverCompatibilityManifestJson = null,
        IReadOnlyList<CompatibilityIssueDto>? compatibilityIssues = null,
        bool canOverrideCompatibilityBaseline = false,
        bool isAdministrator = false,
        string? authenticatedUserId = null,
        string? displayName = null,
        bool requiresFullCompatibilityManifest = false,
        IReadOnlyList<string>? requestedCompatibilityPackageIds = null)
    {
        Result = result;
        SessionId = sessionId;
        ServerProtocolVersion = serverProtocolVersion;
        EventQueue = eventQueue;
        WorldMapMarkers = worldMapMarkers;
        Notifications = notifications;
        AuthToken = authToken;
        ServerCompatibilityManifestJson = serverCompatibilityManifestJson;
        CompatibilityIssues = compatibilityIssues ?? Array.Empty<CompatibilityIssueDto>();
        CanOverrideCompatibilityBaseline = canOverrideCompatibilityBaseline;
        IsAdministrator = isAdministrator;
        AuthenticatedUserId = authenticatedUserId;
        DisplayName = displayName;
        RequiresFullCompatibilityManifest = requiresFullCompatibilityManifest;
        RequestedCompatibilityPackageIds = requestedCompatibilityPackageIds ?? Array.Empty<string>();
    }

    public ProtocolResponse Result { get; }

    public string? SessionId { get; }

    public string ServerProtocolVersion { get; }

    public EventQueueSummaryDto? EventQueue { get; }

    public WorldMapMarkerDeliveryDto? WorldMapMarkers { get; }

    public IReadOnlyList<ServerNotificationDto> Notifications { get; }

    public string? AuthToken { get; }

    public string? ServerCompatibilityManifestJson { get; }

    public IReadOnlyList<CompatibilityIssueDto> CompatibilityIssues { get; }

    public bool CanOverrideCompatibilityBaseline { get; }

    public bool IsAdministrator { get; }

    public string? AuthenticatedUserId { get; }

    public string? DisplayName { get; }

    public bool RequiresFullCompatibilityManifest { get; }

    public IReadOnlyList<string> RequestedCompatibilityPackageIds { get; }
}

public sealed class CompatibilityIssueDto
{
    public CompatibilityIssueDto(string severity, string code, string message, string? subject)
    {
        Severity = severity;
        Code = code;
        Message = message;
        Subject = subject;
    }

    public string Severity { get; }

    public string Code { get; }

    public string Message { get; }

    public string? Subject { get; }
}

public sealed class OverrideCompatibilityBaselineRequest
{
    public OverrideCompatibilityBaselineRequest(
        string userId,
        string? colonyId,
        string compatibilityManifestJson,
        string? steamAuthTicket = null,
        string? password = null)
    {
        UserId = userId;
        ColonyId = colonyId;
        CompatibilityManifestJson = compatibilityManifestJson;
        SteamAuthTicket = steamAuthTicket;
        Password = password;
    }

    public string UserId { get; }

    public string? ColonyId { get; }

    public string CompatibilityManifestJson { get; }

    public string? SteamAuthTicket { get; }

    public string? Password { get; }
}

public sealed class ChangeOfflinePasswordRequest
{
    public ChangeOfflinePasswordRequest(
        string userId,
        string colonyId,
        string? authToken,
        string? currentPassword,
        string? newPassword)
    {
        UserId = userId;
        ColonyId = colonyId;
        AuthToken = authToken;
        CurrentPassword = currentPassword;
        NewPassword = newPassword;
    }

    public string UserId { get; }

    public string ColonyId { get; }

    public string? AuthToken { get; }

    public string? CurrentPassword { get; }

    public string? NewPassword { get; }
}

public sealed class ChangeOfflinePasswordResponse
{
    public ChangeOfflinePasswordResponse(ProtocolResponse result)
    {
        Result = result;
    }

    public ProtocolResponse Result { get; }
}

public sealed class OverrideCompatibilityBaselineResponse
{
    public OverrideCompatibilityBaselineResponse(
        ProtocolResponse result,
        string? serverCompatibilityManifestJson,
        DateTimeOffset? updatedAtUtc)
    {
        Result = result;
        ServerCompatibilityManifestJson = serverCompatibilityManifestJson;
        UpdatedAtUtc = updatedAtUtc;
    }

    public ProtocolResponse Result { get; }

    public string? ServerCompatibilityManifestJson { get; }

    public DateTimeOffset? UpdatedAtUtc { get; }
}

public sealed class MaintainPresenceRequest
{
    public MaintainPresenceRequest(
        string userId,
        string colonyId,
        string? currentSnapshotId,
        string? sessionId)
    {
        UserId = userId;
        ColonyId = colonyId;
        CurrentSnapshotId = currentSnapshotId;
        SessionId = sessionId;
    }

    public string UserId { get; }

    public string ColonyId { get; }

    public string? CurrentSnapshotId { get; }

    public string? SessionId { get; }
}

public sealed class MaintainPresenceResponse
{
    public MaintainPresenceResponse(
        ProtocolResponse result,
        string userId,
        bool online)
    {
        Result = result;
        UserId = userId;
        Online = online;
    }

    public ProtocolResponse Result { get; }

    public string UserId { get; }

    public bool Online { get; }
}

public sealed class LogoutRequest
{
    public LogoutRequest(string userId, string colonyId, string? sessionId)
    {
        UserId = userId;
        ColonyId = colonyId;
        SessionId = sessionId;
    }

    public string UserId { get; }

    public string ColonyId { get; }

    public string? SessionId { get; }
}

public sealed class LogoutResponse
{
    public LogoutResponse(ProtocolResponse result, string userId, bool disconnected)
    {
        Result = result;
        UserId = userId;
        Disconnected = disconnected;
    }

    public ProtocolResponse Result { get; }

    public string UserId { get; }

    public bool Disconnected { get; }
}

public sealed class ListPlayersRequest
{
    public ListPlayersRequest(string userId, string colonyId, string? currentSnapshotId)
    {
        UserId = userId;
        ColonyId = colonyId;
        CurrentSnapshotId = currentSnapshotId;
    }

    public string UserId { get; }

    public string ColonyId { get; }

    public string? CurrentSnapshotId { get; }
}

public sealed class PlayerSummaryDto
{
    public PlayerSummaryDto(
        string userId,
        string colonyId,
        string? currentSnapshotId,
        bool online,
        DateTimeOffset lastSeenAtUtc,
        string? displayName = null,
        string? relationKind = null,
        int? latestSnapshotWealth = null)
    {
        UserId = userId;
        ColonyId = colonyId;
        CurrentSnapshotId = currentSnapshotId;
        Online = online;
        LastSeenAtUtc = lastSeenAtUtc;
        DisplayName = displayName;
        RelationKind = relationKind;
        LatestSnapshotWealth = latestSnapshotWealth;
    }

    public string UserId { get; }

    public string ColonyId { get; }

    public string? CurrentSnapshotId { get; }

    public bool Online { get; }

    public DateTimeOffset LastSeenAtUtc { get; }

    public string? DisplayName { get; }

    public string? RelationKind { get; }

    public int? LatestSnapshotWealth { get; }
}

public sealed class ListPlayersResponse
{
    public ListPlayersResponse(ProtocolResponse result, IReadOnlyList<PlayerSummaryDto> players)
    {
        Result = result;
        Players = players;
    }

    public ProtocolResponse Result { get; }

    public IReadOnlyList<PlayerSummaryDto> Players { get; }
}

public sealed class SessionStreamEventDto
{
    public SessionStreamEventDto(
        string userId,
        bool online,
        long notificationVersion,
        string? message)
    {
        UserId = userId;
        Online = online;
        NotificationVersion = notificationVersion;
        Message = message;
    }

    public string UserId { get; }

    public bool Online { get; }

    public long NotificationVersion { get; }

    public string? Message { get; }
}

public sealed class ChatStreamEventDto
{
    public ChatStreamEventDto(string userId, long chatVersion, string? message)
    {
        UserId = userId;
        ChatVersion = chatVersion;
        Message = message;
    }

    public string UserId { get; }

    public long ChatVersion { get; }

    public string? Message { get; }
}

public sealed class WorldConfigurationStreamEventDto
{
    public WorldConfigurationStreamEventDto(string userId, long worldConfigurationVersion, string? message)
    {
        UserId = userId;
        WorldConfigurationVersion = worldConfigurationVersion;
        Message = message;
    }

    public string UserId { get; }

    public long WorldConfigurationVersion { get; }

    public string? Message { get; }
}

public sealed class PlayerOnlineStreamEventDto
{
    public PlayerOnlineStreamEventDto(
        string userId,
        string onlineUserId,
        long playerOnlineVersion,
        string? message)
    {
        UserId = userId;
        OnlineUserId = onlineUserId;
        PlayerOnlineVersion = playerOnlineVersion;
        Message = message;
    }

    public string UserId { get; }

    public string OnlineUserId { get; }

    public long PlayerOnlineVersion { get; }

    public string? Message { get; }
}
