using AIRsLight.ClashOfRim.Network.Modules.Authorization;
using AIRsLight.ClashOfRim.Network.Modules.Economy;
using AIRsLight.ClashOfRim.Network.Modules.Raids;
using AIRsLight.ClashOfRim.Network.Plugins;
using AIRsLight.ClashOfRim.Protocol;
using Microsoft.Extensions.Logging;

namespace AIRsLight.ClashOfRim.Network.Modules.Snapshots;

internal sealed class SnapshotUploads(ColonyAccessAuthorization authorization, SnapshotMutationCoordinator mutations,
    SnapshotReceptionService reception, PendingConfirmationReconciler pending, RaidTimeouts timeouts, Func<ILogger> logger)
{
    internal UploadSnapshotResponse Upload(UploadSnapshotMetadataRequest request, byte[] payload)
    {
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        ProtocolResponse? Authorize() => authorization.Authorize(request.AuthToken, request.UserId, request.ColonyId,
            null, null, DateTimeOffset.UtcNow);
        using (var recovery = mutations.Enter(Authorize))
        {
            if (recovery.Rejection is { } failure)
                return new UploadSnapshotResponse(failure, acceptedSnapshotId: null, uploadResult: "AuthFailed");
        }
        // Timeout processing is server maintenance. Admit the caller before starting it,
        // then revalidate the caller after preparing its own snapshot and recovering new work.
        timeouts.Reconcile(nowUtc);

        if (PendingConfirmationReconciler.Rejection(pending.ReconcileAfterRecovery(
                request.UserId, request.ColonyId, nowUtc, forceCancel: false)) is { } pendingConfirmationFailure)
        {
            return new UploadSnapshotResponse(
                pendingConfirmationFailure,
                acceptedSnapshotId: null,
                uploadResult: "PendingConfirmationExpired");
        }

        var received = reception.Receive(
            request.UserId,
            request.ColonyId,
            request.SnapshotId,
            request.Package,
            payload,
            nowUtc,
            confirmationOperation: request.ConfirmationOperation,
            postUploadData: new SnapshotPostUploadExtraData(request.Package.SnapshotUploadKind, request.ConfirmationOperation, request.AchievementCandidates),
            authorizeCommit: Authorize);
        var upload = received.Upload;
        if (!upload.Accepted)
        {
            logger().LogWarning(
                "Snapshot upload rejected: user={UserId} colony={ColonyId} snapshot={SnapshotId} previous={PreviousSnapshotId} kind={Kind} message={Message} operation={Operation}",
                request.UserId,
                request.ColonyId,
                request.SnapshotId,
                request.Package.PreviousSnapshotId,
                upload.Kind,
                upload.Message,
                request.ConfirmationOperation);
        }
        return new UploadSnapshotResponse(
            received.Result,
            upload.AcceptedSnapshot?.Identity.SnapshotId,
            upload.Kind.ToString(),
            upload.AcceptedSnapshot?.Envelope.NextLineageToken);
    }
}
