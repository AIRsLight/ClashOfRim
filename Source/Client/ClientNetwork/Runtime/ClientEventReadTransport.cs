using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.Modules.Events;
using AIRsLight.ClashOfRim.Modules.Transport;

namespace AIRsLight.ClashOfRim.ClientNetwork;

// Owns per-exchange HTTP resources; request and read algorithms belong to the event modules.
internal sealed class ClientEventReadTransport : IClientEventReadProtocol
{
    public async Task<ClashOfRimClientNetworkResult<ModPullPendingEventsResponseDto>> PullAsync(ClashOfRimClientNetworkContext context, CancellationToken token)
    {
        using var http = new HttpClient();
        return await Create(http, context).PullAsync(context, token);
    }

    public async Task<ClashOfRimClientNetworkResult<ModWaitForEventsResponseDto>> WaitAsync(ClashOfRimClientNetworkContext context,
        long knownVersion, int timeoutSeconds, CancellationToken token)
    {
        using var http = new HttpClient();
        return await Create(http, context).WaitAsync(context, knownVersion, timeoutSeconds, token);
    }

    public async Task<ClashOfRimClientNetworkResult<ModPullEventDetailsResponseDto>> DetailsAsync(ClashOfRimClientNetworkContext context,
        IReadOnlyList<string> ids, CancellationToken token)
    {
        using var http = new HttpClient();
        return await Create(http, context).DetailsAsync(context, ids, token);
    }

    private static ClientEventProtocol Create(HttpClient http, ClashOfRimClientNetworkContext context) => new(
        new ClientProtocolTransport(http, context, new ClientProtocolEnvironment(
            (message, owner) => ClashOfRimModNetworkClient.SessionExpired?.Invoke(message, owner))),
        () => ClashOfRimText.Key("ClashOfRim.Network.StatusNotConfigured"));
}
