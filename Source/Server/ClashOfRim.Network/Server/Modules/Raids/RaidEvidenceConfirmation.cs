using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network.Modules.Authorization;
using AIRsLight.ClashOfRim.Network.Modules.Economy;
using AIRsLight.ClashOfRim.Network.Modules.Snapshots;
using AIRsLight.ClashOfRim.Network.Plugins;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using Microsoft.Extensions.Logging;
using static AIRsLight.ClashOfRim.Network.Modules.Snapshots.SnapshotTransactions;

namespace AIRsLight.ClashOfRim.Network.Modules.Raids;

internal sealed class RaidEvidenceConfirmation(IAuthoritativeEventLedger ledger, SnapshotPostUploadJobRegistry jobs,
    RaidEvidenceScheduling scheduling, SnapshotMutationCoordinator mutations, SnapshotRequestAuthorization requests,
    ColonyAccessAuthorization colonies, SnapshotTransactions transactions, PendingConfirmationReconciler pending,
    IRaidEvidencePipeline pipeline, Func<ILogger> logger)
{
    internal bool Handles(string eventId, string? sourceEventId) => string.IsNullOrWhiteSpace(sourceEventId)
        && ledger.Find(eventId) is { Type: ServerEventType.Raid,
            Payload: RaidEventPayload { OpponentKind: RaidOpponentKind.Player, AttackerLoss: null } };

    internal ConfirmEventApplicationResponse Confirm(ConfirmEventApplicationMetadataRequest request, byte[] bytes,
        string? principalToken, DateTimeOffset nowUtc)
    {
        string binding = RaidEvidenceRequestBinding.Hash(request, bytes);
        AuthoritativeEvent? source = null;
        using (var mutation = mutations.Enter(() => ValidateAuthority(request, principalToken, out source)))
        {
            if (mutation.Rejection is { } denied) return Rejected(request, denied, "AuthOrSourceFailed");
            if (pending.ReconcileSnapshot(request.UserId, request.ColonyId, DateTimeOffset.UtcNow) is { } expired)
                return Rejected(request, expired, "PendingConfirmationExpired");
            if (source!.Status == ServerEventStatus.AppliedToSnapshot) return Completed(request, binding);
            var existing = jobs.Find("raid-settlement:" + request.EventId);
            if (existing is not null && RaidSettlementDeferredPayload.Deserialize(existing.PayloadJson).RequestHash != binding)
                return Rejected(request, ProtocolResponse.Reject(ProtocolErrorCode.Conflict,
                    ServerText.Format("Raid.SettlementQueueFailed")), "QueuedRequestConflict");
        }
        var receiver = transactions.CreateSnapshotReceiver();
        var package = ProtocolDtoMapper.ToSaveSnapshotPackage(request.ConfirmedSnapshot, bytes);
        var uploadContext = new SnapshotUploadContext(request.UserId, request.ColonyId, request.ConfirmedSnapshot.SnapshotId,
            SnapshotUploadKind: SnapshotUploadKinds.RaidSettlementEvidence, RequiredRaidEventId: request.EventId);
        var preparedUpload = SnapshotPreparationService.Prepare(receiver, uploadContext, package, nowUtc);
        while (true)
        {
            RaidEvidenceSchedulePreparation preparation;
            RaidEvidenceSchedule evidence;
            using (var mutation = mutations.Enter(() => ValidateAuthority(request, principalToken, out _)))
            {
                if (mutation.Rejection is { } denied) return Rejected(request, denied, "AuthOrSourceFailed");
                if (Revalidate(request, source, binding) is { } rejection) return rejection;
                if (!preparedUpload.Accepted || preparedUpload.AcceptedSnapshot is null)
                    return Rejected(request, ToProtocolResponse(preparedUpload), preparedUpload.Kind.ToString());
                lock (jobs.SnapshotCommitGate)
                {
                    bool replay = jobs.Find("raid-settlement:" + source.EventId) is not null;
                    var upload = replay ? preparedUpload : receiver.CommitPrepared(uploadContext, package, preparedUpload, storeAccepted: false);
                    if (!upload.Accepted || upload.AcceptedSnapshot is null)
                        return Rejected(request, ToProtocolResponse(upload), upload.Kind.ToString());
                    evidence = new(request.EventId, upload.AcceptedSnapshot,
                        new(SnapshotPostUploadKind.RaidSettlementEvidence, request.UserId, request.ColonyId, null,
                            upload.AcceptedSnapshot.Identity.SnapshotId, nowUtc), bytes,
                        RaidSettlementOrigin.OnlineEvidence, request.ClientApplicationResult, binding);
                    if (!scheduling.SupportsPreparation) return RunSynchronous(request, evidence, replay);
                    try { preparation = scheduling.Capture(evidence); }
                    catch (Exception ex) { return QueueFailure(request, ex); }
                }
            }
            using (preparation)
            {
                Exception? prepareFailure = null;
                try { SnapshotPreparationService.Run(() => { preparation.Prepare(preparedUpload.PreparedStoragePayload); return true; }); }
                catch (Exception ex) { prepareFailure = ex; }
                using (var mutation = mutations.Enter(() => ValidateAuthority(request, principalToken, out _)))
                {
                    if (mutation.Rejection is { } denied) return Rejected(request, denied, "AuthOrSourceFailed");
                    if (Revalidate(request, source, binding) is { } rejection) return rejection;
                    lock (jobs.SnapshotCommitGate)
                    {
                        // A newer same-key task wins over errors from obsolete preparation.
                        if (!preparation.HasSameJob) continue;
                        if (prepareFailure is not null) return QueueFailure(request, prepareFailure);
                        try
                        {
                            if (!preparation.TryValidate()) continue;
                            evidence = evidence with { Snapshot = preparation.Snapshot,
                                Job = evidence.Job with { OccurredAtUtc = preparation.OccurredAtUtc } };
                            pipeline.Run(evidence, preparation);
                            return Queued(request, preparation.Envelope, preparation.IsReplay);
                        }
                        catch (Exception ex) { return QueueFailure(request, ex); }
                    }
                }
            }
        }
    }

    private ProtocolResponse? ValidateAuthority(ConfirmEventApplicationMetadataRequest request,
        string? principalToken, out AuthoritativeEvent? source)
    {
        source = null;
        if (!requests.ReauthorizePrincipal(principalToken, new ProtocolIdentity(request.UserId, request.ColonyId, null))
            || !colonies.TryAuthorize(request.AuthToken, request.UserId, request.ColonyId, request.EventId,
                null, DateTimeOffset.UtcNow, out _))
            return ProtocolResponse.Reject(ProtocolErrorCode.Unauthorized, ServerText.Format("Auth.Failed"));
        source = ledger.Find(request.EventId);
        if (source?.Type != ServerEventType.Raid
            || source.Payload is not RaidEventPayload { OpponentKind: RaidOpponentKind.Player, AttackerLoss: null }
            || source.Actor.UserId != request.UserId || source.Actor.ColonyId != request.ColonyId
            || request.ConfirmedSnapshot.OwnerId != request.UserId || request.ConfirmedSnapshot.ColonyId != request.ColonyId
            || source.Status is ServerEventStatus.Cancelled or ServerEventStatus.RejectedByTarget or ServerEventStatus.Conflict)
            return ProtocolResponse.Reject(ProtocolErrorCode.Conflict, ServerText.Format("Auth.IdentityDenied"));
        return null;
    }

    private ConfirmEventApplicationResponse? Revalidate(ConfirmEventApplicationMetadataRequest request,
        AuthoritativeEvent source, string binding)
    {
        var current = ledger.Find(request.EventId);
        if (current is null || !RaidSettlementSnapshotMutation.SameSource(current, source)
            || current.Status != source.Status && current.Status != ServerEventStatus.AppliedToSnapshot)
            return Rejected(request, ProtocolResponse.Reject(ProtocolErrorCode.Conflict,
                ServerText.Format("Raid.SettlementQueueFailed")), "SourceChanged");
        if (pending.ReconcileSnapshot(request.UserId, request.ColonyId, DateTimeOffset.UtcNow) is { } expired)
            return Rejected(request, expired, "PendingConfirmationExpired");
        return current.Status == ServerEventStatus.AppliedToSnapshot ? Completed(request, binding) : null;
    }

    private ConfirmEventApplicationResponse RunSynchronous(ConfirmEventApplicationMetadataRequest request,
        RaidEvidenceSchedule evidence, bool replay)
    {
        try
        {
            if (replay) evidence = scheduling.ReadReplay(evidence);
            pipeline.Run(evidence, null);
            return Queued(request, evidence.Snapshot.Envelope, replay);
        }
        catch (Exception ex) { return QueueFailure(request, ex); }
    }

    private ConfirmEventApplicationResponse Completed(ConfirmEventApplicationMetadataRequest request, string binding)
    {
        var source = ledger.Find(request.EventId)!;
        var settlement = ledger.FindByIdempotencyKey("raid-settlement:" + request.EventId);
        if (settlement?.Type != ServerEventType.Raid || settlement.Actor != source.Actor || settlement.Target != source.Target
            || settlement.TargetContext != source.TargetContext
            || settlement.Payload is not RaidEventPayload { Settlement: not null } receipt
            || receipt.EvidenceRequestHash != binding || receipt.ReturnedSnapshotId != request.ConfirmedSnapshot.SnapshotId
            || receipt.DefenderSnapshotId != ((RaidEventPayload)source.Payload).DefenderSnapshotId)
            return Rejected(request, ProtocolResponse.Reject(ProtocolErrorCode.Conflict,
                ServerText.Format("Raid.SettlementQueueFailed")), "CompletedRequestConflict");
        return new(new ProtocolResponse(true, ProtocolErrorCode.DuplicateRequest, ServerText.Format("Raid.SettlementAlreadyConfirmed")),
            request.EventId, receipt.ReturnedSnapshotId, "AlreadyApplied",
            transactions.FindSnapshotNextLineageToken(request.UserId, request.ColonyId, receipt.ReturnedSnapshotId));
    }

    private static ConfirmEventApplicationResponse Rejected(ConfirmEventApplicationMetadataRequest request,
        ProtocolResponse failure, string validation) => new(failure, request.EventId, null, validation);

    private static ConfirmEventApplicationResponse Queued(ConfirmEventApplicationMetadataRequest request,
        SaveSnapshotEnvelope envelope, bool replay) => new(new ProtocolResponse(true,
            replay ? ProtocolErrorCode.DuplicateRequest : ProtocolErrorCode.None, ServerText.Format("Raid.SettlementQueued")),
            request.EventId, envelope.Identity.SnapshotId, "SettlementQueued", envelope.NextLineageToken);

    private ConfirmEventApplicationResponse QueueFailure(ConfirmEventApplicationMetadataRequest request, Exception error)
    {
        logger().LogWarning(error, "Failed to prepare or queue raid evidence: raid={RaidEventId} user={UserId}", request.EventId, request.UserId);
        return Rejected(request, ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected,
            ServerText.Format("Raid.SettlementQueueFailed")), "SettlementQueueFailed");
    }
}
