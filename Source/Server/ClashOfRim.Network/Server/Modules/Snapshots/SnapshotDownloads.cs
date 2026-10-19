using AIRsLight.ClashOfRim.Network.Modules.Authorization;
using AIRsLight.ClashOfRim.Network.Modules.Raids;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;

namespace AIRsLight.ClashOfRim.Network.Modules.Snapshots;

internal enum SnapshotDownloadStatus { Available, InvalidRequest, Unauthorized, UnsupportedStore, NotFound }
internal sealed record SnapshotDownload(ProtocolResponse Result, SaveSnapshotPackage? Package,
    ActiveRaidRecoveryDto? Recovery = null, SnapshotDownloadStatus Status = SnapshotDownloadStatus.Available);

internal sealed class SnapshotDownloads(ColonyAccessAuthorization authorization, SnapshotMutationCoordinator mutations,
    IColonySnapshotIndexStore snapshots, RaidTimeouts timeouts, RaidBattleCleanup cleanup, RaidQueries raids)
{
    internal DownloadLatestSnapshotResponse Metadata(DownloadLatestSnapshotRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.UserId) || string.IsNullOrWhiteSpace(request.ColonyId))
            return new(ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed,
                ServerText.Format("Snapshot.DownloadMissingIdentity")), null, null);

        var download = Capture(request.UserId, request.ColonyId, request.AuthToken,
            request.AuthorizationEventId, request.AuthorizationScope, requestedSnapshotId: null);
        return new(download.Result, download.Package?.Envelope.Identity.SnapshotId,
            download.Package is null ? null : ProtocolDtoMapper.ToMetadataDto(download.Package), download.Recovery);
    }

    internal SnapshotDownload Payload(DownloadLatestSnapshotPayloadRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.UserId) || string.IsNullOrWhiteSpace(request.ColonyId)
            || string.IsNullOrWhiteSpace(request.SnapshotId))
            return new(ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed,
                ServerText.Format("Snapshot.PayloadDownloadMissingIdentity")), null, Status: SnapshotDownloadStatus.InvalidRequest);

        return Capture(request.UserId, request.ColonyId, request.AuthToken,
            request.AuthorizationEventId, request.AuthorizationScope, request.SnapshotId);
    }

    private SnapshotDownload Capture(string userId, string colonyId,
        string? authToken, string? authorizationEventId, string? authorizationScope, string? requestedSnapshotId)
    {
        ProtocolResponse? Authorize() => authorization.Authorize(authToken, userId, colonyId,
            authorizationEventId, authorizationScope, DateTimeOffset.UtcNow);
        SnapshotDownload Denied(ProtocolResponse failure) => new(failure, null, Status: SnapshotDownloadStatus.Unauthorized);

        using (var recovery = mutations.Enter(Authorize))
        {
            if (recovery.Rejection is { } failure) return Denied(failure);
        }
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        // Timeout maintenance is server work; the caller's access is checked again before cleanup and delivery.
        if (requestedSnapshotId is null) timeouts.Reconcile(nowUtc);
        if (snapshots is not IColonySnapshotPackageStore packageStore)
            return new(ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, ServerText.Format("Snapshot.StoreDownloadUnsupported")),
                null, Status: SnapshotDownloadStatus.UnsupportedStore);

        var package = packageStore.GetLatestPackage(userId, colonyId);
        if (package is not null)
            package = cleanup.BeforeDownload(package, userId, colonyId, nowUtc, Authorize);

        using (mutations.EnterUnrecovered())
        {
            if (Authorize() is { } failure) return Denied(failure);
            if (package is null)
                return new(ProtocolResponse.Reject(ProtocolErrorCode.EventNotFound,
                    ServerText.Format(requestedSnapshotId is null ? "Snapshot.LatestNotFound" : "Snapshot.PayloadNotFound")),
                    null, Status: SnapshotDownloadStatus.NotFound);
            // A skipped cleanup returns its captured package. It must still be current at the response boundary.
            if (!SnapshotCommitService.SameVersion(packageStore.GetLatest(userId, colonyId)?.Envelope, package.Envelope))
                return new(ProtocolResponse.Reject(ProtocolErrorCode.SnapshotMismatch, ServerText.Format("Snapshot.PayloadNotFound")),
                    null, Status: SnapshotDownloadStatus.NotFound);
            if (requestedSnapshotId is not null && package.Envelope.Identity.SnapshotId != requestedSnapshotId)
                return new(ProtocolResponse.Reject(ProtocolErrorCode.EventNotFound, ServerText.Format("Snapshot.PayloadNotFound")),
                    null, Status: SnapshotDownloadStatus.NotFound);
            return new(ProtocolResponse.Ok(ServerText.Format("Snapshot.LatestReturned")), package,
                requestedSnapshotId is null ? raids.Recovery(userId, colonyId, DateTimeOffset.UtcNow) : null);
        }
    }
}
