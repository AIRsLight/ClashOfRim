using AIRsLight.ClashOfRim.Network.Modules.Authorization;
using AIRsLight.ClashOfRim.Network.Modules.Snapshots;
using AIRsLight.ClashOfRim.Network.Modules.World;
using AIRsLight.ClashOfRim.Protocol;
using Microsoft.Extensions.Logging;
using static AIRsLight.ClashOfRim.Network.ServerText;

namespace AIRsLight.ClashOfRim.Network.Modules.Admin;

internal sealed class AdministratorActions(AdministratorAuthorization authorization, SnapshotMutationCoordinator mutations,
    AdminControlRegistry control, AdministratorPlayers players, AdministratorBroadcasts broadcasts,
    WorldConfigurationRegistry world, WorldConfigurationChanges worldChanges, Func<ILogger> logger)
{
    internal AdminActionResponse Execute(AdminActionRequest request)
    {
        string kind = request.ActionKind ?? string.Empty;
        AdminActionResponse Reject(ProtocolResponse failure) => new(failure, kind, request.TargetUserId,
            control.MaintenanceLoginLocked, null, 0);
        ProtocolResponse? Authorize() => authorization.Authorize(request.UserId, request.ColonyId, request.AuthToken, ProtocolErrorCode.Unauthorized);
        if (Authorize() is { } initialFailure) return Reject(initialFailure);
        kind = kind.Trim();
        if (kind.Length == 0) return Reject(ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, Format("Admin.Action.Missing")));
        // Protocol action strings remain exact and case-sensitive; numeric enum values are not action names.
        if (!Enum.TryParse(kind, out Action action) || !Enum.IsDefined(action) || action.ToString() != kind)
            return Reject(ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, Format("Admin.Action.Unknown")));
        string? target = string.IsNullOrWhiteSpace(request.TargetUserId) ? null : request.TargetUserId.Trim();
        if (action is not (Action.Broadcast or Action.LockMaintenance or Action.UnlockMaintenance) && target is null)
            return Reject(ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, Format("Admin.Action.TargetMissing")));
        if (action is Action.Kick or Action.Ban or Action.RevokeAdmin or Action.DeletePlayerSave && target == request.UserId)
            return Reject(ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, Format("Admin.Action.CannotTargetSelf")));
        using var recovery = mutations.Enter(Authorize);
        if (recovery.Rejection is { } failure) return Reject(failure);
        var at = DateTimeOffset.UtcNow;
        int affected = 0;
        ProtocolResponse result;
        switch (action)
        {
            case Action.Broadcast:
                affected = broadcasts.Publish(request.UserId, target, request.Message, request.NotificationSeverity, request.PersistentNotification, at);
                result = ProtocolResponse.Ok(Format("Admin.Action.Broadcasted")); break;
            case Action.Kick: result = players.Kick(target!, out affected); break;
            case Action.Ban: result = players.Ban(target!, out affected); break;
            case Action.Unban: result = players.Unban(target!); break;
            case Action.DeletePlayerSave: result = players.Delete(target!, request.TargetColonyId, at, out affected); break;
            case Action.ResetOfflinePassword: result = players.ResetPassword(target!, request.Message, at); break;
            case Action.PromoteAdmin:
                world.PromoteAdministrator(target!);
                result = ProtocolResponse.Ok(Format("Admin.Action.Promoted")); break;
            case Action.RevokeAdmin:
                result = world.RevokeAdministrator(target!) ? ProtocolResponse.Ok(Format("Admin.Action.Revoked"))
                    : ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, Format("Admin.Action.LastAdmin")); break;
            case Action.LockMaintenance:
                control.SetMaintenanceLoginLocked(true, request.Message);
                result = ProtocolResponse.Ok(Format("Admin.Action.MaintenanceLocked")); break;
            case Action.UnlockMaintenance:
                control.SetMaintenanceLoginLocked(false, null);
                result = ProtocolResponse.Ok(Format("Admin.Action.MaintenanceUnlocked")); break;
            default: throw new InvalidOperationException("Unknown administrator action.");
        }
        AdminAuditRecord? audit = null;
        if (result.Accepted)
        {
            audit = control.AddAudit(kind, request.UserId, target, request.Message, at);
            if (action is Action.PromoteAdmin or Action.RevokeAdmin) worldChanges.Signal(request.UserId, target);
            logger().LogInformation("管理员动作完成：action={ActionKind} actor={ActorUserId} target={TargetUserId} affectedOnline={AffectedOnlineUsers} maintenanceLocked={MaintenanceLocked}",
                kind, request.UserId, target, affected, control.MaintenanceLoginLocked);
        }
        return new(result, kind, request.TargetUserId, control.MaintenanceLoginLocked, audit?.ToDto(), affected);
    }

    private enum Action { Broadcast, Kick, Ban, Unban, PromoteAdmin, RevokeAdmin, DeletePlayerSave, ResetOfflinePassword, LockMaintenance, UnlockMaintenance }
}
