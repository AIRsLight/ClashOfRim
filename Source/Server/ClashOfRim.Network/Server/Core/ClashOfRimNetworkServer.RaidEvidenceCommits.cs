using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network.Plugins;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace AIRsLight.ClashOfRim.Network;

public static partial class ClashOfRimNetworkServer
{
    private static IResult ConfirmRaidEvidenceSnapshot(HttpRequest http, ClashOfRimNetworkState state,
        ConfirmEventApplicationMetadataRequest request, byte[] bytes, DateTimeOffset nowUtc)
    {
        string binding = RaidEvidenceRequestBinding.Hash(request, bytes);
        AuthoritativeEvent source;
        using (state.EnterSnapshotMutation())
        {
            var failure = ValidateRaidEvidenceAuthority(http, state, request, out source!);
            if (failure is not null) return RaidEvidenceRejected(request, failure, "AuthOrSourceFailed");
            if (TryRejectExpiredPendingConfirmationSnapshot(state, request.UserId, request.ColonyId, DateTimeOffset.UtcNow, out var pending))
                return RaidEvidenceRejected(request, pending!, "PendingConfirmationExpired");
            if (source.Status == ServerEventStatus.AppliedToSnapshot) return CompletedRaidEvidenceReply(state, request, binding);
            var existing = state.SnapshotPostUploadJobs.Find("raid-settlement:" + request.EventId);
            if (existing is not null && RaidSettlementDeferredPayload.Deserialize(existing.PayloadJson).RequestHash != binding)
                return RaidEvidenceRejected(request, ProtocolResponse.Reject(ProtocolErrorCode.Conflict,
                    T("Raid.SettlementQueueFailed")), "QueuedRequestConflict");
        }
        var receiver = CreateSnapshotReceiver(state);
        var package = ProtocolDtoMapper.ToSaveSnapshotPackage(request.ConfirmedSnapshot, bytes);
        var uploadContext = new SnapshotUploadContext(request.UserId, request.ColonyId, request.ConfirmedSnapshot.SnapshotId,
            SnapshotUploadKind: SnapshotUploadKinds.RaidSettlementEvidence, RequiredRaidEventId: request.EventId);
        var preparedUpload = SnapshotPreparationService.Prepare(receiver, uploadContext, package, nowUtc);
        while (true)
        {
            RaidEvidenceSchedulePreparation preparation;
            using (state.EnterSnapshotMutation())
            {
                var rejection = RevalidateRaidEvidence(http, state, request, source, binding);
                if (rejection is not null) return rejection;
                if (!preparedUpload.Accepted || preparedUpload.AcceptedSnapshot is null)
                    return RaidEvidenceRejected(request, ToProtocolResponse(preparedUpload), preparedUpload.Kind.ToString());
                lock (state.SnapshotPostUploadJobs.SnapshotCommitGate)
                {
                    bool replay = state.SnapshotPostUploadJobs.Find("raid-settlement:" + source.EventId) is not null;
                    var upload = replay ? preparedUpload : receiver.CommitPrepared(uploadContext, package, preparedUpload, storeAccepted: false);
                    if (!upload.Accepted || upload.AcceptedSnapshot is null)
                        return RaidEvidenceRejected(request, ToProtocolResponse(upload), upload.Kind.ToString());
                    var context = CreateRaidEvidenceContext(state, request, bytes, upload.AcceptedSnapshot, nowUtc, binding);
                    if (!RaidEvidenceSchedulePreparation.Supports(state))
                        return RunLegacyRaidEvidencePipeline(state, request, context, replay);
                    try
                    {
                        preparation = new RaidEvidenceSchedulePreparation(state,
                            new RaidSettlementScheduleRequest(source.EventId, context, bytes, RaidSettlementOrigin.OnlineEvidence, request.ClientApplicationResult, binding));
                    }
                    catch (Exception ex) { return RaidEvidenceQueueFailure(state, request, ex); }
                }
            }
            using (preparation)
            {
                Exception? prepareFailure = null;
                try { SnapshotPreparationService.Run(() => { preparation.Prepare(preparedUpload.PreparedStoragePayload); return true; }); }
                catch (Exception ex) { prepareFailure = ex; }
                using (state.EnterSnapshotMutation())
                {
                    var rejection = RevalidateRaidEvidence(http, state, request, source, binding);
                    if (rejection is not null) return rejection;
                    lock (state.SnapshotPostUploadJobs.SnapshotCommitGate)
                    {
                        // Prefer the newly committed same-key task over errors from an obsolete preparation.
                        if (!preparation.HasSameJob) continue;
                        if (prepareFailure is not null) return RaidEvidenceQueueFailure(state, request, prepareFailure);
                        try
                        {
                            if (!preparation.TryValidate()) continue;
                            var context = preparation.Context;
                            context = context with { ExtraData = context.ExtraData with
                                { RaidSettlement = context.ExtraData.RaidSettlement! with { PreparedSchedule = preparation } } };
                            RunSnapshotPostUploadProcessors(state, context);
                            return QueuedRaidEvidenceReply(request, preparation.Envelope, preparation.IsReplay);
                        }
                        catch (Exception ex) { return RaidEvidenceQueueFailure(state, request, ex); }
                    }
                }
            }
        }
    }

