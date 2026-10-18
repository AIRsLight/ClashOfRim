using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Network;

public static partial class ClashOfRimNetworkServer
{
    private static ProtocolResponse? AuthorizeSession(ClashOfRimNetworkState state, string userId,
        string colonyId, string? sessionId, bool requirePending = false)
        => state.LoginSessions.IsValid(userId, colonyId, sessionId, DateTimeOffset.UtcNow, requirePending)
            ? null : ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, T("Auth.SessionExpired"));

    private static ProtocolResponse? OpenSessionConnection(ClashOfRimNetworkState state, string userId,
        string colonyId, string? sessionId, string? snapshotId, out OnlinePresenceLease? presence)
    {
        presence = null;
        using var mutation = EnterRecoveredSnapshotMutation(state, () => AuthorizeSession(state, userId, colonyId, sessionId, requirePending: true));
        if (mutation.Rejection is { } denied) return denied;
        if (!state.LoginSessions.TryBeginStream(userId, colonyId, sessionId, DateTimeOffset.UtcNow))
            return ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, T("SessionStream.InvalidSession"));
        // Connection registration and login publication share the mutation boundary. No await may split them.
        bool opened = false;
        try
        {
            if (!state.OnlinePresence.TryConnectExclusive(userId, sessionId, out presence))
                return ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, T("SessionStream.AlreadyOnline"));
            RecordPlayerSeen(state, userId, colonyId, snapshotId, DateTimeOffset.UtcNow);
            state.PlayerOnlineNotifications.SignalPlayerOnline(userId, state.OnlinePresence.ListOnlineUsers(), DateTimeOffset.UtcNow);
            opened = true;
            return null;
        }
        finally
        {
            if (!opened)
            {
                EndSessionConnectionLocked(state, userId, sessionId);
                presence?.Dispose();
            }
        }
    }

    private static LogoutResponse DisconnectSession(ClashOfRimNetworkState state, string userId,
        string colonyId, string? sessionId)
    {
        bool disconnected;
        RaidTimeoutProcessingResult timeouts;
        DateTimeOffset nowUtc;
        using (var mutation = EnterRecoveredSnapshotMutation(state, () => AuthorizeSession(state, userId, colonyId, sessionId)))
        {
            if (mutation.Rejection is { } denied) return new(denied, userId, disconnected: false);
            nowUtc = DateTimeOffset.UtcNow;
            // Recovery can release the gate or replace a session. Only its revalidated owner may end it.
            // Keep login publication excluded through the colony cleanup and the raid offline decision.
            disconnected = EndSessionConnectionLocked(state, userId, sessionId);
            ReconcilePendingConfirmationsLocked(state, userId, colonyId, nowUtc, forceCancel: false);
            timeouts = state.RaidTimeouts.ExpireUnderMutation(nowUtc);
        }
        // These are durable server jobs with their own source checks; prepare their snapshots outside the gate.
        state.RaidTimeouts.CompletePending(timeouts, nowUtc);
        return new(ProtocolResponse.Ok(T("Logout.Success")), userId, disconnected);
    }

    private static void CloseSessionConnection(ClashOfRimNetworkState state, string userId,
        string colonyId, string? sessionId, OnlinePresenceLease? presence)
    {
        try { DisconnectSession(state, userId, colonyId, sessionId); }
        finally
        {
            // A broken transport must release its own resources even if recovery/cleanup fails.
            // Both removals match the original identity, so late disposal cannot touch a replacement.
            using var mutation = state.EnterSnapshotMutation();
            EndSessionConnectionLocked(state, userId, sessionId);
            presence?.Dispose();
        }
    }

    private static bool EndSessionConnectionLocked(ClashOfRimNetworkState state, string userId, string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return false;
        state.LoginSessions.End(userId, sessionId);
        state.AuthTokens.RevokeForSession(userId, sessionId);
        return state.OnlinePresence.DisconnectSession(userId, sessionId);
    }
}
