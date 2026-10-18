using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.Modules.Events;
using AIRsLight.ClashOfRim.Modules.Transport;

namespace AIRsLight.ClashOfRim.ClientNetwork;

internal sealed class ClientEventConfirmationTransport : IClientEventConfirmationProtocol
{
    public async Task<ClashOfRimClientNetworkResult<ModConfirmEventApplicationResponseDto>> ConfirmAsync(
        ClashOfRimClientNetworkContext context, ClientEventConfirmationCommand command, CancellationToken token)
    {
        using var http = new HttpClient();
        if (command.HttpTimeout.HasValue) http.Timeout = command.HttpTimeout.Value;
        return await Create(http, context).ConfirmAsync(context, command, token);
    }
    public async Task<ClashOfRimClientNetworkResult<ModConfirmEventApplicationsResponseDto>> ConfirmBatchAsync(
        ClashOfRimClientNetworkContext context, ClientEventBatchConfirmationCommand command, CancellationToken token)
    {
        using var http = new HttpClient();
        return await Create(http, context).ConfirmBatchAsync(context, command, token);
    }
    private static ClientEventConfirmationProtocol Create(HttpClient http, ClashOfRimClientNetworkContext context) => new(
        new ClientProtocolTransport(http, context, new ClientProtocolEnvironment()),
        () => ClashOfRimText.Key("ClashOfRim.Network.StatusNotConfigured"));
}
