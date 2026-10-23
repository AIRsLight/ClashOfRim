using AIRsLight.ClashOfRim.Network.Modules.Compatibility;
using AIRsLight.ClashOfRim.Network.Modules.Accounts;
using AIRsLight.ClashOfRim.Network.Modules.Events;
using AIRsLight.ClashOfRim.Network.Modules.Raids;
using AIRsLight.ClashOfRim.Network.Modules.Snapshots;
using AIRsLight.ClashOfRim.Network.Modules.World;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Protocol;
using Microsoft.Extensions.Logging;

namespace AIRsLight.ClashOfRim.Network.Modules.Sessions;

internal sealed class LoginService(AccountService accounts, EntryHandshakePolicy handshakes,
    SnapshotMutationCoordinator mutations, RaidTimeouts timeouts, RaidQueries raids,
    LoginSessionRegistry sessions, AuthTokenRegistry tokens, WorldConfigurationRegistry world,
    EventDeliveryService delivery, PlayerSessionActivity activity, ClientEntryLifecycle lifecycle,
    WorldMarkerDelivery markers, Func<ILogger> logger)
{
    internal LoginResponse Login(LoginRequest request)
    {
        if (ProtocolApiVersion.Validate(request.ProtocolVersion) is { } protocolFailure)
            return RejectLogin(protocolFailure);
        if (string.IsNullOrWhiteSpace(request.UserId) || string.IsNullOrWhiteSpace(request.ColonyId))
            return RejectLogin(ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, ServerLocalization.Text("SessionStream.MissingIdentity")));

        var prepared = accounts.PrepareCredentials(request.UserId, request.SteamAuthTicket, request.Password, DateTimeOffset.UtcNow);
        if (prepared.Revalidate() is { } invalid) return RejectLogin(invalid);
        var input = CompatibilityHandshakeInput.Prepare(request.CompatibilityManifestJson, request.CompatibilityManifestId, request.CompatibilityManifestSummaryJson);
        var entry = handshakes.Prepare(prepared, input, EntryPurpose.Login);
        string userId = entry.UserId, displayName = entry.DisplayName;
        using var mutation = mutations.Enter(entry.Authorize);
        if (mutation.Rejection is { } denied) return RejectLogin(denied, entry.Compatibility);
        var nowUtc = DateTimeOffset.UtcNow;
        timeouts.Reconcile(nowUtc);
        if (raids.BlockingDefender(userId, request.ColonyId) is not null)
            return RejectLogin(ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, ServerLocalization.Text("Login.BlockedByDefenseRaid")),
                userId: userId, displayName: displayName, isAdministrator: world.IsAdministrator(userId));

        var session = sessions.Prepare(userId, request.ColonyId, nowUtc);
        if (session is null) return RejectLogin(ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, ServerLocalization.Text("Login.AlreadyOnline")));
        if (entry.PublishBaseline(nowUtc) is { } incompatible) return RejectLogin(incompatible, entry.Compatibility);

        // These writes are independently persisted. A failed login must not publish its session
        // or displace an existing pending session, even when a lifecycle hook throws.
        delivery.ReconcileDeliveryLeases(userId, nowUtc);
        activity.Record(userId, request.ColonyId, request.CurrentSnapshotId, nowUtc, displayName);
        lifecycle.Apply(ClientLifecycleEvent.LoggedIn(userId, request.ColonyId, session.SessionId, request.CurrentSnapshotId, nowUtc));
        var queue = delivery.ReadQueue(userId, request.ColonyId);
        var worldMarkers = ProtocolDtoMapper.ToDto(markers.Build(userId, request.ColonyId, nowUtc));
        logger().LogInformation(
            "准备发布玩家登录会话：user={UserId} colony={ColonyId} session={SessionId}", userId, request.ColonyId, session.SessionId);
        if (entry.Authorize() is { } finalDenied) return RejectLogin(finalDenied, entry.Compatibility);
        if (raids.BlockingDefender(userId, request.ColonyId) is not null)
            return RejectLogin(ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, ServerLocalization.Text("Login.BlockedByDefenseRaid")),
                userId: userId, displayName: displayName, isAdministrator: world.IsAdministrator(userId));

        string? token = tokens.Issue(userId, userId, request.ColonyId, session.SessionId, DateTimeOffset.UtcNow);
        try
        {
            var result = new LoginResponse(ProtocolResponse.Ok(ServerLocalization.Text("Login.Success")), session.SessionId,
                ProtocolApiVersion.Current, queue, worldMarkers, Array.Empty<ServerNotificationDto>(), token,
                serverCompatibilityManifestJson: entry.Compatibility!.ServerCompatibilityManifestJson,
                compatibilityIssues: entry.Compatibility!.CompatibilityIssues,
                canOverrideCompatibilityBaseline: entry.Compatibility!.CanOverrideCompatibilityBaseline,
                isAdministrator: world.IsAdministrator(userId), authenticatedUserId: userId, displayName: displayName);
            if (!session.TryPublish(DateTimeOffset.UtcNow))
                return RejectLogin(ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, ServerLocalization.Text("Login.AlreadyOnline")));
            token = null; // Publication is the last business operation; only this token survives.
            return result;
        }
        finally
        {
            if (token is not null) tokens.Revoke(token);
        }
    }

    private static LoginResponse RejectLogin(ProtocolResponse rejection, CompatibilityHandshakeResult? compatibility = null,
        string? userId = null, string? displayName = null, bool isAdministrator = false)
        => new LoginResponse(rejection, null, ProtocolApiVersion.Current, null, null, Array.Empty<ServerNotificationDto>(),
            serverCompatibilityManifestJson: compatibility?.ServerCompatibilityManifestJson,
            compatibilityIssues: compatibility?.CompatibilityIssues,
            canOverrideCompatibilityBaseline: compatibility?.CanOverrideCompatibilityBaseline ?? false,
            isAdministrator: isAdministrator || (compatibility?.CanOverrideCompatibilityBaseline ?? false),
            authenticatedUserId: userId, displayName: displayName,
            requiresFullCompatibilityManifest: compatibility?.RequiresFullCompatibilityManifest ?? false,
            requestedCompatibilityPackageIds: compatibility?.RequestedCompatibilityPackageIds);
}
