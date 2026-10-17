using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.Modules.Sessions;

namespace AIRsLight.ClashOfRim.ClientNetwork;

// Protocol adapter; the session module receives only the streaming capability.
internal sealed class ClientSessionStreamTransport : IClientSessionStreamTransport
{
    public async Task<ClashOfRimClientNetworkResult<ModSessionStreamClosedDto>> StreamAsync(
        ClashOfRimClientNetworkContext context, string sessionId, long notificationVersion, long worldVersion,
        Action<ModSessionStreamEvent> onEvent, CancellationToken cancellationToken)
    {
        using var http = new HttpClient();
        var client = new ClashOfRimModNetworkClient(http, context);
        return await client.StreamSessionAsync(sessionId, notificationVersion, worldVersion, onEvent, cancellationToken);
    }
}
