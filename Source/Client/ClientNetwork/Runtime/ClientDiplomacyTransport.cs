using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.Modules.Diplomacy;
using AIRsLight.ClashOfRim.Modules.Transport;

namespace AIRsLight.ClashOfRim.ClientNetwork;

internal sealed class ClientDiplomacyTransport : IClientDiplomacyCreationTransport
{
    private readonly IClientProtocolEnvironment environment;
    internal ClientDiplomacyTransport(IClientProtocolEnvironment environment) => this.environment = environment;
    public async Task<ClashOfRimClientNetworkResult<ModDiplomacyEventResponseDto>> CreateAsync(
        ClashOfRimClientNetworkContext context, ClientDiplomacyCommand command, CancellationToken token)
    {
        using var http = new HttpClient();
        var protocol = new ClientDiplomacyProtocol(new ClientProtocolTransport(http, context, environment),
            () => ClashOfRimText.Key("ClashOfRim.Network.StatusNotConfigured"));
        return await protocol.CreateAsync(context, command, token);
    }
}
