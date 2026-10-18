using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.Modules.Transport;
using AIRsLight.ClashOfRim.Modules.World;

namespace AIRsLight.ClashOfRim.ClientNetwork;

internal sealed class ClientColonyRegistrationTransport : IClientColonyRegistrationTransport
{
    private readonly IClientProtocolEnvironment environment;
    internal ClientColonyRegistrationTransport(IClientProtocolEnvironment environment) => this.environment = environment;
    public async Task<ClashOfRimClientNetworkResult<ModRegisterPlayerColonySitesResponseDto>> RegisterAsync(ClashOfRimClientNetworkContext context,
        IReadOnlyCollection<ModPlayerColonySiteDto> sites, IReadOnlyCollection<ModWorldConfigurationExtensionDto>? extensions, bool suppressNotification, CancellationToken token)
    {
        using var http = new HttpClient();
        var protocol = new ClientColonyProtocol(new ClientProtocolTransport(http, context, environment), () => ClashOfRimText.Key("ClashOfRim.Network.StatusNotConfigured"));
        return await protocol.RegisterAsync(context, sites, extensions, suppressNotification, token).ConfigureAwait(false);
    }
}
