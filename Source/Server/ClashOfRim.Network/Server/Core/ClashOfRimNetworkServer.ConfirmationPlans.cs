using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using AIRsLight.ClashOfRim.Network.Plugins;

namespace AIRsLight.ClashOfRim.Network;

public static partial class ClashOfRimNetworkServer
{
    private static ConfirmEventApplicationsResponse ReceiveConfirmationSnapshot(ClashOfRimNetworkState state,
        string userId, string colonyId, string? token, SnapshotPackageMetadataDto metadata, byte[] payload,
        SnapshotConfirmationRequest request, DateTimeOffset acceptedAtUtc)
    {
        try { request.Validate(); }
        catch (InvalidDataException ex)
        { return new(ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, ex.Message), null, []); }
        var received = ReceiveSnapshotCore(state, userId, colonyId, metadata.SnapshotId, metadata, payload, acceptedAtUtc,
            postUploadData: new SnapshotPostUploadExtraData(metadata.SnapshotUploadKind, null, null), confirmation: request,
            authorizeCommit: () => AuthorizeColony(state, token, userId, colonyId,
                request.Single ? request.Applications[0].EventId : null, null, DateTimeOffset.UtcNow),
            validateCommit: () =>
            {
                // Recovery may have published this receipt; do not expire a committed confirmation.
                if (state.SnapshotCommits.FindConfirmationReceipt(new SnapshotIdentity(userId, colonyId, metadata.SnapshotId), metadata.OriginalSha256, metadata.PayloadSha256) is null)
                    return state.PendingConfirmations.ReconcileSnapshot(userId, colonyId, DateTimeOffset.UtcNow);
                return null;
            });
        var upload = received.Upload;
        if (!upload.Accepted || upload.AcceptedSnapshot is null)
        {
            var failure = received.Result;
            return new(failure, null, request.Applications.Select(item => new ConfirmEventApplicationResultDto(item.EventId,
                failure, null, upload.Kind.ToString())).ToArray());
        }
        var receipt = state.SnapshotCommits.FindConfirmationReceipt(upload.AcceptedSnapshot.Envelope)
            ?? throw new InvalidOperationException("Confirmed snapshot has no durable result receipt.");
        return new(ProtocolResponse.Ok(T("Events.BatchApplicationConfirmed")), receipt.Envelope.Identity.SnapshotId,
            receipt.Results, receipt.Envelope.NextLineageToken);
    }
}
