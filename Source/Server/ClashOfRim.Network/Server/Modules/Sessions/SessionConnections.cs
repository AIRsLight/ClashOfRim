using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network.Modules.Economy;
using AIRsLight.ClashOfRim.Network.Modules.Raids;
using AIRsLight.ClashOfRim.Network.Modules.Snapshots;
using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Network.Modules.Sessions;

internal sealed class SessionConnections(LoginSessionRegistry sessions, AuthTokenRegistry tokens,
    OnlinePresenceRegistry online, PlayerOnlineNotificationHub notifications, PlayerSessionActivity players,
    SnapshotMutationCoordinator mutations, PendingConfirmationReconciler pending, RaidTimeouts raids)
{
    internal static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(5);

    internal ProtocolResponse? Check(string userId, string colonyId, string? sessionId, OnlinePresenceLease presence)
        => ValidateConnection(userId, colonyId, sessionId, presence, refresh: false);

    internal ProtocolResponse? Heartbeat(string userId, string colonyId, string? sessionId, OnlinePresenceLease presence)
        => ValidateConnection(userId, colonyId, sessionId, presence, refresh: true);

    private ProtocolResponse? ValidateConnection(string userId, string colonyId, string? sessionId, OnlinePresenceLease presence, bool refresh)
    {
        using var mutation = mutations.EnterUnrecovered();
        if (!presence.IsActive())
            return ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, ServerLocalization.Text("SessionStream.LeaseExpired"));
        bool valid = refresh ? sessions.Refresh(userId, colonyId, sessionId, DateTimeOffset.UtcNow)
            : sessions.IsValid(userId, colonyId, sessionId, DateTimeOffset.UtcNow);
        if (!valid) return ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, ServerLocalization.Text("Auth.SessionExpired"));
        if (refresh && !presence.Touch())
            return ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, ServerLocalization.Text("SessionStream.LeaseExpired"));
        return null;
    }

    internal LogoutResponse Logout(LogoutRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.UserId) || string.IsNullOrWhiteSpace(request.ColonyId)
            || string.IsNullOrWhiteSpace(request.SessionId))
            return new(ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, ServerLocalization.Text("Presence.MissingIdentity")),
                request.UserId ?? string.Empty, disconnected: false);
        return Disconnect(request.UserId, request.ColonyId, request.SessionId);
    }

    private ProtocolResponse? Authorize(string userId,
        string colonyId, string? sessionId, bool requirePending = false)
        => sessions.IsValid(userId, colonyId, sessionId, DateTimeOffset.UtcNow, requirePending)
            ? null : ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, ServerLocalization.Text("Auth.SessionExpired"));

    internal ProtocolResponse? Open(string userId,
        string colonyId, string? sessionId, string? snapshotId, out OnlinePresenceLease? presence)
    {
        presence = null;
        using var mutation = mutations.Enter(() => Authorize(userId, colonyId, sessionId, requirePending: true));
        if (mutation.Rejection is { } denied) return denied;
        if (!sessions.TryBeginStream(userId, colonyId, sessionId, DateTimeOffset.UtcNow))
            return ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, ServerLocalization.Text("SessionStream.InvalidSession"));
        // Connection registration and login publication share the mutation boundary. No await may split them.
        bool opened = false;
        try
        {
            if (!online.TryConnectExclusive(userId, sessionId, out presence))
                return ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, ServerLocalization.Text("SessionStream.AlreadyOnline"));
            players.Record(userId, colonyId, snapshotId, DateTimeOffset.UtcNow);
            notifications.SignalPlayerOnline(userId, online.ListOnlineUsers(), DateTimeOffset.UtcNow);
            opened = true;
            return null;
        }
        finally
        {
            if (!opened)
            {
                EndUnderMutation(userId, sessionId);
                presence?.Dispose();
            }
        }
    }

    private LogoutResponse Disconnect(string userId,
        string colonyId, string? sessionId)
    {
        bool disconnected;
        RaidTimeoutProcessingResult timeouts;
        DateTimeOffset nowUtc;
        using (var mutation = mutations.Enter(() => Authorize(userId, colonyId, sessionId)))
        {
            if (mutation.Rejection is { } denied) return new(denied, userId, disconnected: false);
            nowUtc = DateTimeOffset.UtcNow;
            // Recovery can release the gate or replace a session. Only its revalidated owner may end it.
            // Keep login publication excluded through the colony cleanup and the raid offline decision.
            disconnected = EndUnderMutation(userId, sessionId);
            pending.Reconcile(userId, colonyId, nowUtc, forceCancel: false);
            timeouts = raids.ExpireUnderMutation(nowUtc);
        }
        // These are durable server jobs with their own source checks; prepare their snapshots outside the gate.
        raids.CompletePending(timeouts, nowUtc);
        return new(ProtocolResponse.Ok(ServerLocalization.Text("Logout.Success")), userId, disconnected);
    }

    internal void Close(string userId,
        string colonyId, string? sessionId, OnlinePresenceLease? presence)
    {
        try { Disconnect(userId, colonyId, sessionId); }
        finally
        {
            // A broken transport must release its own resources even if recovery/cleanup fails.
            // Both removals match the original identity, so late disposal cannot touch a replacement.
            using var mutation = mutations.EnterUnrecovered();
            EndUnderMutation(userId, sessionId);
            presence?.Dispose();
        }
    }

    internal void EndColonyUnderMutation(string userId, string colonyId)
    {
        // A replay for an abandoned colony must leave its replacement's connection intact.
        EndUnderMutation(userId, sessions.FindSessionIdForColony(userId, colonyId));
        tokens.RevokeForColony(userId, colonyId);
    }

    // Explicit administrator commands retire the current account connection, even if its colony changed.
    internal bool EndUserUnderMutation(string userId)
    {
        bool ended = sessions.EndUser(userId);
        ended |= online.ForceDisconnect(userId);
        tokens.RevokeForUser(userId);
        return ended;
    }

    internal bool EndUnderMutation(string userId, string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return false;
        sessions.End(userId, sessionId);
        tokens.RevokeForSession(userId, sessionId);
        return online.DisconnectSession(userId, sessionId);
    }
}
