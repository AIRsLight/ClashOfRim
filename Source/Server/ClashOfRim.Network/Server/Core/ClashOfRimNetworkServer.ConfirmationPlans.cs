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
        ProtocolResponse? validationFailure = null;
        var upload = ReceiveSnapshotCore(state, userId, colonyId, metadata.SnapshotId, metadata, payload, acceptedAtUtc,
            postUploadData: new SnapshotPostUploadExtraData(metadata.SnapshotUploadKind, null, null), confirmation: request,
            authorizeCommit: () =>
            {
                validationFailure = IsAuthorizedForColony(state, token, userId, colonyId,
                    request.Single ? request.Applications[0].EventId : null, null, DateTimeOffset.UtcNow, out var authFailure)
                    ? null : ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, authFailure);
                return validationFailure?.Message;
            },
            validateCommit: () =>
            {
                // Recovery may have published this receipt; do not expire a committed confirmation.
                if (state.SnapshotCommits.FindConfirmationReceipt(new SnapshotIdentity(userId, colonyId, metadata.SnapshotId), metadata.OriginalSha256, metadata.PayloadSha256) is null)
                    validationFailure = state.PendingConfirmations.ReconcileSnapshot(userId, colonyId, DateTimeOffset.UtcNow);
                return validationFailure?.Message;
            });
        if (!upload.Accepted || upload.AcceptedSnapshot is null)
        {
            var failure = validationFailure ?? ToProtocolResponse(upload);
            return new(failure, null, request.Applications.Select(item => new ConfirmEventApplicationResultDto(item.EventId,
                failure, null, upload.Kind.ToString())).ToArray());
        }
        var receipt = state.SnapshotCommits.FindConfirmationReceipt(upload.AcceptedSnapshot.Envelope)
            ?? throw new InvalidOperationException("Confirmed snapshot has no durable result receipt.");
        return new(ProtocolResponse.Ok(T("Events.BatchApplicationConfirmed")), receipt.Envelope.Identity.SnapshotId,
            receipt.Results, receipt.Envelope.NextLineageToken);
    }

    private static SnapshotConfirmationPlan PlanSnapshotConfirmations(ClashOfRimNetworkState state,
        SnapshotConfirmationMutation mutation, LatestSnapshotRecord snapshot)
    {
        mutation.Preflight(state.Ledger);
        var copy = new InMemoryAuthoritativeEventLedger();
        var sources = mutation.Sources.Select(item => state.Ledger.Find(item.EventId)).Where(item => item is not null).Cast<AuthoritativeEvent>().ToArray();
        foreach (var source in sources) copy.Append(source);
        var achievements = new Dictionary<string, AuthoritativeEvent>(StringComparer.Ordinal);
        var results = mutation.Request.Applications.Select(application => EvaluateEventApplication(application,
            snapshot.Identity.OwnerId!, snapshot.Identity.ColonyId!, mutation.Request.BaseSnapshotId, snapshot, copy,
            snapshot.AcceptedAtUtc, item => achievements[item.EventId] = item)).ToArray();
        var plan = new SnapshotConfirmationPlan(results, sources.Select(source => new LedgerConfirmationChange(source, copy.Find(source.EventId)!)).ToArray(),
            achievements.Values.ToArray());
        (mutation with { Plan = plan }).Validate(snapshot);
        return plan;
    }
}
