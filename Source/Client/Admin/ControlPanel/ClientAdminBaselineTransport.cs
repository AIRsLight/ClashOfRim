using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Administration;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Transport;

namespace AIRsLight.ClashOfRim.Admin;

internal sealed class ClientAdminBaselineTransport : IClientAdminBaselineTransport
{
    private readonly IClientProtocolEnvironment environment;
    internal ClientAdminBaselineTransport(IClientProtocolEnvironment environment) => this.environment = environment;
    public Task<ClashOfRimClientNetworkResult<ModGetAdminBaselineRequirementsResponseDto>> RequirementsAsync(ClashOfRimClientNetworkContext context, CancellationToken token)
        => Run(context, protocol => protocol.RequirementsAsync(context, token));
    public Task<ClashOfRimClientNetworkResult<ModSubmitAdminBaselineResponseDto>> SubmitAsync(ClashOfRimClientNetworkContext context, ModSubmitAdminBaselineRequestDto request, CancellationToken token)
        => Run(context, protocol => protocol.SubmitAsync(context, request, token));
    private async Task<T> Run<T>(ClashOfRimClientNetworkContext context, Func<ClientAdminBaselineProtocol, Task<T>> operation)
    {
        using var http = new HttpClient();
        var diagnostics = new ClientAuthenticationEnvironment();
        var transport = new ClientProtocolTransport(http, context, environment);
        var authentication = new ClientSessionProtocol(transport, diagnostics, diagnostics);
        return await operation(new(new ClientSetupAuthenticationLease(authentication, transport, diagnostics), () => diagnostics.NotConfiguredMessage)).ConfigureAwait(false);
    }
}
