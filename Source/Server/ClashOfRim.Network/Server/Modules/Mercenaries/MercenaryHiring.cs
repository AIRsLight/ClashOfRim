using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using AIRsLight.ClashOfRim.Network.Modules.Bank;
using static AIRsLight.ClashOfRim.Network.Modules.Bank.BankLoanTerms;
using static AIRsLight.ClashOfRim.Network.ServerText;
using AIRsLight.ClashOfRim.Network.Modules.Authorization;
using AIRsLight.ClashOfRim.Network.Modules.Snapshots;
using static AIRsLight.ClashOfRim.Network.Modules.Snapshots.SnapshotTransactions;
using static AIRsLight.ClashOfRim.Network.Modules.Mercenaries.MercenaryRules;

namespace AIRsLight.ClashOfRim.Network.Modules.Mercenaries;

internal sealed class MercenaryHiring(MercenaryContractRegistry contracts, MercenaryGuardContractRegistry guards,
    BankRequestAccess access, BankService bank, SnapshotRequestAuthorization requests, SnapshotTransactions transactions,
    MercenarySnapshotCommitter commits, Func<ClashOfRimServerConfiguration> configuration)
{
    internal MercenaryHireResponse HireMercenaryWithSnapshot(HireMercenaryWithSnapshotRequest request, byte[] payload, string? principalToken)
    {
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey) || string.IsNullOrWhiteSpace(request.RequestedContractId)
            || string.IsNullOrWhiteSpace(request.ConfirmedSnapshot?.SnapshotId))
            return new MercenaryHireResponse(ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed,
                Format("Mercenary.TransactionMissingFields")), null, null);
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        BankRequestContext? context = null;
        MercenaryHireResponse Failure(ProtocolResponse failure) => new MercenaryHireResponse(failure, null,
            context is null ? null : bank.BuildBankStatusResponse(context, ProtocolResponse.Ok()));
        // Both phases run under the mutation scope and this registry's commit gate.
        MercenaryHireResponse? CheckReplay()
        {
            if (requests.AuthorizePrincipal(principalToken, new ProtocolIdentity(request.UserId, request.ColonyId, request.CurrentSnapshotId)) is { } principalFailure)
                return new MercenaryHireResponse(principalFailure, null, null);

            var existing = contracts.FindByIdempotencyKey(request.IdempotencyKey);
            context = access.ReadBankRequestContext(request.UserId, request.ColonyId, request.CurrentSnapshotId, request.AuthToken,
                request.CurrentGameTicks, 0, nowUtc, out var rejection,
                committedSnapshotId: MatchesMercenarySnapshot(existing, request) ? existing!.SnapshotId : null);
            if (context is null) return Failure(rejection!);
            if (existing is not null) return MercenarySnapshotReplayResponse(request, existing, context);
            context = access.ReconcileBankSnapshotContext(context, nowUtc, out rejection);
            if (rejection is not null) return Failure(rejection);
            return null;
        }

        using (var recovery = requests.EnterColony(request.UserId, request.ColonyId, request.CurrentSnapshotId, request.AuthToken, principalToken))
        lock (contracts.SnapshotCommitGate)
        {
            if (recovery.Rejection is { } denied)
                return new MercenaryHireResponse(denied, null, null);
            if (CheckReplay() is { } reply) return reply;
            var validation = ValidateMercenarySnapshotHire(request);
            if (validation is not null) return Failure(validation);
        }
        var upload = transactions.PrepareTransactionSnapshot(request.UserId, request.ColonyId, request.ConfirmedSnapshot!.SnapshotId!,
            request.ConfirmedSnapshot, payload, nowUtc);

        using (var recovery = requests.EnterColony(request.UserId, request.ColonyId, request.CurrentSnapshotId, request.AuthToken, principalToken))
        lock (contracts.SnapshotCommitGate)
        {
            if (recovery.Rejection is { } denied)
                return new MercenaryHireResponse(denied, null, null);
            nowUtc = DateTimeOffset.UtcNow;
            if (CheckReplay() is { } reply) return reply;
            if (!upload.Accepted || upload.AcceptedSnapshot is null) return Failure(ToProtocolResponse(upload));
            var validation = ValidateMercenarySnapshotHire(request);
            if (validation is not null) return Failure(validation);
            var package = ProtocolDtoMapper.ToSaveSnapshotPackage(request.ConfirmedSnapshot, payload);
            upload = transactions.CreateSnapshotReceiver().CommitPrepared(new SnapshotUploadContext(request.UserId, request.ColonyId,
                request.ConfirmedSnapshot.SnapshotId!, null, upload.SnapshotUploadKind), package, upload, storeAccepted: false);
            if (!upload.Accepted || upload.AcceptedSnapshot is null) return Failure(ToProtocolResponse(upload));
            string snapshotId = upload.AcceptedSnapshot.Identity.SnapshotId!;
            var contract = new MercenaryContractRecord(request.RequestedContractId, request.IdempotencyKey, request.UserId,
                request.ColonyId, snapshotId, request.SkillDefName, request.SkillLevel, request.DurationDays,
                request.ExpectedPriceSilver, request.ExpectedHarmfulSurgeryFineSilver, request.ExpectedDeathFineSilver,
                context!.CurrentGameTicks, checked(context.CurrentGameTicks + request.DurationDays * BankLoanPolicy.GameTicksPerDay),
                nowUtc, MercenaryContractRegistry.StatusActive, nowUtc);
            commits.Commit(package, upload, new MercenarySnapshotMutation(Contract: contract,
                MaximumOpenContracts: configuration().MaxActiveMercenariesPerColony));
            return new MercenaryHireResponse(ProtocolResponse.Ok(Format("Mercenary.Activated")), ToMercenaryContractDto(contract),
                bank.BuildBankStatusResponse(context, ProtocolResponse.Ok()), snapshotId, upload.AcceptedSnapshot.Envelope.NextLineageToken);
        }
    }

    internal MercenaryGuardHireResponse HireMercenaryGuardWithSnapshot(HireMercenaryGuardWithSnapshotRequest request, byte[] payload, string? principalToken)
    {
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey) || string.IsNullOrWhiteSpace(request.RequestedContractId)
            || string.IsNullOrWhiteSpace(request.ConfirmedSnapshot?.SnapshotId))
            return new MercenaryGuardHireResponse(ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed,
                Format("Mercenary.GuardTransactionMissingFields")), null, null);
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        BankRequestContext? context = null;
        MercenaryGuardHireResponse Failure(ProtocolResponse failure) => new MercenaryGuardHireResponse(failure, null,
            context is null ? null : bank.BuildBankStatusResponse(context, ProtocolResponse.Ok()));
        // Both phases run under the mutation scope and this registry's commit gate.
        MercenaryGuardHireResponse? CheckReplay()
        {
            if (requests.AuthorizePrincipal(principalToken, new ProtocolIdentity(request.UserId, request.ColonyId, request.CurrentSnapshotId)) is { } principalFailure)
                return new MercenaryGuardHireResponse(principalFailure, null, null);

            var existing = guards.FindByIdempotencyKey(request.IdempotencyKey);
            context = access.ReadBankRequestContext(request.UserId, request.ColonyId, request.CurrentSnapshotId, request.AuthToken,
                request.CurrentGameTicks, 0, nowUtc, out var rejection,
                committedSnapshotId: MatchesGuardSnapshot(existing, request) ? existing!.SnapshotId : null);
            if (context is null) return Failure(rejection!);
            if (existing is not null) return GuardSnapshotReplayResponse(request, existing, context);
            context = access.ReconcileBankSnapshotContext(context, nowUtc, out rejection);
            if (rejection is not null) return Failure(rejection);
            return null;
        }

        using (var recovery = requests.EnterColony(request.UserId, request.ColonyId, request.CurrentSnapshotId, request.AuthToken, principalToken))
        lock (guards.SnapshotCommitGate)
        {
            if (recovery.Rejection is { } denied)
                return new MercenaryGuardHireResponse(denied, null, null);
            if (CheckReplay() is { } reply) return reply;
            var validation = ValidateGuardSnapshotHire(request);
            if (validation is not null) return Failure(validation);
        }
        var upload = transactions.PrepareTransactionSnapshot(request.UserId, request.ColonyId, request.ConfirmedSnapshot!.SnapshotId!,
            request.ConfirmedSnapshot, payload, nowUtc);

        using (var recovery = requests.EnterColony(request.UserId, request.ColonyId, request.CurrentSnapshotId, request.AuthToken, principalToken))
        lock (guards.SnapshotCommitGate)
        {
            if (recovery.Rejection is { } denied)
                return new MercenaryGuardHireResponse(denied, null, null);
            nowUtc = DateTimeOffset.UtcNow;
            if (CheckReplay() is { } reply) return reply;
            if (!upload.Accepted || upload.AcceptedSnapshot is null) return Failure(ToProtocolResponse(upload));
            var validation = ValidateGuardSnapshotHire(request);
            if (validation is not null) return Failure(validation);
            var package = ProtocolDtoMapper.ToSaveSnapshotPackage(request.ConfirmedSnapshot, payload);
            upload = transactions.CreateSnapshotReceiver().CommitPrepared(new SnapshotUploadContext(request.UserId, request.ColonyId,
                request.ConfirmedSnapshot.SnapshotId!, null, upload.SnapshotUploadKind), package, upload, storeAccepted: false);
            if (!upload.Accepted || upload.AcceptedSnapshot is null) return Failure(ToProtocolResponse(upload));
            string snapshotId = upload.AcceptedSnapshot.Identity.SnapshotId!;
            var contract = new MercenaryGuardContractRecord(request.RequestedContractId, request.IdempotencyKey, request.UserId,
                request.ColonyId, snapshotId, NormalizeMercenaryGuardTier(request.Tier), request.ExpectedPriceSilver,
                CalculateMercenaryGuardPointRatio(NormalizeMercenaryGuardTier(request.Tier), configuration()),
                context!.CurrentGameTicks, nowUtc, MercenaryGuardContractRegistry.StatusActive, null, null);
            commits.Commit(package, upload, new MercenarySnapshotMutation(Guard: contract));
            return new MercenaryGuardHireResponse(ProtocolResponse.Ok(Format("Mercenary.GuardActivated")), ToMercenaryGuardContractDto(contract),
                bank.BuildBankStatusResponse(context, ProtocolResponse.Ok()), snapshotId, upload.AcceptedSnapshot.Envelope.NextLineageToken);
        }
    }

    private MercenaryHireResponse MercenarySnapshotReplayResponse(HireMercenaryWithSnapshotRequest request,
        MercenaryContractRecord contract, BankRequestContext context)
    {
        if (!MatchesMercenarySnapshot(contract, request)) return new MercenaryHireResponse(
            ProtocolResponse.Reject(ProtocolErrorCode.Conflict, Format("Mercenary.ContractLedgerFailed")), null, null);
        return new MercenaryHireResponse(
            new ProtocolResponse(true, ProtocolErrorCode.DuplicateRequest, Format("Mercenary.DuplicateTransaction")),
            ToMercenaryContractDto(contract), bank.BuildBankStatusResponse(context, ProtocolResponse.Ok()), contract.SnapshotId,
            transactions.FindSnapshotNextLineageToken(request.UserId, request.ColonyId, contract.SnapshotId));
    }

    private MercenaryGuardHireResponse GuardSnapshotReplayResponse(HireMercenaryGuardWithSnapshotRequest request,
        MercenaryGuardContractRecord contract, BankRequestContext context)
    {
        if (!MatchesGuardSnapshot(contract, request)) return new MercenaryGuardHireResponse(
            ProtocolResponse.Reject(ProtocolErrorCode.Conflict, Format("Mercenary.GuardContractLedgerFailed")), null, null);
        return new MercenaryGuardHireResponse(
            new ProtocolResponse(true, ProtocolErrorCode.DuplicateRequest, Format("Mercenary.GuardDuplicateTransaction")),
            ToMercenaryGuardContractDto(contract), bank.BuildBankStatusResponse(context, ProtocolResponse.Ok()), contract.SnapshotId,
            transactions.FindSnapshotNextLineageToken(request.UserId, request.ColonyId, contract.SnapshotId));
    }

    private ProtocolResponse? ValidateMercenarySnapshotHire(HireMercenaryWithSnapshotRequest request)
    {
        var current = configuration();
        var available = ValidateMercenaryAvailability(contracts, request.UserId, request.ColonyId,
            request.SkillDefName, request.SkillLevel, request.DurationDays, current);
        if (available is not null) return available;
        if (contracts.Find(request.RequestedContractId) is not null)
            return ProtocolResponse.Reject(ProtocolErrorCode.Conflict, Format("Mercenary.ContractLedgerFailed"));
        if (request.ExpectedPriceSilver != CalculateMercenaryPriceSilver(request.SkillLevel, request.DurationDays, current)
            || request.ExpectedHarmfulSurgeryFineSilver != current.MercenaryHarmfulSurgeryFineSilver
            || request.ExpectedDeathFineSilver != CalculateMercenaryDeathFineSilver(request.SkillLevel, current))
            return ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, Format("Mercenary.QuoteMismatch"));
        return null;
    }

    private ProtocolResponse? ValidateGuardSnapshotHire(HireMercenaryGuardWithSnapshotRequest request)
    {
        var current = configuration();
        var available = ValidateGuardAvailability(guards, request.UserId, request.ColonyId, current);
        if (available is not null) return available;
        string tier = NormalizeMercenaryGuardTier(request.Tier);
        if (guards.ContainsContractId(request.RequestedContractId))
            return ProtocolResponse.Reject(ProtocolErrorCode.Conflict, Format("Mercenary.GuardContractLedgerFailed"));
        if (request.ExpectedPriceSilver != CalculateMercenaryGuardPriceSilver(tier, current)
            || !float.IsFinite(request.ExpectedPointRatio)
            || Math.Abs(request.ExpectedPointRatio - CalculateMercenaryGuardPointRatio(tier, current)) > 0.0001f)
            return ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, Format("Mercenary.GuardQuoteMismatch"));
        return null;
    }
}
