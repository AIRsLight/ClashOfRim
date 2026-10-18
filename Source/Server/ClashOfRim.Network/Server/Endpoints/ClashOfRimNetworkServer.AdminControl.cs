using AIRsLight.ClashOfRim.Network.Modules.Accounts;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace AIRsLight.ClashOfRim.Network;

public static partial class ClashOfRimNetworkServer
{
    private static IResult AdminAction(
        AdminActionRequest request,
        ClashOfRimNetworkState state,
        ILoggerFactory loggerFactory)
    {
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        if (!TryAuthorizeAdmin(state, request.UserId, request.ColonyId, request.AuthToken, nowUtc, out string failure))
        {
            return Results.Ok(new AdminActionResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.Unauthorized, failure),
                request.ActionKind ?? string.Empty,
                request.TargetUserId,
                state.AdminControl.MaintenanceLoginLocked,
                auditRecord: null,
                affectedOnlineUsers: 0));
        }

        string actionKind = NormalizeAdminActionKind(request.ActionKind);
        if (string.IsNullOrWhiteSpace(actionKind))
        {
            return Results.Ok(new AdminActionResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Admin.Action.Missing")),
                request.ActionKind ?? string.Empty,
                request.TargetUserId,
                state.AdminControl.MaintenanceLoginLocked,
                auditRecord: null,
                affectedOnlineUsers: 0));
        }

        int affected = 0;
        ProtocolResponse result;
        switch (actionKind)
        {
            case "Broadcast":
                affected = BroadcastAdminNotification(
                    state,
                    request.UserId,
                    request.TargetUserId,
                    request.Message,
                    ParseAdminBroadcastSeverity(request.NotificationSeverity),
                    request.PersistentNotification,
                    nowUtc);
                result = ProtocolResponse.Ok(T("Admin.Action.Broadcasted"));
                break;
            case "Kick":
                result = TryRequireTarget(request.TargetUserId, out string? kickTarget)
                    ? RejectSelfTarget(request.UserId, kickTarget!)
                        ?? KickUser(state, kickTarget!, request.TargetColonyId, out affected)
                    : ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Admin.Action.TargetMissing"));
                break;
            case "Ban":
                result = TryRequireTarget(request.TargetUserId, out string? banTarget)
                    ? RejectSelfTarget(request.UserId, banTarget!)
                        ?? BanUser(state, banTarget!, request.TargetColonyId, out affected)
                    : ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Admin.Action.TargetMissing"));
                break;
            case "Unban":
                result = TryRequireTarget(request.TargetUserId, out string? unbanTarget)
                    ? UnbanUser(state, unbanTarget!)
                    : ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Admin.Action.TargetMissing"));
                break;
            case "PromoteAdmin":
                result = TryRequireTarget(request.TargetUserId, out string? promoteTarget)
                    ? PromoteAdmin(state, promoteTarget!)
                    : ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Admin.Action.TargetMissing"));
                break;
            case "RevokeAdmin":
                result = TryRequireTarget(request.TargetUserId, out string? revokeTarget)
                    ? RejectSelfTarget(request.UserId, revokeTarget!)
                        ?? RevokeAdmin(state, revokeTarget!)
                    : ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Admin.Action.TargetMissing"));
                break;
            case "DeletePlayerSave":
                result = TryRequireTarget(request.TargetUserId, out string? deleteTarget)
                    ? RejectSelfTarget(request.UserId, deleteTarget!)
                        ?? DeletePlayerSave(state, deleteTarget!, request.TargetColonyId, nowUtc, out affected)
                    : ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Admin.Action.TargetMissing"));
                break;
            case "ResetOfflinePassword":
                result = TryRequireTarget(request.TargetUserId, out string? passwordTarget)
                    ? ResetOfflinePassword(state, passwordTarget!, request.Message, nowUtc)
                    : ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Admin.Action.TargetMissing"));
                break;
            case "LockMaintenance":
                state.AdminControl.SetMaintenanceLoginLocked(true, request.Message);
                result = ProtocolResponse.Ok(T("Admin.Action.MaintenanceLocked"));
                break;
            case "UnlockMaintenance":
                state.AdminControl.SetMaintenanceLoginLocked(false, null);
                result = ProtocolResponse.Ok(T("Admin.Action.MaintenanceUnlocked"));
                break;
            default:
                result = ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Admin.Action.Unknown"));
                break;
        }

        AdminAuditRecord? audit = null;
        if (result.Accepted)
        {
            audit = state.AdminControl.AddAudit(actionKind, request.UserId, request.TargetUserId, request.Message, nowUtc);
            if (string.Equals(actionKind, "PromoteAdmin", StringComparison.Ordinal)
                || string.Equals(actionKind, "RevokeAdmin", StringComparison.Ordinal))
            {
                state.WorldChanges.Signal(request.UserId, request.TargetUserId);
            }

            RuntimeLogger(loggerFactory).LogInformation(
                "管理员动作完成：action={ActionKind} actor={ActorUserId} target={TargetUserId} affectedOnline={AffectedOnlineUsers} maintenanceLocked={MaintenanceLocked}",
                actionKind,
                request.UserId,
                request.TargetUserId,
                affected,
                state.AdminControl.MaintenanceLoginLocked);
        }

        return Results.Ok(new AdminActionResponse(
            result,
            actionKind,
            request.TargetUserId,
            state.AdminControl.MaintenanceLoginLocked,
            audit?.ToDto(),
            affected));
    }

    private static bool TryAuthorizeAdmin(ClashOfRimNetworkState state, string userId, string colonyId,
        string? authToken, DateTimeOffset nowUtc, out string failure)
        => state.AdminAuthorization.TryAuthorize(userId, colonyId, authToken, nowUtc, out failure);

    private static string NormalizeAdminActionKind(string? actionKind)
    {
        return string.IsNullOrWhiteSpace(actionKind) ? string.Empty : actionKind.Trim();
    }

    private static bool TryRequireTarget(string? targetUserId, out string? target)
    {
        target = string.IsNullOrWhiteSpace(targetUserId) ? null : targetUserId.Trim();
        return !string.IsNullOrWhiteSpace(target);
    }

    private static ProtocolResponse? RejectSelfTarget(string actorUserId, string targetUserId)
    {
        return string.Equals(actorUserId, targetUserId, StringComparison.Ordinal)
            ? ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, T("Admin.Action.CannotTargetSelf"))
            : null;
    }

    private static ProtocolResponse KickUser(
        ClashOfRimNetworkState state,
        string targetUserId,
        string? targetColonyId,
        out int affected)
    {
        using var mutation = state.EnterSnapshotMutation();
        affected = DisconnectUserConnectionsLocked(state, targetUserId);

        if (!string.IsNullOrWhiteSpace(targetColonyId))
        {
            state.AuthTokens.RevokeForColony(targetUserId, targetColonyId!);
        }
        else
        {
            state.AuthTokens.RevokeForUser(targetUserId);
        }

        state.EventNotifications.SignalUser(targetUserId);
        return ProtocolResponse.Ok(T("Admin.Action.Kicked"));
    }

    private static ProtocolResponse BanUser(
        ClashOfRimNetworkState state,
        string targetUserId,
        string? targetColonyId,
        out int affected)
    {
        state.AdminControl.Ban(targetUserId);
        ProtocolResponse kicked = KickUser(state, targetUserId, targetColonyId, out affected);
        return kicked.Accepted
            ? ProtocolResponse.Ok(T("Admin.Action.Banned"))
            : kicked;
    }

    private static ProtocolResponse UnbanUser(ClashOfRimNetworkState state, string targetUserId)
    {
        state.AdminControl.Unban(targetUserId);
        return ProtocolResponse.Ok(T("Admin.Action.Unbanned"));
    }

    private static ProtocolResponse PromoteAdmin(ClashOfRimNetworkState state, string targetUserId)
    {
        state.WorldConfiguration.PromoteAdministrator(targetUserId);
        return ProtocolResponse.Ok(T("Admin.Action.Promoted"));
    }

    private static ProtocolResponse RevokeAdmin(ClashOfRimNetworkState state, string targetUserId)
    {
        return state.WorldConfiguration.RevokeAdministrator(targetUserId)
            ? ProtocolResponse.Ok(T("Admin.Action.Revoked"))
            : ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, T("Admin.Action.LastAdmin"));
    }

    private static ProtocolResponse DeletePlayerSave(
        ClashOfRimNetworkState state,
        string targetUserId,
        string? targetColonyId,
        DateTimeOffset nowUtc,
        out int affected)
    {
        affected = 0;
        string? colonyId = string.IsNullOrWhiteSpace(targetColonyId)
            ? state.Players.FindByUserId(targetUserId)?.ColonyId
            : targetColonyId.Trim();
        if (string.IsNullOrWhiteSpace(colonyId))
        {
            return ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Admin.Action.TargetColonyMissing"));
        }

        using (state.EnterSnapshotMutation())
            affected = DisconnectUserConnectionsLocked(state, targetUserId);

        state.ColonyDeletion.Delete(targetUserId, colonyId, nowUtc);
        return ProtocolResponse.Ok(T("Admin.Action.PlayerSaveDeleted"));
    }

    // Administrator actions intentionally disconnect the user's current session, regardless of its ID.
    private static int DisconnectUserConnectionsLocked(ClashOfRimNetworkState state, string userId)
        => (state.LoginSessions.EndUser(userId) ? 1 : 0) + (state.OnlinePresence.ForceDisconnect(userId) ? 1 : 0);

    private static ProtocolResponse ResetOfflinePassword(
        ClashOfRimNetworkState state,
        string targetUserId,
        string? newPassword,
        DateTimeOffset nowUtc)
    {
        return state.OfflineAccounts.ResetPassword(targetUserId, newPassword, nowUtc, out string failure)
            ? ProtocolResponse.Ok(T("Admin.Action.PasswordReset"))
            : ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, CredentialAuthentication.LocalizeOfflineFailure(failure));
    }

    private static int BroadcastAdminNotification(
        ClashOfRimNetworkState state,
        string actorUserId,
        string? targetUserId,
        string? message,
        ServerNotificationSeverity severity,
        bool persistentNotification,
        DateTimeOffset nowUtc)
    {
        string normalizedMessage = string.IsNullOrWhiteSpace(message) ? T("Admin.Broadcast.DefaultMessage") : message.Trim();
        IReadOnlyList<PlayerSessionRecord> players = state.Players.List()
            .Where(player => string.IsNullOrWhiteSpace(targetUserId)
                || string.Equals(player.UserId, targetUserId, StringComparison.Ordinal))
            .Where(player => persistentNotification || state.OnlinePresence.IsUserOnline(player.UserId))
            .ToList();

        foreach (PlayerSessionRecord player in players)
        {
            string idempotencyKey = $"admin-broadcast:{actorUserId}:{player.UserId}:{nowUtc:O}";
            AuthoritativeEvent notification = AuthoritativeEventFactory.CreateForLedger(state.Ledger,
                ServerEventType.ServerNotification,
                new EventParty("server"),
                new EventParty(player.UserId, player.ColonyId),
                idempotencyKey,
                state.OnlinePresence.IsUserOnline(player.UserId),
                new ServerNotificationEventPayload(
                    idempotencyKey,
                    T("Admin.Broadcast.Title"),
                    normalizedMessage,
                    severity,
                    FromAdministrator: true,
                    actorUserId,
                    OnlineOnly: !persistentNotification),
                nowUtc);
            LogEventAppend(state, state.Ledger.Append(notification), "admin-broadcast");
        }

        state.EventNotifications.SignalUsers(players.Select(player => player.UserId));
        return players.Count;
    }

    private static ServerNotificationSeverity ParseAdminBroadcastSeverity(string? severity)
    {
        return string.Equals(severity, "Warning", StringComparison.OrdinalIgnoreCase)
            ? ServerNotificationSeverity.Warning
            : string.Equals(severity, "Critical", StringComparison.OrdinalIgnoreCase)
                || string.Equals(severity, "Error", StringComparison.OrdinalIgnoreCase)
                    ? ServerNotificationSeverity.Critical
                    : ServerNotificationSeverity.Info;
    }
}
