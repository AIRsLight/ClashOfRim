using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Network.Modules.Bank;
using static AIRsLight.ClashOfRim.Network.Modules.Bank.BankLoanTerms;
using static AIRsLight.ClashOfRim.Network.ServerText;
using static AIRsLight.ClashOfRim.Network.Modules.Mercenaries.MercenaryRules;

namespace AIRsLight.ClashOfRim.Network.Modules.Mercenaries;

internal sealed class MercenaryService(MercenaryContractRegistry contracts, MercenaryGuardContractRegistry guards,
    BankLoanRegistry loans, BankRequestAccess access, BankService bank, Func<ClashOfRimServerConfiguration> configuration)
{
    internal MercenaryQuoteResponse QuoteMercenary(QuoteMercenaryRequest request)
    {
        using var mutation = access.EnterBankRequestMutation(request.UserId, request.ColonyId, request.CurrentSnapshotId,
            request.AuthToken, request.CurrentGameTicks, 0, out var context, out var rejection,
            () => ValidateMercenaryRequestShape(request.SkillDefName, request.SkillLevel, request.DurationDays, configuration()));
        var current = configuration();
        MercenaryQuoteResponse Reply(ProtocolResponse result, int price = 0) => new(result, request.SkillLevel,
            request.DurationDays, price, current.MercenaryHarmfulSurgeryFineSilver,
            CalculateMercenaryDeathFineSilver(request.SkillLevel, current),
            context is null ? null : bank.BuildBankStatusResponse(context, ProtocolResponse.Ok()));
        if (context is null) return Reply(rejection!);
        rejection = ValidateMercenaryAvailability(contracts, request.UserId, request.ColonyId,
            request.SkillDefName, request.SkillLevel, request.DurationDays, current);
        return rejection is not null ? Reply(rejection)
            : Reply(ProtocolResponse.Ok(Format("Mercenary.QuoteCreated")),
                CalculateMercenaryPriceSilver(request.SkillLevel, request.DurationDays, current));
    }

    internal MercenaryGuardQuoteResponse QuoteMercenaryGuard(QuoteMercenaryGuardRequest request)
    {
        using var mutation = access.EnterBankRequestMutation(request.UserId, request.ColonyId, request.CurrentSnapshotId,
            request.AuthToken, request.CurrentGameTicks, 0, out var context, out var rejection);
        string tier = NormalizeMercenaryGuardTier(request.Tier);
        var current = configuration();
        if (context is not null)
            rejection = ValidateGuardAvailability(guards, request.UserId, request.ColonyId, current);
        return new MercenaryGuardQuoteResponse(rejection ?? ProtocolResponse.Ok(Format("Mercenary.GuardQuoteCreated")), tier,
            rejection is not null ? 0 : CalculateMercenaryGuardPriceSilver(tier, current),
            rejection is not null ? 0 : CalculateMercenaryGuardPointRatio(tier, current),
            context is null ? null : bank.BuildBankStatusResponse(context, ProtocolResponse.Ok()));
    }

    internal MercenaryIncidentResponse ReportMercenaryIncident(ReportMercenaryIncidentRequest request)
    {
        using var mutation = access.EnterBankRequestMutation(request.UserId, request.ColonyId, request.CurrentSnapshotId,
            request.AuthToken, request.CurrentGameTicks, 0, out BankRequestContext? context, out ProtocolResponse? rejection, () => ValidateMercenaryIncidentShape(request));
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        if (context is null)
        {
            return new MercenaryIncidentResponse(rejection!, debt: null, bankStatus: null);
        }

        BankDebtRecord? existingDebt = loans.FindDebtByIdempotencyKey(request.IdempotencyKey);
        if (existingDebt is not null)
        {
            if (existingDebt.IdempotencyKey != request.IdempotencyKey || existingDebt.UserId != request.UserId
                || existingDebt.ColonyId != request.ColonyId || existingDebt.SourceId != request.ContractId
                || existingDebt.SourceKind != BankLoanRegistry.DebtSourceFine || existingDebt.Reason != "Mercenary:" + request.IncidentKind)
                return new MercenaryIncidentResponse(ProtocolResponse.Reject(ProtocolErrorCode.Conflict,
                    Format("Mercenary.IncidentMissingFields")), null, null);
            return new MercenaryIncidentResponse(
                new ProtocolResponse(true, ProtocolErrorCode.DuplicateRequest, Format("Mercenary.DuplicateIncident")),
                ToBankDebtSummary(existingDebt), bank.BuildBankStatusResponse(context, ProtocolResponse.Ok()));
        }
        if (loans.ListUnfinalizedMercenaryIncidents().Any(debt => debt.UserId == request.UserId
                && debt.ColonyId == request.ColonyId && debt.SourceId == request.ContractId
                && debt.Status == BankLoanRegistry.StatusPendingActivation && MercenaryIncidentRecovery.ClosesContract(debt.Reason)))
            return new MercenaryIncidentResponse(ProtocolResponse.Reject(ProtocolErrorCode.Conflict,
                Format("Mercenary.ContractLedgerFailed")), null, bank.BuildBankStatusResponse(context, ProtocolResponse.Ok()));

        if (string.Equals(request.IncidentKind, "Completed", StringComparison.Ordinal))
        {
            MercenaryContractRecord? completionContract = contracts.Find(request.ContractId);
            if (completionContract is null
                || !string.Equals(completionContract.UserId, request.UserId, StringComparison.Ordinal)
                || !string.Equals(completionContract.ColonyId, request.ColonyId, StringComparison.Ordinal))
            {
                return new MercenaryIncidentResponse(
                    ProtocolResponse.Reject(ProtocolErrorCode.EventNotFound, Format("Mercenary.ContractNotFound")),
                    debt: null,
                    bank.BuildBankStatusResponse(context, ProtocolResponse.Ok()));
            }

            if (!string.Equals(completionContract.Status, MercenaryContractRegistry.StatusCompleted, StringComparison.Ordinal))
            {
                if (!string.Equals(completionContract.Status, MercenaryContractRegistry.StatusActive, StringComparison.Ordinal))
                {
                    return new MercenaryIncidentResponse(
                        ProtocolResponse.Reject(ProtocolErrorCode.EventNotFound, Format("Mercenary.ContractNotFound")),
                        debt: null,
                        bank.BuildBankStatusResponse(context, ProtocolResponse.Ok()));
                }

                contracts.Complete(completionContract.ContractId, request.UserId, request.ColonyId);
            }

            return new MercenaryIncidentResponse(
                ProtocolResponse.Ok(Format("Mercenary.Completed")),
                debt: null,
                bank.BuildBankStatusResponse(context, ProtocolResponse.Ok()));
        }

        MercenaryContractRecord? contract = contracts.FindActive(request.ContractId);
        if (contract is null
            || !string.Equals(contract.UserId, request.UserId, StringComparison.Ordinal)
            || !string.Equals(contract.ColonyId, request.ColonyId, StringComparison.Ordinal))
        {
            return new MercenaryIncidentResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.EventNotFound, Format("Mercenary.ContractNotFound")),
                debt: null,
                bank.BuildBankStatusResponse(context, ProtocolResponse.Ok()));
        }

        int fine = request.IncidentKind switch
        {
            "Death" => contract.DeathFineSilver,
            "HostileBehavior" => contract.HarmfulSurgeryFineSilver,
            "HarmfulSurgery" => contract.HarmfulSurgeryFineSilver,
            "OvertimeService" => CalculateMercenaryOvertimeFineSilver(contract),
            "ShuttleDestroyed" => RecallShuttleDestroyedFineSilver,
            _ => -1
        };
        if (fine < 0)
        {
            return new MercenaryIncidentResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, Format("Mercenary.InvalidIncidentKind")),
                debt: null,
                bank.BuildBankStatusResponse(context, ProtocolResponse.Ok()));
        }

        BankDebtRecord debt = loans.CreateDebt(
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

        return new MercenaryIncidentResponse(
            ProtocolResponse.Ok(Format("Mercenary.FineCreated")),
            ToBankDebtSummary(debt),
            bank.BuildBankStatusResponse(context, ProtocolResponse.Ok()));
    }
}
