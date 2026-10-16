using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using Microsoft.AspNetCore.Http;
using System.Text.Json;

namespace AIRsLight.ClashOfRim.Network;

public static partial class ClashOfRimNetworkServer
{
    private static IResult CommitRaidWithSnapshot(HttpRequest httpRequest, ClashOfRimNetworkState state, CreateRaidWithSnapshotRequest transaction, byte[] bytes)
    {
        var request = transaction.Raid;
        string binding = SnapshotRequestBinding.Hash(transaction, bytes);
        var now = DateTimeOffset.UtcNow;
        using (var recovery = EnterPrincipalSnapshotMutation(httpRequest, state, request.Attacker))
        {
            if (recovery.Rejection is not null)
                return RaidStartRejected(ProtocolResponse.Reject(ProtocolErrorCode.Unauthorized, T("Auth.Failed")));
        }
        ReconcileExpiredRaidEvents(state, now);
        using (var recovery = EnterPrincipalSnapshotMutation(httpRequest, state, request.Attacker))
        {
            if (recovery.Rejection is not null)
                return RaidStartRejected(ProtocolResponse.Reject(ProtocolErrorCode.Unauthorized, T("Auth.Failed")));
            if (state.Ledger.FindByIdempotencyKey(request.IdempotencyKey) is { } existing)
                return RaidSnapshotReplayResponse(state, transaction, existing, binding);
            var rejection = ValidateRaidSnapshotStart(state, request, now, out _, out _, out var blockingRaidId);
            if (rejection is not null) return RaidStartRejected(rejection, blockingRaidId);
        }
        var upload = PrepareTransactionSnapshot(state, request.Attacker.UserId, request.Attacker.ColonyId!,
            transaction.ConfirmedSnapshot.SnapshotId!, transaction.ConfirmedSnapshot, bytes, now);
        using (var recovery = EnterPrincipalSnapshotMutation(httpRequest, state, request.Attacker))
        {
            if (recovery.Rejection is not null)
                return RaidStartRejected(ProtocolResponse.Reject(ProtocolErrorCode.Unauthorized, T("Auth.Failed")));
            if (state.Ledger.FindByIdempotencyKey(request.IdempotencyKey) is { } existing)
                return RaidSnapshotReplayResponse(state, transaction, existing, binding);
            if (!upload.Accepted || upload.AcceptedSnapshot is null) return RaidStartRejected(ToProtocolResponse(upload));
            now = DateTimeOffset.UtcNow;
            var rejection = ValidateRaidSnapshotStart(state, request, now, out var eligibility, out var guard, out var blockingRaidId);
            if (rejection is not null) return RaidStartRejected(rejection, blockingRaidId);
            var package = ProtocolDtoMapper.ToSaveSnapshotPackage(transaction.ConfirmedSnapshot, bytes);
            upload = CreateSnapshotReceiver(state).CommitPrepared(new SnapshotUploadContext(request.Attacker.UserId,
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
                eligibility!.DefenderOnline, T("Mercenary.GuardDeployedTitle"), T("Mercenary.GuardDeployedMessage"), now);
            state.SnapshotCommits.CommitRaid(state.SnapshotStore, state.BankLoans, state.Ledger, state.MercenaryContracts,
                state.MercenaryGuards, package, upload.AcceptedSnapshot, new RaidSnapshotMutation(raid, guard, notice),
                (snapshot, kind) => RunCommittedSnapshotPostUpload(state, snapshot, kind), user => state.EventNotifications.SignalUser(user),
                upload.SnapshotUploadKind, preparedStoragePayload: upload.PreparedStoragePayload);
            LogEventAppend(state, new LedgerAppendResult(raid, true), "raid-start-with-snapshot");
            return Results.Ok(CreateRaidEventCreationResponse(state, ProtocolResponse.Ok(T("Raid.CreatedWithSnapshot")), raid,
                ProtocolDeliverySemantics.OnlineImmediate, snapshotId, upload.AcceptedSnapshot.Envelope.NextLineageToken));
        }
    }

    private static IResult RaidStartRejected(ProtocolResponse response, string? blockingRaidId = null) =>
        Results.Ok(new EventCreationResponse(response, blockingRaidId,
            blockingRaidId is null ? ProtocolDeliverySemantics.OnlineImmediate : ProtocolDeliverySemantics.ServerNotification));

    private static ProtocolResponse? ValidateRaidSnapshotStart(ClashOfRimNetworkState state, CreateRaidRequest request,
        DateTimeOffset now, out RaidEligibilityRequest? eligibility, out MercenaryGuardContractRecord? guard, out string? blockingRaidId)
    {
        eligibility = null;
        guard = null;
        blockingRaidId = null;
        if (!state.ServerConfiguration.PvpEnabled) return ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, T("Pvp.Disabled"));
        if (ResolveRaidOpponentKind(request.OpponentKind) != RaidOpponentKind.Player)
            return ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Raid.PlayerOnlyTransaction"));
        if (FindUnsettledAttackerRaid(state, request.Attacker.UserId, request.Attacker.ColonyId, request.IdempotencyKey) is { } blockingRaid)
        {
            blockingRaidId = blockingRaid.EventId;
            return ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, T("Raid.BlockedByUnsettledAttackerRaid"));
        }
        var defender = state.SnapshotStore.GetLatest(request.Defender.UserId, request.Defender.ColonyId!);
        if (defender is null) return ProtocolResponse.Reject(ProtocolErrorCode.SnapshotMismatch, T("Raid.MissingDefenderSnapshot"));
        if (!IsCurrentDefenderSnapshot(request.DefenderSnapshotId, defender))
            return ProtocolResponse.Reject(ProtocolErrorCode.SnapshotMismatch, T("Raid.DefenderSnapshotChanged"));
        eligibility = new RaidEligibilityRequest(ToEventParty(request.Attacker), ToEventParty(request.Defender),
            state.DiplomacyRelations.GetRelationKind(request.Attacker.UserId, request.Attacker.ColonyId,
                request.Defender.UserId, request.Defender.ColonyId) == DiplomacyRelationRegistry.RelationHostile,
            request.DefenderOnline || state.OnlinePresence.IsUserOnline(request.Defender.UserId), now,
            CurrentRaidCooldownUntil(state, request.Defender.UserId, request.Defender.ColonyId, now),
            CalculateSnapshotWealth(defender) ?? 0, defender.Identity, defender.Index.Maps, request.TargetMapId);
        var checkedEligibility = RaidEligibilityChecker.Check(eligibility, BuildRaidEligibilityPolicy(state.ServerConfiguration));
        if (!checkedEligibility.Eligible) return ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected,
            T("Raid.PrepareRejected", ("REASONS", string.Join(", ", checkedEligibility.FailureReasons))));
        if (ReconcileSnapshotConfirmationsLocked(state, request.Attacker.UserId, request.Attacker.ColonyId!, now) is { } expired) return expired;
        // A missing deployment ID is also a quote: a guard bought after preparation requires a new preparation.
        guard = state.MercenaryGuards.FindActiveForColony(request.Defender.UserId, request.Defender.ColonyId!);
        if (guard?.ContractId != NormalizeGuardDeployment(request.GuardDeploymentId))
            return ProtocolResponse.Reject(ProtocolErrorCode.Conflict, T("Mercenary.GuardChanged"));
        return null;
    }

    private static IResult RaidSnapshotReplayResponse(ClashOfRimNetworkState state, CreateRaidWithSnapshotRequest transaction,
        AuthoritativeEvent existing, string binding)
    {
        if (existing.Payload is not RaidEventPayload payload || payload.CreationRequestHash != binding
            || transaction.ConfirmedSnapshot.OwnerId != transaction.Raid.Attacker.UserId
            || transaction.ConfirmedSnapshot.ColonyId != transaction.Raid.Attacker.ColonyId
            || !MatchesRaidSnapshotRequest(existing, transaction.Raid, transaction.ConfirmedSnapshot.SnapshotId))
            return RaidStartRejected(ProtocolResponse.Reject(ProtocolErrorCode.Conflict, T("Raid.Rejected")));
        string snapshotId = ((RaidEventPayload)existing.Payload).AttackForce!.AttackerSnapshotId;
        return Results.Ok(CreateRaidEventCreationResponse(state,
            new ProtocolResponse(true, ProtocolErrorCode.DuplicateRequest, T("Raid.TransactionDuplicate")), existing,
            ProtocolDeliverySemantics.OnlineImmediate, snapshotId,
            FindSnapshotNextLineageToken(state, transaction.Raid.Attacker.UserId, transaction.Raid.Attacker.ColonyId!, snapshotId)));
    }

    private static bool MatchesRaidSnapshotRequest(AuthoritativeEvent existing, CreateRaidRequest request, string? snapshotId)
    {
        if (existing.Type != ServerEventType.Raid || existing.Actor != ToEventParty(request.Attacker)
            || existing.Target != ToEventParty(request.Defender) || existing.Payload is not RaidEventPayload { AttackForce: not null } payload
            || payload.OpponentKind != ResolveRaidOpponentKind(request.OpponentKind) || payload.DefenderSnapshotId != request.DefenderSnapshotId
            || payload.AttackForce.AttackerSnapshotId != snapshotId || payload.GuardDeploymentId != NormalizeGuardDeployment(request.GuardDeploymentId)
            || existing.TargetContext != new EventTargetContext(request.TargetWorldObjectId, request.TargetMapId, request.TargetTile, EventLandingMode.MapEdge)
            || !payload.AttackForce.PawnGlobalKeys.SequenceEqual(request.PawnGlobalKeys)) return false;
        return JsonSerializer.Serialize(payload.AttackForce.CarriedThings)
            == JsonSerializer.Serialize(request.CarriedThings.Select(thing => ToEventThingReference(thing, snapshotId)).ToArray());
    }

    private static string? NormalizeGuardDeployment(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
