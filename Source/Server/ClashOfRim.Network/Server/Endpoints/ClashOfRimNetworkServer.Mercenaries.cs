using AIRsLight.ClashOfRim.Network.Modules.Bank;
using static AIRsLight.ClashOfRim.Network.Modules.Bank.BankLoanTerms;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using Microsoft.AspNetCore.Http;
using System.Globalization;

namespace AIRsLight.ClashOfRim.Network;

public static partial class ClashOfRimNetworkServer
{
    private const int MercenaryRecallShuttleDestroyedFineSilver = 6000;

    // Retained for an explicit upgrade response; do not bind state or recover writes.
    private static IResult HireMercenary() => Results.Ok(new MercenaryHireResponse(
        ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, T("Economy.SnapshotEndpointRequired")),
        contract: null, bankStatus: null));

    private static async Task<IResult> HireMercenaryWithSnapshot(HttpRequest httpRequest, ClashOfRimNetworkState state)
    {
        var multipart = await ReadMultipartSnapshotRequest<HireMercenaryWithSnapshotRequest>(httpRequest);
        if (multipart?.Request is null || multipart.Payload is null)
            return Results.Ok(new MercenaryHireResponse(ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed,
                T("Mercenary.TransactionMissingPayload")), null, null));
        var request = multipart.Request;
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey) || string.IsNullOrWhiteSpace(request.RequestedContractId)
            || string.IsNullOrWhiteSpace(request.ConfirmedSnapshot?.SnapshotId))
            return Results.Ok(new MercenaryHireResponse(ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed,
                T("Mercenary.TransactionMissingFields")), null, null));
        if (!MultipartSnapshotAuthentication.MatchesPrincipal(httpRequest, request.UserId, request.ColonyId))
            return Results.Ok(new MercenaryHireResponse(ProtocolResponse.Reject(ProtocolErrorCode.Unauthorized, T("Auth.Failed")), null, null));
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        BankRequestContext? context = null;
        IResult Failure(ProtocolResponse failure) => Results.Ok(new MercenaryHireResponse(failure, null,
            context is null ? null : state.Bank.BuildBankStatusResponse(context, ProtocolResponse.Ok())));
        using (var recovery = EnterColonySnapshotMutation(httpRequest, state, request.UserId, request.ColonyId,
            request.CurrentSnapshotId, request.AuthToken))
        lock (state.MercenaryContracts.SnapshotCommitGate)
        {
            if (recovery.Rejection is { } denied)
                return Results.Ok(new MercenaryHireResponse(denied, null, null));
            if (!ReauthorizeSnapshotPrincipal(httpRequest, state, new ProtocolIdentity(request.UserId, request.ColonyId, request.CurrentSnapshotId)))
                return Results.Ok(new MercenaryHireResponse(ProtocolResponse.Reject(ProtocolErrorCode.Unauthorized, T("Auth.Failed")), null, null));

            var existing = state.MercenaryContracts.FindByIdempotencyKey(request.IdempotencyKey);
            context = state.BankRequests.ReadBankRequestContext(request.UserId, request.ColonyId, request.CurrentSnapshotId, request.AuthToken,
                request.CurrentGameTicks, 0, nowUtc, out var rejection,
                committedSnapshotId: MatchesMercenarySnapshot(existing, request) ? existing!.SnapshotId : null);
            if (context is null) return Failure(rejection!);
            if (existing is not null) return MercenarySnapshotReplayResponse(state, request, existing, context);
            context = state.BankRequests.ReconcileBankSnapshotContext(context, nowUtc, out rejection);
            if (rejection is not null) return Failure(rejection);
            var validation = ValidateMercenarySnapshotHire(state, request);
            if (validation is not null) return Failure(validation);
        }
        var upload = PrepareTransactionSnapshot(state, request.UserId, request.ColonyId, request.ConfirmedSnapshot!.SnapshotId!,
            request.ConfirmedSnapshot, multipart.Payload, nowUtc);

        using (var recovery = EnterColonySnapshotMutation(httpRequest, state, request.UserId, request.ColonyId,
            request.CurrentSnapshotId, request.AuthToken))
        lock (state.MercenaryContracts.SnapshotCommitGate)
        {
            if (recovery.Rejection is { } denied)
                return Results.Ok(new MercenaryHireResponse(denied, null, null));
            if (!ReauthorizeSnapshotPrincipal(httpRequest, state, new ProtocolIdentity(request.UserId, request.ColonyId, request.CurrentSnapshotId)))
                return Results.Ok(new MercenaryHireResponse(ProtocolResponse.Reject(ProtocolErrorCode.Unauthorized, T("Auth.Failed")), null, null));

            nowUtc = DateTimeOffset.UtcNow;
            var existing = state.MercenaryContracts.FindByIdempotencyKey(request.IdempotencyKey);
            context = state.BankRequests.ReadBankRequestContext(request.UserId, request.ColonyId, request.CurrentSnapshotId, request.AuthToken,
                request.CurrentGameTicks, 0, nowUtc, out var rejection,
                committedSnapshotId: MatchesMercenarySnapshot(existing, request) ? existing!.SnapshotId : null);
            if (context is null) return Failure(rejection!);
            if (existing is not null) return MercenarySnapshotReplayResponse(state, request, existing, context);
            context = state.BankRequests.ReconcileBankSnapshotContext(context, nowUtc, out rejection);
            if (rejection is not null) return Failure(rejection);
            if (!upload.Accepted || upload.AcceptedSnapshot is null) return Failure(ToProtocolResponse(upload));
            var validation = ValidateMercenarySnapshotHire(state, request);
            if (validation is not null) return Failure(validation);
            var package = ProtocolDtoMapper.ToSaveSnapshotPackage(request.ConfirmedSnapshot, multipart.Payload);
            upload = CreateSnapshotReceiver(state).CommitPrepared(new SnapshotUploadContext(request.UserId, request.ColonyId,
                request.ConfirmedSnapshot.SnapshotId!, null, upload.SnapshotUploadKind), package, upload, storeAccepted: false);
            if (!upload.Accepted || upload.AcceptedSnapshot is null) return Failure(ToProtocolResponse(upload));
            string snapshotId = upload.AcceptedSnapshot.Identity.SnapshotId!;
            var contract = new MercenaryContractRecord(request.RequestedContractId, request.IdempotencyKey, request.UserId,
                request.ColonyId, snapshotId, request.SkillDefName, request.SkillLevel, request.DurationDays,
                request.ExpectedPriceSilver, request.ExpectedHarmfulSurgeryFineSilver, request.ExpectedDeathFineSilver,
                context.CurrentGameTicks, checked(context.CurrentGameTicks + request.DurationDays * BankLoanPolicy.GameTicksPerDay),
                nowUtc, MercenaryContractRegistry.StatusActive, nowUtc);
            CommitMercenarySnapshot(state, package, upload, new MercenarySnapshotMutation(Contract: contract,
                MaximumOpenContracts: state.ServerConfiguration.MaxActiveMercenariesPerColony));
            return Results.Ok(new MercenaryHireResponse(ProtocolResponse.Ok(T("Mercenary.Activated")), ToMercenaryContractDto(contract),
                state.Bank.BuildBankStatusResponse(context, ProtocolResponse.Ok()), snapshotId, upload.AcceptedSnapshot.Envelope.NextLineageToken));
        }
    }

    private static IResult QuoteMercenary(QuoteMercenaryRequest request, ClashOfRimNetworkState state)
    {
        using var mutation = state.BankRequests.EnterBankRequestMutation(request.UserId, request.ColonyId, request.CurrentSnapshotId,
            request.AuthToken, request.CurrentGameTicks, 0, out BankRequestContext? context, out ProtocolResponse? rejection, () => ValidateMercenaryRequestShape(request.SkillDefName, request.SkillLevel, request.DurationDays, state.ServerConfiguration));
        if (context is null)
        {
            return Results.Ok(new MercenaryQuoteResponse(
                rejection!,
                request.SkillLevel,
                request.DurationDays,
                priceSilver: 0,
                state.ServerConfiguration.MercenaryHarmfulSurgeryFineSilver,
                CalculateMercenaryDeathFineSilver(request.SkillLevel, state.ServerConfiguration),
                bankStatus: null));
        }

        if (!state.ServerConfiguration.MercenariesEnabled)
        {
            return Results.Ok(new MercenaryQuoteResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, T("Mercenary.Disabled")),
                request.SkillLevel,
                request.DurationDays,
                priceSilver: 0,
                state.ServerConfiguration.MercenaryHarmfulSurgeryFineSilver,
                CalculateMercenaryDeathFineSilver(request.SkillLevel, state.ServerConfiguration),
                state.Bank.BuildBankStatusResponse(context, ProtocolResponse.Ok())));
        }

        ProtocolResponse? validation = ValidateMercenaryRequestShape(
            request.SkillDefName,
            request.SkillLevel,
            request.DurationDays,
            state.ServerConfiguration);
        if (validation is not null)
        {
            return Results.Ok(new MercenaryQuoteResponse(
                validation,
                request.SkillLevel,
                request.DurationDays,
                priceSilver: 0,
                state.ServerConfiguration.MercenaryHarmfulSurgeryFineSilver,
                CalculateMercenaryDeathFineSilver(request.SkillLevel, state.ServerConfiguration),
                state.Bank.BuildBankStatusResponse(context, ProtocolResponse.Ok())));
        }

        ProtocolResponse? limitRejection = ValidateMercenaryActiveLimit(state, request.UserId, request.ColonyId);
        if (limitRejection is not null)
        {
            return Results.Ok(new MercenaryQuoteResponse(
                limitRejection,
                request.SkillLevel,
                request.DurationDays,
                priceSilver: 0,
                state.ServerConfiguration.MercenaryHarmfulSurgeryFineSilver,
                CalculateMercenaryDeathFineSilver(request.SkillLevel, state.ServerConfiguration),
                state.Bank.BuildBankStatusResponse(context, ProtocolResponse.Ok())));
        }

        int priceSilver = CalculateMercenaryPriceSilver(
            request.SkillLevel,
            request.DurationDays,
            state.ServerConfiguration);
        return Results.Ok(new MercenaryQuoteResponse(
            ProtocolResponse.Ok(T("Mercenary.QuoteCreated")),
            request.SkillLevel,
            request.DurationDays,
            priceSilver,
            state.ServerConfiguration.MercenaryHarmfulSurgeryFineSilver,
            CalculateMercenaryDeathFineSilver(request.SkillLevel, state.ServerConfiguration),
            state.Bank.BuildBankStatusResponse(context, ProtocolResponse.Ok())));
    }

    private static IResult QuoteMercenaryGuard(QuoteMercenaryGuardRequest request, ClashOfRimNetworkState state)
    {
        using var mutation = state.BankRequests.EnterBankRequestMutation(request.UserId, request.ColonyId, request.CurrentSnapshotId,
            request.AuthToken, request.CurrentGameTicks, 0, out BankRequestContext? context, out ProtocolResponse? rejection);
        string tier = NormalizeMercenaryGuardTier(request.Tier);
        if (context is null)
        {
            return Results.Ok(new MercenaryGuardQuoteResponse(
                rejection!,
                tier,
                priceSilver: 0,
                pointRatio: 0,
                bankStatus: null));
        }

        if (!state.ServerConfiguration.PvpEnabled)
        {
            return Results.Ok(new MercenaryGuardQuoteResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, T("Mercenary.GuardPvpDisabled")),
                tier,
                priceSilver: 0,
                pointRatio: 0,
                state.Bank.BuildBankStatusResponse(context, ProtocolResponse.Ok())));
        }

        if (!state.ServerConfiguration.MercenariesEnabled || !state.ServerConfiguration.MercenaryGuardsEnabled)
        {
            return Results.Ok(new MercenaryGuardQuoteResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, T("Mercenary.GuardDisabled")),
                tier,
                priceSilver: 0,
                pointRatio: 0,
                state.Bank.BuildBankStatusResponse(context, ProtocolResponse.Ok())));
        }

        if (!IsValidMercenaryGuardTier(tier))
        {
            return Results.Ok(new MercenaryGuardQuoteResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Mercenary.GuardInvalidTier")),
                tier,
                priceSilver: 0,
                pointRatio: 0,
                state.Bank.BuildBankStatusResponse(context, ProtocolResponse.Ok())));
        }

        if (state.MercenaryGuards.HasActiveForColony(request.UserId, request.ColonyId))
        {
            return Results.Ok(new MercenaryGuardQuoteResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.Conflict, T("Mercenary.GuardAlreadyActive")),
                tier,
                priceSilver: 0,
                pointRatio: 0,
                state.Bank.BuildBankStatusResponse(context, ProtocolResponse.Ok())));
        }

        return Results.Ok(new MercenaryGuardQuoteResponse(
            ProtocolResponse.Ok(T("Mercenary.GuardQuoteCreated")),
            tier,
            CalculateMercenaryGuardPriceSilver(tier, state.ServerConfiguration),
            CalculateMercenaryGuardPointRatio(tier, state.ServerConfiguration),
            state.Bank.BuildBankStatusResponse(context, ProtocolResponse.Ok())));
    }

    private static async Task<IResult> HireMercenaryGuardWithSnapshot(HttpRequest httpRequest, ClashOfRimNetworkState state)
    {
        var multipart = await ReadMultipartSnapshotRequest<HireMercenaryGuardWithSnapshotRequest>(httpRequest);
        if (multipart?.Request is null || multipart.Payload is null)
            return Results.Ok(new MercenaryGuardHireResponse(ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed,
                T("Mercenary.GuardTransactionMissingPayload")), null, null));
        var request = multipart.Request;
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey) || string.IsNullOrWhiteSpace(request.RequestedContractId)
            || string.IsNullOrWhiteSpace(request.ConfirmedSnapshot?.SnapshotId))
            return Results.Ok(new MercenaryGuardHireResponse(ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed,
                T("Mercenary.GuardTransactionMissingFields")), null, null));
        if (!MultipartSnapshotAuthentication.MatchesPrincipal(httpRequest, request.UserId, request.ColonyId))
            return Results.Ok(new MercenaryGuardHireResponse(ProtocolResponse.Reject(ProtocolErrorCode.Unauthorized, T("Auth.Failed")), null, null));
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        BankRequestContext? context = null;
        IResult Failure(ProtocolResponse failure) => Results.Ok(new MercenaryGuardHireResponse(failure, null,
            context is null ? null : state.Bank.BuildBankStatusResponse(context, ProtocolResponse.Ok())));
        using (var recovery = EnterColonySnapshotMutation(httpRequest, state, request.UserId, request.ColonyId,
            request.CurrentSnapshotId, request.AuthToken))
        lock (state.MercenaryGuards.SnapshotCommitGate)
        {
            if (recovery.Rejection is { } denied)
                return Results.Ok(new MercenaryGuardHireResponse(denied, null, null));
            if (!ReauthorizeSnapshotPrincipal(httpRequest, state, new ProtocolIdentity(request.UserId, request.ColonyId, request.CurrentSnapshotId)))
                return Results.Ok(new MercenaryGuardHireResponse(ProtocolResponse.Reject(ProtocolErrorCode.Unauthorized, T("Auth.Failed")), null, null));

            var existing = state.MercenaryGuards.FindByIdempotencyKey(request.IdempotencyKey);
            context = state.BankRequests.ReadBankRequestContext(request.UserId, request.ColonyId, request.CurrentSnapshotId, request.AuthToken,
                request.CurrentGameTicks, 0, nowUtc, out var rejection,
                committedSnapshotId: MatchesGuardSnapshot(existing, request) ? existing!.SnapshotId : null);
            if (context is null) return Failure(rejection!);
            if (existing is not null) return GuardSnapshotReplayResponse(state, request, existing, context);
            context = state.BankRequests.ReconcileBankSnapshotContext(context, nowUtc, out rejection);
            if (rejection is not null) return Failure(rejection);
            var validation = ValidateGuardSnapshotHire(state, request);
            if (validation is not null) return Failure(validation);
        }
        var upload = PrepareTransactionSnapshot(state, request.UserId, request.ColonyId, request.ConfirmedSnapshot!.SnapshotId!,
            request.ConfirmedSnapshot, multipart.Payload, nowUtc);

        using (var recovery = EnterColonySnapshotMutation(httpRequest, state, request.UserId, request.ColonyId,
            request.CurrentSnapshotId, request.AuthToken))
        lock (state.MercenaryGuards.SnapshotCommitGate)
        {
            if (recovery.Rejection is { } denied)
                return Results.Ok(new MercenaryGuardHireResponse(denied, null, null));
            if (!ReauthorizeSnapshotPrincipal(httpRequest, state, new ProtocolIdentity(request.UserId, request.ColonyId, request.CurrentSnapshotId)))
                return Results.Ok(new MercenaryGuardHireResponse(ProtocolResponse.Reject(ProtocolErrorCode.Unauthorized, T("Auth.Failed")), null, null));

            nowUtc = DateTimeOffset.UtcNow;
            var existing = state.MercenaryGuards.FindByIdempotencyKey(request.IdempotencyKey);
            context = state.BankRequests.ReadBankRequestContext(request.UserId, request.ColonyId, request.CurrentSnapshotId, request.AuthToken,
                request.CurrentGameTicks, 0, nowUtc, out var rejection,
                committedSnapshotId: MatchesGuardSnapshot(existing, request) ? existing!.SnapshotId : null);
            if (context is null) return Failure(rejection!);
            if (existing is not null) return GuardSnapshotReplayResponse(state, request, existing, context);
            context = state.BankRequests.ReconcileBankSnapshotContext(context, nowUtc, out rejection);
            if (rejection is not null) return Failure(rejection);
            if (!upload.Accepted || upload.AcceptedSnapshot is null) return Failure(ToProtocolResponse(upload));
            var validation = ValidateGuardSnapshotHire(state, request);
            if (validation is not null) return Failure(validation);
            var package = ProtocolDtoMapper.ToSaveSnapshotPackage(request.ConfirmedSnapshot, multipart.Payload);
            upload = CreateSnapshotReceiver(state).CommitPrepared(new SnapshotUploadContext(request.UserId, request.ColonyId,
                request.ConfirmedSnapshot.SnapshotId!, null, upload.SnapshotUploadKind), package, upload, storeAccepted: false);
            if (!upload.Accepted || upload.AcceptedSnapshot is null) return Failure(ToProtocolResponse(upload));
            string snapshotId = upload.AcceptedSnapshot.Identity.SnapshotId!;
            var contract = new MercenaryGuardContractRecord(request.RequestedContractId, request.IdempotencyKey, request.UserId,
                request.ColonyId, snapshotId, NormalizeMercenaryGuardTier(request.Tier), request.ExpectedPriceSilver,
                CalculateMercenaryGuardPointRatio(NormalizeMercenaryGuardTier(request.Tier), state.ServerConfiguration),
                context.CurrentGameTicks, nowUtc, MercenaryGuardContractRegistry.StatusActive, null, null);
            CommitMercenarySnapshot(state, package, upload, new MercenarySnapshotMutation(Guard: contract));
            return Results.Ok(new MercenaryGuardHireResponse(ProtocolResponse.Ok(T("Mercenary.GuardActivated")), ToMercenaryGuardContractDto(contract),
                state.Bank.BuildBankStatusResponse(context, ProtocolResponse.Ok()), snapshotId, upload.AcceptedSnapshot.Envelope.NextLineageToken));
        }
    }

    private static IResult ReportMercenaryIncident(ReportMercenaryIncidentRequest request, ClashOfRimNetworkState state)
    {
        using var mutation = state.BankRequests.EnterBankRequestMutation(request.UserId, request.ColonyId, request.CurrentSnapshotId,
            request.AuthToken, request.CurrentGameTicks, 0, out BankRequestContext? context, out ProtocolResponse? rejection, () => ValidateMercenaryIncidentShape(request));
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        if (context is null)
        {
            return Results.Ok(new MercenaryIncidentResponse(rejection!, debt: null, bankStatus: null));
        }

        if (string.IsNullOrWhiteSpace(request.IdempotencyKey)
            || string.IsNullOrWhiteSpace(request.ContractId))
        {
            return Results.Ok(new MercenaryIncidentResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Mercenary.IncidentMissingFields")),
                debt: null,
                state.Bank.BuildBankStatusResponse(context, ProtocolResponse.Ok())));
        }

        BankDebtRecord? existingDebt = state.BankLoans.FindDebtByIdempotencyKey(request.IdempotencyKey);
        if (existingDebt is not null)
        {
            if (existingDebt.IdempotencyKey != request.IdempotencyKey || existingDebt.UserId != request.UserId
                || existingDebt.ColonyId != request.ColonyId || existingDebt.SourceId != request.ContractId
                || existingDebt.SourceKind != BankLoanRegistry.DebtSourceFine || existingDebt.Reason != "Mercenary:" + request.IncidentKind)
                return Results.Ok(new MercenaryIncidentResponse(ProtocolResponse.Reject(ProtocolErrorCode.Conflict,
                    T("Mercenary.IncidentMissingFields")), null, null));
            return Results.Ok(new MercenaryIncidentResponse(
                new ProtocolResponse(true, ProtocolErrorCode.DuplicateRequest, T("Mercenary.DuplicateIncident")),
                ToBankDebtSummary(existingDebt), state.Bank.BuildBankStatusResponse(context, ProtocolResponse.Ok())));
        }
        if (state.BankLoans.ListUnfinalizedMercenaryIncidents().Any(debt => debt.UserId == request.UserId
                && debt.ColonyId == request.ColonyId && debt.SourceId == request.ContractId
                && debt.Status == BankLoanRegistry.StatusPendingActivation && MercenaryIncidentRecovery.ClosesContract(debt.Reason)))
            return Results.Ok(new MercenaryIncidentResponse(ProtocolResponse.Reject(ProtocolErrorCode.Conflict,
                T("Mercenary.ContractLedgerFailed")), null, state.Bank.BuildBankStatusResponse(context, ProtocolResponse.Ok())));

        if (string.Equals(request.IncidentKind, "Completed", StringComparison.Ordinal))
        {
            MercenaryContractRecord? completionContract = state.MercenaryContracts.Find(request.ContractId);
            if (completionContract is null
                || !string.Equals(completionContract.UserId, request.UserId, StringComparison.Ordinal)
                || !string.Equals(completionContract.ColonyId, request.ColonyId, StringComparison.Ordinal))
            {
                return Results.Ok(new MercenaryIncidentResponse(
                    ProtocolResponse.Reject(ProtocolErrorCode.EventNotFound, T("Mercenary.ContractNotFound")),
                    debt: null,
                    state.Bank.BuildBankStatusResponse(context, ProtocolResponse.Ok())));
            }

            if (!string.Equals(completionContract.Status, MercenaryContractRegistry.StatusCompleted, StringComparison.Ordinal))
            {
                if (!string.Equals(completionContract.Status, MercenaryContractRegistry.StatusActive, StringComparison.Ordinal))
                {
                    return Results.Ok(new MercenaryIncidentResponse(
                        ProtocolResponse.Reject(ProtocolErrorCode.EventNotFound, T("Mercenary.ContractNotFound")),
                        debt: null,
                        state.Bank.BuildBankStatusResponse(context, ProtocolResponse.Ok())));
                }

                state.MercenaryContracts.Complete(completionContract.ContractId, request.UserId, request.ColonyId);
            }

            return Results.Ok(new MercenaryIncidentResponse(
                ProtocolResponse.Ok(T("Mercenary.Completed")),
                debt: null,
                state.Bank.BuildBankStatusResponse(context, ProtocolResponse.Ok())));
        }

        MercenaryContractRecord? contract = state.MercenaryContracts.FindActive(request.ContractId);
        if (contract is null
            || !string.Equals(contract.UserId, request.UserId, StringComparison.Ordinal)
            || !string.Equals(contract.ColonyId, request.ColonyId, StringComparison.Ordinal))
        {
            return Results.Ok(new MercenaryIncidentResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.EventNotFound, T("Mercenary.ContractNotFound")),
                debt: null,
                state.Bank.BuildBankStatusResponse(context, ProtocolResponse.Ok())));
        }

        int fine = request.IncidentKind switch
        {
            "Death" => contract.DeathFineSilver,
            "HostileBehavior" => contract.HarmfulSurgeryFineSilver,
            "HarmfulSurgery" => contract.HarmfulSurgeryFineSilver,
            "OvertimeService" => CalculateMercenaryOvertimeFineSilver(contract),
            "ShuttleDestroyed" => MercenaryRecallShuttleDestroyedFineSilver,
            _ => -1
        };
        if (fine < 0)
        {
            return Results.Ok(new MercenaryIncidentResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Mercenary.InvalidIncidentKind")),
                debt: null,
                state.Bank.BuildBankStatusResponse(context, ProtocolResponse.Ok())));
        }

        BankDebtRecord debt = state.BankLoans.CreateDebt(
            request.IdempotencyKey,
            request.UserId,
            request.ColonyId,
            request.CurrentSnapshotId,
            fine,
            BankLoanRegistry.DebtSourceFine,
            "Mercenary:" + request.IncidentKind,
            contract.ContractId,
            context.CurrentGameTicks,
            nowUtc,
            pendingActivation: true);
        // The client's next confirmed snapshot makes this fine authoritative. Until then,
        // cancellation must leave the contract open and must not publish a fine notification.

        return Results.Ok(new MercenaryIncidentResponse(
            ProtocolResponse.Ok(T("Mercenary.FineCreated")),
            ToBankDebtSummary(debt),
            state.Bank.BuildBankStatusResponse(context, ProtocolResponse.Ok())));
    }

    private static AuthoritativeEvent CreateBankFineNotification(
        ClashOfRimNetworkState state,
        BankDebtRecord debt)
    {
        string notificationId = "bank-fine-created:" + debt.DebtId;
        return AuthoritativeEventFactory.CreateForLedger(state.Ledger,
            ServerEventType.ServerNotification,
            new EventParty("server"),
            new EventParty(debt.UserId, debt.ColonyId),
            notificationId,
            state.OnlinePresence.IsUserOnline(debt.UserId),
            new ServerNotificationEventPayload(
                notificationId,
                T("Bank.FineCreatedTitle"),
                T(
                    "Bank.FineCreatedMessage",
                    ("AMOUNT", debt.AmountSilver.ToString(CultureInfo.InvariantCulture))),
                ServerNotificationSeverity.Warning,
                FromAdministrator: false,
                RelatedUserId: debt.UserId,
                RelatedColonyId: debt.ColonyId),
            debt.CreatedAtUtc);
    }

    private static ChatMessageDto ToChatMessageDto(ChatMessageRecord message)
    {
        return new ChatMessageDto(
            message.Sequence,
            message.MessageId,
            message.Channel,
            message.FromUserId,
            message.FromColonyId,
            message.TargetUserId,
            message.Text,
            message.SentAtUtc);
    }

    private static MercenaryContractDto ToMercenaryContractDto(MercenaryContractRecord contract)
    {
        return new MercenaryContractDto(
            contract.ContractId,
            contract.SkillDefName,
            contract.SkillLevel,
            contract.DurationDays,
            contract.PriceSilver,
            contract.HarmfulSurgeryFineSilver,
            contract.DeathFineSilver,
            contract.CreatedAtGameTicks,
            contract.ExpiresAtGameTicks);
    }

    private static MercenaryGuardContractDto ToMercenaryGuardContractDto(MercenaryGuardContractRecord contract)
    {
        return new MercenaryGuardContractDto(
            contract.ContractId,
            contract.Tier,
            contract.PriceSilver,
            contract.PointRatio,
            contract.CreatedAtGameTicks);
    }

    private static bool IsAllowedMercenarySkill(string? skillDefName)
    {
        return skillDefName is
            "Construction" or
            "Plants" or
            "Intellectual" or
            "Mining" or
            "Shooting" or
            "Melee" or
            "Social" or
            "Animals" or
            "Cooking" or
            "Medicine" or
            "Artistic" or
            "Crafting" or
            "Hauling";
    }

    private static ProtocolResponse? ValidateMercenaryRequestShape(
        string? skillDefName,
        int skillLevel,
        int durationDays,
        ClashOfRimServerConfiguration configuration)
    {
        if (!IsAllowedMercenarySkill(skillDefName))
        {
            return ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Mercenary.InvalidSkill"));
        }

        if (skillLevel is not (7 or 14 or 20))
        {
            return ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Mercenary.InvalidTier"));
        }

        if (durationDays < configuration.MercenaryMinDurationDays
            || durationDays > configuration.MercenaryMaxDurationDays)
        {
            return ProtocolResponse.Reject(
                ProtocolErrorCode.ValidationFailed,
                T(
                    "Mercenary.InvalidDuration",
                    ("MIN", configuration.MercenaryMinDurationDays.ToString(CultureInfo.InvariantCulture)),
                    ("MAX", configuration.MercenaryMaxDurationDays.ToString(CultureInfo.InvariantCulture))));
        }

        return null;
    }

    private static int CalculateMercenaryDeathFineSilver(int skillLevel, ClashOfRimServerConfiguration configuration)
    {
        return skillLevel switch
        {
            7 => configuration.MercenaryApprenticeDeathFineSilver,
            14 => configuration.MercenarySkilledDeathFineSilver,
            20 => configuration.MercenaryMasterDeathFineSilver,
            _ => 0
        };
    }

    private static int CalculateMercenaryOvertimeFineSilver(MercenaryContractRecord contract)
    {
        int durationDays = Math.Max(1, contract.DurationDays);
        return Math.Max(1, (int)Math.Ceiling(contract.PriceSilver / (double)durationDays * 3d));
    }

    private static string NormalizeMercenaryGuardTier(string? tier)
    {
        if (string.Equals(tier, "Skilled", StringComparison.OrdinalIgnoreCase))
        {
            return "Skilled";
        }

        if (string.Equals(tier, "Master", StringComparison.OrdinalIgnoreCase))
        {
            return "Master";
        }

        return "Apprentice";
    }

    private static bool IsValidMercenaryGuardTier(string? tier)
    {
        return tier is "Apprentice" or "Skilled" or "Master";
    }

    private static int CalculateMercenaryGuardPriceSilver(
        string tier,
        ClashOfRimServerConfiguration configuration)
    {
        return Math.Max(0, tier switch
        {
            "Skilled" => configuration.MercenaryGuardSkilledSilver,
            "Master" => configuration.MercenaryGuardMasterSilver,
            _ => configuration.MercenaryGuardApprenticeSilver
        });
    }

    private static float CalculateMercenaryGuardPointRatio(
        string tier,
        ClashOfRimServerConfiguration configuration)
    {
        return Math.Max(0f, tier switch
        {
            "Skilled" => configuration.MercenaryGuardSkilledPointsRatio,
            "Master" => configuration.MercenaryGuardMasterPointsRatio,
            _ => configuration.MercenaryGuardApprenticePointsRatio
        });
    }

    private static ProtocolResponse? ValidateMercenaryActiveLimit(
        ClashOfRimNetworkState state,
        string userId,
        string colonyId)
    {
        int limit = state.ServerConfiguration.MaxActiveMercenariesPerColony;
        if (limit <= 0)
        {
            return ProtocolResponse.Reject(
                ProtocolErrorCode.ServerRejected,
                T("Mercenary.ActiveLimitReached", ("MAX", "0")));
        }

        int active = state.MercenaryContracts.CountOpenForColony(userId, colonyId);
        return active >= limit
            ? ProtocolResponse.Reject(
                ProtocolErrorCode.ServerRejected,
                T("Mercenary.ActiveLimitReached", ("MAX", limit.ToString(CultureInfo.InvariantCulture))))
            : null;
    }

    private static int CalculateMercenaryPriceSilver(
        int skillLevel,
        int durationDays,
        ClashOfRimServerConfiguration configuration)
    {
        int daily = skillLevel switch
        {
            7 => configuration.MercenaryApprenticeDailySilver,
            14 => configuration.MercenarySkilledDailySilver,
            20 => configuration.MercenaryMasterDailySilver,
            _ => 0
        };
        double price = Math.Max(0, daily)
            * Math.Max(0, durationDays)
            * InterpolateBankInterestDurationMultiplier(
                durationDays,
                configuration.MercenaryDurationMultiplierCurve);
        return (int)Math.Ceiling(price);
    }

    private static ProtocolResponse? ValidateMercenaryIncidentShape(ReportMercenaryIncidentRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey) || string.IsNullOrWhiteSpace(request.ContractId))
            return ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Mercenary.IncidentMissingFields"));
        return request.IncidentKind is "Completed" or "Death" or "HostileBehavior" or "HarmfulSurgery" or "OvertimeService" or "ShuttleDestroyed"
            ? null : ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Mercenary.InvalidIncidentKind"));
    }
}
