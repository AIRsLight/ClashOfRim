using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using Microsoft.AspNetCore.Http;

namespace AIRsLight.ClashOfRim.Network;

public static partial class ClashOfRimNetworkServer
{
    private static bool MatchesMercenarySnapshot(MercenaryContractRecord? contract, HireMercenaryWithSnapshotRequest request) =>
        contract is not null && contract.Status is MercenaryContractRegistry.StatusActive or MercenaryContractRegistry.StatusCompleted
        && contract.ActivatedAtUtc is not null && contract.UserId == request.UserId && contract.ColonyId == request.ColonyId
        && contract.SnapshotId == request.ConfirmedSnapshot?.SnapshotId && contract.ContractId == request.RequestedContractId
        && contract.SkillDefName == request.SkillDefName && contract.SkillLevel == request.SkillLevel && contract.DurationDays == request.DurationDays
        && contract.PriceSilver == request.ExpectedPriceSilver && contract.HarmfulSurgeryFineSilver == request.ExpectedHarmfulSurgeryFineSilver
        && contract.DeathFineSilver == request.ExpectedDeathFineSilver;

    private static bool MatchesGuardSnapshot(MercenaryGuardContractRecord? contract, HireMercenaryGuardWithSnapshotRequest request) =>
        contract is not null && contract.Status is MercenaryGuardContractRegistry.StatusActive or MercenaryGuardContractRegistry.StatusConsumed
        && contract.UserId == request.UserId && contract.ColonyId == request.ColonyId
        && contract.SnapshotId == request.ConfirmedSnapshot?.SnapshotId && contract.ContractId == request.RequestedContractId
        && contract.Tier == NormalizeMercenaryGuardTier(request.Tier) && contract.PriceSilver == request.ExpectedPriceSilver
        && float.IsFinite(request.ExpectedPointRatio) && Math.Abs(contract.PointRatio - request.ExpectedPointRatio) <= 0.0001f;

    private static IResult MercenarySnapshotReplayResponse(ClashOfRimNetworkState state, HireMercenaryWithSnapshotRequest request,
        MercenaryContractRecord contract, BankRequestContext context)
    {
        if (!MatchesMercenarySnapshot(contract, request)) return Results.Ok(new MercenaryHireResponse(
            ProtocolResponse.Reject(ProtocolErrorCode.Conflict, T("Mercenary.ContractLedgerFailed")), null, null));
        return Results.Ok(new MercenaryHireResponse(
            new ProtocolResponse(true, ProtocolErrorCode.DuplicateRequest, T("Mercenary.DuplicateTransaction")),
            ToMercenaryContractDto(contract), BuildBankStatusResponse(state, context, ProtocolResponse.Ok()), contract.SnapshotId,
            FindSnapshotNextLineageToken(state, request.UserId, request.ColonyId, contract.SnapshotId)));
    }

    private static IResult GuardSnapshotReplayResponse(ClashOfRimNetworkState state, HireMercenaryGuardWithSnapshotRequest request,
        MercenaryGuardContractRecord contract, BankRequestContext context)
    {
        if (!MatchesGuardSnapshot(contract, request)) return Results.Ok(new MercenaryGuardHireResponse(
            ProtocolResponse.Reject(ProtocolErrorCode.Conflict, T("Mercenary.GuardContractLedgerFailed")), null, null));
        return Results.Ok(new MercenaryGuardHireResponse(
            new ProtocolResponse(true, ProtocolErrorCode.DuplicateRequest, T("Mercenary.GuardDuplicateTransaction")),
            ToMercenaryGuardContractDto(contract), BuildBankStatusResponse(state, context, ProtocolResponse.Ok()), contract.SnapshotId,
            FindSnapshotNextLineageToken(state, request.UserId, request.ColonyId, contract.SnapshotId)));
    }

    private static ProtocolResponse? ValidateMercenarySnapshotHire(ClashOfRimNetworkState state, HireMercenaryWithSnapshotRequest request)
    {
        if (!state.ServerConfiguration.MercenariesEnabled)
            return ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, T("Mercenary.Disabled"));
        var shape = ValidateMercenaryRequestShape(request.SkillDefName, request.SkillLevel, request.DurationDays, state.ServerConfiguration);
        if (shape is not null) return shape;
        var limit = ValidateMercenaryActiveLimit(state, request.UserId, request.ColonyId);
        if (limit is not null) return limit;
        if (state.MercenaryContracts.Find(request.RequestedContractId) is not null)
            return ProtocolResponse.Reject(ProtocolErrorCode.Conflict, T("Mercenary.ContractLedgerFailed"));
        if (request.ExpectedPriceSilver != CalculateMercenaryPriceSilver(request.SkillLevel, request.DurationDays, state.ServerConfiguration)
            || request.ExpectedHarmfulSurgeryFineSilver != state.ServerConfiguration.MercenaryHarmfulSurgeryFineSilver
            || request.ExpectedDeathFineSilver != CalculateMercenaryDeathFineSilver(request.SkillLevel, state.ServerConfiguration))
            return ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Mercenary.QuoteMismatch"));
        return null;
    }

    private static ProtocolResponse? ValidateGuardSnapshotHire(ClashOfRimNetworkState state, HireMercenaryGuardWithSnapshotRequest request)
    {
        if (!state.ServerConfiguration.PvpEnabled)
            return ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, T("Mercenary.GuardPvpDisabled"));
        if (!state.ServerConfiguration.MercenariesEnabled || !state.ServerConfiguration.MercenaryGuardsEnabled)
            return ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, T("Mercenary.GuardDisabled"));
        string tier = NormalizeMercenaryGuardTier(request.Tier);
        if (!IsValidMercenaryGuardTier(tier))
            return ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Mercenary.GuardInvalidTier"));
        if (state.MercenaryGuards.HasActiveForColony(request.UserId, request.ColonyId))
            return ProtocolResponse.Reject(ProtocolErrorCode.Conflict, T("Mercenary.GuardAlreadyActive"));
        if (state.MercenaryGuards.ContainsContractId(request.RequestedContractId))
            return ProtocolResponse.Reject(ProtocolErrorCode.Conflict, T("Mercenary.GuardContractLedgerFailed"));
        if (request.ExpectedPriceSilver != CalculateMercenaryGuardPriceSilver(tier, state.ServerConfiguration)
            || !float.IsFinite(request.ExpectedPointRatio)
            || Math.Abs(request.ExpectedPointRatio - CalculateMercenaryGuardPointRatio(tier, state.ServerConfiguration)) > 0.0001f)
            return ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Mercenary.GuardQuoteMismatch"));
        return null;
    }

    private static void CommitMercenarySnapshot(ClashOfRimNetworkState state, SaveSnapshotPackage package,
        SnapshotUploadResult upload, MercenarySnapshotMutation mutation) =>
        state.SnapshotCommits.CommitMercenary(state.SnapshotStore, state.BankLoans, state.MercenaryContracts, state.MercenaryGuards,
            package, upload.AcceptedSnapshot!, mutation,
            (snapshot, kind) => RunCommittedSnapshotPostUpload(state, snapshot, kind), upload.SnapshotUploadKind, preparedStoragePayload: upload.PreparedStoragePayload);
}
