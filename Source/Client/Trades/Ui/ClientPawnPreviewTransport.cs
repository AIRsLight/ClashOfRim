using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Packages;
using AIRsLight.ClashOfRim.Modules.Trades;
using AIRsLight.ClashOfRim.Modules.Transport;

namespace AIRsLight.ClashOfRim.Trades;

internal sealed class ClientPawnPreviewTransport : IClientPawnPreviewTransport
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly IClientProtocolEnvironment environment;
    internal ClientPawnPreviewTransport(IClientProtocolEnvironment environment) => this.environment = environment;
    private ClientPackageReader Reader(ClashOfRimClientNetworkContext context) => new(new(Http, context, environment), context,
        () => ClashOfRimText.Key("ClashOfRim.Network.StatusNotConfigured"));
    public Task<ClashOfRimClientNetworkResult<byte[]>> PreviewAsync(ClashOfRimClientNetworkContext context, string id, CancellationToken token)
        => Reader(context).GetPawnPackagePreviewAsync(id, token);
    public Task<ClashOfRimClientNetworkResult<ModGetPawnPackageResponseDto>> PackageAsync(ClashOfRimClientNetworkContext context, string id, CancellationToken token)
        => Reader(context).GetPawnPackageAsync(id, token);
}
