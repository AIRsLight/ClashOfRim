using System;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Transport;
using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Modules.Snapshots;

// Both local reloads and remote maps use the same identity-bound download codec.
internal sealed class ClientSnapshotDownloadProtocol
{
    private readonly ClientProtocolTransport transport;
    private readonly ClashOfRimClientNetworkContext context;
    private readonly Func<string> notConfigured;

    internal ClientSnapshotDownloadProtocol(ClientProtocolTransport transport, ClashOfRimClientNetworkContext context,
        Func<string> notConfigured)
    { this.transport = transport.ForContext(context); this.context = context; this.notConfigured = notConfigured; }

    internal Task<ClashOfRimClientNetworkResult<ModDownloadLatestSnapshotResponseDto>> MetadataAsync(
        ClientSnapshotDownloadTarget target, CancellationToken token = default)
    {
        if (!context.IsConfigured) return NotConfigured<ModDownloadLatestSnapshotResponseDto>();
        return transport.PostAsync<ModDownloadLatestSnapshotRequestDto, ModDownloadLatestSnapshotResponseDto>(
            ProtocolContractManifest.Find(ProtocolMessageKind.DownloadLatestSnapshot).Route,
            new ModDownloadLatestSnapshotRequestDto
            {
                UserId = target.UserId, ColonyId = target.ColonyId, AuthToken = context.AuthToken,
                AuthorizationEventId = target.AuthorizationEventId, AuthorizationScope = target.AuthorizationScope
            }, token);
    }

    internal Task<ClashOfRimClientNetworkResult<byte[]>> PayloadAsync(
        ClientSnapshotDownloadTarget target, string snapshotId, CancellationToken token = default)
    {
        if (!context.IsConfigured) return NotConfigured<byte[]>();
        return transport.PostForBytesAsync(ProtocolContractManifest.Find(ProtocolMessageKind.DownloadLatestSnapshotPayload).Route,
            new ModDownloadLatestSnapshotPayloadRequestDto
            {
                UserId = target.UserId, ColonyId = target.ColonyId, SnapshotId = snapshotId ?? string.Empty,
                AuthToken = context.AuthToken, AuthorizationEventId = target.AuthorizationEventId,
                AuthorizationScope = target.AuthorizationScope
            }, token);
    }

    private Task<ClashOfRimClientNetworkResult<T>> NotConfigured<T>() =>
        Task.FromResult(ClashOfRimClientNetworkResult<T>.Failed("NotConfigured", notConfigured()));
}

// Capture the complete authorization scope before either request starts.
internal sealed record ClientSnapshotDownloadTarget(string UserId, string ColonyId,
    string? AuthorizationEventId = null, string? AuthorizationScope = null);
