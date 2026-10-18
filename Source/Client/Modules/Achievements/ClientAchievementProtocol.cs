using System;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Transport;
using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Modules.Achievements;

internal sealed class ClientAchievementProtocol : IClientAchievementTransport
{
    private readonly ClientProtocolTransport transport;
    private readonly Func<string> notConfigured;
    internal ClientAchievementProtocol(ClientProtocolTransport transport, Func<string> notConfigured)
    { this.transport = transport; this.notConfigured = notConfigured; }
    public Task<ClashOfRimClientNetworkResult<ModListAchievementsResponseDto>> ListAsync(
        ClashOfRimClientNetworkContext context, ClientAchievementTarget target, CancellationToken token)
        => context.IsConfigured ? transport.ForContext(context).PostAsync<ModListAchievementsRequestDto, ModListAchievementsResponseDto>(
            ProtocolContractManifest.Find(ProtocolMessageKind.ListAchievements).Route, new()
            {
                UserId = context.UserId, ColonyId = context.ColonyId, CurrentSnapshotId = context.CurrentSnapshotId,
                TargetUserId = target.UserId, TargetColonyId = target.ColonyId
            }, token)
            : Task.FromResult(ClashOfRimClientNetworkResult<ModListAchievementsResponseDto>.Failed("NotConfigured", notConfigured()));
}
