using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Administration;
using AIRsLight.ClashOfRim.Modules.Transport;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim.Admin;

internal sealed class ClientAdminTransport : IClientAdminTransport
{
    private readonly IClientProtocolEnvironment environment;
    internal ClientAdminTransport(IClientProtocolEnvironment environment) => this.environment = environment;
    public Task<ClashOfRimClientNetworkResult<ModAdminStatusResponseDto>> ReadAsync(ClashOfRimClientNetworkContext context, CancellationToken token)
        => Run(context, protocol => protocol.ReadAsync(context, token));
    public Task<ClashOfRimClientNetworkResult<ModAdminUpdateConfigurationResponseDto>> SaveAsync(ClashOfRimClientNetworkContext context, ModAdminConfigurationDto configuration, CancellationToken token)
        => Run(context, protocol => protocol.SaveAsync(context, configuration, token));
    public Task<ClashOfRimClientNetworkResult<ModAdminActionResponseDto>> ActAsync(ClashOfRimClientNetworkContext context, ClientAdminCommand command, CancellationToken token)
        => Run(context, protocol => protocol.ActAsync(context, command, token));
    private async Task<T> Run<T>(ClashOfRimClientNetworkContext context, Func<ClientAdminProtocol, Task<T>> operation)
    {
        using var http = new HttpClient();
        return await operation(new(new ClientProtocolTransport(http, context, environment), () => ClashOfRimText.Key("ClashOfRim.Network.StatusNotConfigured"))).ConfigureAwait(false);
    }
}
internal sealed class ClientAdminPresentation : IClientAdminPresentation
{
    public string Text(string key, params (string Name, object? Value)[] arguments)
        => ClashOfRimText.Key(key, arguments.Select(argument => (argument.Value ?? "").Named(argument.Name)).ToArray());
    public void Success(string message) => Messages.Message(message, MessageTypeDefOf.PositiveEvent);
    public void Diagnose(string message) => Log.Warning("[ClashOfRim] " + message);
    public void EnforcePermission() => DeveloperToolAccessPolicy.EnforceCurrentState();
    public void Enqueue(Action action, Action discard) => ClientMainThreadDispatcher.EnqueueSession(action, discard);
}
