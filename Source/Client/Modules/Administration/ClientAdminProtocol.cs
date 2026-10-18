using System;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Transport;
using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Modules.Administration;

internal sealed class ClientAdminProtocol : IClientAdminTransport
{
    private readonly ClientProtocolTransport transport;
    private readonly Func<string> notConfigured;
    internal ClientAdminProtocol(ClientProtocolTransport transport, Func<string> notConfigured)
    { this.transport = transport; this.notConfigured = notConfigured; }
    public Task<ClashOfRimClientNetworkResult<ModAdminStatusResponseDto>> ReadAsync(ClashOfRimClientNetworkContext context, CancellationToken token)
        => Send<ModAdminStatusRequestDto, ModAdminStatusResponseDto>(context, ProtocolMessageKind.AdminStatus,
            new() { UserId = context.UserId, ColonyId = context.ColonyId, AuthToken = context.AuthToken }, token);
    public Task<ClashOfRimClientNetworkResult<ModAdminUpdateConfigurationResponseDto>> SaveAsync(ClashOfRimClientNetworkContext context, ModAdminConfigurationDto configuration, CancellationToken token)
        => Send<ModAdminUpdateConfigurationRequestDto, ModAdminUpdateConfigurationResponseDto>(context, ProtocolMessageKind.AdminUpdateConfiguration,
            new() { UserId = context.UserId, ColonyId = context.ColonyId, AuthToken = context.AuthToken, Configuration = configuration }, token);
    public Task<ClashOfRimClientNetworkResult<ModAdminActionResponseDto>> ActAsync(ClashOfRimClientNetworkContext context, ClientAdminCommand command, CancellationToken token)
        => Send<ModAdminActionRequestDto, ModAdminActionResponseDto>(context, ProtocolMessageKind.AdminAction,
            new() { UserId = context.UserId, ColonyId = context.ColonyId, AuthToken = context.AuthToken, ActionKind = command.Kind,
                TargetUserId = command.User, TargetColonyId = command.Colony, Message = command.Message,
                NotificationSeverity = command.Severity, PersistentNotification = command.Persistent }, token);
    private Task<ClashOfRimClientNetworkResult<TResponse>> Send<TRequest, TResponse>(ClashOfRimClientNetworkContext context,
        ProtocolMessageKind kind, TRequest request, CancellationToken token) => context.IsConfigured
        ? transport.ForContext(context).PostAsync<TRequest, TResponse>(ProtocolContractManifest.Find(kind).Route, request, token)
        : Task.FromResult(ClashOfRimClientNetworkResult<TResponse>.Failed("NotConfigured", notConfigured()));
}
