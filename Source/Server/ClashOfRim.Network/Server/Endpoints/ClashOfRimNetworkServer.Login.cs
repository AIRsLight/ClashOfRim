using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Protocol;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace AIRsLight.ClashOfRim.Network;

public static partial class ClashOfRimNetworkServer
{
    private static IResult Login(LoginRequest request, ClashOfRimNetworkState state, ILoggerFactory loggerFactory)
    {
        if (!string.Equals(request.ProtocolVersion, ProtocolApiVersion.Current, StringComparison.Ordinal))
            return RejectLogin(ProtocolResponse.Reject(ProtocolErrorCode.IncompatibleProtocolVersion, T("Protocol.IncompatibleVersion")));
        if (string.IsNullOrWhiteSpace(request.UserId) || string.IsNullOrWhiteSpace(request.ColonyId))
            return RejectLogin(ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("SessionStream.MissingIdentity")));

        var prepared = PrepareEntryAuthentication(state, request.UserId, request.SteamAuthTicket, request.Password);
        if (prepared.Validate(false) is { } invalid) return RejectLogin(invalid);
        var auth = prepared.Result;
        string userId = auth.AuthenticatedUserId!;
        string displayName = string.IsNullOrWhiteSpace(auth.DisplayName) ? userId : auth.DisplayName!;
        var input = PrepareCompatibilityHandshakeInput(request.CompatibilityManifestJson, request.CompatibilityManifestId, request.CompatibilityManifestSummaryJson);
        CompatibilityHandshakeResult? compatibility = null;
        ProtocolResponse? Authorize()
        {
            compatibility = null;
            if (prepared.Validate(false) is { } rejected) return rejected;
            if (state.AdminControl.IsBanned(userId)) return ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, T("Login.Banned"));
            if (state.AdminControl.MaintenanceLoginLocked && !state.WorldConfiguration.IsAdministrator(userId))
                return ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected,
                    string.IsNullOrWhiteSpace(state.AdminControl.MaintenanceReason) ? T("Login.MaintenanceLocked") : state.AdminControl.MaintenanceReason!);
            if (!state.LoginSessions.CanCreate(userId, DateTimeOffset.UtcNow))
                return ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, T("Login.AlreadyOnline"));
            compatibility = EvaluateCompatibilityHandshake(state, auth, input, DateTimeOffset.UtcNow, publishBaseline: false);
            return compatibility.Result.Accepted ? null : compatibility.Result;
        }

        using var mutation = EnterRecoveredSnapshotMutation(state, Authorize);
        if (mutation.Rejection is { } denied) return RejectLogin(denied, compatibility);
        var nowUtc = DateTimeOffset.UtcNow;
        ReconcileExpiredRaidEvents(state, nowUtc);
        if (FindDefenderLoginBlockingRaid(state, userId, request.ColonyId) is not null)
            return RejectLogin(ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, T("Login.BlockedByDefenseRaid")),
                userId: userId, displayName: displayName, isAdministrator: state.WorldConfiguration.IsAdministrator(userId));

        var session = state.LoginSessions.Prepare(userId, request.ColonyId, nowUtc);
        if (session is null) return RejectLogin(ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, T("Login.AlreadyOnline")));
        compatibility = EvaluateCompatibilityHandshake(state, auth, input, nowUtc, publishBaseline: true);
        if (!compatibility.Result.Accepted) return RejectLogin(compatibility.Result, compatibility);

        // These writes are independently persisted. A failed login must not publish its session
        // or displace an existing pending session, even when a lifecycle hook throws.
        ReconcileExpiredDeliveredEvents(state, userId, nowUtc);
        RecordPlayerSeen(state, userId, request.ColonyId, request.CurrentSnapshotId, nowUtc, displayName);
        RunClientLifecycleHooks(state, ClientLifecycleEvent.LoggedIn(userId, request.ColonyId, session.SessionId, request.CurrentSnapshotId, nowUtc));
        var queue = ProtocolDtoMapper.ToDto(EventQueueSummaryBuilder.BuildForTarget(userId, state.Ledger.ListQueueForTarget(userId)));
        var markers = ProtocolDtoMapper.ToDto(BuildWorldMapMarkerDelivery(userId, request.ColonyId, state, nowUtc));
        RuntimeLogger(loggerFactory).LogInformation(
            "准备发布玩家登录会话：user={UserId} colony={ColonyId} session={SessionId}", userId, request.ColonyId, session.SessionId);
        if (Authorize() is { } finalDenied) return RejectLogin(finalDenied, compatibility);
        if (FindDefenderLoginBlockingRaid(state, userId, request.ColonyId) is not null)
            return RejectLogin(ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, T("Login.BlockedByDefenseRaid")),
                userId: userId, displayName: displayName, isAdministrator: state.WorldConfiguration.IsAdministrator(userId));

        string? token = state.AuthTokens.Issue(userId, userId, request.ColonyId, session.SessionId, DateTimeOffset.UtcNow);
        try
        {
            var result = Results.Ok(new LoginResponse(ProtocolResponse.Ok(T("Login.Success")), session.SessionId,
                ProtocolApiVersion.Current, queue, markers, Array.Empty<ServerNotificationDto>(), token,
                serverCompatibilityManifestJson: compatibility!.ServerCompatibilityManifestJson,
                compatibilityIssues: compatibility.CompatibilityIssues,
                canOverrideCompatibilityBaseline: compatibility.CanOverrideCompatibilityBaseline,
                isAdministrator: state.WorldConfiguration.IsAdministrator(userId), authenticatedUserId: userId, displayName: displayName));
            if (!session.TryPublish(DateTimeOffset.UtcNow))
                return RejectLogin(ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, T("Login.AlreadyOnline")));
            token = null; // Publication is the last business operation; only this token survives.
            return result;
        }
        finally
        {
            if (token is not null) state.AuthTokens.Revoke(token);
        }
    }

    private static IResult RejectLogin(ProtocolResponse rejection, CompatibilityHandshakeResult? compatibility = null,
        string? userId = null, string? displayName = null, bool isAdministrator = false)
        => Results.Ok(new LoginResponse(rejection, null, ProtocolApiVersion.Current, null, null, Array.Empty<ServerNotificationDto>(),
            serverCompatibilityManifestJson: compatibility?.ServerCompatibilityManifestJson,
            compatibilityIssues: compatibility?.CompatibilityIssues,
            canOverrideCompatibilityBaseline: compatibility?.CanOverrideCompatibilityBaseline ?? false,
            isAdministrator: isAdministrator || (compatibility?.CanOverrideCompatibilityBaseline ?? false),
            authenticatedUserId: userId, displayName: displayName,
            requiresFullCompatibilityManifest: compatibility?.RequiresFullCompatibilityManifest ?? false,
            requestedCompatibilityPackageIds: compatibility?.RequestedCompatibilityPackageIds));
}
