using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network.Plugins;
using AIRsLight.ClashOfRim.Network.Plugins.CoreCompatibility;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System.Globalization;

namespace AIRsLight.ClashOfRim.Network;

public static partial class ClashOfRimNetworkServer
{
    private static IResult CreateRaid() => Results.Ok(new EventCreationResponse(
        ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, T("Raid.SnapshotEndpointRequired")),
        eventId: null, ProtocolDeliverySemantics.ServerNotification));

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
            || request.PawnGlobalKeys is null || request.CarriedThings is null
            || transaction.ConfirmedSnapshot is null
            || string.IsNullOrWhiteSpace(transaction.ConfirmedSnapshot.SnapshotId))
        {
            return Results.Ok(new EventCreationResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, ServerLocalization.Text("Raid.TransactionMissingFields")),
                eventId: null,
                ProtocolDeliverySemantics.OnlineImmediate));
        }

        if (!MultipartSnapshotAuthentication.MatchesPrincipal(
                httpRequest,
                request.Attacker.UserId,
                request.Attacker.ColonyId))
        {
            return Results.Ok(new EventCreationResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.Unauthorized, T("Auth.Failed")),
                eventId: null,
                ProtocolDeliverySemantics.OnlineImmediate));
        }

        return CommitRaidWithSnapshot(httpRequest, state, transaction, multipart.Payload);
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
                defenderPackage)));
    }

    private static RaidGuardDeploymentDto? BuildRaidGuardDeployment(
        ClashOfRimNetworkState state,
        string defenderUserId,
        string defenderColonyId,
        string raidEventId,
        int defenderWealth,
        SaveSnapshotPackage defenderSnapshot)
    {
        MercenaryGuardContractRecord? guard = state.MercenaryGuards.FindActiveForColony(defenderUserId, defenderColonyId);
        if (guard is null)
        {
            return null;
        }

        int basePoints = CoreRaidDifficultyServerCompatibility.EstimateDefaultThreatPointsForSnapshot(
            defenderSnapshot,
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

        ProtocolResponse? Authorize()
        {
            if (state.SnapshotAuthorization.Authorize(request.AuthToken, request.Actor.UserId, request.Actor.ColonyId!) is { } failure)
                return failure;
            if (FindEventByIdempotencyKey(state.Ledger, request.IdempotencyKey) is not { } existing)
                return state.SnapshotAuthorization.RequireCurrent(request.Actor.UserId, request.Actor.ColonyId!, request.Actor.SnapshotId);
            // A key is global to the ledger; authenticate and bind a replay before recovery.
            return MatchesEndpoint(existing.Actor, request.Actor.UserId, request.Actor.ColonyId)
                && MatchesEndpoint(existing.Target, request.Target.UserId, request.Target.ColonyId)
                && existing.Type == ResolveDiplomacyEventType(request.Kind)
                && (!IsSupportRequestKind(request.Kind) || existing.Payload is ServerNotificationEventPayload notice
                    && notice.NotificationId == "support-request:" + request.IdempotencyKey)
                ? null : ProtocolResponse.Reject(ProtocolErrorCode.Conflict, T("Diplomacy.NotFoundOrInvisible"));
        }
        using var mutation = EnterRecoveredSnapshotMutation(state, Authorize);
        if (mutation.Rejection is { } denied)
            return Results.Ok(new DiplomacyEventResponse(denied, null, null, null));

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
        AuthoritativeEvent diplomacyEvent = AuthoritativeEventFactory.CreateForLedger(state.Ledger,
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

        ProtocolResponse? Authorize()
        {
            if (state.SnapshotAuthorization.Authorize(request.AuthToken, request.UserId, request.ColonyId) is { } failure)
                return failure;
            var existing = state.Ledger.Find(request.EventId);
            if (existing is null || !IsDiplomacyEventType(existing.Type)
                || !IsVisibleParty(existing.Target, request.UserId, request.ColonyId))
                return ProtocolResponse.Reject(ProtocolErrorCode.EventNotFound, T("Diplomacy.NotFoundOrInvisible"));
            return existing.Status is ServerEventStatus.AppliedToSnapshot or ServerEventStatus.RejectedByTarget ? null
                : state.SnapshotAuthorization.RequireCurrent(request.UserId, request.ColonyId, request.CurrentSnapshotId);
        }
        using var mutation = EnterRecoveredSnapshotMutation(state, Authorize);
        if (mutation.Rejection is { } denied)
            return Results.Ok(new DiplomacyEventResponse(denied, request.EventId, null, null));

        AuthoritativeEvent diplomacyEvent = state.Ledger.Find(request.EventId)!;

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
        notification = AuthoritativeEventFactory.ResolvePersistedIdentity(state.Ledger, notification);
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

    private static IResult CreateSupportPawn() => Results.Ok(new EventCreationResponse(
        ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, T("Support.SnapshotEndpointRequired")),
        eventId: null, ProtocolDeliverySemantics.ServerNotification));

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

        if (!MultipartSnapshotAuthentication.MatchesPrincipal(
                httpRequest,
                request.Actor.UserId,
                request.Actor.ColonyId))
        {
            return Results.Ok(new EventCreationResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.Unauthorized, T("Auth.Failed")),
                eventId: null,
                ProtocolDeliverySemantics.OnlineImmediate));
        }

        return CommitSupportWithSnapshot(httpRequest, state, multipart.Request, multipart.Payload);
    }

    private static IResult FinishSupportPawn(FinishSupportPawnRequest request, ClashOfRimNetworkState state)
    {
        using (var recovery = EnterColonySnapshotMutation(state, request.AuthToken, request.UserId, request.ColonyId))
        {
            if (recovery.Rejection is { } failure)
                return Results.Ok(new FinishSupportPawnResponse(
                    failure, request.EventId, null, null, false));
            return FinishSupportPawnLocked(request, state);
        }
    }

    private static IResult FinishSupportPawnLocked(FinishSupportPawnRequest request, ClashOfRimNetworkState state)
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

        if (payload.ReturnToSender || request.PawnGlobalKey != payload.PawnGlobalKey)
            return Results.Ok(new FinishSupportPawnResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Support.ReferenceMismatch")), request.EventId, null, null, false));

        if (FindSupportClosure(state.Ledger, supportEvent) is { } existing)
        {
            CommitSupportClosure(state, supportEvent, existing);
            return Results.Ok(new FinishSupportPawnResponse(
                new ProtocolResponse(true, ProtocolErrorCode.DuplicateRequest, T("Support.AlreadyEnded")),
                supportEvent.EventId, existing.Event.EventId, null, false));
        }
        if (supportEvent.Status is ServerEventStatus.Cancelled or ServerEventStatus.Failed or ServerEventStatus.RejectedByTarget)
            return Results.Ok(new FinishSupportPawnResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.Conflict, T("Support.AlreadyEnded")), supportEvent.EventId, null, null, false));
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
            if (pawnPackage.Reference.GlobalId != payload.PawnGlobalKey)
                return Results.Ok(new FinishSupportPawnResponse(
                    ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Support.PackageReferenceMismatch")),
                    request.EventId, null, null, false));

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
        bool created = CommitSupportClosure(state, supportEvent,
            new SupportClosure(finishEvent, request.PawnDead ? SupportClosureKind.Loss : SupportClosureKind.Return));
        return Results.Ok(new FinishSupportPawnResponse(
            ProtocolResponse.Ok(request.PawnDead ? T("Support.Dead") : T("Support.Finished")),
            supportEvent.EventId, finishEvent.EventId, null, created));
    }
    private static IResult RejectSupportPawn(RejectSupportPawnRequest request, ClashOfRimNetworkState state)
    {
        using (var recovery = EnterColonySnapshotMutation(state, request.AuthToken, request.UserId, request.ColonyId))
        {
            if (recovery.Rejection is { } failure)
                return Results.Ok(new RejectSupportPawnResponse(
                    failure, request.EventId, null, false));
            return RejectSupportPawnLocked(request, state);
        }
    }

    private static IResult RejectSupportPawnLocked(RejectSupportPawnRequest request, ClashOfRimNetworkState state)
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

        if (FindSupportClosure(state.Ledger, supportEvent) is { } existing)
        {
            if (existing.Kind != SupportClosureKind.Rejection)
                return Results.Ok(new RejectSupportPawnResponse(
                    ProtocolResponse.Reject(ProtocolErrorCode.Conflict, T("Support.AlreadyEnded")), request.EventId, null, false));
            CommitSupportClosure(state, supportEvent, existing);
            return Results.Ok(new RejectSupportPawnResponse(
                new ProtocolResponse(true, ProtocolErrorCode.DuplicateRequest, T("Support.RejectedDuplicate")),
                supportEvent.EventId, existing.Event.EventId, false));
        }
        if (supportEvent.Status is ServerEventStatus.AppliedToSnapshot or ServerEventStatus.Cancelled or ServerEventStatus.Failed)
            return Results.Ok(new RejectSupportPawnResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.Conflict, T("Support.AlreadyAppliedCannotReject")), request.EventId, null, false));

        // A legacy MarkRejected may have committed without its return event. Keep its original decision.
        DateTimeOffset nowUtc = supportEvent.Status == ServerEventStatus.RejectedByTarget
            ? supportEvent.DecisionAtUtc ?? throw new InvalidOperationException("Rejected support has no original decision time.")
            : DateTimeOffset.UtcNow;
        string? reason = supportEvent.Status == ServerEventStatus.RejectedByTarget ? supportEvent.DecisionReason : request.Reason;
        AuthoritativeEvent rejected = new LedgerRejectionChange(supportEvent, nowUtc, reason).Updated;
        AuthoritativeEvent returnEvent = CreateRejectedSupportPawnReturnEvent(rejected, supportPayload, reason,
            state.OnlinePresence.IsUserOnline(supportEvent.Actor.UserId), nowUtc);
        bool created = CommitSupportClosure(state, supportEvent, new SupportClosure(returnEvent, SupportClosureKind.Rejection));
        return Results.Ok(new RejectSupportPawnResponse(ProtocolResponse.Ok(T("Support.Rejected")), supportEvent.EventId, returnEvent.EventId, created));
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
                RelatedEventType: diplomacyEvent.Type,
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
        // Abandonment reconciles and closes the colony in one recovered boundary below.
        if (lifecycleEvent.Kind == ClientLifecycleEventKind.ColonyAbandoned
            || string.IsNullOrWhiteSpace(lifecycleEvent.UserId)
            || string.IsNullOrWhiteSpace(lifecycleEvent.ColonyId))
        {
            return;
        }

        ReconcilePendingConfirmationsForColony(
            state,
            lifecycleEvent.UserId,
            lifecycleEvent.ColonyId,
            lifecycleEvent.OccurredAtUtc,
            forceCancel: lifecycleEvent.Kind is ClientLifecycleEventKind.LoggedIn
                or ClientLifecycleEventKind.InitialWorldSessionPrepared);
    }

    private static PendingConfirmationReconciliationResult ReconcilePendingConfirmationsForColony(
        ClashOfRimNetworkState state,
        string userId,
        string colonyId,
        DateTimeOffset nowUtc,
        bool forceCancel)
    {
        using var mutation = EnterRecoveredSnapshotMutation(state);
        return ReconcilePendingConfirmationsLocked(state, userId, colonyId, nowUtc, forceCancel);
    }

    private static PendingConfirmationReconciliationResult ReconcilePendingConfirmationsLocked(
        ClashOfRimNetworkState state, string userId, string colonyId, DateTimeOffset nowUtc, bool forceCancel)
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
        rejection = PendingConfirmationRejection(result);
        return rejection is not null;
    }

    private static ProtocolResponse? PendingConfirmationRejection(PendingConfirmationReconciliationResult result) =>
        !result.Changed ? null : ProtocolResponse.Reject(ProtocolErrorCode.Conflict, T("PendingConfirmation.ExpiredCancelled"));

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

        using var mutation = EnterRecoveredSnapshotMutation(state);
        ReconcilePendingConfirmationsLocked(state, lifecycleEvent.UserId, lifecycleEvent.ColonyId,
            lifecycleEvent.OccurredAtUtc, forceCancel: false);
        ReconcileAbandonedColonyLocked(state, lifecycleEvent);
    }

    private static void ReconcileAbandonedColonyLocked(
        ClashOfRimNetworkState state, ClientLifecycleEvent lifecycleEvent)
    {
        string userId = lifecycleEvent.UserId;
        string colonyId = lifecycleEvent.ColonyId;

        state.RaidPreparations.RemoveForColony(userId, colonyId);
        state.RaidProtectionActivations.RemoveForColony(userId, colonyId);
        state.MercenaryContracts.CloseForColony(userId, colonyId);

        LedgerBatch batch = BuildAbandonedColonyEventClosure(state.Ledger, userId, colonyId, out HashSet<string> usersToSignal);
        if (batch.StatusChanges.Count > 0) state.Ledger.ApplyBatch(batch);
        if (usersToSignal.Count > 0) state.EventNotifications.SignalUsers(usersToSignal);
    }

    // Build the complete graph before changing any status. An order and its
    // acceptance memos must not be split by a persistence failure.
    private static LedgerBatch BuildAbandonedColonyEventClosure(
        IAuthoritativeEventLedger ledger, string userId, string colonyId, out HashSet<string> usersToSignal)
    {
        var users = new HashSet<string>(StringComparer.Ordinal);
        var changes = new Dictionary<string, LedgerStatusChange>(StringComparer.Ordinal);
        void Close(AuthoritativeEvent item, ServerEventStatus status)
        {
            changes[item.EventId] = new LedgerStatusChange(item.EventId, item.Status, status);
            AddSignalUser(users, item.Actor.UserId);
            AddSignalUser(users, item.Target.UserId);
        }

        foreach (AuthoritativeEvent item in ledger.ListAll())
        {
            if (IsTerminalForAbandonedColonyCleanup(item.Status) || IsColonyTombstoneEvent(item, userId, colonyId)) continue;
            bool actorMatches = IsVisibleParty(item.Actor, userId, colonyId);
            if (!actorMatches && !IsVisibleParty(item.Target, userId, colonyId)) continue;

            if (item.Type == ServerEventType.Trade && actorMatches
                && item.Payload is TradeEventPayload { Stage: TradeStage.MarketOrder })
            {
                foreach (AuthoritativeEvent memo in FindActiveTradeAcceptanceMemos(ledger, item.EventId))
                    Close(memo, ServerEventStatus.Cancelled);
            }
            Close(item, item.Type == ServerEventType.Raid && actorMatches ? ServerEventStatus.Failed : ServerEventStatus.Cancelled);
        }
        usersToSignal = users;
        return new LedgerBatch(Array.Empty<AuthoritativeEvent>(), changes.Values.ToArray());
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
        RaidTimeoutProcessingResult result;
        using (EnterRecoveredSnapshotMutation(state))
        {
            result = ProcessExpiredRaidEventsLocked(state, nowUtc);
        }
        return CompletePendingRaidTimeoutWork(state, result, nowUtc);
    }

    private static RaidTimeoutProcessingResult ProcessExpiredRaidEventsLocked(ClashOfRimNetworkState state, DateTimeOffset nowUtc)
        => RaidTimeoutProcessor.ProcessExpiredRaids(state.Ledger, state.Ledger.ListByType(ServerEventType.Raid), nowUtc,
            BuildRaidDefenseLockPolicy(state.ServerConfiguration), party => state.OnlinePresence.IsUserOnline(party.UserId));

    internal static void RecoverPendingRaidTimeoutWork(ClashOfRimNetworkState state, DateTimeOffset nowUtc)
        => CompletePendingRaidTimeoutWork(state, new RaidTimeoutProcessingResult([], [], [], []), nowUtc);

    private static RaidTimeoutProcessingResult CompletePendingRaidTimeoutWork(ClashOfRimNetworkState state,
        RaidTimeoutProcessingResult result, DateTimeOffset nowUtc)
    {
        IReadOnlyList<AuthoritativeEvent> pendingTimeouts;
        using (EnterRecoveredSnapshotMutation(state))
        {
            pendingTimeouts = RaidTimeoutProcessor.PendingWork(state.Ledger);
        }
        if (pendingTimeouts.Count == 0) return result;
        var offlinePending = pendingTimeouts.Where(raid => raid.Payload is RaidEventPayload { TimeoutWork: { AttackerWasOffline: true } }).ToArray();
        // Retain offline evidence and its durable job before permitting attacker cleanup; preparation owns no outer lock.
        var queuedOfflineSettlements = SettleOfflineTimedOutRaidSnapshots(state,
            offlinePending.Where(raid => raid.Status != ServerEventStatus.AppliedToSnapshot).ToArray(), nowUtc);
        var offlineRaidIds = offlinePending.Select(raid => raid.EventId).ToHashSet(StringComparer.Ordinal);
        var cleanupEligibleRaids = pendingTimeouts.Where(raid => !offlineRaidIds.Contains(raid.EventId)
            || queuedOfflineSettlements.Contains(raid.EventId) || raid.Status == ServerEventStatus.AppliedToSnapshot).ToArray();

        var cleanupEvents = CleanupTimedOutRaidAttackerSnapshots(state, cleanupEligibleRaids, nowUtc);
        var usersToSignal = result.NotificationEvents.Select(evt => evt.Target.UserId)
            .Concat(result.AttackerLossEvents.Select(evt => evt.Target.UserId))
            .Concat(cleanupEvents.AttackerLossEvents.Select(evt => evt.Target.UserId))
            .Concat(cleanupEvents.SupportLossNotifications.Select(evt => evt.Target.UserId))
            .Where(userId => !string.IsNullOrWhiteSpace(userId)).Distinct(StringComparer.Ordinal).ToArray();
        state.EventNotifications.SignalUsers(usersToSignal);
        // A stale completion cannot acknowledge changed timeout work or a changed source.
        using (EnterRecoveredSnapshotMutation(state))
        {
            lock (state.SnapshotPostUploadJobs.SnapshotCommitGate)
            {
                foreach (var completed in cleanupEvents.CompletedRaids)
                {
                    var current = state.Ledger.Find(completed.Raid.EventId);
                    if (!SameRaidCleanupSource(current, completed.Raid) || !CanFinishOfflineTimeoutCleanup(state, current!)
                        || !SnapshotCommitService.SameVersion(state.SnapshotStore.GetLatest(current!.Actor.UserId, current.Actor.ColonyId!)?.Envelope,
                            completed.Snapshot)) continue;
                    state.EventNotifications.SignalUsers(new[] { current!.Actor.UserId, current.Target.UserId });
                    RaidTimeoutProcessor.CompleteWork(state.Ledger, current.EventId);
                }
            }
        }
        return result;
    }

    private static bool SameRaidCleanupSource(AuthoritativeEvent? current, AuthoritativeEvent expected)
        => current is not null && current.Status == expected.Status && RaidSettlementSnapshotMutation.SameSource(current, expected);

    private static bool MatchesRaidSettlementJob(AuthoritativeEvent raid, SnapshotPostUploadJobRecord job)
    {
        var scheduled = RaidSettlementDeferredPayload.Deserialize(job.PayloadJson);
        return job.ProcessorId == RaidSettlementDeferredScheduler.ProcessorId
            && job.Kind == SnapshotPostUploadKind.RaidSettlementEvidence
            && job.UserId == raid.Actor.UserId && job.ColonyId == raid.Actor.ColonyId
            && scheduled.RaidEventId == raid.EventId && scheduled.AttackerUserId == raid.Actor.UserId
            && scheduled.AttackerColonyId == raid.Actor.ColonyId && scheduled.DefenderUserId == raid.Target.UserId
            && scheduled.DefenderColonyId == raid.Target.ColonyId && job.SnapshotId == scheduled.EvidenceSnapshotId
            && raid.Payload is RaidEventPayload payload && payload.DefenderSnapshotId == scheduled.DefenderSnapshotId;
    }

    private static bool CanFinishOfflineTimeoutCleanup(ClashOfRimNetworkState state, AuthoritativeEvent raid)
        => raid.Status == ServerEventStatus.AppliedToSnapshot
            || raid.Payload is not RaidEventPayload { TimeoutWork: { AttackerWasOffline: true } }
            || state.SnapshotPostUploadJobs.Find("raid-settlement:" + raid.EventId) is { State: SnapshotPostUploadJobState.Ready } job
                && MatchesRaidSettlementJob(raid, job);

    private static IReadOnlySet<string> SettleOfflineTimedOutRaidSnapshots(
        ClashOfRimNetworkState state,
        IReadOnlyList<AuthoritativeEvent> offlineFailedRaids,
        DateTimeOffset nowUtc)
    {
        var queuedRaidIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raid in offlineFailedRaids)
        {
            try
            {
                var job = ScheduleOfflineTimedOutRaid(state, raid, nowUtc);
                if (job is null) continue;
                queuedRaidIds.Add(raid.EventId);
                state.RuntimeLogger.LogInformation("Queued offline raid timeout settlement: raid={RaidEventId} job={JobId} evidence={EvidenceSnapshotId}",
                    raid.EventId, job.JobId, job.SnapshotId);
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException)
            {
                state.RuntimeLogger.LogWarning(ex, "Offline raid timeout settlement could not be queued: raid={RaidEventId}", raid.EventId);
            }
        }
        return queuedRaidIds;
    }

    private static RaidCleanupEventAppendResult CleanupTimedOutRaidAttackerSnapshots(
        ClashOfRimNetworkState state,
        IReadOnlyList<AuthoritativeEvent> failedRaids,
        DateTimeOffset nowUtc)
    {
        if (failedRaids.Count == 0 || state.SnapshotStore is not IColonySnapshotPackageStore packageStore)
            return RaidCleanupEventAppendResult.Empty;
        var attackerLossEvents = new List<AuthoritativeEvent>();
        var supportLossNotifications = new List<AuthoritativeEvent>();
        var completedRaids = new List<RaidCleanupCompletion>();
        foreach (var raid in failedRaids)
        {
            if (raid.Payload is not RaidEventPayload { OpponentKind: RaidOpponentKind.Player }
                || string.IsNullOrWhiteSpace(raid.Actor.UserId) || string.IsNullOrWhiteSpace(raid.Actor.ColonyId)) continue;
            string owner = raid.Actor.UserId, colony = raid.Actor.ColonyId;
            var package = SnapshotPreparationService.Run(() => packageStore.GetLatestPackage(owner, colony));
            if (package is null)
            {
                state.RuntimeLogger.LogWarning("Timed out raid cleanup skipped missing snapshot: raid={RaidEventId} attacker={AttackerUserId}/{AttackerColonyId}",
                    raid.EventId, owner, colony);
                continue;
            }
            using (EnterRecoveredSnapshotMutation(state))
            {
                var current = state.Ledger.Find(raid.EventId);
                if (!SameRaidCleanupSource(current, raid) || !CanFinishOfflineTimeoutCleanup(state, current!)
                    || !SnapshotCommitService.SameVersion(packageStore.GetLatest(owner, colony)?.Envelope, package.Envelope)) continue;
                if (!package.Index.WorldObjects.Any(item => item.Def == "ClashOfRim_RemoteRaidBattleMapParent"))
                {
                    // Publication may already have completed before its acknowledgement failed.
                    completedRaids.Add(new RaidCleanupCompletion(current!, package.Envelope));
                    continue;
                }
                if (!CanCleanupRaidBattleSnapshot(state, current!, package)) continue;
            }

            (bool Changed, SaveSnapshotPackage Package, RaidAttackerSnapshotCleanupResult Result, PreparedSnapshotPayload? Storage) prepared;
            try
            {
                prepared = SnapshotPreparationService.Run(() =>
                {
                    bool changed = RaidAttackerSnapshotCleanupEditor.TryRemoveRaidBattleState(package,
                        BuildTimedOutRaidCleanupSnapshotId(colony, raid.EventId, nowUtc), nowUtc,
                        out var cleaned, out RaidAttackerSnapshotCleanupResult cleanup);
                    var storage = changed ? (packageStore as IPreparedColonySnapshotPackageStore)?.PreparePayload(cleaned.Payload) : null;
                    return (changed, cleaned, cleanup, storage);
                });
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or System.Xml.XmlException or InvalidOperationException)
            {
                state.RuntimeLogger.LogWarning(ex, "Timed out raid cleanup preparation failed: raid={RaidEventId} attacker={AttackerUserId}/{AttackerColonyId}",
                    raid.EventId, owner, colony);
                continue;
            }
            if (!prepared.Changed)
            {
                state.RuntimeLogger.LogWarning("Timed out raid cleanup found no removable battle: raid={RaidEventId} sourceSnapshot={SourceSnapshotId}",
                    raid.EventId, package.Envelope.Identity.SnapshotId);
                continue;
            }
            using (EnterRecoveredSnapshotMutation(state))
            {
                lock (state.SnapshotPostUploadJobs.SnapshotCommitGate)
                {
                    var current = state.Ledger.Find(raid.EventId);
                    if (!SameRaidCleanupSource(current, raid) || !CanFinishOfflineTimeoutCleanup(state, current!) || current!.Payload is not RaidEventPayload payload
                        || !SnapshotCommitService.SameVersion(packageStore.GetLatest(owner, colony)?.Envelope, package.Envelope)
                        || !CanCleanupRaidBattleSnapshot(state, current, package)) continue;
                    var mutation = BuildRaidCleanupMutation(state, current, payload, package, prepared.Package, prepared.Result, nowUtc);
                    state.SnapshotCommits.CommitRaidCleanup(packageStore, state.BankLoans, state.Ledger, state.Players,
                        prepared.Package, mutation, nowUtc, user => state.EventNotifications.SignalUser(user), prepared.Storage);
                    attackerLossEvents.AddRange(mutation.Batch.Events.Where(item => item.Type == ServerEventType.Raid));
                    supportLossNotifications.AddRange(mutation.Batch.Events.Where(item => item.Type == ServerEventType.SupportPawn));
                    completedRaids.Add(new RaidCleanupCompletion(current, prepared.Package.Envelope));
                }
            }
            state.RuntimeLogger.LogInformation(
                "Timed out raid cleanup stored snapshot: raid={RaidEventId} attacker={AttackerUserId}/{AttackerColonyId} sourceSnapshot={SourceSnapshotId} cleanedSnapshot={CleanedSnapshotId}",
                raid.EventId, owner, colony, package.Envelope.Identity.SnapshotId, prepared.Package.Envelope.Identity.SnapshotId);
        }
        return new RaidCleanupEventAppendResult(attackerLossEvents, supportLossNotifications, completedRaids);
    }

    private static RaidCleanupSnapshotMutation BuildRaidCleanupMutation(
        ClashOfRimNetworkState state,
        AuthoritativeEvent raid,
        RaidEventPayload raidPayload,
        SaveSnapshotPackage originalAttackerPackage,
        SaveSnapshotPackage cleanedAttackerPackage,
        RaidAttackerSnapshotCleanupResult cleanupResult,
        DateTimeOffset nowUtc,
        string lossReason = "timeout")
    {
        var events = new List<AuthoritativeEvent>();
        var supportSources = new List<AuthoritativeEvent>();
        var statuses = new List<LedgerStatusChange>();
        if (cleanupResult.LostAttackPawns.Count > 0)
        {
            string cleanedSnapshotId = cleanedAttackerPackage.Envelope.Identity.SnapshotId ?? string.Empty;
            RaidAttackerLossRecord loss = new(
                raid.EventId,
                cleanedSnapshotId,
                cleanupResult.LostAttackPawns.Select(pawn => pawn.GlobalKey).ToList(),
                raidPayload.AttackForce?.CarriedThings ?? Array.Empty<EventThingReference>(),
                lossReason);

            AuthoritativeEvent lossEvent = AuthoritativeEventFactory.CreateForLedger(state.Ledger,
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

            events.Add(lossEvent);
        }

        IReadOnlyList<AuthoritativeEvent> allEvents = state.Ledger.ListAll();
        foreach (RaidBattleLostSupportPawnSummary lostSupport in cleanupResult.LostSupportPawns.DistinctBy(pawn => pawn.EventId))
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
            events.Add(notification);
            supportSources.Add(supportEvent);
            statuses.Add(new LedgerStatusChange(supportEvent.EventId, supportEvent.Status, ServerEventStatus.Failed));
        }
        return new RaidCleanupSnapshotMutation(raid, originalAttackerPackage.Envelope, new LedgerBatch(events, statuses), supportSources);
    }

    private static bool CanCleanupRaidBattleSnapshot(ClashOfRimNetworkState state, AuthoritativeEvent raid, SaveSnapshotPackage package)
    {
        if (raid.Actor.UserId != package.Envelope.Identity.OwnerId || raid.Actor.ColonyId != package.Envelope.Identity.ColonyId
            || FindActiveSourceRaidForAttacker(state, raid.Actor.UserId, raid.Actor.ColonyId) is not null) return false;
        var battles = package.Index.WorldObjects.Where(item => item.Def == "ClashOfRim_RemoteRaidBattleMapParent").ToArray();
        return battles.Length > 0 && battles.All(item => item.ClashOfRimRelatedEventId == raid.EventId);
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
        return allEvents.Any(evt =>
            IsSupportClosureCandidate(supportEvent, evt)
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

    private sealed record RaidCleanupCompletion(AuthoritativeEvent Raid, SaveSnapshotEnvelope Snapshot);

    private sealed record RaidCleanupEventAppendResult(
        IReadOnlyList<AuthoritativeEvent> AttackerLossEvents,
        IReadOnlyList<AuthoritativeEvent> SupportLossNotifications,
        IReadOnlyList<RaidCleanupCompletion> CompletedRaids)
    {
        public static RaidCleanupEventAppendResult Empty { get; } = new(
            Array.Empty<AuthoritativeEvent>(),
            Array.Empty<AuthoritativeEvent>(),
            Array.Empty<RaidCleanupCompletion>());
    }

    private enum ClientLifecycleEventKind
    {
        InitialWorldSessionPrepared,
        LoggedIn,
        ColonyAbandoned
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
