using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.Modules.Transport;
using AIRsLight.ClashOfRim.Modules.World;

namespace AIRsLight.ClashOfRim.ClientNetwork;

internal sealed class ClientWorldMarkerTransport : IClientWorldMarkerTransport
{
    private readonly IClientProtocolEnvironment environment;
    internal ClientWorldMarkerTransport(IClientProtocolEnvironment environment) => this.environment = environment;
    public Task<ClashOfRimClientNetworkResult<ModWorldMapMarkerDeliveryDto>> ReadAsync(ClashOfRimClientNetworkContext context, CancellationToken token)
        => Run(context, protocol => protocol.ReadAsync(context, token));
    public Task<ClashOfRimClientNetworkResult<ModSyncRuntimeWorldObjectsResponseDto>> WriteAsync(ClashOfRimClientNetworkContext context,
        IReadOnlyCollection<ModRuntimeWorldObjectMarkerDto> objects, CancellationToken token)
        => Run(context, protocol => protocol.WriteAsync(context, objects, token));
    private async Task<ClashOfRimClientNetworkResult<T>> Run<T>(ClashOfRimClientNetworkContext context, Func<ClientWorldMarkerProtocol, Task<ClashOfRimClientNetworkResult<T>>> action)
    {
        using var http = new HttpClient();
        var protocol = new ClientWorldMarkerProtocol(new ClientProtocolTransport(http, context, environment), () => ClashOfRimText.Key("ClashOfRim.Network.StatusNotConfigured"));
        return await action(protocol).ConfigureAwait(false);
    }
}
