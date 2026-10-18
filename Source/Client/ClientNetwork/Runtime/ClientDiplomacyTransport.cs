using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.Modules.Diplomacy;
using AIRsLight.ClashOfRim.Modules.Transport;

namespace AIRsLight.ClashOfRim.ClientNetwork;

internal sealed class ClientDiplomacyTransport : IClientDiplomacyCreationTransport, IClientPlayerDirectoryTransport
{
    private readonly IClientProtocolEnvironment environment;
    internal ClientDiplomacyTransport(IClientProtocolEnvironment environment) => this.environment = environment;
    public Task<ClashOfRimClientNetworkResult<ModDiplomacyEventResponseDto>> CreateAsync(
        ClashOfRimClientNetworkContext context, ClientDiplomacyCommand command, CancellationToken token) =>
        Exchange(context, protocol => protocol.CreateAsync(context, command, token));
    public Task<ClashOfRimClientNetworkResult<ModListPlayersResponseDto>> ListAsync(ClashOfRimClientNetworkContext context, CancellationToken token) =>
        Exchange(context, protocol => protocol.ListAsync(context, token));

    private async Task<ClashOfRimClientNetworkResult<T>> Exchange<T>(ClashOfRimClientNetworkContext context,
        Func<ClientDiplomacyProtocol, Task<ClashOfRimClientNetworkResult<T>>> send)
    {
        using var http = new HttpClient();
        var protocol = new ClientDiplomacyProtocol(new ClientProtocolTransport(http, context, environment),
            () => ClashOfRimText.Key("ClashOfRim.Network.StatusNotConfigured"));
        return await send(protocol);
    }
}
