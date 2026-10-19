using AIRsLight.ClashOfRim.Network.Modules.Authorization;
using AIRsLight.ClashOfRim.Network.Modules.Economy;
using AIRsLight.ClashOfRim.Network.Modules.Raids;
using AIRsLight.ClashOfRim.Network.Modules.Snapshots;
using AIRsLight.ClashOfRim.Network.Plugins;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;

namespace AIRsLight.ClashOfRim.Network.Modules.Events;

internal sealed class EventConfirmations(ColonyAccessAuthorization authorization,
    SnapshotReceptionService snapshots, SnapshotCommitService commits, PendingConfirmationReconciler pending,
    RaidEvidenceConfirmation raidEvidence)
{
    internal ConfirmEventApplicationResponse Confirm(ConfirmEventApplicationMetadataRequest request,
        byte[] payload, string? principalToken)
    {
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        if (authorization.Authorize(request.AuthToken, request.UserId, request.ColonyId, request.EventId,
                authorizationScope: null, nowUtc) is { } authFailure)
            return new(authFailure, request.EventId, appliedSnapshotId: null, serverValidationResult: "AuthFailed");

        if (raidEvidence.Handles(request.EventId, request.SourceEventId))
            return raidEvidence.Confirm(request, payload, principalToken, nowUtc);

        var response = Receive(request.UserId, request.ColonyId, request.AuthToken,
            request.ConfirmedSnapshot, payload, new SnapshotConfirmationRequest(request.BaseSnapshotId,
                [new ConfirmEventApplicationEntry(request.EventId, request.SourceEventId, request.ClientApplicationResult)], true), nowUtc);
        var result = response.Applications.FirstOrDefault();
        return new(result?.Result ?? response.Result, request.EventId,
            result?.AppliedSnapshotId, result?.ServerValidationResult ?? "InvalidConfirmation", response.NextLineageToken);
    }

    internal ConfirmEventApplicationsResponse Confirm(ConfirmEventApplicationsMetadataRequest request, byte[] payload)
    {
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        if (authorization.Authorize(request.AuthToken, request.UserId, request.ColonyId,
                authorizationEventId: null, authorizationScope: null, nowUtc) is { } authFailure)
            return new(authFailure, appliedSnapshotId: null, applications: []);

        return Receive(request.UserId, request.ColonyId, request.AuthToken, request.ConfirmedSnapshot, payload,
            new SnapshotConfirmationRequest(request.BaseSnapshotId, request.Applications, false), nowUtc);
    }

    private ConfirmEventApplicationsResponse Receive(
        string userId, string colonyId, string? token, SnapshotPackageMetadataDto metadata, byte[] payload,
        SnapshotConfirmationRequest request, DateTimeOffset acceptedAtUtc)
    {
        try { request.Validate(); }
        catch (InvalidDataException ex)
        { return new(ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, ex.Message), null, []); }
        var received = snapshots.Receive(userId, colonyId, metadata.SnapshotId, metadata, payload, acceptedAtUtc,
            postUploadData: new SnapshotPostUploadExtraData(metadata.SnapshotUploadKind, null, null), confirmation: request,
            authorizeCommit: () => authorization.Authorize(token, userId, colonyId,
                request.Single ? request.Applications[0].EventId : null, null, DateTimeOffset.UtcNow),
            validateCommit: () =>
            {
                // Recovery may have published this receipt; do not expire a committed confirmation.
                if (commits.FindConfirmationReceipt(new SnapshotIdentity(userId, colonyId, metadata.SnapshotId), metadata.OriginalSha256, metadata.PayloadSha256) is null)
                    return pending.ReconcileSnapshot(userId, colonyId, DateTimeOffset.UtcNow);
                return null;
            });
        var upload = received.Upload;
        if (!upload.Accepted || upload.AcceptedSnapshot is null)
        {
            var failure = received.Result;
            return new(failure, null, request.Applications.Select(item => new ConfirmEventApplicationResultDto(item.EventId,
                failure, null, upload.Kind.ToString())).ToArray());
        }
        var receipt = commits.FindConfirmationReceipt(upload.AcceptedSnapshot.Envelope)
            ?? throw new InvalidOperationException("Confirmed snapshot has no durable result receipt.");
        return new(ProtocolResponse.Ok(ServerText.Format("Events.BatchApplicationConfirmed")), receipt.Envelope.Identity.SnapshotId,
            receipt.Results, receipt.Envelope.NextLineageToken);
    }
}
