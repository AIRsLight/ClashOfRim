using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Transport;
using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Modules.World;

internal interface IClientWorldMarkerTransport
{
    Task<ClashOfRimClientNetworkResult<ModWorldMapMarkerDeliveryDto>> ReadAsync(ClashOfRimClientNetworkContext context, CancellationToken token);
    Task<ClashOfRimClientNetworkResult<ModSyncRuntimeWorldObjectsResponseDto>> WriteAsync(ClashOfRimClientNetworkContext context,
        IReadOnlyCollection<ModRuntimeWorldObjectMarkerDto> objects, CancellationToken token);
}

internal sealed class ClientWorldMarkerProtocol : IClientWorldMarkerTransport
{
    private readonly ClientProtocolTransport transport;
    private readonly Func<string> notConfigured;
    internal ClientWorldMarkerProtocol(ClientProtocolTransport transport, Func<string> notConfigured)
    { this.transport = transport; this.notConfigured = notConfigured; }
    public Task<ClashOfRimClientNetworkResult<ModWorldMapMarkerDeliveryDto>> ReadAsync(ClashOfRimClientNetworkContext context, CancellationToken token)
        => !context.IsConfigured ? Missing<ModWorldMapMarkerDeliveryDto>()
            : transport.ForContext(context).PostAsync<ModSyncWorldMapMarkersRequestDto, ModWorldMapMarkerDeliveryDto>(
                ProtocolContractManifest.Find(ProtocolMessageKind.SyncWorldMapMarkers).Route,
                new() { UserId = context.UserId, ColonyId = context.ColonyId, KnownAtUtc = Now() }, token);
    public Task<ClashOfRimClientNetworkResult<ModSyncRuntimeWorldObjectsResponseDto>> WriteAsync(ClashOfRimClientNetworkContext context,
        IReadOnlyCollection<ModRuntimeWorldObjectMarkerDto> objects, CancellationToken token)
        => !context.IsConfigured ? Missing<ModSyncRuntimeWorldObjectsResponseDto>()
            : transport.ForContext(context).PostAsync<ModSyncRuntimeWorldObjectsRequestDto, ModSyncRuntimeWorldObjectsResponseDto>(
                ProtocolContractManifest.Find(ProtocolMessageKind.SyncRuntimeWorldObjects).Route, new()
                {
                    UserId = context.UserId, ColonyId = context.ColonyId, SnapshotId = context.CurrentSnapshotId, AuthToken = context.AuthToken,
                    SentAtUtc = Now(), Objects = ClientProtocolJson.Copy(objects?.ToList() ?? new())
                }, token);
    private Task<ClashOfRimClientNetworkResult<T>> Missing<T>() => Task.FromResult(ClashOfRimClientNetworkResult<T>.Failed("NotConfigured", notConfigured()));
    private static string Now() => DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
}
