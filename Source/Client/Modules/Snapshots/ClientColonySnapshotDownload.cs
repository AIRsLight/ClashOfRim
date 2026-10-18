using System;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Maps;

namespace AIRsLight.ClashOfRim.Modules.Snapshots;

// Produces loadable bytes for the captured colony. Session adoption and game loading
// remain separate operations and must recheck their ownership before publication.
internal sealed class ClientColonySnapshotDownload
{
    private readonly IClientRemoteSnapshotTransport transport;
    internal ClientColonySnapshotDownload(IClientRemoteSnapshotTransport transport) => this.transport = transport;

    internal async Task<ClientColonySnapshotDownloadResult> DownloadAsync(ClashOfRimClientNetworkContext context,
        CancellationToken token, Action<string>? payloadStarting = null)
    {
        token.ThrowIfCancellationRequested();
        var download = await transport.DownloadAsync(context, new(context.UserId, context.ColonyId), token, payloadStarting).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (!ClientRemoteSnapshotDownload.TryGetOpenPayload(download, out var open, out var remoteFailure))
        {
            var kind = remoteFailure!.Kind switch
            {
                ClientRemoteSnapshotFailureKind.Metadata => download.Metadata.Success && download.Metadata.Response is not null
                    ? ClientColonySnapshotFailureKind.Rejected : ClientColonySnapshotFailureKind.Metadata,
                ClientRemoteSnapshotFailureKind.MissingSnapshot => ClientColonySnapshotFailureKind.MissingSnapshot,
                ClientRemoteSnapshotFailureKind.Payload => ClientColonySnapshotFailureKind.Payload,
                _ => throw new ArgumentOutOfRangeException()
            };
            return new(null, new(kind, remoteFailure.Code, remoteFailure.Message));
        }
        var package = open!.Package;
        if (package.OwnerId != context.UserId || package.ColonyId != context.ColonyId)
            return new(null, new(ClientColonySnapshotFailureKind.Identity,
                Expected: context.UserId + "/" + context.ColonyId, Actual: package.OwnerId + "/" + package.ColonyId));
        if (open.SnapshotId != package.SnapshotId)
            return new(null, new(ClientColonySnapshotFailureKind.SnapshotId, Expected: open.SnapshotId, Actual: package.SnapshotId));
        if (!ClientSnapshotRestoreSave.TryBuild(package, open.Payload, context.UserId, context.ColonyId,
            out string name, out byte[] bytes, out var invalid))
            return new(null, new(ClientColonySnapshotFailureKind.Save, SaveFailure: invalid));
        token.ThrowIfCancellationRequested();
        var recovery = download.Metadata.Response!.ActiveRaidRecovery;
        return new(new(open.SnapshotId, package.NextLineageToken ?? "", name, bytes,
            recovery is null ? null : new()
            {
                EventId = recovery.EventId, Status = recovery.Status, ServerNowUtc = recovery.ServerNowUtc,
                StartedAtUtc = recovery.StartedAtUtc, DeadlineUtc = recovery.DeadlineUtc, FinalDeadlineUtc = recovery.FinalDeadlineUtc,
                DefenderUserId = recovery.DefenderUserId, DefenderColonyId = recovery.DefenderColonyId, DefenderSnapshotId = recovery.DefenderSnapshotId
            }));
    }
}

internal sealed record ClientRestoredSnapshot(string SnapshotId, string LineageToken, string SaveName, byte[] SaveBytes,
    ModActiveRaidRecoveryDto? ActiveRaidRecovery);
internal sealed record ClientColonySnapshotDownloadResult(ClientRestoredSnapshot? Snapshot, ClientColonySnapshotFailure? Failure = null);
internal enum ClientColonySnapshotFailureKind { Metadata, Rejected, MissingSnapshot, Payload, Identity, SnapshotId, Save }
internal sealed record ClientColonySnapshotFailure(ClientColonySnapshotFailureKind Kind, string? Code = null, string? Message = null,
    string? Expected = null, string? Actual = null, ClientSnapshotRestoreFailure? SaveFailure = null);
