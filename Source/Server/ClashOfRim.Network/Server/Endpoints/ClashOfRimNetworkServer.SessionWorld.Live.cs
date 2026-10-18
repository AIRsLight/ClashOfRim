using AIRsLight.ClashOfRim.Network.Modules.Sessions;
using AIRsLight.ClashOfRim.Network.Modules.Economy;
using AIRsLight.ClashOfRim.Network.Modules.Snapshots;
using AIRsLight.ClashOfRim.Compatibility;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network.Plugins;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AIRsLight.ClashOfRim.Network;

public static partial class ClashOfRimNetworkServer
{
    private static string? FindSnapshotNextLineageToken(
        ClashOfRimNetworkState state,
        string userId,
        string colonyId,
        string? snapshotId)
        => state.SnapshotTransactions.FindSnapshotNextLineageToken(userId, colonyId, snapshotId);

    private static async Task<IResult> UploadSnapshot(HttpRequest httpRequest, ClashOfRimNetworkState state)
    {
        MultipartSnapshotRequest<UploadSnapshotMetadataRequest>? multipart = await ReadMultipartSnapshotRequest<UploadSnapshotMetadataRequest>(httpRequest);
        if (multipart is null || multipart.Request is null || multipart.Payload is null)
        {
            return Results.Ok(new UploadSnapshotResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Snapshot.UploadMissingPayload")),
                acceptedSnapshotId: null,
                uploadResult: "MissingMultipartPayload"));
        }

        UploadSnapshotMetadataRequest request = multipart.Request;
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        ProtocolResponse? Authorize() => AuthorizeColony(state, request.AuthToken, request.UserId, request.ColonyId,
            null, null, DateTimeOffset.UtcNow);
        using (var recovery = EnterColonySnapshotMutation(state, request.AuthToken, request.UserId, request.ColonyId))
        {
            if (recovery.Rejection is { } failure)
                return Results.Ok(new UploadSnapshotResponse(failure, acceptedSnapshotId: null, uploadResult: "AuthFailed"));
        }
        // Timeout processing is server maintenance. Admit the caller before starting it,
        // then revalidate the caller after preparing its own snapshot and recovering new work.
        state.RaidTimeouts.Reconcile(nowUtc);

        if (PendingConfirmationReconciler.Rejection(state.PendingConfirmations.ReconcileAfterRecovery(
                request.UserId, request.ColonyId, nowUtc, forceCancel: false)) is { } pendingConfirmationFailure)
        {
            return Results.Ok(new UploadSnapshotResponse(
                pendingConfirmationFailure,
                acceptedSnapshotId: null,
                uploadResult: "PendingConfirmationExpired"));
        }

