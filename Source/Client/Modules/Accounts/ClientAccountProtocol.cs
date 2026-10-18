using System;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Transport;
using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Modules.Accounts;

internal sealed class ClientAccountProtocol : IClientPasswordTransport
{
    private readonly ClientProtocolTransport transport;
    private readonly Func<string> notConfigured;
    internal ClientAccountProtocol(ClientProtocolTransport transport, Func<string> notConfigured)
    { this.transport = transport; this.notConfigured = notConfigured; }
    public Task<ClashOfRimClientNetworkResult<ModChangeOfflinePasswordResponseDto>> ChangeAsync(
        ClashOfRimClientNetworkContext context, string? currentPassword, string? newPassword, CancellationToken token) => context.IsConfigured
        ? transport.ForContext(context).PostAsync<ModChangeOfflinePasswordRequestDto, ModChangeOfflinePasswordResponseDto>(
            ProtocolContractManifest.Find(ProtocolMessageKind.ChangeOfflinePassword).Route,
            new() { UserId = context.UserId, ColonyId = context.ColonyId, AuthToken = context.AuthToken,
                CurrentPassword = currentPassword, NewPassword = newPassword }, token)
        : Task.FromResult(ClashOfRimClientNetworkResult<ModChangeOfflinePasswordResponseDto>.Failed("NotConfigured", notConfigured()));
}
