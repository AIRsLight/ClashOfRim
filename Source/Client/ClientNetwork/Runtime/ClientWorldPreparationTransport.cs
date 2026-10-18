using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Transport;
using AIRsLight.ClashOfRim.Modules.World;

namespace AIRsLight.ClashOfRim.ClientNetwork;

internal sealed class ClientWorldPreparationTransport : IClientWorldPreparationTransport, IClientWorldSubstrateTransport, IClientWorldFeatureCatalogTransport, IClientWorldBaselineTransport
{
    private readonly IClientProtocolEnvironment environment;
    internal ClientWorldPreparationTransport(IClientProtocolEnvironment environment) => this.environment = environment;
    public Task<ClashOfRimClientNetworkResult<ModServerHelloResponseDto>> HelloAsync(ClashOfRimClientNetworkContext context, CancellationToken token)
        => Run(context, protocol => protocol.HelloAsync(context, token));
    public Task<ClashOfRimClientNetworkResult<ModPrepareWorldSessionResponseDto>> PrepareAsync(ClashOfRimClientNetworkContext context, bool createAccount, CancellationToken token)
        => Run(context, protocol => protocol.PrepareAsync(context, createAccount, token));
    public Task<ClashOfRimClientNetworkResult<ModGetWorldConfigurationResponseDto>> ReadAsync(ClashOfRimClientNetworkContext context, bool generation, CancellationToken token)
        => Run(context, protocol => protocol.ReadAsync(context, generation, true, true, token));
    public Task<ClashOfRimClientNetworkResult<byte[]>> DownloadAsync(ClashOfRimClientNetworkContext context, string configurationId, CancellationToken token)
        => Run(context, protocol => protocol.DownloadSubstrateAsync(context, configurationId, token));
    public Task<ClashOfRimClientNetworkResult<ModSubmitWorldConfigurationResponseDto>> SubmitAsync(ClashOfRimClientNetworkContext context, ModWorldConfigurationDto configuration, CancellationToken token)
        => Run(context, protocol => protocol.SubmitAsync(context, configuration, token));
    public Task<ClashOfRimClientNetworkResult<ModUploadWorldSubstrateResponseDto>> UploadAsync(ClashOfRimClientNetworkContext context, string worldId, byte[] payload, CancellationToken token)
        => Run(context, protocol => protocol.UploadSubstrateAsync(context, worldId, payload, token));
    public Task<ClashOfRimClientNetworkResult<ModSubmitWorldFeatureNamesResponseDto>> SubmitNamesAsync(ClashOfRimClientNetworkContext context, string language, string world, System.Collections.Generic.IReadOnlyList<ModWorldFeatureDto> features, CancellationToken token)
        => Run(context, protocol => protocol.SubmitNamesAsync(context, language, world, features, token));
    private async Task<T> Run<T>(ClashOfRimClientNetworkContext context, Func<ClientWorldProtocol, Task<T>> operation)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
        var diagnostics = new ClientAuthenticationEnvironment();
        var transport = new ClientProtocolTransport(http, context, environment);
        var login = new ClientSessionProtocol(transport, diagnostics, diagnostics);
        var lease = new ClientSetupAuthenticationLease(login, transport, diagnostics);
        return await operation(new(transport, lease, diagnostics, diagnostics, diagnostics.LogWorldEntry)).ConfigureAwait(false);
    }
}
