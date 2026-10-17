using System;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;

namespace AIRsLight.ClashOfRim.Modules.Sessions;

internal interface IClientSessionStreamTransport
{
    Task<ClashOfRimClientNetworkResult<ModSessionStreamClosedDto>> StreamAsync(
        ClashOfRimClientNetworkContext context, string sessionId, long notificationVersion, long worldVersion,
        Action<ModSessionStreamEvent> onEvent, CancellationToken cancellationToken);
}
