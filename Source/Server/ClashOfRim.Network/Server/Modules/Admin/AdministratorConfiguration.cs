using AIRsLight.ClashOfRim.Network.Modules.Authorization;
using AIRsLight.ClashOfRim.Network.Modules.Snapshots;
using AIRsLight.ClashOfRim.Protocol;
using Microsoft.Extensions.Logging;
using static AIRsLight.ClashOfRim.Network.ServerText;

namespace AIRsLight.ClashOfRim.Network.Modules.Admin;

internal sealed class AdministratorConfiguration(AdministratorAuthorization authorization, SnapshotMutationCoordinator mutations,
    AdminConfigurationChanges changes, WorldConfigurationRegistry world, PlayerRegistry players,
    OnlinePresenceRegistry presence, AdminControlRegistry control, Func<ILogger> logger)
{
    internal AdminStatusResponse Status(AdminStatusRequest request)
    {
        using var mutation = mutations.EnterUnrecovered();
        if (Authorize(request.UserId, request.ColonyId, request.AuthToken) is { } failure)
            return new(failure, false, null, [], control.MaintenanceLoginLocked, control.MaintenanceReason, []);
        // Only settings publication is relevant to this read; it never replays unrelated snapshots.
        changes.Recover();
        return new(ProtocolResponse.Ok(Format("Admin.Status.Success")), true, changes.Current,
            players.List().Select(player => new AdminPlayerSummaryDto(player.UserId, player.ColonyId, player.CurrentSnapshotId,
                presence.IsUserOnline(player.UserId), player.LastSeenAtUtc, player.DisplayName,
                world.IsAdministrator(player.UserId), control.IsBanned(player.UserId)))
                .OrderByDescending(player => player.Online).ThenBy(player => player.UserId, StringComparer.Ordinal).ToList(),
            control.MaintenanceLoginLocked, control.MaintenanceReason, control.ListAudit().Select(record => record.ToDto()).ToList());
    }

    internal AdminUpdateConfigurationResponse Update(AdminUpdateConfigurationRequest request)
    {
        ProtocolResponse? Check() => Authorize(request.UserId, request.ColonyId, request.AuthToken);
        if (Check() is { } initialFailure) return new(initialFailure, null, null);
        if (request.Configuration is null)
            return new(ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, Format("Admin.Configuration.Missing")), null, null);
        using var recovery = mutations.Enter(Check);
        if (recovery.Rejection is { } failure) return new(failure, null, null);
        var at = DateTimeOffset.UtcNow;
        var saved = changes.PublishUnderMutation(request.Configuration, request.UserId, at);
        logger().LogInformation(
            "管理员更新服务器配置：user={UserId} trade={TradeEnabled} bank={BankEnabled} mercenaries={MercenariesEnabled} pvp={PvpEnabled} raidProtectionHours={RaidProtectionHours} raidMaxMinutes={RaidMaxMinutes} raidGraceMinutes={RaidGraceMinutes}",
            request.UserId, saved.TradeMarketplaceEnabled, saved.BankLoansEnabled, saved.MercenariesEnabled, saved.PvpEnabled,
            saved.RaidProtectionHours, saved.RaidMaxDurationMinutes, saved.RaidTimeoutGraceMinutes);
        return new(ProtocolResponse.Ok(Format("Admin.Configuration.Updated")), saved, at);
    }

    private ProtocolResponse? Authorize(string userId, string colonyId, string? token)
        => authorization.TryAuthorize(userId, colonyId, token, DateTimeOffset.UtcNow, out string failure)
            ? null : ProtocolResponse.Reject(ProtocolErrorCode.Unauthorized, failure);
}
