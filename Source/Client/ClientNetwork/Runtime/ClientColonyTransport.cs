using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.Modules.Transport;
using AIRsLight.ClashOfRim.Modules.World;

namespace AIRsLight.ClashOfRim.ClientNetwork;

internal sealed class ClientColonyTransport : IClientColonyRegistrationTransport, IClientColonyAbandonTransport, IClientColonyRelocationTransport
{
    private readonly IClientProtocolEnvironment environment;
    internal ClientColonyTransport(IClientProtocolEnvironment environment) => this.environment = environment;
    public Task<ClashOfRimClientNetworkResult<ModRegisterPlayerColonySitesResponseDto>> RegisterAsync(ClashOfRimClientNetworkContext context,
        IReadOnlyCollection<ModPlayerColonySiteDto> sites, IReadOnlyCollection<ModWorldConfigurationExtensionDto>? extensions, bool suppressNotification, CancellationToken token)
        => Run(context, protocol => protocol.RegisterAsync(context, sites, extensions, suppressNotification, token));
    public Task<ClashOfRimClientNetworkResult<ModAbandonPlayerColonyResponseDto>> AbandonAsync(ClashOfRimClientNetworkContext context, string key, CancellationToken token)
        => Run(context, protocol => protocol.AbandonAsync(context, key, token));
    public Task<ClashOfRimClientNetworkResult<ModColonyRelocationResponseDto>> PreflightAsync(ClashOfRimClientNetworkContext context, int tile, string key, int layer, CancellationToken token)
        => Run(context, protocol => protocol.PreflightAsync(context, tile, key, layer, token));
    public Task<ClashOfRimClientNetworkResult<ModColonyRelocationResponseDto>> ConfirmAsync(ClashOfRimClientNetworkContext context, string previous, string relocated, int tile, string key, int layer, CancellationToken token)
        => Run(context, protocol => protocol.ConfirmAsync(context, previous, relocated, tile, key, layer, token));
    private async Task<ClashOfRimClientNetworkResult<T>> Run<T>(ClashOfRimClientNetworkContext context, Func<ClientColonyProtocol, Task<ClashOfRimClientNetworkResult<T>>> action)
    {
        using var http = new HttpClient();
        var protocol = new ClientColonyProtocol(new ClientProtocolTransport(http, context, environment), () => ClashOfRimText.Key("ClashOfRim.Network.StatusNotConfigured"));
        return await action(protocol).ConfigureAwait(false);
    }
}
