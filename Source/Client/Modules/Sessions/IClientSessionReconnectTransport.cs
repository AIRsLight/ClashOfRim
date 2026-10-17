using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;

namespace AIRsLight.ClashOfRim.Modules.Sessions;

internal interface IClientSessionReconnectTransport
{
    Task<ClashOfRimClientNetworkResult<ModPrepareWorldSessionResponseDto>> PrepareAsync(ClashOfRimClientNetworkContext context, CancellationToken token);
    Task<ClashOfRimClientNetworkResult<ModLoginResponseDto>> LoginAsync(ClashOfRimClientNetworkContext context, CancellationToken token);
    Task<ClashOfRimClientNetworkResult<ModDownloadLatestSnapshotResponseDto>> ReadSnapshotAsync(ClashOfRimClientNetworkContext context, CancellationToken token);
}
