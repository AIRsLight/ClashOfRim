using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Compatibility;
using AIRsLight.ClashOfRim.Modules.Transport;

namespace AIRsLight.ClashOfRim.CompatibilityClient;

internal sealed class ClientCompatibilityOverrideTransport : IClientCompatibilityOverrideTransport
{
    private readonly IClientProtocolEnvironment environment;
    internal ClientCompatibilityOverrideTransport(IClientProtocolEnvironment environment) => this.environment = environment;
    public async Task<ClashOfRimClientNetworkResult<ModOverrideCompatibilityBaselineResponseDto>> SubmitAsync(
        ClashOfRimClientNetworkContext context, string manifest, CancellationToken token)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var protocol = new ClientCompatibilityOverrideProtocol(new ClientProtocolTransport(http, context, environment),
            () => ClashOfRimText.Key("ClashOfRim.Network.StatusNotConfigured"));
        return await protocol.SubmitAsync(context, manifest, token).ConfigureAwait(false);
    }
}