        var received = ReceiveSnapshotCore(
            state,
            request.UserId,
            request.ColonyId,
            request.SnapshotId,
            request.Package,
            multipart.Payload,
            nowUtc,
            confirmationOperation: request.ConfirmationOperation,
            postUploadData: new SnapshotPostUploadExtraData(request.Package.SnapshotUploadKind, request.ConfirmationOperation, request.AchievementCandidates),
            authorizeCommit: Authorize);
        var upload = received.Upload;
        if (!upload.Accepted)
        {
            state.RuntimeLogger.LogWarning(
                "Snapshot upload rejected: user={UserId} colony={ColonyId} snapshot={SnapshotId} previous={PreviousSnapshotId} kind={Kind} message={Message} operation={Operation}",
                request.UserId,
                request.ColonyId,
                request.SnapshotId,
                request.Package.PreviousSnapshotId,
                upload.Kind,
                upload.Message,
                request.ConfirmationOperation);
        }
        return Results.Ok(new UploadSnapshotResponse(
            received.Result,
            upload.AcceptedSnapshot?.Identity.SnapshotId,
            upload.Kind.ToString(),
            upload.AcceptedSnapshot?.Envelope.NextLineageToken));
    }

    private static IResult DownloadLatestSnapshot(DownloadLatestSnapshotRequest request, ClashOfRimNetworkState state)
    {
        if (string.IsNullOrWhiteSpace(request.UserId) || string.IsNullOrWhiteSpace(request.ColonyId))
            return Results.Ok(new DownloadLatestSnapshotResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Snapshot.DownloadMissingIdentity")), null, null));

        var download = CaptureSnapshotDownload(state, request.UserId, request.ColonyId, request.AuthToken,
            request.AuthorizationEventId, request.AuthorizationScope, requestedSnapshotId: null);
        return Results.Ok(new DownloadLatestSnapshotResponse(download.Result, download.Package?.Envelope.Identity.SnapshotId,
            download.Package is null ? null : ProtocolDtoMapper.ToMetadataDto(download.Package), download.Recovery));
    }

    private static IResult DownloadLatestSnapshotPayload(DownloadLatestSnapshotPayloadRequest request, ClashOfRimNetworkState state)
    {
        if (string.IsNullOrWhiteSpace(request.UserId) || string.IsNullOrWhiteSpace(request.ColonyId)
            || string.IsNullOrWhiteSpace(request.SnapshotId))
            return Results.BadRequest(new { result = ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed,
                T("Snapshot.PayloadDownloadMissingIdentity")) });

        var download = CaptureSnapshotDownload(state, request.UserId, request.ColonyId, request.AuthToken,
            request.AuthorizationEventId, request.AuthorizationScope, request.SnapshotId);
        if (download.Package is null) return Results.Json(new { result = download.Result }, statusCode: download.HttpStatusCode);
        return Results.File(download.Package.Payload, "application/octet-stream",
            (download.Package.Envelope.Identity.SnapshotId ?? "latest") + ".payload");
    }

    private sealed record SnapshotDownloadResult(ProtocolResponse Result, SaveSnapshotPackage? Package,
        ActiveRaidRecoveryDto? Recovery = null, int HttpStatusCode = StatusCodes.Status200OK);

    private static SnapshotDownloadResult CaptureSnapshotDownload(ClashOfRimNetworkState state, string userId, string colonyId,
        string? authToken, string? authorizationEventId, string? authorizationScope, string? requestedSnapshotId)
    {
        ProtocolResponse? Authorize() => AuthorizeColony(state, authToken, userId, colonyId,
            authorizationEventId, authorizationScope, DateTimeOffset.UtcNow);
        SnapshotDownloadResult Denied(ProtocolResponse failure) => new(failure, null, HttpStatusCode: StatusCodes.Status401Unauthorized);

        using (var recovery = state.SnapshotMutations.Enter(Authorize))
        {
            if (recovery.Rejection is { } failure) return Denied(failure);
        }
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        // Timeout maintenance is server work; the caller's access is checked again before cleanup and delivery.
        if (requestedSnapshotId is null) state.RaidTimeouts.Reconcile(nowUtc);
        if (state.SnapshotStore is not IColonySnapshotPackageStore packageStore)
            return new(ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, T("Snapshot.StoreDownloadUnsupported")),
                null, HttpStatusCode: StatusCodes.Status500InternalServerError);

        var package = packageStore.GetLatestPackage(userId, colonyId);
        if (package is not null)
            package = state.RaidBattleCleanup.BeforeDownload(package, userId, colonyId, nowUtc, Authorize);

        using (state.EnterSnapshotMutation())
        {
            if (Authorize() is { } failure) return Denied(failure);
            if (package is null)
                return new(ProtocolResponse.Reject(ProtocolErrorCode.EventNotFound,
                    T(requestedSnapshotId is null ? "Snapshot.LatestNotFound" : "Snapshot.PayloadNotFound")),
                    null, HttpStatusCode: StatusCodes.Status404NotFound);
            // A skipped cleanup returns its captured package. It must still be current at the response boundary.
            if (!SnapshotCommitService.SameVersion(packageStore.GetLatest(userId, colonyId)?.Envelope, package.Envelope))
                return new(ProtocolResponse.Reject(ProtocolErrorCode.SnapshotMismatch, T("Snapshot.PayloadNotFound")),
                    null, HttpStatusCode: StatusCodes.Status404NotFound);
            if (requestedSnapshotId is not null && package.Envelope.Identity.SnapshotId != requestedSnapshotId)
                return new(ProtocolResponse.Reject(ProtocolErrorCode.EventNotFound, T("Snapshot.PayloadNotFound")),
                    null, HttpStatusCode: StatusCodes.Status404NotFound);
            return new(ProtocolResponse.Ok(T("Snapshot.LatestReturned")), package,
                requestedSnapshotId is null ? state.RaidQueries.Recovery(userId, colonyId, DateTimeOffset.UtcNow) : null);
        }
    }

}
