using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network.Plugins.CoreCompatibility;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System.Globalization;

namespace AIRsLight.ClashOfRim.Network;

public static partial class ClashOfRimNetworkServer
{
    private static IResult CreateRaid(CreateRaidRequest request, ClashOfRimNetworkState state)
    {
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        ReconcileExpiredRaidEvents(state, nowUtc);
        AuthoritativeEvent? existingEvent = FindEventByIdempotencyKey(state.Ledger, request.IdempotencyKey);
        if (existingEvent is not null && existingEvent.Type == ServerEventType.Raid)
        {
            return Results.Ok(CreateRaidEventCreationResponse(
                state,
                new ProtocolResponse(true, ProtocolErrorCode.DuplicateRequest, ServerLocalization.Text("Raid.Duplicate")),
                existingEvent,
                ProtocolDeliverySemantics.OnlineImmediate));
        }

        RaidOpponentKind opponentKind = ResolveRaidOpponentKind(request.OpponentKind);
        if (opponentKind == RaidOpponentKind.Player)
        {
            return Results.Ok(new EventCreationResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, ServerLocalization.Text("Raid.PlayerRaidRequiresSnapshot")),
                eventId: null,
                ProtocolDeliverySemantics.OnlineImmediate));
        }

        if (opponentKind == RaidOpponentKind.VanillaNpc)
        {
            return Results.Ok(new EventCreationResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Raid.NpcMultiplayerDisabled")),
                eventId: null,
                ProtocolDeliverySemantics.OnlineImmediate));
        }

        AuthoritativeEvent? blockingRaid = FindUnsettledAttackerRaid(
            state,
            request.Attacker.UserId,
            request.Attacker.ColonyId,
            request.IdempotencyKey);
        if (blockingRaid is not null)
        {
            return Results.Ok(new EventCreationResponse(
                ProtocolResponse.Reject(
                    ProtocolErrorCode.ServerRejected,
                    ServerLocalization.Text("Raid.BlockedByUnsettledAttackerRaid")),
                eventId: blockingRaid.EventId,
                ProtocolDeliverySemantics.ServerNotification));
        }

        LatestSnapshotRecord? defenderSnapshot = state.SnapshotStore.GetLatest(
            request.Defender.UserId,
            request.Defender.ColonyId ?? string.Empty);
        if (defenderSnapshot == null)
        {
            return Results.Ok(new EventCreationResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.SnapshotMismatch, ServerLocalization.Text("Raid.MissingDefenderSnapshot")),
                eventId: null,
                ProtocolDeliverySemantics.OnlineImmediate));
        }

        if (!IsCurrentDefenderSnapshot(request.DefenderSnapshotId, defenderSnapshot))
        {
            return Results.Ok(new EventCreationResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.SnapshotMismatch, ServerLocalization.Text("Raid.DefenderSnapshotChanged")),
                eventId: null,
                ProtocolDeliverySemantics.OnlineImmediate));
        }

        string relationKind = state.DiplomacyRelations.GetRelationKind(
            request.Attacker.UserId,
            request.Attacker.ColonyId,
            request.Defender.UserId,
            request.Defender.ColonyId);
        var initiationRequest = new RaidInitiationRequest(
            request.IdempotencyKey,
            new RaidEligibilityRequest(
                ToEventParty(request.Attacker),
                ToEventParty(request.Defender),
                string.Equals(relationKind, DiplomacyRelationRegistry.RelationHostile, StringComparison.Ordinal),
                request.DefenderOnline || state.OnlinePresence.IsUserOnline(request.Defender.UserId),
                nowUtc,
                CurrentRaidCooldownUntil(state, request.Defender.UserId, request.Defender.ColonyId, nowUtc),
                request.DefenderWealth,
                defenderSnapshot.Identity,
                defenderSnapshot.Index.Maps,
                request.TargetMapId),
            new EventTargetContext(
                request.TargetWorldObjectId,
                request.TargetMapId,
                request.TargetTile,
                EventLandingMode.MapEdge),
            AttackerOnline: true,
            nowUtc,
            new RaidAttackForceRecord(
                request.Attacker.SnapshotId ?? string.Empty,
                request.PawnGlobalKeys,
                request.CarriedThings.Select(thing => ToEventThingReference(thing, request.Attacker.SnapshotId)).ToList()));

        RaidInitiationResult result = RaidInitiationService.StartRaid(
            state.Ledger,
            initiationRequest,
            BuildRaidEligibilityPolicy(state.ServerConfiguration));
        if (result.Started && result.RaidEvent != null)
        {
            LogEventAppend(state, new LedgerAppendResult(result.RaidEvent, result.Created), "raid-start");
            if (result.Created)
            {
                state.EventNotifications.SignalUser(result.RaidEvent.Target.UserId);
            }

            return Results.Ok(CreateRaidEventCreationResponse(
                state,
                result.Created
                    ? ProtocolResponse.Ok(ServerLocalization.Text("Raid.Created"))
                    : new ProtocolResponse(true, ProtocolErrorCode.DuplicateRequest, ServerLocalization.Text("Raid.Duplicate")),
                result.RaidEvent,
                ProtocolDeliverySemantics.OnlineImmediate));
        }

        if (result.Created && result.NotificationEvent != null)
        {
            LogEventAppend(state, new LedgerAppendResult(result.NotificationEvent, result.Created), "raid-start-rejected");
            state.EventNotifications.SignalUser(result.NotificationEvent.Target.UserId);
        }

        return Results.Ok(new EventCreationResponse(
            result.Created
                ? ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, ServerLocalization.Text("Raid.RejectedWithNotification"))
                : ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, ServerLocalization.Text("Raid.Rejected")),
            result.NotificationEvent?.EventId,
            ProtocolDeliverySemantics.ServerNotification));
    }

    private static EventCreationResponse CreateRaidEventCreationResponse(
        ClashOfRimNetworkState state,
        ProtocolResponse response,
        AuthoritativeEvent raidEvent,
        ProtocolDeliverySemantics deliverySemantics,
        string? appliedSnapshotId = null,
        string? nextLineageToken = null)
    {
        DateTimeOffset? startedAtUtc = raidEvent.Payload is RaidEventPayload payload
            ? payload.StartedAtUtc
            : null;
        RaidDefenseLockPolicy policy = BuildRaidDefenseLockPolicy(state.ServerConfiguration);
        return new EventCreationResponse(
            response,
            raidEvent.EventId,
            deliverySemantics,
            appliedSnapshotId,
            nextLineageToken,
            startedAtUtc,
            startedAtUtc + policy.MaxRaidDuration,
            startedAtUtc + policy.ServerTimeoutDuration);
    }

    private static async Task<IResult> CreateRaidWithSnapshot(HttpRequest httpRequest, ClashOfRimNetworkState state)
    {
        MultipartSnapshotRequest<CreateRaidWithSnapshotRequest>? multipart =
            await ReadMultipartSnapshotRequest<CreateRaidWithSnapshotRequest>(httpRequest);
        if (multipart is null || multipart.Request is null || multipart.Payload is null)
        {
            return Results.Ok(new EventCreationResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, ServerLocalization.Text("Raid.TransactionMissingPayload")),
                eventId: null,
                ProtocolDeliverySemantics.OnlineImmediate));
        }

        CreateRaidWithSnapshotRequest transaction = multipart.Request;
        CreateRaidRequest? request = transaction.Raid;
        if (request is null
            || request.Attacker is null
            || request.Defender is null
            || string.IsNullOrWhiteSpace(request.IdempotencyKey)
            || string.IsNullOrWhiteSpace(request.Attacker.UserId)
            || string.IsNullOrWhiteSpace(request.Attacker.ColonyId)
            || string.IsNullOrWhiteSpace(request.Defender.UserId)
            || string.IsNullOrWhiteSpace(request.Defender.ColonyId)
            || string.IsNullOrWhiteSpace(request.TargetWorldObjectId)
            || string.IsNullOrWhiteSpace(request.TargetMapId)
            || string.IsNullOrWhiteSpace(request.DefenderSnapshotId)
            || transaction.ConfirmedSnapshot is null
            || string.IsNullOrWhiteSpace(transaction.ConfirmedSnapshot.SnapshotId))
        {
            return Results.Ok(new EventCreationResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, ServerLocalization.Text("Raid.TransactionMissingFields")),
                eventId: null,
                ProtocolDeliverySemantics.OnlineImmediate));
        }

        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        ReconcileExpiredRaidEvents(state, nowUtc);
        AuthoritativeEvent? existingEvent = FindEventByIdempotencyKey(state.Ledger, request.IdempotencyKey);
        if (existingEvent is not null && existingEvent.Type == ServerEventType.Raid)
        {
            string? attackerSnapshotId = existingEvent.Payload is RaidEventPayload payload
                ? payload.AttackForce?.AttackerSnapshotId
                : null;
            string? nextLineageToken = FindSnapshotNextLineageToken(
                state,
                request.Attacker.UserId,
                request.Attacker.ColonyId ?? string.Empty,
                attackerSnapshotId);
            return Results.Ok(CreateRaidEventCreationResponse(
                state,
                new ProtocolResponse(true, ProtocolErrorCode.DuplicateRequest, ServerLocalization.Text("Raid.TransactionDuplicate")),
                existingEvent,
                ProtocolDeliverySemantics.OnlineImmediate,
                attackerSnapshotId,
                nextLineageToken));
        }

        if (!state.ServerConfiguration.PvpEnabled)
        {
            return Results.Ok(new EventCreationResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, ServerLocalization.Text("Pvp.Disabled")),
                eventId: null,
                ProtocolDeliverySemantics.OnlineImmediate));
        }

        AuthoritativeEvent? blockingRaid = FindUnsettledAttackerRaid(
            state,
            request.Attacker.UserId,
            request.Attacker.ColonyId,
            request.IdempotencyKey);
        if (blockingRaid is not null)
        {
            return Results.Ok(new EventCreationResponse(
                ProtocolResponse.Reject(
                    ProtocolErrorCode.ServerRejected,
                    ServerLocalization.Text("Raid.BlockedByUnsettledAttackerRaid")),
                eventId: blockingRaid.EventId,
                ProtocolDeliverySemantics.ServerNotification));
        }

        RaidOpponentKind opponentKind = ResolveRaidOpponentKind(request.OpponentKind);
        if (opponentKind != RaidOpponentKind.Player)
        {
            return Results.Ok(new EventCreationResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, ServerLocalization.Text("Raid.PlayerOnlyTransaction")),
                eventId: null,
                ProtocolDeliverySemantics.OnlineImmediate));
        }

        LatestSnapshotRecord? defenderSnapshot = state.SnapshotStore.GetLatest(
            request.Defender.UserId,
            request.Defender.ColonyId ?? string.Empty);
        if (defenderSnapshot == null)
        {
            return Results.Ok(new EventCreationResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.SnapshotMismatch, ServerLocalization.Text("Raid.MissingDefenderSnapshot")),
                eventId: null,
                ProtocolDeliverySemantics.OnlineImmediate));
        }

        if (!IsCurrentDefenderSnapshot(request.DefenderSnapshotId, defenderSnapshot))
        {
            return Results.Ok(new EventCreationResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.SnapshotMismatch, ServerLocalization.Text("Raid.DefenderSnapshotChanged")),
                eventId: null,
                ProtocolDeliverySemantics.OnlineImmediate));
        }

        string relationKind = state.DiplomacyRelations.GetRelationKind(
            request.Attacker.UserId,
            request.Attacker.ColonyId,
            request.Defender.UserId,
            request.Defender.ColonyId);
        int defenderWealth = CalculateSnapshotWealth(new LatestSnapshotRecord(
            defenderSnapshot.Identity,
            defenderSnapshot.Envelope,
            defenderSnapshot.Index,
            defenderSnapshot.AcceptedAtUtc))
            ?? 0;
        var eligibilityRequest = new RaidEligibilityRequest(
            ToEventParty(request.Attacker),
            ToEventParty(request.Defender),
            string.Equals(relationKind, DiplomacyRelationRegistry.RelationHostile, StringComparison.Ordinal),
            request.DefenderOnline || state.OnlinePresence.IsUserOnline(request.Defender.UserId),
            nowUtc,
            CurrentRaidCooldownUntil(state, request.Defender.UserId, request.Defender.ColonyId, nowUtc),
            defenderWealth,
            defenderSnapshot.Identity,
            defenderSnapshot.Index.Maps,
            request.TargetMapId);
        RaidEligibilityResult eligibility = RaidEligibilityChecker.Check(
            eligibilityRequest,
            BuildRaidEligibilityPolicy(state.ServerConfiguration));
        if (!eligibility.Eligible)
        {
            return Results.Ok(new EventCreationResponse(
                ProtocolResponse.Reject(
                    ProtocolErrorCode.ServerRejected,
                    T("Raid.PrepareRejected", ("REASONS", string.Join(", ", eligibility.FailureReasons)))),
                eventId: null,
                ProtocolDeliverySemantics.OnlineImmediate));
        }

        if (TryRejectExpiredPendingConfirmationSnapshot(
                state,
                request.Attacker.UserId,
                request.Attacker.ColonyId ?? string.Empty,
                nowUtc,
                out ProtocolResponse? pendingRejection))
        {
            return Results.Ok(new EventCreationResponse(
                pendingRejection!,
                eventId: null,
                ProtocolDeliverySemantics.OnlineImmediate));
        }

        if (!string.IsNullOrWhiteSpace(request.GuardDeploymentId))
        {
            MercenaryGuardContractRecord? activeGuard = state.MercenaryGuards.FindActiveForColony(
                request.Defender.UserId,
                request.Defender.ColonyId ?? string.Empty);
            if (activeGuard is null
                || !string.Equals(activeGuard.ContractId, request.GuardDeploymentId, StringComparison.Ordinal))
            {
                return Results.Ok(new EventCreationResponse(
                    ProtocolResponse.Reject(ProtocolErrorCode.Conflict, T("Mercenary.GuardChanged")),
                    eventId: null,
                    ProtocolDeliverySemantics.OnlineImmediate));
            }
        }

        SnapshotUploadResult upload = ReceiveSnapshot(
            state,
            request.Attacker.UserId,
            request.Attacker.ColonyId ?? string.Empty,
            transaction.ConfirmedSnapshot.SnapshotId!,
            transaction.ConfirmedSnapshot,
            multipart.Payload,
            nowUtc);
        if (!upload.Accepted || upload.AcceptedSnapshot is null)
        {
            return Results.Ok(new EventCreationResponse(
                ToProtocolResponse(upload),
                eventId: null,
                ProtocolDeliverySemantics.OnlineImmediate));
        }

        string acceptedSnapshotId = upload.AcceptedSnapshot.Identity.SnapshotId
            ?? transaction.ConfirmedSnapshot.SnapshotId!;
        var initiationRequest = new RaidInitiationRequest(
            request.IdempotencyKey,
            eligibilityRequest,
            new EventTargetContext(
                request.TargetWorldObjectId,
                request.TargetMapId,
                request.TargetTile,
                EventLandingMode.MapEdge),
            AttackerOnline: true,
            nowUtc,
            new RaidAttackForceRecord(
                acceptedSnapshotId,
                request.PawnGlobalKeys,
                request.CarriedThings.Select(thing => ToEventThingReference(thing, acceptedSnapshotId)).ToList()));

        RaidInitiationResult result = RaidInitiationService.StartRaid(
            state.Ledger,
            initiationRequest,
            BuildRaidEligibilityPolicy(state.ServerConfiguration));
        RunSnapshotPostUploadProcessors(
            state,
            request.Attacker.UserId,
            request.Attacker.ColonyId ?? string.Empty,
            sessionId: null,
            upload,
            nowUtc);

        if (result.Started && result.RaidEvent != null)
        {
            LogEventAppend(state, new LedgerAppendResult(result.RaidEvent, result.Created), "raid-start-with-snapshot");
            if (result.Created)
            {
                if (!string.IsNullOrWhiteSpace(request.GuardDeploymentId))
                {
                    state.MercenaryGuards.ConsumeForRaid(
                        request.Defender.UserId,
                        request.Defender.ColonyId ?? string.Empty,
                        result.RaidEvent.EventId,
                        request.GuardDeploymentId!,
                        nowUtc);
                }

                state.EventNotifications.SignalUser(result.RaidEvent.Target.UserId);
            }

            return Results.Ok(CreateRaidEventCreationResponse(
                state,
                result.Created
                    ? ProtocolResponse.Ok(ServerLocalization.Text("Raid.CreatedWithSnapshot"))
                    : new ProtocolResponse(true, ProtocolErrorCode.DuplicateRequest, ServerLocalization.Text("Raid.Duplicate")),
                result.RaidEvent,
                ProtocolDeliverySemantics.OnlineImmediate,
                acceptedSnapshotId,
                upload.AcceptedSnapshot.Envelope.NextLineageToken));
        }

        if (result.Created && result.NotificationEvent != null)
        {
            LogEventAppend(state, new LedgerAppendResult(result.NotificationEvent, result.Created), "raid-start-with-snapshot-rejected");
            state.EventNotifications.SignalUser(result.NotificationEvent.Target.UserId);
        }

        return Results.Ok(new EventCreationResponse(
            result.Created
                ? ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, ServerLocalization.Text("Raid.RejectedWithNotification"))
                : ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, ServerLocalization.Text("Raid.Rejected")),
            result.NotificationEvent?.EventId,
            ProtocolDeliverySemantics.ServerNotification,
            acceptedSnapshotId,
            upload.AcceptedSnapshot.Envelope.NextLineageToken));
    }

    private static IResult PrepareRaid(PrepareRaidRequest request, ClashOfRimNetworkState state)
    {
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        ReconcileExpiredRaidEvents(state, nowUtc);

        if (request.Attacker is null
            || string.IsNullOrWhiteSpace(request.Attacker.UserId)
            || string.IsNullOrWhiteSpace(request.Attacker.ColonyId)
            || string.IsNullOrWhiteSpace(request.Attacker.SnapshotId)
            || request.Defender is null
            || string.IsNullOrWhiteSpace(request.Defender.UserId)
            || string.IsNullOrWhiteSpace(request.Defender.ColonyId)
            || string.IsNullOrWhiteSpace(request.TargetWorldObjectId)
            || string.IsNullOrWhiteSpace(request.TargetMapId))
        {
            return Results.Ok(new PrepareRaidResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, ServerLocalization.Text("Raid.PrepareMissingFields")),
                raidEventId: null,
                raidPreparationId: null,
                defenderSnapshotId: null,
                defenderPackage: null,
                expiresAtUtc: null));
        }

        AuthoritativeEvent? blockingRaid = FindUnsettledAttackerRaid(
            state,
            request.Attacker.UserId,
            request.Attacker.ColonyId,
            request.IdempotencyKey);
        if (blockingRaid is not null)
        {
            return Results.Ok(new PrepareRaidResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, ServerLocalization.Text("Raid.BlockedByUnsettledAttackerRaid")),
                blockingRaid.EventId,
                raidPreparationId: null,
                defenderSnapshotId: null,
                defenderPackage: null,
                expiresAtUtc: null));
        }

        RaidOpponentKind opponentKind = ResolveRaidOpponentKind(request.OpponentKind);
        if (opponentKind != RaidOpponentKind.Player)
        {
            return Results.Ok(new PrepareRaidResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, ServerLocalization.Text("Raid.PreparePlayerOnly")),
                raidEventId: null,
                raidPreparationId: null,
                defenderSnapshotId: null,
                defenderPackage: null,
                expiresAtUtc: null));
        }

        if (!state.ServerConfiguration.PvpEnabled)
        {
            return Results.Ok(new PrepareRaidResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, ServerLocalization.Text("Pvp.Disabled")),
                raidEventId: null,
                raidPreparationId: null,
                defenderSnapshotId: null,
                defenderPackage: null,
                expiresAtUtc: null));
        }

        if (state.SnapshotStore is not IColonySnapshotPackageStore packageStore)
        {
            return Results.Ok(new PrepareRaidResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, ServerLocalization.Text("Raid.PrepareSnapshotStoreUnsupported")),
                raidEventId: null,
                raidPreparationId: null,
                defenderSnapshotId: null,
                defenderPackage: null,
                expiresAtUtc: null));
        }

        SaveSnapshotPackage? defenderPackage = packageStore.GetLatestPackage(
            request.Defender.UserId,
            request.Defender.ColonyId);
        if (defenderPackage is null)
        {
            return Results.Ok(new PrepareRaidResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.SnapshotMismatch, ServerLocalization.Text("Raid.MissingDefenderSnapshot")),
                raidEventId: null,
                raidPreparationId: null,
                defenderSnapshotId: null,
                defenderPackage: null,
                expiresAtUtc: null));
        }

        string relationKind = state.DiplomacyRelations.GetRelationKind(
            request.Attacker.UserId,
            request.Attacker.ColonyId,
            request.Defender.UserId,
            request.Defender.ColonyId);
        int defenderWealth = CalculateSnapshotWealth(new LatestSnapshotRecord(
            defenderPackage.Envelope.Identity,
            defenderPackage.Envelope,
            defenderPackage.Index,
            nowUtc))
            ?? 0;
        var eligibility = new RaidEligibilityRequest(
            ToEventParty(request.Attacker),
            ToEventParty(request.Defender),
            string.Equals(relationKind, DiplomacyRelationRegistry.RelationHostile, StringComparison.Ordinal),
            state.OnlinePresence.IsUserOnline(request.Defender.UserId),
            nowUtc,
            CurrentRaidCooldownUntil(state, request.Defender.UserId, request.Defender.ColonyId, nowUtc),
            defenderWealth,
            defenderPackage.Envelope.Identity,
            defenderPackage.Index.Maps,
            request.TargetMapId);
        RaidEligibilityResult eligibilityResult = RaidEligibilityChecker.Check(
            eligibility,
            BuildRaidEligibilityPolicy(state.ServerConfiguration));
        if (!eligibilityResult.Eligible)
        {
            return Results.Ok(new PrepareRaidResponse(
                ProtocolResponse.Reject(
                    ProtocolErrorCode.ServerRejected,
                    T("Raid.PrepareRejected", ("REASONS", string.Join(", ", eligibilityResult.FailureReasons)))),
                raidEventId: null,
                raidPreparationId: null,
                defenderSnapshotId: null,
                defenderPackage: null,
                expiresAtUtc: null));
        }

        string raidEventId = AuthoritativeEventFactory.BuildEventId(ServerEventType.Raid, request.IdempotencyKey);
        RaidPreparationRecord preparation = state.RaidPreparations.Create(
            request.IdempotencyKey,
            raidEventId,
            request.Attacker.UserId,
            request.Attacker.ColonyId,
            request.Defender.UserId,
            request.Defender.ColonyId,
            defenderPackage.Envelope.Identity.SnapshotId ?? string.Empty,
            request.TargetWorldObjectId,
            request.TargetMapId,
            request.TargetTile,
            nowUtc,
            TimeSpan.FromMinutes(10));

        return Results.Ok(new PrepareRaidResponse(
            ProtocolResponse.Ok(ServerLocalization.Text("Raid.PrepareCreated")),
            raidEventId,
            preparation.PreparationId,
            defenderPackage.Envelope.Identity.SnapshotId,
            ProtocolDtoMapper.ToMetadataDto(defenderPackage),
            preparation.ExpiresAtUtc,
            state.ServerConfiguration.RaidMaxDuration.TotalMinutes,
            state.ServerConfiguration.RaidTimeoutGracePeriod.TotalMinutes,
            BuildRaidGuardDeployment(
                state,
                request.Defender.UserId,
                request.Defender.ColonyId,
                raidEventId,
                defenderWealth,
                defenderPackage.Index)));
    }

    private static RaidGuardDeploymentDto? BuildRaidGuardDeployment(
        ClashOfRimNetworkState state,
        string defenderUserId,
        string defenderColonyId,
        string raidEventId,
        int defenderWealth,
        SaveSnapshotIndex defenderIndex)
    {
        MercenaryGuardContractRecord? guard = state.MercenaryGuards.FindActiveForColony(defenderUserId, defenderColonyId);
        if (guard is null)
        {
            return null;
        }

        int basePoints = CoreRaidDifficultyServerCompatibility.EstimateDefaultThreatPointsForSnapshot(
            defenderIndex,
            defenderWealth,
            state.ServerConfiguration.RaidMinimumDefenderWealth,
            state.WorldConfiguration.Current?.Extensions);
        int points = Math.Max(35, (int)Math.Ceiling(basePoints * Math.Max(0f, guard.PointRatio)));
        int seed = HashCode.Combine(guard.ContractId, raidEventId);
        return new RaidGuardDeploymentDto(
            guard.ContractId,
            guard.Tier,
            guard.PriceSilver,
            guard.PointRatio,
            points,
            seed);
    }

    private static bool IsCurrentDefenderSnapshot(string? requestedSnapshotId, LatestSnapshotRecord defenderSnapshot)
    {
        return !string.IsNullOrWhiteSpace(requestedSnapshotId)
            && string.Equals(
                requestedSnapshotId,
                defenderSnapshot.Identity.SnapshotId ?? string.Empty,
                StringComparison.Ordinal);
    }

    private static IResult CreateDiplomacyEvent(CreateDiplomacyEventRequest request, ClashOfRimNetworkState state)
    {
        ProtocolResponse? validation = ValidateDiplomacyCreateRequest(request);
        if (validation is not null)
        {
            return Results.Ok(new DiplomacyEventResponse(validation, null, null, null));
        }

        ServerEventType type = ResolveDiplomacyEventType(request.Kind);
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        DiplomacyRelationRecord? currentRelation = state.DiplomacyRelations.GetRelation(
            request.Actor.UserId,
            request.Actor.ColonyId,
            request.Target.UserId,
            request.Target.ColonyId);
        string currentRelationKind = currentRelation?.RelationKind ?? DiplomacyRelationRegistry.RelationNeutral;
        AuthoritativeEvent? existingDiplomacyEvent = FindEventByIdempotencyKey(state.Ledger, request.IdempotencyKey);
        if (existingDiplomacyEvent is not null)
        {
            return Results.Ok(new DiplomacyEventResponse(
                new ProtocolResponse(true, ProtocolErrorCode.DuplicateRequest, ServerLocalization.Text("Diplomacy.EventDuplicate")),
                existingDiplomacyEvent.EventId,
                null,
                currentRelationKind));
        }

        bool supportRequest = IsSupportRequestKind(request.Kind);
        ProtocolResponse? relationValidation = supportRequest
            ? ValidateSupportRequestDiplomacyTransition(state, request, currentRelationKind, nowUtc)
            : ValidateDiplomacyRelationTransition(
                type,
                currentRelationKind,
                currentRelation,
                nowUtc,
                state.ServerConfiguration.DiplomacyRelationChangeCooldown);
        if (relationValidation is not null)
        {
            return Results.Ok(new DiplomacyEventResponse(relationValidation, null, null, currentRelationKind));
        }

        bool targetOnline = state.OnlinePresence.IsUserOnline(request.Target.UserId);
        AuthoritativeEvent diplomacyEvent = AuthoritativeEventFactory.Create(
            type,
            ToEventParty(request.Actor),
            ToEventParty(request.Target),
            request.IdempotencyKey,
            targetOnline,
            supportRequest
                ? CreateSupportRequestPayload(request)
                : CreateDiplomacyPayload(type, request.IdempotencyKey, request.Message, request.ExpiresAtUtc),
            nowUtc);

        LedgerAppendResult append = state.Ledger.Append(diplomacyEvent);
        string? relationKind = type is ServerEventType.WarDeclaration or ServerEventType.AllianceCancellation
            ? PersistDiplomacyRelation(state, append.Event, accepted: true, nowUtc)
            : supportRequest ? currentRelationKind : null;
        SignalIfCreated(state, append, diplomacyEvent.Target.UserId);
        return Results.Ok(new DiplomacyEventResponse(
            append.Created
                ? ProtocolResponse.Ok(ServerLocalization.Text("Diplomacy.EventCreated"))
                : new ProtocolResponse(true, ProtocolErrorCode.DuplicateRequest, ServerLocalization.Text("Diplomacy.EventDuplicate")),
            append.Event.EventId,
            null,
            relationKind));
    }

    private static IResult RespondDiplomacyEvent(RespondDiplomacyEventRequest request, ClashOfRimNetworkState state)
    {
        if (string.IsNullOrWhiteSpace(request.EventId)
            || string.IsNullOrWhiteSpace(request.UserId)
            || string.IsNullOrWhiteSpace(request.ColonyId)
            || string.IsNullOrWhiteSpace(request.CurrentSnapshotId))
        {
            return Results.Ok(new DiplomacyEventResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Diplomacy.ResponseMissingFields")),
                request.EventId,
                null,
                null));
        }

        AuthoritativeEvent? diplomacyEvent = state.Ledger.Find(request.EventId);
        if (diplomacyEvent is null
            || !IsDiplomacyEventType(diplomacyEvent.Type)
            || !IsVisibleParty(diplomacyEvent.Target, request.UserId, request.ColonyId))
        {
            return Results.Ok(new DiplomacyEventResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.EventNotFound, T("Diplomacy.NotFoundOrInvisible")),
                request.EventId,
                null,
                null));
        }

        if (diplomacyEvent.Status is ServerEventStatus.AppliedToSnapshot or ServerEventStatus.RejectedByTarget)
        {
            return Results.Ok(new DiplomacyEventResponse(
                new ProtocolResponse(true, ProtocolErrorCode.DuplicateRequest, ServerLocalization.Text("Diplomacy.EventProcessed")),
                diplomacyEvent.EventId,
                null,
                ResolveDiplomacyRelationKind(
                    state,
                    diplomacyEvent.Actor.UserId,
                    diplomacyEvent.Actor.ColonyId,
                    diplomacyEvent.Target.UserId,
                    diplomacyEvent.Target.ColonyId)));
        }

        if (!request.Accepted && diplomacyEvent.RejectionPolicy != EventRejectionPolicy.RejectableByTarget)
        {
            return Results.Ok(new DiplomacyEventResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, T("Diplomacy.NotRejectable")),
                diplomacyEvent.EventId,
                null,
                null));
        }

        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        if (request.Accepted && diplomacyEvent.Type is ServerEventType.AllianceRequest or ServerEventType.PeaceRequest)
        {
            DiplomacyRelationRecord? currentRelation = state.DiplomacyRelations.GetRelation(
                diplomacyEvent.Actor.UserId,
                diplomacyEvent.Actor.ColonyId,
                diplomacyEvent.Target.UserId,
                diplomacyEvent.Target.ColonyId);
            string currentRelationKind = currentRelation?.RelationKind ?? DiplomacyRelationRegistry.RelationNeutral;
            ProtocolResponse? relationValidation = ValidateDiplomacyRelationTransition(
                diplomacyEvent.Type,
                currentRelationKind,
                currentRelation,
                nowUtc,
                state.ServerConfiguration.DiplomacyRelationChangeCooldown);
            if (relationValidation is not null)
            {
                return Results.Ok(new DiplomacyEventResponse(
                    relationValidation,
                    diplomacyEvent.EventId,
                    null,
                    currentRelationKind));
            }
        }

        AuthoritativeEvent changed = request.Accepted
            ? state.Ledger.MarkAccepted(diplomacyEvent.EventId, nowUtc, request.Reason)
            : state.Ledger.MarkRejected(diplomacyEvent.EventId, nowUtc, request.Reason);
        AuthoritativeEvent notification = CreateDiplomacyResponseNotification(
            changed,
            request.Accepted,
            state.OnlinePresence.IsUserOnline(changed.Actor.UserId),
            nowUtc);
        LedgerAppendResult notificationAppend = state.Ledger.Append(notification);
        string? relationKind = request.Accepted && changed.Type is ServerEventType.AllianceRequest or ServerEventType.PeaceRequest
            ? PersistDiplomacyRelation(state, changed, accepted: true, nowUtc)
            : null;
        SignalIfCreated(state, notificationAppend, notification.Target.UserId);

        return Results.Ok(new DiplomacyEventResponse(
            ProtocolResponse.Ok(request.Accepted ? ServerLocalization.Text("Diplomacy.Accepted") : ServerLocalization.Text("Diplomacy.Rejected")),
            changed.EventId,
            notificationAppend.Event.EventId,
            relationKind));
    }

    private static IResult CreateSupportPawn(CreateSupportPawnRequest request, ClashOfRimNetworkState state)
    {
        ProtocolResponse? validation = ValidateSupportPawnRequest(request);
        if (validation is not null)
        {
            return Results.Ok(new EventCreationResponse(
                validation,
                eventId: null,
                ProtocolDeliverySemantics.OnlineImmediate));
        }

        LatestSnapshotRecord? targetSnapshot = state.SnapshotStore.GetLatest(
            request.Target.UserId,
            request.Target.ColonyId ?? string.Empty);
        if (targetSnapshot is null)
        {
            return Results.Ok(new EventCreationResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.SnapshotMismatch, T("Support.TargetSnapshotNotFound")),
                eventId: null,
                ProtocolDeliverySemantics.OnlineImmediate));
        }

        EventTargetContext? targetContext = request.TargetContext is not null
            ? ToEventTargetContext(request.TargetContext)
            : ResolveSupportPawnTargetContext(targetSnapshot);
        if (targetContext is null || string.IsNullOrWhiteSpace(targetContext.MapUniqueId))
        {
            return Results.Ok(new EventCreationResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Support.TargetMapMissing")),
                eventId: null,
                ProtocolDeliverySemantics.OnlineImmediate));
        }

        if (!TryToPawnExchangePackage(request.PawnPackage, out PawnExchangePackage? pawnPackage, out string pawnPackageFailure)
            || pawnPackage is null)
        {
            return Results.Ok(new EventCreationResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Support.PawnPackageInvalid", ("MESSAGE", pawnPackageFailure))),
                eventId: null,
                ProtocolDeliverySemantics.OnlineImmediate));
        }

        try
        {
            _ = SafePawnExchangeSerializer.Serialize(pawnPackage);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return Results.Ok(new EventCreationResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Support.PawnPackageInvalid", ("MESSAGE", ex.Message))),
                eventId: null,
                ProtocolDeliverySemantics.OnlineImmediate));
        }

        bool targetOnline = state.OnlinePresence.IsUserOnline(request.Target.UserId);
        AuthoritativeEvent supportEvent = AuthoritativeEventFactory.Create(
            ServerEventType.SupportPawn,
            ToEventParty(request.Actor),
            ToEventParty(request.Target),
            request.IdempotencyKey,
            targetOnline,
            new SupportPawnEventPayload(
                request.PawnGlobalKey,
                request.SourceSnapshotId,
                request.PawnName,
                request.TemporaryControl,
                request.ExpectedReturnAtUtc,
                ToCrossMapPawnReference(request.PawnReference),
                pawnPackage,
                request.SourceTile,
                request.SourceCaravanLoadId,
                ReturnToSender: false,
                RejectionReason: null,
                request.PermanentSupport,
                request.SupportDurationDays,
                request.ExpiresAtGameTicks,
                request.AutoReturnOnSettlement,
                SourceEventId: null,
                ReturnReason: null),
            DateTimeOffset.UtcNow,
            targetContext);

        LedgerAppendResult append = state.Ledger.Append(supportEvent);
        SignalIfCreated(state, append, supportEvent.Target.UserId);
        return Results.Ok(ToEventCreationResponse(
            append,
            targetOnline ? ProtocolDeliverySemantics.OnlineImmediate : ProtocolDeliverySemantics.OfflinePending,
            T("Support.Created")));
    }

    private static async Task<IResult> CreateSupportPawnWithSnapshot(HttpRequest httpRequest, ClashOfRimNetworkState state)
    {
        MultipartSnapshotRequest<CreateSupportPawnWithSnapshotRequest>? multipart =
            await ReadMultipartSnapshotRequest<CreateSupportPawnWithSnapshotRequest>(httpRequest);
        if (multipart is null || multipart.Request is null || multipart.Payload is null)
        {
            return Results.Ok(new EventCreationResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Support.TransactionMissingPayload")),
                eventId: null,
                ProtocolDeliverySemantics.OnlineImmediate));
        }

        CreateSupportPawnRequest? request = multipart.Request.SupportPawn;
        if (request is null
            || multipart.Request.ConfirmedSnapshot is null
            || string.IsNullOrWhiteSpace(multipart.Request.ConfirmedSnapshot.SnapshotId))
        {
            return Results.Ok(new EventCreationResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Support.TransactionMissingFields")),
                eventId: null,
                ProtocolDeliverySemantics.OnlineImmediate));
        }

        ProtocolResponse? validation = ValidateSupportPawnRequest(request);
        if (validation is not null)
        {
            return Results.Ok(new EventCreationResponse(
                validation,
                eventId: null,
                ProtocolDeliverySemantics.OnlineImmediate));
        }

        AuthoritativeEvent? existingEvent = FindEventByIdempotencyKey(state.Ledger, request.IdempotencyKey);
        if (existingEvent is not null)
        {
            string? sourceSnapshotId = existingEvent.Payload is SupportPawnEventPayload supportPayload
                ? supportPayload.SourceSnapshotId
                : request.SourceSnapshotId;
            string? nextLineageToken = FindSnapshotNextLineageToken(
                state,
                request.Actor.UserId,
                request.Actor.ColonyId ?? string.Empty,
                sourceSnapshotId);
            return Results.Ok(new EventCreationResponse(
                new ProtocolResponse(true, ProtocolErrorCode.DuplicateRequest, ServerLocalization.Text("Support.DuplicateTransaction")),
                existingEvent.EventId,
                state.OnlinePresence.IsUserOnline(existingEvent.Target.UserId)
                    ? ProtocolDeliverySemantics.OnlineImmediate
                    : ProtocolDeliverySemantics.OfflinePending,
                sourceSnapshotId,
                nextLineageToken));
        }

        LatestSnapshotRecord? targetSnapshot = state.SnapshotStore.GetLatest(
            request.Target.UserId,
            request.Target.ColonyId ?? string.Empty);
        if (targetSnapshot is null)
        {
            return Results.Ok(new EventCreationResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.SnapshotMismatch, T("Support.TargetSnapshotNotFound")),
                eventId: null,
                ProtocolDeliverySemantics.OnlineImmediate));
        }

        EventTargetContext? targetContext = request.TargetContext is not null
            ? ToEventTargetContext(request.TargetContext)
            : ResolveSupportPawnTargetContext(targetSnapshot);
        if (targetContext is null || string.IsNullOrWhiteSpace(targetContext.MapUniqueId))
        {
            return Results.Ok(new EventCreationResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Support.TargetMapMissing")),
                eventId: null,
                ProtocolDeliverySemantics.OnlineImmediate));
        }

        if (!TryToPawnExchangePackage(request.PawnPackage, out PawnExchangePackage? pawnPackage, out string pawnPackageFailure)
            || pawnPackage is null)
        {
            return Results.Ok(new EventCreationResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Support.PawnPackageInvalid", ("MESSAGE", pawnPackageFailure))),
                eventId: null,
                ProtocolDeliverySemantics.OnlineImmediate));
        }

        try
        {
            _ = SafePawnExchangeSerializer.Serialize(pawnPackage);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return Results.Ok(new EventCreationResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Support.PawnPackageInvalid", ("MESSAGE", ex.Message))),
                eventId: null,
                ProtocolDeliverySemantics.OnlineImmediate));
        }

        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        if (TryRejectExpiredPendingConfirmationSnapshot(
                state,
                request.Actor.UserId,
                request.Actor.ColonyId ?? string.Empty,
                nowUtc,
                out ProtocolResponse? pendingRejection))
        {
            return Results.Ok(new EventCreationResponse(
                pendingRejection!,
                eventId: null,
                ProtocolDeliverySemantics.OnlineImmediate));
        }

        SnapshotUploadResult upload = ReceiveSnapshot(
            state,
            request.Actor.UserId,
            request.Actor.ColonyId ?? string.Empty,
            multipart.Request.ConfirmedSnapshot.SnapshotId!,
            multipart.Request.ConfirmedSnapshot,
            multipart.Payload,
            nowUtc);
        if (!upload.Accepted || upload.AcceptedSnapshot is null)
        {
            return Results.Ok(new EventCreationResponse(
                ToProtocolResponse(upload),
                eventId: null,
                ProtocolDeliverySemantics.OnlineImmediate));
        }

        string acceptedSnapshotId = upload.AcceptedSnapshot.Identity.SnapshotId
            ?? multipart.Request.ConfirmedSnapshot.SnapshotId!;
        bool targetOnline = state.OnlinePresence.IsUserOnline(request.Target.UserId);
        AuthoritativeEvent supportEvent = AuthoritativeEventFactory.Create(
            ServerEventType.SupportPawn,
            ToEventParty(request.Actor),
            ToEventParty(request.Target),
            request.IdempotencyKey,
            targetOnline,
            new SupportPawnEventPayload(
                request.PawnGlobalKey,
                acceptedSnapshotId,
                request.PawnName,
                request.TemporaryControl,
                request.ExpectedReturnAtUtc,
                ToCrossMapPawnReference(request.PawnReference),
                pawnPackage,
                request.SourceTile,
                request.SourceCaravanLoadId,
                ReturnToSender: false,
                RejectionReason: null,
                request.PermanentSupport,
                request.SupportDurationDays,
                request.ExpiresAtGameTicks,
                request.AutoReturnOnSettlement,
                SourceEventId: null,
                ReturnReason: null),
            nowUtc,
            targetContext);

        LedgerAppendResult append = state.Ledger.Append(supportEvent);
        SignalIfCreated(state, append, supportEvent.Target.UserId);
        RunSnapshotPostUploadProcessors(
            state,
            request.Actor.UserId,
            request.Actor.ColonyId ?? string.Empty,
            sessionId: null,
            upload,
            nowUtc);

        EventCreationResponse created = ToEventCreationResponse(
            append,
            targetOnline ? ProtocolDeliverySemantics.OnlineImmediate : ProtocolDeliverySemantics.OfflinePending,
            T("Support.Created"));
        return Results.Ok(new EventCreationResponse(
            created.Result,
            created.EventId,
            created.DeliverySemantics,
            acceptedSnapshotId,
            upload.AcceptedSnapshot.Envelope.NextLineageToken));
    }

    private static IResult FinishSupportPawn(FinishSupportPawnRequest request, ClashOfRimNetworkState state)
    {
        ProtocolResponse? validation = ValidateFinishSupportPawnRequest(request);
        if (validation is not null)
        {
            return Results.Ok(new FinishSupportPawnResponse(validation, request.EventId, null, null, created: false));
        }

        AuthoritativeEvent? supportEvent = state.Ledger.Find(request.EventId);
        if (supportEvent is null
            || supportEvent.Type != ServerEventType.SupportPawn
            || supportEvent.Payload is not SupportPawnEventPayload payload
            || !IsVisibleParty(supportEvent.Target, request.UserId, request.ColonyId))
        {
            return Results.Ok(new FinishSupportPawnResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.EventNotFound, T("Support.NotFoundOrInvisible")),
                request.EventId,
                null,
                null,
                created: false));
        }

        if (supportEvent.Status == ServerEventStatus.AppliedToSnapshot)
        {
            AuthoritativeEvent? existing = FindSupportFinishEventFor(state.Ledger, supportEvent, request.FinishReason);
            if (existing is not null)
            {
                return Results.Ok(new FinishSupportPawnResponse(
                    new ProtocolResponse(true, ProtocolErrorCode.DuplicateRequest, T("Support.AlreadyEnded")),
                    supportEvent.EventId,
                    existing.Type == ServerEventType.SupportPawn ? existing.EventId : null,
                    existing.Type == ServerEventType.ServerNotification ? existing.EventId : null,
                    created: false));
            }
        }
        else if (supportEvent.Status is ServerEventStatus.Cancelled or ServerEventStatus.Failed)
        {
            AuthoritativeEvent? existing = FindSupportFinishEventFor(state.Ledger, supportEvent, request.FinishReason);
            return Results.Ok(new FinishSupportPawnResponse(
                new ProtocolResponse(true, ProtocolErrorCode.DuplicateRequest, T("Support.AlreadyEnded")),
                supportEvent.EventId,
                existing?.Type == ServerEventType.SupportPawn ? existing.EventId : null,
                existing?.Type == ServerEventType.ServerNotification ? existing.EventId : null,
                created: false));
        }

        PawnExchangePackage? pawnPackage = null;
        if (!request.PawnDead)
        {
            if (!TryToPawnExchangePackage(request.PawnPackage, out pawnPackage, out string pawnPackageFailure)
                || pawnPackage is null)
            {
                return Results.Ok(new FinishSupportPawnResponse(
                    ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Support.FinishPawnPackageInvalid", ("MESSAGE", pawnPackageFailure))),
                    request.EventId,
                    null,
                    null,
                    created: false));
            }

            try
            {
                _ = SafePawnExchangeSerializer.Serialize(pawnPackage);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                return Results.Ok(new FinishSupportPawnResponse(
                    ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Support.FinishPawnPackageInvalid", ("MESSAGE", ex.Message))),
                    request.EventId,
                    null,
                    null,
                    created: false));
            }
        }

        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        bool ownerOnline = state.OnlinePresence.IsUserOnline(supportEvent.Actor.UserId);
        AuthoritativeEvent finishEvent = request.PawnDead
            ? CreateSupportPawnLossReturnEvent(supportEvent, payload, request.PawnName, request.FinishReason, ownerOnline, nowUtc)
            : CreateSupportPawnFinishReturnEvent(supportEvent, payload, request, pawnPackage, ownerOnline, nowUtc);
        LedgerAppendResult append = state.Ledger.Append(finishEvent);
        state.Ledger.ChangeStatus(supportEvent.EventId, request.PawnDead ? ServerEventStatus.Failed : ServerEventStatus.AppliedToSnapshot);
        SignalIfCreated(state, append, supportEvent.Actor.UserId);

        return Results.Ok(new FinishSupportPawnResponse(
            append.Created
                ? ProtocolResponse.Ok(request.PawnDead ? ServerLocalization.Text("Support.Dead") : ServerLocalization.Text("Support.Finished"))
                : new ProtocolResponse(true, ProtocolErrorCode.DuplicateRequest, ServerLocalization.Text("Support.FinishDuplicate")),
            supportEvent.EventId,
            finishEvent.Type == ServerEventType.SupportPawn ? finishEvent.EventId : null,
            finishEvent.Type == ServerEventType.ServerNotification ? finishEvent.EventId : null,
            append.Created));
    }

    private static IResult RejectSupportPawn(RejectSupportPawnRequest request, ClashOfRimNetworkState state)
    {
        if (string.IsNullOrWhiteSpace(request.EventId)
            || string.IsNullOrWhiteSpace(request.UserId)
            || string.IsNullOrWhiteSpace(request.ColonyId)
            || string.IsNullOrWhiteSpace(request.CurrentSnapshotId))
        {
            return Results.Ok(new RejectSupportPawnResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Support.RejectMissingFields")),
                request.EventId,
                returnEventId: null,
                returnEventCreated: false));
        }

        AuthoritativeEvent? supportEvent = state.Ledger.Find(request.EventId);
        if (supportEvent is null || !IsVisibleParty(supportEvent.Target, request.UserId, request.ColonyId))
        {
            return Results.Ok(new RejectSupportPawnResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.EventNotFound, T("Support.NotFoundOrInvisible")),
                request.EventId,
                returnEventId: null,
                returnEventCreated: false));
        }

        if (supportEvent.Type != ServerEventType.SupportPawn || supportEvent.Payload is not SupportPawnEventPayload supportPayload)
        {
            return Results.Ok(new RejectSupportPawnResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Support.RejectSupportOnly")),
                request.EventId,
                returnEventId: null,
                returnEventCreated: false));
        }

        if (supportEvent.RejectionPolicy != EventRejectionPolicy.RejectableByTarget || !supportPayload.TemporaryControl)
        {
            return Results.Ok(new RejectSupportPawnResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, T("Support.NotRejectable")),
                request.EventId,
                returnEventId: null,
                returnEventCreated: false));
        }

        if (supportEvent.Status == ServerEventStatus.AppliedToSnapshot)
        {
            return Results.Ok(new RejectSupportPawnResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.Conflict, T("Support.AlreadyAppliedCannotReject")),
                request.EventId,
                returnEventId: null,
                returnEventCreated: false));
        }

        if (supportEvent.Status == ServerEventStatus.RejectedByTarget)
        {
            AuthoritativeEvent? existingReturn = FindSupportPawnReturnFor(state.Ledger, supportEvent);
            if (existingReturn is not null)
            {
                return Results.Ok(new RejectSupportPawnResponse(
                    new ProtocolResponse(true, ProtocolErrorCode.DuplicateRequest, ServerLocalization.Text("Support.RejectedDuplicate")),
                    supportEvent.EventId,
                    existingReturn.EventId,
                    returnEventCreated: false));
            }
        }

        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        AuthoritativeEvent rejected = state.Ledger.MarkRejected(supportEvent.EventId, nowUtc, request.Reason);
        bool originalActorOnline = state.OnlinePresence.IsUserOnline(supportEvent.Actor.UserId);
        AuthoritativeEvent returnEvent = CreateRejectedSupportPawnReturnEvent(
            rejected,
            supportPayload,
            request.Reason,
            originalActorOnline,
            nowUtc);
        LedgerAppendResult append = state.Ledger.Append(returnEvent);
        SignalIfCreated(state, append, supportEvent.Actor.UserId);

        return Results.Ok(new RejectSupportPawnResponse(
            append.Created
                ? ProtocolResponse.Ok(ServerLocalization.Text("Support.Rejected"))
                : new ProtocolResponse(true, ProtocolErrorCode.DuplicateRequest, ServerLocalization.Text("Support.RejectedDuplicate")),
            rejected.EventId,
            append.Event.EventId,
            append.Created));
    }

    private static ProtocolResponse? ValidateSupportPawnRequest(CreateSupportPawnRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey)
            || request.Actor is null
            || request.Target is null
            || string.IsNullOrWhiteSpace(request.Actor.UserId)
            || string.IsNullOrWhiteSpace(request.Actor.ColonyId)
            || string.IsNullOrWhiteSpace(request.Actor.SnapshotId)
            || string.IsNullOrWhiteSpace(request.Target.UserId)
            || string.IsNullOrWhiteSpace(request.Target.ColonyId)
            || string.IsNullOrWhiteSpace(request.PawnGlobalKey)
            || string.IsNullOrWhiteSpace(request.SourceSnapshotId))
        {
            return ProtocolResponse.Reject(
                ProtocolErrorCode.ValidationFailed,
                T("Support.CreateMissingFields"));
        }

        if (request.PawnReference is not null
            && !string.Equals(request.PawnReference.GlobalId, request.PawnGlobalKey, StringComparison.Ordinal))
        {
            return ProtocolResponse.Reject(
                ProtocolErrorCode.ValidationFailed,
                T("Support.ReferenceMismatch"));
        }

        if (request.PawnPackage is not null
            && (request.PawnPackage.Reference is null
                || !string.Equals(request.PawnPackage.Reference.GlobalId, request.PawnGlobalKey, StringComparison.Ordinal)))
        {
            return ProtocolResponse.Reject(
                ProtocolErrorCode.ValidationFailed,
                T("Support.PackageReferenceMismatch"));
        }

        if (request.PermanentSupport)
        {
            return null;
        }

        if (request.SupportDurationDays is < 3 or > 30)
        {
            return ProtocolResponse.Reject(
                ProtocolErrorCode.ValidationFailed,
                T("Support.TemporaryDurationInvalid"));
        }

        return null;
    }

    private static ProtocolResponse? ValidateDiplomacyCreateRequest(CreateDiplomacyEventRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey)
            || request.Actor is null
            || request.Target is null
            || string.IsNullOrWhiteSpace(request.Actor.UserId)
            || string.IsNullOrWhiteSpace(request.Actor.ColonyId)
            || string.IsNullOrWhiteSpace(request.Actor.SnapshotId)
            || string.IsNullOrWhiteSpace(request.Target.UserId)
            || string.IsNullOrWhiteSpace(request.Target.ColonyId)
            || string.IsNullOrWhiteSpace(request.Kind))
        {
            return ProtocolResponse.Reject(
                ProtocolErrorCode.ValidationFailed,
                T("Diplomacy.CreateMissingFields"));
        }

        if (!IsDiplomacyKind(request.Kind))
        {
            return ProtocolResponse.Reject(
                ProtocolErrorCode.ValidationFailed,
                T("Diplomacy.UnknownKind"));
        }

        if (string.Equals(request.Actor.UserId, request.Target.UserId, StringComparison.Ordinal)
            && string.Equals(request.Actor.ColonyId, request.Target.ColonyId, StringComparison.Ordinal))
        {
            return ProtocolResponse.Reject(
                ProtocolErrorCode.ValidationFailed,
                T("Diplomacy.CannotTargetSelf"));
        }

        return null;
    }

    private static ProtocolResponse? ValidateDiplomacyRelationTransition(
        ServerEventType type,
        string currentRelationKind,
        DiplomacyRelationRecord? currentRelation,
        DateTimeOffset nowUtc,
        TimeSpan cooldown)
    {
        ProtocolResponse? transitionValidation = type switch
        {
            ServerEventType.AllianceRequest when !string.Equals(currentRelationKind, "Neutral", StringComparison.Ordinal) =>
                ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, T("Diplomacy.AllianceRequiresNeutral")),
            ServerEventType.AllianceCancellation when !string.Equals(currentRelationKind, "Ally", StringComparison.Ordinal) =>
                ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, T("Diplomacy.CancelAllianceRequiresAlly")),
            ServerEventType.WarDeclaration when string.Equals(currentRelationKind, "Ally", StringComparison.Ordinal) =>
                ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, T("Diplomacy.CannotDeclareWarOnAlly")),
            ServerEventType.WarDeclaration when string.Equals(currentRelationKind, "Hostile", StringComparison.Ordinal) =>
                ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, T("Diplomacy.AlreadyHostile")),
            ServerEventType.PeaceRequest when !string.Equals(currentRelationKind, "Hostile", StringComparison.Ordinal) =>
                ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, T("Diplomacy.PeaceRequiresHostile")),
            _ => null
        };
        if (transitionValidation is not null)
        {
            return transitionValidation;
        }

        return ValidateDiplomacyRelationCooldown(currentRelation, nowUtc, cooldown);
    }

    private static ProtocolResponse? ValidateDiplomacyRelationCooldown(
        DiplomacyRelationRecord? currentRelation,
        DateTimeOffset nowUtc,
        TimeSpan cooldown)
    {
        if (currentRelation is null || cooldown <= TimeSpan.Zero)
        {
            return null;
        }

        DateTimeOffset availableAtUtc = currentRelation.UpdatedAtUtc + cooldown;
        if (availableAtUtc <= nowUtc)
        {
            return null;
        }

        TimeSpan remaining = availableAtUtc - nowUtc;
        string remainingText = remaining.TotalHours >= 1
            ? T("Diplomacy.TimeHours", ("VALUE", Math.Ceiling(remaining.TotalHours).ToString("0", CultureInfo.InvariantCulture)))
            : T("Diplomacy.TimeMinutes", ("VALUE", Math.Ceiling(remaining.TotalMinutes).ToString("0", CultureInfo.InvariantCulture)));
        return ProtocolResponse.Reject(
            ProtocolErrorCode.ServerRejected,
            T("Diplomacy.RelationCooldown", ("REMAINING", remainingText)));
    }

    private static ProtocolResponse? ValidateSupportRequestDiplomacyTransition(
        ClashOfRimNetworkState state,
        CreateDiplomacyEventRequest request,
        string currentRelationKind,
        DateTimeOffset nowUtc)
    {
        if (!string.Equals(currentRelationKind, DiplomacyRelationRegistry.RelationAlly, StringComparison.Ordinal))
        {
            return ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, T("Diplomacy.SupportRequestRequiresAlly"));
        }

        AuthoritativeEvent? latest = FindLatestSupportRequestEvent(
            state.Ledger,
            request.Actor.UserId,
            request.Actor.ColonyId,
            request.Target.UserId,
            request.Target.ColonyId);
        if (latest is null || state.ServerConfiguration.DiplomacySupportRequestCooldown <= TimeSpan.Zero)
        {
            return null;
        }

        DateTimeOffset availableAtUtc = latest.CreatedAtUtc + state.ServerConfiguration.DiplomacySupportRequestCooldown;
        if (availableAtUtc <= nowUtc)
        {
            return null;
        }

        TimeSpan remaining = availableAtUtc - nowUtc;
        string remainingText = remaining.TotalMinutes >= 1
            ? T("Diplomacy.TimeMinutes", ("VALUE", Math.Ceiling(remaining.TotalMinutes).ToString("0", CultureInfo.InvariantCulture)))
            : T("Diplomacy.TimeSeconds", ("VALUE", Math.Ceiling(remaining.TotalSeconds).ToString("0", CultureInfo.InvariantCulture)));
        return ProtocolResponse.Reject(
            ProtocolErrorCode.ServerRejected,
            T("Diplomacy.SupportRequestCooldown", ("REMAINING", remainingText)));
    }

    private static AuthoritativeEvent? FindLatestSupportRequestEvent(
        IAuthoritativeEventLedger ledger,
        string userA,
        string? colonyA,
        string userB,
        string? colonyB)
    {
        return ledger.ListByTypeForActor(ServerEventType.ServerNotification, userA, colonyA)
            .Where(IsSupportRequestNotification)
            .Where(evt => MatchesEndpoint(evt.Actor, userA, colonyA)
                && MatchesEndpoint(evt.Target, userB, colonyB))
            .OrderByDescending(evt => evt.CreatedAtUtc)
            .FirstOrDefault();
    }

    private static bool IsSupportRequestNotification(AuthoritativeEvent evt)
    {
        return evt.Type == ServerEventType.ServerNotification
            && evt.Payload is ServerNotificationEventPayload payload
            && payload.NotificationId.StartsWith("support-request:", StringComparison.Ordinal);
    }

    private static bool MatchesEndpoint(EventParty party, string userId, string? colonyId)
    {
        return string.Equals(party.UserId, userId, StringComparison.Ordinal)
            && string.Equals(party.ColonyId ?? string.Empty, colonyId ?? string.Empty, StringComparison.Ordinal);
    }

    private static AuthoritativeEvent? FindEventByIdempotencyKey(
        IAuthoritativeEventLedger ledger,
        string idempotencyKey)
    {
        return string.IsNullOrWhiteSpace(idempotencyKey)
            ? null
            : ledger.FindByIdempotencyKey(idempotencyKey);
    }

    private static bool IsDiplomacyKind(string? kind)
    {
        return string.Equals(kind, "AllianceRequest", StringComparison.OrdinalIgnoreCase)
            || string.Equals(kind, "AllianceCancellation", StringComparison.OrdinalIgnoreCase)
            || string.Equals(kind, "SupportRequest", StringComparison.OrdinalIgnoreCase)
            || string.Equals(kind, "WarDeclaration", StringComparison.OrdinalIgnoreCase)
            || string.Equals(kind, "PeaceRequest", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSupportRequestKind(string? kind)
    {
        return string.Equals(kind, "SupportRequest", StringComparison.OrdinalIgnoreCase);
    }

    private static ServerEventType ResolveDiplomacyEventType(string kind)
    {
        if (string.Equals(kind, "AllianceRequest", StringComparison.OrdinalIgnoreCase))
        {
            return ServerEventType.AllianceRequest;
        }

        if (string.Equals(kind, "AllianceCancellation", StringComparison.OrdinalIgnoreCase))
        {
            return ServerEventType.AllianceCancellation;
        }

        if (IsSupportRequestKind(kind))
        {
            return ServerEventType.ServerNotification;
        }

        if (string.Equals(kind, "PeaceRequest", StringComparison.OrdinalIgnoreCase))
        {
            return ServerEventType.PeaceRequest;
        }

        return ServerEventType.WarDeclaration;
    }

    private static RaidOpponentKind ResolveRaidOpponentKind(string? kind)
    {
        if (Enum.TryParse(kind, ignoreCase: true, out RaidOpponentKind parsed))
        {
            return parsed;
        }

        return RaidOpponentKind.Player;
    }

    private static LedgerEventPayload CreateDiplomacyPayload(
        ServerEventType type,
        string id,
        string? message,
        DateTimeOffset? expiresAtUtc)
    {
        return type switch
        {
            ServerEventType.AllianceRequest => new AllianceRequestEventPayload(id, message, expiresAtUtc),
            ServerEventType.AllianceCancellation => new AllianceCancellationEventPayload(id, message),
            ServerEventType.PeaceRequest => new PeaceRequestEventPayload(id, message, expiresAtUtc),
            ServerEventType.WarDeclaration => new WarDeclarationEventPayload(id, message),
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, T("Diplomacy.NotDiplomacyEventType"))
        };
    }

    private static ServerNotificationEventPayload CreateSupportRequestPayload(CreateDiplomacyEventRequest request)
    {
        string message = string.IsNullOrWhiteSpace(request.Message)
            ? T("Diplomacy.SupportRequestDefaultMessage", ("USER", request.Actor.UserId))
            : request.Message!;
        string fullMessage = T(
            "Diplomacy.SupportRequestMessage",
            ("USER", request.Actor.UserId),
            ("COLONY", request.Actor.ColonyId),
            ("MESSAGE", message));
        return new ServerNotificationEventPayload(
            "support-request:" + request.IdempotencyKey,
            T("Diplomacy.SupportRequestTitle"),
            fullMessage,
            ServerNotificationSeverity.Info,
            FromAdministrator: false,
            AdministratorUserId: null,
            OnlineOnly: false);
    }

    private static bool IsDiplomacyEventType(ServerEventType type)
    {
        return type is ServerEventType.AllianceRequest
            or ServerEventType.AllianceCancellation
            or ServerEventType.WarDeclaration
            or ServerEventType.PeaceRequest;
    }

    private static string? ResolveDiplomacyRelationKind(ServerEventType type, bool accepted)
    {
        if (!accepted)
        {
            return null;
        }

        return type switch
        {
            ServerEventType.AllianceRequest => "Ally",
            ServerEventType.AllianceCancellation => "Neutral",
            ServerEventType.WarDeclaration => "Hostile",
            ServerEventType.PeaceRequest => "Neutral",
            _ => null
        };
    }

    private static string? PersistDiplomacyRelation(
        ClashOfRimNetworkState state,
        AuthoritativeEvent diplomacyEvent,
        bool accepted,
        DateTimeOffset nowUtc)
    {
        string? relationKind = ResolveDiplomacyRelationKind(diplomacyEvent.Type, accepted);
        if (relationKind is null)
        {
            return null;
        }

        DiplomacyRelationRecord record = state.DiplomacyRelations.SetRelationKind(
            diplomacyEvent.Actor.UserId,
            diplomacyEvent.Actor.ColonyId,
            diplomacyEvent.Target.UserId,
            diplomacyEvent.Target.ColonyId,
            relationKind,
            diplomacyEvent.EventId,
            nowUtc);
        return record.RelationKind;
    }

    private static AuthoritativeEvent CreateDiplomacyResponseNotification(
        AuthoritativeEvent diplomacyEvent,
        bool accepted,
        bool originalActorOnline,
        DateTimeOffset createdAtUtc)
    {
        string action = accepted ? T("Diplomacy.ActionAccepted") : T("Diplomacy.ActionRejected");
        string title = diplomacyEvent.Type switch
        {
            ServerEventType.AllianceRequest => T("Diplomacy.AllianceRequestRespondedTitle", ("ACTION", action)),
            ServerEventType.AllianceCancellation => T("Diplomacy.AllianceCancellationConfirmedTitle"),
            ServerEventType.PeaceRequest => T("Diplomacy.PeaceRequestRespondedTitle", ("ACTION", action)),
            ServerEventType.WarDeclaration => T("Diplomacy.WarDeclarationConfirmedTitle"),
            _ => T("Diplomacy.EventProcessedTitle")
        };
        string message = T(
            "Diplomacy.ResponseNotificationMessage",
            ("USER", diplomacyEvent.Target.UserId),
            ("COLONY", diplomacyEvent.Target.ColonyId),
            ("ACTION", action),
            ("EVENT", diplomacyEvent.EventId));
        string notificationId = $"diplomacy-response:{diplomacyEvent.EventId}:{accepted}";
        return AuthoritativeEventFactory.Create(
            ServerEventType.ServerNotification,
            new EventParty("server"),
            diplomacyEvent.Actor,
            notificationId,
            originalActorOnline,
            new ServerNotificationEventPayload(
                notificationId,
                title,
                message,
                accepted ? ServerNotificationSeverity.Info : ServerNotificationSeverity.Warning,
                FromAdministrator: false,
                RelatedEventId: diplomacyEvent.EventId,
                RelatedEventType: diplomacyEvent.Type.ToString(),
                RelatedUserId: diplomacyEvent.Target.UserId,
                RelatedColonyId: diplomacyEvent.Target.ColonyId,
                RelatedAccepted: accepted),
            createdAtUtc);
    }

    private static ProtocolResponse? ValidateFinishSupportPawnRequest(FinishSupportPawnRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey)
            || string.IsNullOrWhiteSpace(request.EventId)
            || string.IsNullOrWhiteSpace(request.UserId)
            || string.IsNullOrWhiteSpace(request.ColonyId)
            || string.IsNullOrWhiteSpace(request.CurrentSnapshotId)
            || string.IsNullOrWhiteSpace(request.FinishReason)
            || string.IsNullOrWhiteSpace(request.PawnGlobalKey))
        {
            return ProtocolResponse.Reject(
                ProtocolErrorCode.ValidationFailed,
                T("Support.FinishMissingFields"));
        }

        return null;
    }

    private static void SignalIfCreated(
        ClashOfRimNetworkState state,
        LedgerAppendResult append,
        string targetUserId)
    {
        LogEventAppend(state, append, "signal-target");
        if (append.Created && !string.IsNullOrWhiteSpace(targetUserId))
        {
            state.EventNotifications.SignalUser(targetUserId);
        }
    }

    private static void SignalWorldConfigurationChanged(ClashOfRimNetworkState state, params string?[] additionalUserIds)
    {
        IEnumerable<string> knownUsers = state.Players.List().Select(player => player.UserId);
        IEnumerable<string> additionalUsers = additionalUserIds
            .Where(userId => !string.IsNullOrWhiteSpace(userId))
            .Select(userId => userId!);
        state.WorldConfigurationNotifications.SignalUsers(knownUsers.Concat(additionalUsers));
    }

    private static AuthoritativeEvent? FindUnsettledAttackerRaid(
        ClashOfRimNetworkState state,
        string? attackerUserId,
        string? attackerColonyId,
        string? excludedIdempotencyKey)
    {
        if (string.IsNullOrWhiteSpace(attackerUserId))
        {
            return null;
        }

        return RaidUnsettledProjector.BuildForAttacker(
                attackerUserId!,
                attackerColonyId,
                state.Ledger.ListByTypeForActor(ServerEventType.Raid, attackerUserId!, attackerColonyId),
                excludedIdempotencyKey)
            .FirstOrDefault();
    }

    private static AuthoritativeEvent? FindDefenderLoginBlockingRaid(
        ClashOfRimNetworkState state,
        string? defenderUserId,
        string? defenderColonyId)
    {
        if (string.IsNullOrWhiteSpace(defenderUserId))
        {
            return null;
        }

        return RaidUnsettledProjector.BuildForDefender(
                defenderUserId!,
                defenderColonyId,
                state.Ledger.ListByTypeForTarget(ServerEventType.Raid, defenderUserId!, defenderColonyId))
            .FirstOrDefault();
    }

    private static void RunClientLifecycleHooks(
        ClashOfRimNetworkState state,
        ClientLifecycleEvent lifecycleEvent)
    {
        foreach (ClientLifecycleHook hook in ClientLifecycleHooks)
        {
            hook(state, lifecycleEvent);
        }
    }

    private static void RunSnapshotUploadedLifecycleHooks(
        ClashOfRimNetworkState state,
        string userId,
        string colonyId,
        string? sessionId,
        SnapshotUploadResult upload,
        DateTimeOffset occurredAtUtc)
    {
        if (upload.AcceptedSnapshot is null ||
            string.IsNullOrWhiteSpace(upload.AcceptedSnapshot.Identity.SnapshotId))
        {
            return;
        }

        RunClientLifecycleHooks(
            state,
            ClientLifecycleEvent.SnapshotUploaded(
                userId,
                colonyId,
                sessionId,
                upload.AcceptedSnapshot.Identity.SnapshotId,
                occurredAtUtc));
    }

    private static void ReconcileRaidTimeoutsOnClientLifecycle(
        ClashOfRimNetworkState state,
        ClientLifecycleEvent lifecycleEvent)
    {
        if (lifecycleEvent.Kind != ClientLifecycleEventKind.Disconnected)
        {
            return;
        }

        ReconcileExpiredRaidEvents(state, lifecycleEvent.OccurredAtUtc);
    }

    private static void ActivateRaidProtectionOnClientLifecycle(
        ClashOfRimNetworkState state,
        ClientLifecycleEvent lifecycleEvent)
    {
        if (lifecycleEvent.Kind != ClientLifecycleEventKind.LoggedIn
            || string.IsNullOrWhiteSpace(lifecycleEvent.UserId)
            || string.IsNullOrWhiteSpace(lifecycleEvent.ColonyId))
        {
            return;
        }

        RaidCooldownPolicy policy = BuildRaidCooldownPolicy(state.ServerConfiguration);
        int activated = 0;
        foreach (AuthoritativeEvent raid in state.Ledger.ListByTypeForTarget(
            ServerEventType.Raid,
            lifecycleEvent.UserId,
            lifecycleEvent.ColonyId))
        {
            if (!ShouldActivateRaidProtectionTimer(raid, policy))
            {
                continue;
            }

            if (state.RaidProtectionActivations.ActivateIfMissing(
                raid.EventId,
                raid.Target.UserId,
                raid.Target.ColonyId,
                lifecycleEvent.OccurredAtUtc))
            {
                activated++;
            }
        }

        if (activated > 0)
        {
            state.RuntimeLogger.LogInformation(
                "Activated raid protection countdown for defender user={UserId} colony={ColonyId} count={Count}",
                lifecycleEvent.UserId,
                lifecycleEvent.ColonyId,
                activated);
        }
    }

    private static bool ShouldActivateRaidProtectionTimer(
        AuthoritativeEvent raid,
        RaidCooldownPolicy policy)
    {
        if (raid.Payload is not RaidEventPayload { OpponentKind: RaidOpponentKind.Player } payload)
        {
            return false;
        }

        if (payload.FinishedAtUtc != null || payload.Settlement != null || payload.ReturnedSnapshotId != null)
        {
            return policy.SettlementCooldown > TimeSpan.Zero;
        }

        if (raid.Status == ServerEventStatus.Failed)
        {
            return policy.TimeoutCooldown > TimeSpan.Zero;
        }

        if (raid.Status == ServerEventStatus.Cancelled)
        {
            return policy.CancelledCooldown > TimeSpan.Zero;
        }

        return false;
    }

    private static DateTimeOffset? ResolveRaidProtectionActivatedAt(
        ClashOfRimNetworkState state,
        AuthoritativeEvent raid)
    {
        return state.RaidProtectionActivations.FindActivatedAt(
            raid.EventId,
            raid.Target.UserId,
            raid.Target.ColonyId);
    }

    private static void ReconcilePendingConfirmationsOnClientLifecycle(
        ClashOfRimNetworkState state,
        ClientLifecycleEvent lifecycleEvent)
    {
        if (string.IsNullOrWhiteSpace(lifecycleEvent.UserId)
            || string.IsNullOrWhiteSpace(lifecycleEvent.ColonyId))
        {
            return;
        }

        if (lifecycleEvent.Kind is ClientLifecycleEventKind.LoggedIn
            or ClientLifecycleEventKind.InitialWorldSessionPrepared)
        {
            ReconcilePendingConfirmationsForColony(
                state,
                lifecycleEvent.UserId,
                lifecycleEvent.ColonyId,
                lifecycleEvent.OccurredAtUtc,
                forceCancel: true);
            return;
        }

        if (lifecycleEvent.Kind != ClientLifecycleEventKind.SnapshotUploaded)
        {
            ReconcilePendingConfirmationsForColony(
                state,
                lifecycleEvent.UserId,
                lifecycleEvent.ColonyId,
                lifecycleEvent.OccurredAtUtc,
                forceCancel: false);
        }
    }

    private static PendingConfirmationReconciliationResult ReconcilePendingConfirmationsForColony(
        ClashOfRimNetworkState state,
        string userId,
        string colonyId,
        DateTimeOffset nowUtc,
        bool forceCancel)
    {
        TimeSpan timeout = state.ServerConfiguration.PendingConfirmationTimeout;
        BankPendingConfirmationReconciliationResult bankResult = state.BankLoans.ReconcilePendingConfirmations(
            userId,
            colonyId,
            nowUtc,
            timeout,
            forceCancel);
        MercenaryPendingConfirmationReconciliationResult mercenaryResult = state.MercenaryContracts.ReconcilePendingConfirmations(
            userId,
            colonyId,
            nowUtc,
            timeout,
            forceCancel);
        return new PendingConfirmationReconciliationResult(bankResult, mercenaryResult);
    }

    private static bool TryRejectExpiredPendingConfirmationSnapshot(
        ClashOfRimNetworkState state,
        string userId,
        string colonyId,
        DateTimeOffset nowUtc,
        out ProtocolResponse? rejection)
    {
        PendingConfirmationReconciliationResult result = ReconcilePendingConfirmationsForColony(
            state,
            userId,
            colonyId,
            nowUtc,
            forceCancel: false);
        if (!result.Changed)
        {
            rejection = null;
            return false;
        }

        rejection = ProtocolResponse.Reject(
            ProtocolErrorCode.Conflict,
            T("PendingConfirmation.ExpiredCancelled"));
        return true;
    }

    private static void ConfirmPendingOperationsOnClientLifecycle(
        ClashOfRimNetworkState state,
        ClientLifecycleEvent lifecycleEvent)
    {
        if (lifecycleEvent.Kind != ClientLifecycleEventKind.SnapshotUploaded
            || string.IsNullOrWhiteSpace(lifecycleEvent.UserId)
            || string.IsNullOrWhiteSpace(lifecycleEvent.ColonyId)
            || string.IsNullOrWhiteSpace(lifecycleEvent.CurrentSnapshotId))
        {
            return;
        }

        LatestSnapshotRecord? latest = state.SnapshotStore.GetLatest(
            lifecycleEvent.UserId,
            lifecycleEvent.ColonyId);
        if (!string.Equals(
                latest?.Identity.SnapshotId,
                lifecycleEvent.CurrentSnapshotId,
                StringComparison.Ordinal))
        {
            return;
        }

        state.BankLoans.ConfirmPendingForSnapshot(
            lifecycleEvent.UserId,
            lifecycleEvent.ColonyId,
            lifecycleEvent.CurrentSnapshotId!,
            latest?.Envelope.GameTicks,
            lifecycleEvent.OccurredAtUtc);
        state.MercenaryContracts.ConfirmPendingForSnapshot(
            lifecycleEvent.UserId,
            lifecycleEvent.ColonyId,
            lifecycleEvent.CurrentSnapshotId!,
            lifecycleEvent.OccurredAtUtc);
    }

    private static void ReconcileAbandonedColonyOnClientLifecycle(
        ClashOfRimNetworkState state,
        ClientLifecycleEvent lifecycleEvent)
    {
        if (lifecycleEvent.Kind != ClientLifecycleEventKind.ColonyAbandoned
            || string.IsNullOrWhiteSpace(lifecycleEvent.UserId)
            || string.IsNullOrWhiteSpace(lifecycleEvent.ColonyId))
        {
            return;
        }

        string userId = lifecycleEvent.UserId;
        string colonyId = lifecycleEvent.ColonyId;
        DateTimeOffset nowUtc = lifecycleEvent.OccurredAtUtc;

        state.RaidPreparations.RemoveForColony(userId, colonyId);
        state.RaidProtectionActivations.RemoveForColony(userId, colonyId);
        state.MercenaryContracts.CloseForColony(userId, colonyId);

        HashSet<string> usersToSignal = new(StringComparer.Ordinal);
        foreach (AuthoritativeEvent ledgerEvent in state.Ledger.ListAll())
        {
            if (IsTerminalForAbandonedColonyCleanup(ledgerEvent.Status)
                || IsColonyTombstoneEvent(ledgerEvent, userId, colonyId))
            {
                continue;
            }

            bool actorMatches = IsVisibleParty(ledgerEvent.Actor, userId, colonyId);
            bool targetMatches = IsVisibleParty(ledgerEvent.Target, userId, colonyId);
            if (!actorMatches && !targetMatches)
            {
                continue;
            }

            if (TryCloseAbandonedTradeEvent(state, ledgerEvent, userId, colonyId, nowUtc, usersToSignal))
            {
                continue;
            }

            ServerEventStatus terminalStatus = ResolveAbandonedColonyTerminalStatus(ledgerEvent, actorMatches, targetMatches);
            state.Ledger.ChangeStatus(ledgerEvent.EventId, terminalStatus);
            AddSignalUser(usersToSignal, ledgerEvent.Actor.UserId);
            AddSignalUser(usersToSignal, ledgerEvent.Target.UserId);
        }

        if (usersToSignal.Count > 0)
        {
            state.EventNotifications.SignalUsers(usersToSignal);
        }
    }

    private static bool TryCloseAbandonedTradeEvent(
        ClashOfRimNetworkState state,
        AuthoritativeEvent ledgerEvent,
        string abandonedUserId,
        string abandonedColonyId,
        DateTimeOffset nowUtc,
        HashSet<string> usersToSignal)
    {
        if (ledgerEvent.Type != ServerEventType.Trade
            || ledgerEvent.Payload is not TradeEventPayload payload)
        {
            return false;
        }

        if (payload.Stage == TradeStage.MarketOrder)
        {
            if (!IsVisibleParty(ledgerEvent.Actor, abandonedUserId, abandonedColonyId))
            {
                return false;
            }

            state.Ledger.ChangeStatus(ledgerEvent.EventId, ServerEventStatus.Cancelled);
            foreach (AuthoritativeEvent memo in FindActiveTradeAcceptanceMemos(state.Ledger, ledgerEvent.EventId))
            {
                state.Ledger.ChangeStatus(memo.EventId, ServerEventStatus.Cancelled);
                AddSignalUser(usersToSignal, memo.Target.UserId);
            }

            AddSignalUser(usersToSignal, ledgerEvent.Actor.UserId);
            return true;
        }

        if (payload.Stage == TradeStage.AcceptedMemo
            && IsVisibleParty(ledgerEvent.Actor, abandonedUserId, abandonedColonyId))
        {
            state.Ledger.ChangeStatus(ledgerEvent.EventId, ServerEventStatus.Cancelled);
            AddSignalUser(usersToSignal, ledgerEvent.Actor.UserId);
            return true;
        }

        return false;
    }

    private static ServerEventStatus ResolveAbandonedColonyTerminalStatus(
        AuthoritativeEvent ledgerEvent,
        bool actorMatches,
        bool targetMatches)
    {
        if (ledgerEvent.Type == ServerEventType.Raid && actorMatches)
        {
            return ServerEventStatus.Failed;
        }

        return ServerEventStatus.Cancelled;
    }

    private static int ReconcileFailedSupportPawnEventsForDeletedActors(
        ClashOfRimNetworkState state,
        string? visibleUserId)
    {
        if (string.IsNullOrWhiteSpace(visibleUserId))
        {
            return 0;
        }

        int updated = 0;
        foreach (AuthoritativeEvent ledgerEvent in state.Ledger.ListForUser(visibleUserId!))
        {
            if (ledgerEvent.Type != ServerEventType.SupportPawn
                || ledgerEvent.Status != ServerEventStatus.Failed
                || !string.IsNullOrWhiteSpace(ledgerEvent.AppliedSnapshotId)
                || string.IsNullOrWhiteSpace(ledgerEvent.Actor.UserId)
                || string.IsNullOrWhiteSpace(ledgerEvent.Actor.ColonyId)
                || !state.Players.IsDeleted(ledgerEvent.Actor.UserId, ledgerEvent.Actor.ColonyId))
            {
                continue;
            }

            state.Ledger.ChangeStatus(ledgerEvent.EventId, ServerEventStatus.Cancelled);
            updated++;
        }

        if (updated > 0)
        {
            state.EventNotifications.SignalUser(visibleUserId!);
        }

        return updated;
    }

    private static bool IsTerminalForAbandonedColonyCleanup(ServerEventStatus status)
    {
        return status is ServerEventStatus.AppliedToSnapshot
            or ServerEventStatus.RejectedByTarget
            or ServerEventStatus.Conflict
            or ServerEventStatus.Cancelled
            or ServerEventStatus.Failed;
    }

    private static bool IsColonyTombstoneEvent(AuthoritativeEvent ledgerEvent, string userId, string colonyId)
    {
        return ledgerEvent.Payload is ServerNotificationEventPayload payload
            && string.Equals(payload.NotificationId, BuildColonyTombstoneNotificationId(userId, colonyId), StringComparison.Ordinal);
    }

    private static void AddSignalUser(HashSet<string> users, string? userId)
    {
        if (!string.IsNullOrWhiteSpace(userId) && !string.Equals(userId, "server", StringComparison.Ordinal))
        {
            users.Add(userId!);
        }
    }

    private static AuthoritativeEvent? FindActiveSourceRaidForAttacker(
        ClashOfRimNetworkState state,
        string? attackerUserId,
        string? attackerColonyId)
    {
        if (string.IsNullOrWhiteSpace(attackerUserId))
        {
            return null;
        }

        return RaidUnsettledProjector.BuildActiveSourceForAttacker(
                attackerUserId!,
                attackerColonyId,
                state.Ledger.ListByTypeForActor(ServerEventType.Raid, attackerUserId!, attackerColonyId))
            .FirstOrDefault();
    }

    private static ActiveRaidRecoveryDto? BuildActiveRaidRecoveryDto(
        ClashOfRimNetworkState state,
        string? attackerUserId,
        string? attackerColonyId,
        DateTimeOffset nowUtc)
    {
        AuthoritativeEvent? raid = FindActiveSourceRaidForAttacker(state, attackerUserId, attackerColonyId);
        if (raid?.Payload is not RaidEventPayload payload || payload.StartedAtUtc is null)
        {
            return null;
        }

        RaidDefenseLockPolicy policy = BuildRaidDefenseLockPolicy(state.ServerConfiguration);
        DateTimeOffset startedAtUtc = payload.StartedAtUtc.Value;
        return new ActiveRaidRecoveryDto(
            raid.EventId,
            raid.Status.ToString(),
            nowUtc,
            startedAtUtc,
            startedAtUtc + policy.MaxRaidDuration,
            startedAtUtc + policy.ServerTimeoutDuration,
            raid.Target.UserId,
            raid.Target.ColonyId,
            payload.DefenderSnapshotId);
    }

    private static RaidTimeoutProcessingResult ReconcileExpiredRaidEvents(
        ClashOfRimNetworkState state,
        DateTimeOffset nowUtc)
    {
        RaidTimeoutProcessingResult result = RaidTimeoutProcessor.ProcessExpiredRaids(
            state.Ledger,
            state.Ledger.ListByType(ServerEventType.Raid),
            nowUtc,
            BuildRaidDefenseLockPolicy(state.ServerConfiguration),
            party => state.OnlinePresence.IsUserOnline(party.UserId));
        if (result.FailedRaidCount == 0 &&
            result.NotificationCount == 0 &&
            result.AttackerLossEventCount == 0)
        {
            return result;
        }

        RaidCleanupEventAppendResult cleanupEvents;
        lock (state.RaidSettlementSnapshotMutationGate)
        {
            SettleOfflineTimedOutRaidSnapshots(state, result.OfflineFailedRaids, nowUtc);
            cleanupEvents = CleanupTimedOutRaidAttackerSnapshots(state, result.FailedRaids, nowUtc);
        }

        IReadOnlyList<string> usersToSignal = result.NotificationEvents
            .Select(evt => evt.Target.UserId)
            .Concat(result.AttackerLossEvents.Select(evt => evt.Target.UserId))
            .Concat(cleanupEvents.AttackerLossEvents.Select(evt => evt.Target.UserId))
            .Concat(cleanupEvents.SupportLossNotifications.Select(evt => evt.Target.UserId))
            .Where(userId => !string.IsNullOrWhiteSpace(userId))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        state.EventNotifications.SignalUsers(usersToSignal);
        return result;
    }

    private static void SettleOfflineTimedOutRaidSnapshots(
        ClashOfRimNetworkState state,
        IReadOnlyList<AuthoritativeEvent> offlineFailedRaids,
        DateTimeOffset nowUtc)
    {
        if (offlineFailedRaids.Count == 0 || state.SnapshotStore is not IColonySnapshotPackageStore packageStore)
        {
            return;
        }

        foreach (AuthoritativeEvent raid in offlineFailedRaids)
        {
            if (raid.Payload is not RaidEventPayload { OpponentKind: RaidOpponentKind.Player } payload
                || payload.Settlement is not null
                || payload.ReturnedSnapshotId is not null)
            {
                continue;
            }

            string attackerUserId = raid.Actor.UserId;
            string attackerColonyId = raid.Actor.ColonyId ?? string.Empty;
            string defenderUserId = raid.Target.UserId;
            string defenderColonyId = raid.Target.ColonyId ?? string.Empty;
            if (string.IsNullOrWhiteSpace(attackerUserId)
                || string.IsNullOrWhiteSpace(attackerColonyId)
                || string.IsNullOrWhiteSpace(defenderUserId)
                || string.IsNullOrWhiteSpace(defenderColonyId))
            {
                continue;
            }

            SaveSnapshotPackage? attackerEvidencePackage = packageStore.GetLatestPackage(attackerUserId, attackerColonyId);
            SaveSnapshotPackage? originalDefensePackage = packageStore.GetLatestPackage(defenderUserId, defenderColonyId);
            if (attackerEvidencePackage is null
                || originalDefensePackage is null
                || !string.Equals(originalDefensePackage.Envelope.Identity.SnapshotId, payload.DefenderSnapshotId, StringComparison.Ordinal))
            {
                state.RuntimeLogger.LogWarning(
                    "Offline raid timeout settlement missing snapshot: raid={RaidEventId} attacker={AttackerUserId}/{AttackerColonyId} defender={DefenderUserId}/{DefenderColonyId} attackerSnapshotFound={AttackerSnapshotFound} defenderSnapshotFound={DefenderSnapshotFound} expectedDefenderSnapshot={ExpectedDefenderSnapshot} actualDefenderSnapshot={ActualDefenderSnapshot}",
                    raid.EventId,
                    attackerUserId,
                    attackerColonyId,
                    defenderUserId,
                    defenderColonyId,
                    attackerEvidencePackage is not null,
                    originalDefensePackage is not null,
                    payload.DefenderSnapshotId,
                    originalDefensePackage?.Envelope.Identity.SnapshotId ?? string.Empty);
                state.Ledger.ReportApplicationResult(
                    raid.EventId,
                    EventApplicationResultKind.NeedsManualReview,
                    T("Raid.OfflineTimeoutSettlementMissingSnapshot"),
                    nextRetryAtUtc: null);
                continue;
            }

            string targetMapId = raid.TargetContext?.MapUniqueId ?? string.Empty;
            string returnedMapId = ResolveRaidSettlementEvidenceMapId(attackerEvidencePackage.Index, targetMapId, raid.EventId);
            RaidSettlementReturnResult settlement = RaidSettlementReturnProcessor.Process(
                new RaidSettlementReturnRequest(
                    raid.EventId,
                    originalDefensePackage.Envelope.Identity,
                    originalDefensePackage,
                    attackerEvidencePackage,
                    targetMapId,
                    LossRatio: state.ServerConfiguration.RaidSettlementLossRatio,
                    PackableBuildingDefNames: state.AdminBaseline.Current?.PackableBuildingDefNames,
                    BuildingMaxHitPointsByDefName: state.AdminBaseline.Current?.EstimatedSettlementMaxHitPointsByDefName,
                    StuffHitPointFactorByDefName: state.AdminBaseline.Current?.StuffHitPointFactorByDefName,
                    StuffHitPointOffsetByDefName: state.AdminBaseline.Current?.StuffHitPointOffsetByDefName,
                    MinimumRemainingHitPointsRatio: state.ServerConfiguration.RaidSettlementMinimumRemainingHitPointsRatio,
                    IgnoredThingDefNames: state.Plugins.ActiveIgnoredRaidSettlementThingDefNames(state.CompatibilityBaseline.Current),
                    ReturnedMapUniqueId: returnedMapId,
                    BuildingHitPointsLossRatio: state.ServerConfiguration.RaidSettlementBuildingHitPointsLossRatio,
                    TrapDefNames: state.AdminBaseline.Current?.ApprovedTrapDefNames));

            if (!settlement.Accepted)
            {
                state.RuntimeLogger.LogWarning(
                    "Offline raid timeout settlement rejected: raid={RaidEventId} attacker={AttackerUserId}/{AttackerColonyId} defender={DefenderUserId}/{DefenderColonyId} kind={SettlementKind} returnedMap={ReturnedMapId}",
                    raid.EventId,
                    attackerUserId,
                    attackerColonyId,
                    defenderUserId,
                    defenderColonyId,
                    settlement.Kind,
                    returnedMapId);
                RaidSettlementLedgerRecorder.Record(
                    state.Ledger,
                    raid.EventId,
                    settlement,
                    nowUtc,
                    defenderOnline: false);
                continue;
            }

            string editedSnapshotId = BuildRaidSettlementSnapshotId(defenderColonyId, raid.EventId, nowUtc);
            SaveSnapshotPackage editedDefensePackage;
            try
            {
                editedDefensePackage = RaidSettlementSnapshotEditor.ApplySettlementLosses(
                    originalDefensePackage,
                    settlement,
                    editedSnapshotId,
                    nowUtc,
                    state.Plugins.ActiveRaidSettlementSnapshotEditorExtensions(state.CompatibilityBaseline.Current));
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or System.Xml.XmlException)
            {
                state.RuntimeLogger.LogWarning(
                    ex,
                    "Offline raid timeout defender snapshot edit failed: raid={RaidEventId} defender={DefenderUserId}/{DefenderColonyId} editedSnapshot={EditedSnapshotId}",
                    raid.EventId,
                    defenderUserId,
                    defenderColonyId,
                    editedSnapshotId);
                state.Ledger.ReportApplicationResult(
                    raid.EventId,
                    EventApplicationResultKind.NeedsManualReview,
                    T("Raid.SettlementSnapshotEditFailed", ("MESSAGE", ex.Message)),
                    nextRetryAtUtc: null);
                continue;
            }

            if (!string.Equals(editedDefensePackage.Envelope.Identity.OwnerId, defenderUserId, StringComparison.Ordinal)
                || !string.Equals(editedDefensePackage.Envelope.Identity.ColonyId, defenderColonyId, StringComparison.Ordinal))
            {
                state.RuntimeLogger.LogWarning(
                    "Offline raid timeout defender snapshot identity mismatch: raid={RaidEventId} expected={ExpectedUserId}/{ExpectedColonyId} actual={ActualUserId}/{ActualColonyId}",
                    raid.EventId,
                    defenderUserId,
                    defenderColonyId,
                    editedDefensePackage.Envelope.Identity.OwnerId,
                    editedDefensePackage.Envelope.Identity.ColonyId);
                state.Ledger.ReportApplicationResult(
                    raid.EventId,
                    EventApplicationResultKind.NeedsManualReview,
                    T("Raid.SettlementSnapshotIdentityMismatch"),
                    nextRetryAtUtc: null);
                continue;
            }

            packageStore.StoreLatest(editedDefensePackage, editedDefensePackage.Index, nowUtc);
            RecordLatestSnapshotReference(
                state,
                defenderUserId,
                defenderColonyId,
                new LatestSnapshotRecord(editedDefensePackage.Envelope.Identity, editedDefensePackage.Envelope, editedDefensePackage.Index, nowUtc),
                nowUtc);

            RaidSettlementLedgerRecordResult record = RaidSettlementLedgerRecorder.Record(
                state.Ledger,
                raid.EventId,
                settlement,
                nowUtc,
                defenderOnline: false);
            if (record.Kind is RaidSettlementLedgerRecordResultKind.SettlementEventCreated
                or RaidSettlementLedgerRecordResultKind.SettlementEventAlreadyExists)
            {
                state.Ledger.ReportApplicationResult(
                    raid.EventId,
                    EventApplicationResultKind.Applied,
                    failureReason: null,
                    nextRetryAtUtc: null);
            }
        }
    }

    private static RaidCleanupEventAppendResult CleanupTimedOutRaidAttackerSnapshots(
        ClashOfRimNetworkState state,
        IReadOnlyList<AuthoritativeEvent> failedRaids,
        DateTimeOffset nowUtc)
    {
        if (failedRaids.Count == 0 || state.SnapshotStore is not IColonySnapshotPackageStore packageStore)
        {
            return RaidCleanupEventAppendResult.Empty;
        }

        var attackerLossEvents = new List<AuthoritativeEvent>();
        var supportLossNotifications = new List<AuthoritativeEvent>();
        foreach (AuthoritativeEvent raid in failedRaids)
        {
            if (raid.Payload is not RaidEventPayload { OpponentKind: RaidOpponentKind.Player } raidPayload)
            {
                continue;
            }

            string attackerUserId = raid.Actor.UserId;
            string attackerColonyId = raid.Actor.ColonyId ?? string.Empty;
            if (string.IsNullOrWhiteSpace(attackerUserId) || string.IsNullOrWhiteSpace(attackerColonyId))
            {
                continue;
            }

            SaveSnapshotPackage? package = packageStore.GetLatestPackage(attackerUserId, attackerColonyId);
            if (package is null)
            {
                state.RuntimeLogger.LogWarning(
                    "Timed out raid attacker cleanup skipped because latest attacker snapshot is missing: raid={RaidEventId} attacker={AttackerUserId}/{AttackerColonyId}",
                    raid.EventId,
                    attackerUserId,
                    attackerColonyId);
                continue;
            }

            string snapshotId = BuildTimedOutRaidCleanupSnapshotId(attackerColonyId, raid.EventId, nowUtc);
            try
            {
                if (!RaidAttackerSnapshotCleanupEditor.TryRemoveRaidBattleState(
                        package,
                        snapshotId,
                        nowUtc,
                        out SaveSnapshotPackage cleaned,
                        out RaidAttackerSnapshotCleanupResult cleanupResult))
                {
                    state.RuntimeLogger.LogWarning(
                        "Timed out raid attacker cleanup found no removable raid battle state: raid={RaidEventId} attacker={AttackerUserId}/{AttackerColonyId} sourceSnapshot={SourceSnapshotId}",
                        raid.EventId,
                        attackerUserId,
                        attackerColonyId,
                        package.Envelope.Identity.SnapshotId);
                    continue;
                }

                packageStore.StoreLatest(cleaned, cleaned.Index, nowUtc);
                RecordLatestSnapshotReference(
                    state,
                    attackerUserId,
                    attackerColonyId,
                    new LatestSnapshotRecord(cleaned.Envelope.Identity, cleaned.Envelope, cleaned.Index, nowUtc),
                    nowUtc);

                AppendRaidCleanupLossEvents(
                    state,
                    raid,
                    raidPayload,
                    cleaned,
                    cleanupResult,
                    nowUtc,
                    attackerLossEvents,
                    supportLossNotifications);
                state.RuntimeLogger.LogInformation(
                    "Timed out raid attacker cleanup stored cleaned snapshot: raid={RaidEventId} attacker={AttackerUserId}/{AttackerColonyId} sourceSnapshot={SourceSnapshotId} cleanedSnapshot={CleanedSnapshotId} removedMaps={RemovedMapCount} lostAttackPawns={LostAttackPawns} lostSupportPawns={LostSupportPawns}",
                    raid.EventId,
                    attackerUserId,
                    attackerColonyId,
                    package.Envelope.Identity.SnapshotId,
                    cleaned.Envelope.Identity.SnapshotId,
                    cleanupResult.RemovedMapCount,
                    cleanupResult.LostAttackPawns.Count,
                    cleanupResult.LostSupportPawns.Count);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or System.Xml.XmlException or InvalidOperationException)
            {
                state.RuntimeLogger.LogWarning(
                    ex,
                    "Timed out raid attacker cleanup failed: raid={RaidEventId} attacker={AttackerUserId}/{AttackerColonyId} sourceSnapshot={SourceSnapshotId}",
                    raid.EventId,
                    attackerUserId,
                    attackerColonyId,
                    package.Envelope.Identity.SnapshotId);
            }
        }

        return new RaidCleanupEventAppendResult(attackerLossEvents, supportLossNotifications);
    }

    private static void AppendRaidCleanupLossEvents(
        ClashOfRimNetworkState state,
        AuthoritativeEvent raid,
        RaidEventPayload raidPayload,
        SaveSnapshotPackage cleanedAttackerPackage,
        RaidAttackerSnapshotCleanupResult cleanupResult,
        DateTimeOffset nowUtc,
        List<AuthoritativeEvent> attackerLossEvents,
        List<AuthoritativeEvent> supportLossNotifications,
        string lossReason = "timeout")
    {
        if (cleanupResult.LostAttackPawns.Count > 0)
        {
            string cleanedSnapshotId = cleanedAttackerPackage.Envelope.Identity.SnapshotId ?? string.Empty;
            RaidAttackerLossRecord loss = new(
                raid.EventId,
                cleanedSnapshotId,
                cleanupResult.LostAttackPawns.Select(pawn => pawn.GlobalKey).ToList(),
                raidPayload.AttackForce?.CarriedThings ?? Array.Empty<EventThingReference>(),
                lossReason);

            AuthoritativeEvent lossEvent = AuthoritativeEventFactory.Create(
                ServerEventType.Raid,
                new EventParty("server"),
                raid.Actor,
                $"raid-attacker-loss:{raid.EventId}:{cleanedSnapshotId}",
                state.OnlinePresence.IsUserOnline(raid.Actor.UserId),
                new RaidEventPayload(
                    raidPayload.DefenderSnapshotId,
                    ReturnedSnapshotId: null,
                    raidPayload.StartedAtUtc,
                    FinishedAtUtc: nowUtc,
                    Settlement: null,
                    AttackForce: raidPayload.AttackForce,
                    AttackerLoss: loss),
                nowUtc,
                raid.TargetContext);

            LedgerAppendResult append = state.Ledger.Append(lossEvent);
            LogEventAppend(state, append, "raid-attacker-map-loss");
            if (append.Created)
            {
                attackerLossEvents.Add(append.Event);
            }
        }

        IReadOnlyList<AuthoritativeEvent> allEvents = state.Ledger.ListAll();
        foreach (RaidBattleLostSupportPawnSummary lostSupport in cleanupResult.LostSupportPawns)
        {
            AuthoritativeEvent? supportEvent = state.Ledger.Find(lostSupport.EventId);
            if (supportEvent?.Payload is not SupportPawnEventPayload payload)
            {
                continue;
            }

            if (supportEvent.Status is ServerEventStatus.Cancelled
                or ServerEventStatus.Failed
                or ServerEventStatus.RejectedByTarget
                or ServerEventStatus.Conflict
                || HasSupportFinishEvent(supportEvent, allEvents))
            {
                continue;
            }

            AuthoritativeEvent notification = CreateSupportPawnLossReturnEvent(
                supportEvent,
                payload,
                lostSupport.PawnName ?? payload.PawnName,
                "RaidSettlementLost",
                state.OnlinePresence.IsUserOnline(supportEvent.Actor.UserId),
                nowUtc);
            LedgerAppendResult append = state.Ledger.Append(notification);
            LogEventAppend(state, append, "raid-support-map-loss");
            state.Ledger.ChangeStatus(supportEvent.EventId, ServerEventStatus.Failed);
            if (append.Created)
            {
                supportLossNotifications.Add(append.Event);
            }
        }
    }

    private static string BuildTimedOutRaidCleanupSnapshotId(string colonyId, string raidEventId, DateTimeOffset nowUtc)
    {
        string safeColony = SanitizeSnapshotIdPart(colonyId);
        string safeRaid = SanitizeSnapshotIdPart(raidEventId);
        if (safeRaid.Length > 24)
        {
            safeRaid = safeRaid[..24];
        }

        return $"{safeColony}-raid-cleanup-{nowUtc:yyyyMMddHHmmss}-{safeRaid}";
    }

    private static bool HasSupportFinishEvent(
        AuthoritativeEvent supportEvent,
        IReadOnlyList<AuthoritativeEvent> allEvents)
    {
        string deathId = $"support-death:{supportEvent.EventId}:RaidSettlementLost";
        string returnId = $"support-finish:{supportEvent.EventId}:RaidSettlementLost";
        string lossId = $"support-loss:{supportEvent.EventId}:RaidSettlementLost";
        return allEvents.Any(evt =>
            string.Equals(evt.IdempotencyKey, returnId, StringComparison.Ordinal)
            || string.Equals(evt.IdempotencyKey, lossId, StringComparison.Ordinal)
            || string.Equals(evt.IdempotencyKey, $"{supportEvent.IdempotencyKey}:rejected-return", StringComparison.Ordinal)
            || (evt.Payload is ServerNotificationEventPayload notification
                && string.Equals(notification.NotificationId, deathId, StringComparison.Ordinal)));
    }

    private delegate void ClientLifecycleHook(
        ClashOfRimNetworkState state,
        ClientLifecycleEvent lifecycleEvent);

    private sealed record PendingConfirmationReconciliationResult(
        BankPendingConfirmationReconciliationResult Bank,
        MercenaryPendingConfirmationReconciliationResult Mercenary)
    {
        public bool Changed => Bank.Changed || Mercenary.Changed;
    }

    private sealed record RaidCleanupEventAppendResult(
        IReadOnlyList<AuthoritativeEvent> AttackerLossEvents,
        IReadOnlyList<AuthoritativeEvent> SupportLossNotifications)
    {
        public static RaidCleanupEventAppendResult Empty { get; } = new(
            Array.Empty<AuthoritativeEvent>(),
            Array.Empty<AuthoritativeEvent>());
    }

    private enum ClientLifecycleEventKind
    {
        InitialWorldSessionPrepared,
        LoggedIn,
        SnapshotUploaded,
        ColonyAbandoned,
        Disconnected
    }

    private readonly record struct ClientLifecycleEvent(
        ClientLifecycleEventKind Kind,
        string UserId,
        string ColonyId,
        string? CurrentSnapshotId,
        string? SessionId,
        DateTimeOffset OccurredAtUtc)
    {
        public static ClientLifecycleEvent InitialWorldSessionPrepared(
            string userId,
            string colonyId,
            string? currentSnapshotId,
            DateTimeOffset occurredAtUtc)
        {
            return new ClientLifecycleEvent(
                ClientLifecycleEventKind.InitialWorldSessionPrepared,
                userId,
                colonyId,
                currentSnapshotId,
                SessionId: null,
                occurredAtUtc);
        }

        public static ClientLifecycleEvent LoggedIn(
            string userId,
            string colonyId,
            string sessionId,
            string? currentSnapshotId,
            DateTimeOffset occurredAtUtc)
        {
            return new ClientLifecycleEvent(
                ClientLifecycleEventKind.LoggedIn,
                userId,
                colonyId,
                currentSnapshotId,
                sessionId,
                occurredAtUtc);
        }

        public static ClientLifecycleEvent SnapshotUploaded(
            string userId,
            string colonyId,
            string? sessionId,
            string currentSnapshotId,
            DateTimeOffset occurredAtUtc)
        {
            return new ClientLifecycleEvent(
                ClientLifecycleEventKind.SnapshotUploaded,
                userId,
                colonyId,
                currentSnapshotId,
                sessionId,
                occurredAtUtc);
        }

        public static ClientLifecycleEvent Disconnected(
            string userId,
            string colonyId,
            string? sessionId,
            DateTimeOffset occurredAtUtc)
        {
            return new ClientLifecycleEvent(
                ClientLifecycleEventKind.Disconnected,
                userId,
                colonyId,
                CurrentSnapshotId: null,
                sessionId,
                occurredAtUtc);
        }

        public static ClientLifecycleEvent ColonyAbandoned(
            string userId,
            string colonyId,
            string? currentSnapshotId,
            DateTimeOffset occurredAtUtc)
        {
            return new ClientLifecycleEvent(
                ClientLifecycleEventKind.ColonyAbandoned,
                userId,
                colonyId,
                currentSnapshotId,
                SessionId: null,
                occurredAtUtc);
        }
    }
}
