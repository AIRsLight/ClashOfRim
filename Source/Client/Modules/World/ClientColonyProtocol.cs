using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Transport;
using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Modules.World;

internal interface IClientColonyRegistrationTransport
{
    Task<ClashOfRimClientNetworkResult<ModRegisterPlayerColonySitesResponseDto>> RegisterAsync(ClashOfRimClientNetworkContext context,
        IReadOnlyCollection<ModPlayerColonySiteDto> sites, IReadOnlyCollection<ModWorldConfigurationExtensionDto>? extensions,
        bool suppressNotification, CancellationToken token);
}

internal sealed class ClientColonyProtocol : IClientColonyRegistrationTransport, IClientColonyAbandonTransport, IClientColonyRelocationTransport
{
    private readonly ClientProtocolTransport transport;
    private readonly Func<string> notConfigured;
    internal ClientColonyProtocol(ClientProtocolTransport transport, Func<string> notConfigured)
    { this.transport = transport; this.notConfigured = notConfigured; }
    public Task<ClashOfRimClientNetworkResult<ModRegisterPlayerColonySitesResponseDto>> RegisterAsync(ClashOfRimClientNetworkContext context,
        IReadOnlyCollection<ModPlayerColonySiteDto> sites, IReadOnlyCollection<ModWorldConfigurationExtensionDto>? extensions,
        bool suppressNotification, CancellationToken token)
        => Send<ModRegisterPlayerColonySitesRequestDto, ModRegisterPlayerColonySitesResponseDto>(context, ProtocolMessageKind.RegisterPlayerColonySites, new()
        {
            UserId = context.UserId, ColonyId = context.ColonyId, AuthToken = context.AuthToken,
            Sites = sites?.ToList() ?? new(), Extensions = extensions?.ToList() ?? new(), SuppressWorldConfigurationNotification = suppressNotification
        }, token);
    public Task<ClashOfRimClientNetworkResult<ModColonyRelocationResponseDto>> PreflightAsync(ClashOfRimClientNetworkContext context,
        int tile, string key, int layer, CancellationToken token)
        => Send<ModPreflightColonyRelocationRequestDto, ModColonyRelocationResponseDto>(context, ProtocolMessageKind.PreflightColonyRelocation, new()
        {
            UserId = context.UserId, ColonyId = context.ColonyId, AuthToken = context.AuthToken, CurrentSnapshotId = context.CurrentSnapshotId ?? "",
            TargetTile = tile, TargetTileLayerId = Math.Max(0, layer), IdempotencyKey = key
        }, token);
    public Task<ClashOfRimClientNetworkResult<ModColonyRelocationResponseDto>> ConfirmAsync(ClashOfRimClientNetworkContext context,
        string previous, string relocated, int tile, string key, int layer, CancellationToken token)
        => Send<ModConfirmColonyRelocationRequestDto, ModColonyRelocationResponseDto>(context, ProtocolMessageKind.ConfirmColonyRelocation, new()
        {
            UserId = context.UserId, ColonyId = context.ColonyId, AuthToken = context.AuthToken, PreviousSnapshotId = previous ?? "", RelocatedSnapshotId = relocated ?? "",
            TargetTile = tile, TargetTileLayerId = Math.Max(0, layer), IdempotencyKey = key
        }, token);
    public Task<ClashOfRimClientNetworkResult<ModAbandonPlayerColonyResponseDto>> AbandonAsync(ClashOfRimClientNetworkContext context, string key, CancellationToken token)
        => Send<ModAbandonPlayerColonyRequestDto, ModAbandonPlayerColonyResponseDto>(context, ProtocolMessageKind.AbandonPlayerColony, new()
        {
            UserId = context.UserId, ColonyId = context.ColonyId, AuthToken = context.AuthToken, CurrentSnapshotId = context.CurrentSnapshotId, IdempotencyKey = key
        }, token);
    private Task<ClashOfRimClientNetworkResult<TResponse>> Send<TRequest, TResponse>(ClashOfRimClientNetworkContext context,
        ProtocolMessageKind kind, TRequest request, CancellationToken token)
        => !context.IsConfigured ? Task.FromResult(ClashOfRimClientNetworkResult<TResponse>.Failed("NotConfigured", notConfigured()))
            : transport.ForContext(context).PostAsync<TRequest, TResponse>(ProtocolContractManifest.Find(kind).Route, ClientProtocolJson.Copy(request), token);
}
