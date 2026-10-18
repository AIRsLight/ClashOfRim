using AIRsLight.ClashOfRim.Network.Modules.Accounts;
using AIRsLight.ClashOfRim.Network.Modules.Sessions;
using AIRsLight.ClashOfRim.Network.Modules.World;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using static AIRsLight.ClashOfRim.Network.ServerText;

namespace AIRsLight.ClashOfRim.Network.Modules.Admin;

internal sealed class AdministratorPlayers(AdminControlRegistry control, SessionConnections connections,
    EventNotificationHub notifications, PlayerRegistry players, IColonySnapshotIndexStore snapshots,
    ColonyDeletion deletion, OfflineAccountRegistry accounts)
{
    // The command dispatcher owns authorization and the recovered mutation boundary.
    internal ProtocolResponse Kick(string userId, out int affected)
    {
        affected = connections.EndUserUnderMutation(userId) ? 1 : 0;
        notifications.SignalUser(userId);
        return ProtocolResponse.Ok(Format("Admin.Action.Kicked"));
    }

    internal ProtocolResponse Ban(string userId, out int affected)
    {
        control.Ban(userId);
        Kick(userId, out affected);
        return ProtocolResponse.Ok(Format("Admin.Action.Banned"));
    }

    internal ProtocolResponse Unban(string userId)
    {
        control.Unban(userId);
        return ProtocolResponse.Ok(Format("Admin.Action.Unbanned"));
    }

    internal ProtocolResponse Delete(string userId, string? requestedColonyId, DateTimeOffset at, out int affected)
    {
        affected = 0;
        string? colonyId = string.IsNullOrWhiteSpace(requestedColonyId) ? players.FindByUserId(userId)?.ColonyId : requestedColonyId.Trim();
        if (string.IsNullOrWhiteSpace(colonyId))
            return ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, Format("Admin.Action.TargetColonyMissing"));
        affected = connections.EndUserUnderMutation(userId) ? 1 : 0;
        deletion.DeleteUnderMutation(userId, colonyId, snapshots.GetLatest(userId, colonyId), at);
        return ProtocolResponse.Ok(Format("Admin.Action.PlayerSaveDeleted"));
    }

    internal ProtocolResponse ResetPassword(string userId, string? password, DateTimeOffset at)
        => accounts.ResetPassword(userId, password, at, out string failure)
            ? ProtocolResponse.Ok(Format("Admin.Action.PasswordReset"))
            : ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, CredentialAuthentication.LocalizeOfflineFailure(failure));
}