    private static IResult? RevalidateRaidEvidence(HttpRequest http, ClashOfRimNetworkState state,
        ConfirmEventApplicationMetadataRequest request, AuthoritativeEvent source, string binding)
    {
        var failure = ValidateRaidEvidenceAuthority(http, state, request, out _);
        if (failure is not null) return RaidEvidenceRejected(request, failure, "AuthOrSourceFailed");
        RecoverSnapshotCommits(state);
        var current = state.Ledger.Find(request.EventId);
        if (current is null || !RaidSettlementSnapshotMutation.SameSource(current, source)
            || current.Status != source.Status && current.Status != ServerEventStatus.AppliedToSnapshot)
            return RaidEvidenceRejected(request, ProtocolResponse.Reject(ProtocolErrorCode.Conflict, T("Raid.SettlementQueueFailed")), "SourceChanged");
        if (TryRejectExpiredPendingConfirmationSnapshot(state, request.UserId, request.ColonyId, DateTimeOffset.UtcNow, out var pending))
            return RaidEvidenceRejected(request, pending!, "PendingConfirmationExpired");
        return current.Status == ServerEventStatus.AppliedToSnapshot ? CompletedRaidEvidenceReply(state, request, binding) : null;
    }

    private static SnapshotPostUploadContext CreateRaidEvidenceContext(ClashOfRimNetworkState state,
        ConfirmEventApplicationMetadataRequest request, byte[] bytes, LatestSnapshotRecord snapshot, DateTimeOffset at, string binding)
        => new(state, SnapshotPostUploadKind.RaidSettlementEvidence, snapshot, request.UserId, request.ColonyId, null, at,
            new SnapshotPostUploadExtraData(SnapshotUploadKinds.RaidSettlementEvidence, null, null)
            { RaidSettlement = new(request.EventId, bytes, RaidSettlementOrigin.OnlineEvidence, request.ClientApplicationResult, binding) }, false);

    private static IResult RunLegacyRaidEvidencePipeline(ClashOfRimNetworkState state,
        ConfirmEventApplicationMetadataRequest request, SnapshotPostUploadContext context, bool replay)
    {
        try
        {
            if (replay)
            {
                // Validate the original request before running any replayable post-processing.
                var data = context.ExtraData.RaidSettlement!;
                var job = state.SnapshotPostUploadJobs.Find("raid-settlement:" + request.EventId)!;
                var payload = RaidSettlementDeferredPayload.Deserialize(job.PayloadJson);
                var original = state.SnapshotPostUploadArtifacts.Read(payload.EvidenceArtifactId) ?? throw new IOException("Raid evidence artifact is missing.");
                RaidSettlementDeferredScheduler.ValidatePayload(original);
                RaidSettlementDeferredScheduler.ValidateEvidenceBinding(state, job, payload, original);
                if (job.State is not (SnapshotPostUploadJobState.Prepared or SnapshotPostUploadJobState.Ready)
                    || payload.RequestHash != data.RequestHash || payload.Origin != data.Origin || payload.ClientApplicationResult != data.ClientApplicationResult
                    || original.Envelope != (context.Snapshot.Envelope with { CreatedAtUtc = original.Envelope.CreatedAtUtc, NextLineageToken = original.Envelope.NextLineageToken })
                    || !original.Payload.AsSpan().SequenceEqual(data.EvidencePayload))
                    throw new InvalidOperationException("Raid evidence retry differs from its original request.");
                context = context with { Snapshot = new LatestSnapshotRecord(original.Envelope.Identity, original.Envelope, original.Index, job.OccurredAtUtc), OccurredAtUtc = job.OccurredAtUtc };
            }
            RunSnapshotPostUploadProcessors(state, context);
            return QueuedRaidEvidenceReply(request, context.Snapshot.Envelope, replay);
        }
        catch (Exception ex) { return RaidEvidenceQueueFailure(state, request, ex); }
    }

    private static IResult QueuedRaidEvidenceReply(ConfirmEventApplicationMetadataRequest request, SaveSnapshotEnvelope envelope, bool replay)
        => Results.Ok(new ConfirmEventApplicationResponse(new ProtocolResponse(true,
            replay ? ProtocolErrorCode.DuplicateRequest : ProtocolErrorCode.None, T("Raid.SettlementQueued")), request.EventId,
            envelope.Identity.SnapshotId, "SettlementQueued", envelope.NextLineageToken));

    private static IResult RaidEvidenceQueueFailure(ClashOfRimNetworkState state, ConfirmEventApplicationMetadataRequest request, Exception error)
    {
        state.RuntimeLogger.LogWarning(error, "Failed to prepare or queue raid evidence: raid={RaidEventId} user={UserId}", request.EventId, request.UserId);
        return RaidEvidenceRejected(request, ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, T("Raid.SettlementQueueFailed")), "SettlementQueueFailed");
    }
}
