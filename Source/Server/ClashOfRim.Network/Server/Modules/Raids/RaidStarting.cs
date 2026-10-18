using static AIRsLight.ClashOfRim.Network.Modules.Events.EventProtocolMapper;
using static AIRsLight.ClashOfRim.Network.Modules.Transfers.ItemTransferMapper;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using AIRsLight.ClashOfRim.Network.Modules.Authorization;
using AIRsLight.ClashOfRim.Network.Modules.Snapshots;
using AIRsLight.ClashOfRim.Network.Modules.Economy;
using static AIRsLight.ClashOfRim.Network.Modules.Snapshots.SnapshotTransactions;
using System.Text.Json;

namespace AIRsLight.ClashOfRim.Network.Modules.Raids;

internal sealed class RaidStarting(IAuthoritativeEventLedger ledger, IColonySnapshotIndexStore snapshots,
    RaidQueries queries, MercenaryGuardContractRegistry guards, SnapshotRequestAuthorization requests,
    SnapshotTransactions transactions, PendingConfirmationReconciler pending, RaidAdmission admission,
    IRaidReconciliation reconciliation, RaidSnapshotCommitter committer, Func<ClashOfRimServerConfiguration> configuration)
{
    internal EventCreationResponse CreateWithSnapshot(CreateRaidWithSnapshotRequest transaction, byte[] bytes, string? principalToken)
    {
        var request = transaction.Raid;
        if (request is null || request.Attacker is null || request.Defender is null
            || string.IsNullOrWhiteSpace(request.IdempotencyKey) || string.IsNullOrWhiteSpace(request.Attacker.UserId)
            || string.IsNullOrWhiteSpace(request.Attacker.ColonyId) || string.IsNullOrWhiteSpace(request.Defender.UserId)
            || string.IsNullOrWhiteSpace(request.Defender.ColonyId) || string.IsNullOrWhiteSpace(request.TargetWorldObjectId)
            || string.IsNullOrWhiteSpace(request.TargetMapId) || string.IsNullOrWhiteSpace(request.DefenderSnapshotId)
            || request.PawnGlobalKeys is null || request.CarriedThings is null || transaction.ConfirmedSnapshot is null
            || string.IsNullOrWhiteSpace(transaction.ConfirmedSnapshot.SnapshotId))
            return RaidStartRejected(ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, ServerText.Format("Raid.TransactionMissingFields")));
        string binding = SnapshotRequestBinding.Hash(transaction, bytes);
        var now = DateTimeOffset.UtcNow;
        using (var recovery = requests.EnterPrincipal(principalToken, request.Attacker))
        {
            if (recovery.Rejection is not null)
                return RaidStartRejected(ProtocolResponse.Reject(ProtocolErrorCode.Unauthorized, ServerText.Format("Auth.Failed")));
        }
        reconciliation.Reconcile(now);
        using (var recovery = requests.EnterPrincipal(principalToken, request.Attacker))
        {
            if (recovery.Rejection is not null)
                return RaidStartRejected(ProtocolResponse.Reject(ProtocolErrorCode.Unauthorized, ServerText.Format("Auth.Failed")));
            if (ledger.FindByIdempotencyKey(request.IdempotencyKey) is { } existing)
                return Replay(transaction, existing, binding);
            var rejection = ValidateStart(request, now, out _, out _, out var blockingRaidId);
            if (rejection is not null) return RaidStartRejected(rejection, blockingRaidId);
        }
        var upload = transactions.PrepareTransactionSnapshot(request.Attacker.UserId, request.Attacker.ColonyId!,
            transaction.ConfirmedSnapshot.SnapshotId!, transaction.ConfirmedSnapshot, bytes, now);
        using (var recovery = requests.EnterPrincipal(principalToken, request.Attacker))
        {
            if (recovery.Rejection is not null)
                return RaidStartRejected(ProtocolResponse.Reject(ProtocolErrorCode.Unauthorized, ServerText.Format("Auth.Failed")));
            if (ledger.FindByIdempotencyKey(request.IdempotencyKey) is { } existing)
                return Replay(transaction, existing, binding);
            if (!upload.Accepted || upload.AcceptedSnapshot is null) return RaidStartRejected(ToProtocolResponse(upload));
            now = DateTimeOffset.UtcNow;
            var rejection = ValidateStart(request, now, out var eligibility, out var guard, out var blockingRaidId);
            if (rejection is not null) return RaidStartRejected(rejection, blockingRaidId);
            var package = ProtocolDtoMapper.ToSaveSnapshotPackage(transaction.ConfirmedSnapshot, bytes);
            upload = transactions.CreateSnapshotReceiver().CommitPrepared(new SnapshotUploadContext(request.Attacker.UserId,
                request.Attacker.ColonyId!, transaction.ConfirmedSnapshot.SnapshotId!), package, upload, storeAccepted: false);
            if (!upload.Accepted || upload.AcceptedSnapshot is null) return RaidStartRejected(ToProtocolResponse(upload));
            string snapshotId = upload.AcceptedSnapshot.Identity.SnapshotId!;
            var initiation = new RaidInitiationRequest(request.IdempotencyKey, eligibility!,
                new EventTargetContext(request.TargetWorldObjectId, request.TargetMapId, request.TargetTile, EventLandingMode.MapEdge),
                true, now, new RaidAttackForceRecord(snapshotId, request.PawnGlobalKeys,
                    request.CarriedThings.Select(thing => ToEventThingReference(thing, snapshotId)).ToArray()));
            var raid = RaidInitiationService.CreateRaidEvent(initiation);
            raid = raid with { Payload = ((RaidEventPayload)raid.Payload) with
                { GuardDeploymentId = guard?.ContractId, CreationRequestHash = binding } };
            var notice = MercenaryGuardActivationNotificationFactory.Create(
                guard is null ? null : MercenaryGuardContractRegistry.Consumed(guard, raid.EventId, now), raid,
                eligibility!.DefenderOnline, ServerText.Format("Mercenary.GuardDeployedTitle"), ServerText.Format("Mercenary.GuardDeployedMessage"), now);
            committer.Commit(package, upload, new RaidSnapshotMutation(raid, guard, notice));
            return CreationResponse(ProtocolResponse.Ok(ServerText.Format("Raid.CreatedWithSnapshot")), raid,
                ProtocolDeliverySemantics.OnlineImmediate, snapshotId, upload.AcceptedSnapshot.Envelope.NextLineageToken);
        }
    }

    private static EventCreationResponse RaidStartRejected(ProtocolResponse response, string? blockingRaidId = null) =>
        new EventCreationResponse(response, blockingRaidId,
            blockingRaidId is null ? ProtocolDeliverySemantics.OnlineImmediate : ProtocolDeliverySemantics.ServerNotification);

    private ProtocolResponse? ValidateStart(CreateRaidRequest request,
        DateTimeOffset now, out RaidEligibilityRequest? eligibility, out MercenaryGuardContractRecord? guard, out string? blockingRaidId)
    {
        eligibility = null;
        guard = null;
        blockingRaidId = null;
        if (!configuration().PvpEnabled) return ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, ServerText.Format("Pvp.Disabled"));
        if (RaidAdmission.OpponentKind(request.OpponentKind) != RaidOpponentKind.Player)
            return ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, ServerText.Format("Raid.PlayerOnlyTransaction"));
        if (queries.UnsettledAttacker(request.Attacker.UserId, request.Attacker.ColonyId, request.IdempotencyKey) is { } blockingRaid)
        {
            blockingRaidId = blockingRaid.EventId;
            return ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, ServerText.Format("Raid.BlockedByUnsettledAttackerRaid"));
        }
        var defender = snapshots.GetLatest(request.Defender.UserId, request.Defender.ColonyId!);
        if (defender is null) return ProtocolResponse.Reject(ProtocolErrorCode.SnapshotMismatch, ServerText.Format("Raid.MissingDefenderSnapshot"));
        if (request.DefenderSnapshotId != defender.Identity.SnapshotId)
            return ProtocolResponse.Reject(ProtocolErrorCode.SnapshotMismatch, ServerText.Format("Raid.DefenderSnapshotChanged"));
        eligibility = admission.Describe(request.Attacker, request.Defender, defender.Identity, defender.Index,
            request.TargetMapId, now, request.DefenderOnline);
        if (admission.Check(eligibility) is { } rejected) return rejected;
        if (pending.ReconcileSnapshot(request.Attacker.UserId, request.Attacker.ColonyId!, now) is { } expired) return expired;
        // A missing deployment ID is also a quote: a guard bought after preparation requires a new preparation.
        guard = guards.FindActiveForColony(request.Defender.UserId, request.Defender.ColonyId!);
        if (guard?.ContractId != NormalizeGuardDeployment(request.GuardDeploymentId))
            return ProtocolResponse.Reject(ProtocolErrorCode.Conflict, ServerText.Format("Mercenary.GuardChanged"));
        return null;
    }

    private EventCreationResponse Replay(CreateRaidWithSnapshotRequest transaction,
        AuthoritativeEvent existing, string binding)
    {
        if (existing.Payload is not RaidEventPayload payload || payload.CreationRequestHash != binding
            || transaction.ConfirmedSnapshot.OwnerId != transaction.Raid.Attacker.UserId
            || transaction.ConfirmedSnapshot.ColonyId != transaction.Raid.Attacker.ColonyId
            || !MatchesRaidSnapshotRequest(existing, transaction.Raid, transaction.ConfirmedSnapshot.SnapshotId))
            return RaidStartRejected(ProtocolResponse.Reject(ProtocolErrorCode.Conflict, ServerText.Format("Raid.Rejected")));
        string snapshotId = ((RaidEventPayload)existing.Payload).AttackForce!.AttackerSnapshotId;
        return CreationResponse(
            new ProtocolResponse(true, ProtocolErrorCode.DuplicateRequest, ServerText.Format("Raid.TransactionDuplicate")), existing,
            ProtocolDeliverySemantics.OnlineImmediate, snapshotId,
            transactions.FindSnapshotNextLineageToken(transaction.Raid.Attacker.UserId, transaction.Raid.Attacker.ColonyId!, snapshotId));
    }

    private static bool MatchesRaidSnapshotRequest(AuthoritativeEvent existing, CreateRaidRequest request, string? snapshotId)
    {
        if (existing.Type != ServerEventType.Raid || existing.Actor != ToEventParty(request.Attacker)
            || existing.Target != ToEventParty(request.Defender) || existing.Payload is not RaidEventPayload { AttackForce: not null } payload
            || payload.OpponentKind != RaidAdmission.OpponentKind(request.OpponentKind) || payload.DefenderSnapshotId != request.DefenderSnapshotId
            || payload.AttackForce.AttackerSnapshotId != snapshotId || payload.GuardDeploymentId != NormalizeGuardDeployment(request.GuardDeploymentId)
            || existing.TargetContext != new EventTargetContext(request.TargetWorldObjectId, request.TargetMapId, request.TargetTile, EventLandingMode.MapEdge)
            || !payload.AttackForce.PawnGlobalKeys.SequenceEqual(request.PawnGlobalKeys)) return false;
        return JsonSerializer.Serialize(payload.AttackForce.CarriedThings)
            == JsonSerializer.Serialize(request.CarriedThings.Select(thing => ToEventThingReference(thing, snapshotId)).ToArray());
    }

    private EventCreationResponse CreationResponse(ProtocolResponse response, AuthoritativeEvent raid,
        ProtocolDeliverySemantics delivery, string snapshotId, string? lineageToken)
    {
        var started = ((RaidEventPayload)raid.Payload).StartedAtUtc;
        var policy = RaidPolicies.BuildDefenseLock(configuration());
        return new(response, raid.EventId, delivery, snapshotId, lineageToken, started,
            started + policy.MaxRaidDuration, started + policy.ServerTimeoutDuration);
    }

    private static string? NormalizeGuardDeployment(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
