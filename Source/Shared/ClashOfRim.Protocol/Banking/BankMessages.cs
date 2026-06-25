using System;
using System.Collections.Generic;
using System.Linq;

namespace AIRsLight.ClashOfRim.Protocol;

public sealed class GetBankStatusRequest
{
    public GetBankStatusRequest(
        string userId,
        string colonyId,
        string currentSnapshotId,
        string? authToken,
        long currentGameTicks,
        int colonyWealth)
    {
        UserId = userId;
        ColonyId = colonyId;
        CurrentSnapshotId = currentSnapshotId;
        AuthToken = authToken;
        CurrentGameTicks = currentGameTicks;
        ColonyWealth = colonyWealth;
    }

    public string UserId { get; }

    public string ColonyId { get; }

    public string CurrentSnapshotId { get; }

    public string? AuthToken { get; }

    public long CurrentGameTicks { get; }

    public int ColonyWealth { get; }
}

public sealed class CreateBankLoanRequest
{
    public CreateBankLoanRequest(
        string idempotencyKey,
        string userId,
        string colonyId,
        string currentSnapshotId,
        string? authToken,
        long currentGameTicks,
        int colonyWealth,
        int principalSilver,
        int durationDays)
    {
        IdempotencyKey = idempotencyKey;
        UserId = userId;
        ColonyId = colonyId;
        CurrentSnapshotId = currentSnapshotId;
        AuthToken = authToken;
        CurrentGameTicks = currentGameTicks;
        ColonyWealth = colonyWealth;
        PrincipalSilver = principalSilver;
        DurationDays = durationDays;
    }

    public string IdempotencyKey { get; }

    public string UserId { get; }

    public string ColonyId { get; }

    public string CurrentSnapshotId { get; }

    public string? AuthToken { get; }

    public long CurrentGameTicks { get; }

    public int ColonyWealth { get; }

    public int PrincipalSilver { get; }

    public int DurationDays { get; }
}

public sealed class CreateBankLoanWithSnapshotRequest
{
    public CreateBankLoanWithSnapshotRequest(
        string idempotencyKey,
        string userId,
        string colonyId,
        string currentSnapshotId,
        string? authToken,
        long currentGameTicks,
        int colonyWealth,
        int principalSilver,
        int durationDays,
        string requestedLoanId,
        int expectedInterestSilver,
        SnapshotPackageMetadataDto confirmedSnapshot)
    {
        IdempotencyKey = idempotencyKey;
        UserId = userId;
        ColonyId = colonyId;
        CurrentSnapshotId = currentSnapshotId;
        AuthToken = authToken;
        CurrentGameTicks = currentGameTicks;
        ColonyWealth = colonyWealth;
        PrincipalSilver = principalSilver;
        DurationDays = durationDays;
        RequestedLoanId = requestedLoanId;
        ExpectedInterestSilver = expectedInterestSilver;
        ConfirmedSnapshot = confirmedSnapshot;
    }

    public string IdempotencyKey { get; }

    public string UserId { get; }

    public string ColonyId { get; }

    public string CurrentSnapshotId { get; }

    public string? AuthToken { get; }

    public long CurrentGameTicks { get; }

    public int ColonyWealth { get; }

    public int PrincipalSilver { get; }

    public int DurationDays { get; }

    public string RequestedLoanId { get; }

    public int ExpectedInterestSilver { get; }

    public SnapshotPackageMetadataDto ConfirmedSnapshot { get; }
}

public sealed class RepayBankLoanRequest
{
    public RepayBankLoanRequest(
        string idempotencyKey,
        string userId,
        string colonyId,
        string currentSnapshotId,
        string? authToken,
        long currentGameTicks,
        int silverPaid)
    {
        IdempotencyKey = idempotencyKey;
        UserId = userId;
        ColonyId = colonyId;
        CurrentSnapshotId = currentSnapshotId;
        AuthToken = authToken;
        CurrentGameTicks = currentGameTicks;
        SilverPaid = silverPaid;
    }

    public string IdempotencyKey { get; }

    public string UserId { get; }

    public string ColonyId { get; }

    public string CurrentSnapshotId { get; }

    public string? AuthToken { get; }

    public long CurrentGameTicks { get; }

    public int SilverPaid { get; }
}

public sealed class RepayBankLoanWithSnapshotRequest
{
    public RepayBankLoanWithSnapshotRequest(
        string idempotencyKey,
        string userId,
        string colonyId,
        string currentSnapshotId,
        string? authToken,
        long currentGameTicks,
        string loanId,
        int silverPaid,
        SnapshotPackageMetadataDto confirmedSnapshot)
    {
        IdempotencyKey = idempotencyKey;
        UserId = userId;
        ColonyId = colonyId;
        CurrentSnapshotId = currentSnapshotId;
        AuthToken = authToken;
        CurrentGameTicks = currentGameTicks;
        LoanId = loanId;
        SilverPaid = silverPaid;
        ConfirmedSnapshot = confirmedSnapshot;
    }

    public string IdempotencyKey { get; }

    public string UserId { get; }

    public string ColonyId { get; }

    public string CurrentSnapshotId { get; }

    public string? AuthToken { get; }

    public long CurrentGameTicks { get; }

    public string LoanId { get; }

    public int SilverPaid { get; }

    public SnapshotPackageMetadataDto ConfirmedSnapshot { get; }
}

public sealed class RepayBankDebtRequest
{
    public RepayBankDebtRequest(
        string idempotencyKey,
        string userId,
        string colonyId,
        string currentSnapshotId,
        string? authToken,
        long currentGameTicks,
        string debtId,
        int silverPaid)
    {
        IdempotencyKey = idempotencyKey;
        UserId = userId;
        ColonyId = colonyId;
        CurrentSnapshotId = currentSnapshotId;
        AuthToken = authToken;
        CurrentGameTicks = currentGameTicks;
        DebtId = debtId;
        SilverPaid = silverPaid;
    }

    public string IdempotencyKey { get; }

    public string UserId { get; }

    public string ColonyId { get; }

    public string CurrentSnapshotId { get; }

    public string? AuthToken { get; }

    public long CurrentGameTicks { get; }

    public string DebtId { get; }

    public int SilverPaid { get; }
}

public sealed class RepayBankDebtWithSnapshotRequest
{
    public RepayBankDebtWithSnapshotRequest(
        string idempotencyKey,
        string userId,
        string colonyId,
        string currentSnapshotId,
        string? authToken,
        long currentGameTicks,
        string debtId,
        int silverPaid,
        SnapshotPackageMetadataDto confirmedSnapshot)
    {
        IdempotencyKey = idempotencyKey;
        UserId = userId;
        ColonyId = colonyId;
        CurrentSnapshotId = currentSnapshotId;
        AuthToken = authToken;
        CurrentGameTicks = currentGameTicks;
        DebtId = debtId;
        SilverPaid = silverPaid;
        ConfirmedSnapshot = confirmedSnapshot;
    }

    public string IdempotencyKey { get; }

    public string UserId { get; }

    public string ColonyId { get; }

    public string CurrentSnapshotId { get; }

    public string? AuthToken { get; }

    public long CurrentGameTicks { get; }

    public string DebtId { get; }

    public int SilverPaid { get; }

    public SnapshotPackageMetadataDto ConfirmedSnapshot { get; }
}

public sealed class BankLoanSummaryDto
{
    public BankLoanSummaryDto(
        string loanId,
        int principalSilver,
        int interestSilver,
        int totalDueSilver,
        int durationDays,
        long createdAtGameTicks,
        long dueAtGameTicks,
        int penaltyRaidPoints,
        string status,
        string sourceKind = "Loan")
    {
        LoanId = loanId;
        PrincipalSilver = principalSilver;
        InterestSilver = interestSilver;
        TotalDueSilver = totalDueSilver;
        DurationDays = durationDays;
        CreatedAtGameTicks = createdAtGameTicks;
        DueAtGameTicks = dueAtGameTicks;
        PenaltyRaidPoints = penaltyRaidPoints;
        Status = status;
        SourceKind = sourceKind;
    }

    public string LoanId { get; }

    public int PrincipalSilver { get; }

    public int InterestSilver { get; }

    public int TotalDueSilver { get; }

    public int DurationDays { get; }

    public long CreatedAtGameTicks { get; }

    public long DueAtGameTicks { get; }

    public int PenaltyRaidPoints { get; }

    public string Status { get; }

    public string SourceKind { get; }
}

public sealed class BankStatusResponse
{
    public BankStatusResponse(
        ProtocolResponse result,
        int colonyWealth,
        int minLoanSilver,
        int configuredMaxLoanSilver,
        int maxLoanSilver,
        float maxLoanWealthRatio,
        float baseAnnualInterestRate,
        int minDurationDays,
        int maxDurationDays,
        bool bankLoansEnabled,
        bool mercenariesEnabled,
        int mercenaryMinDurationDays,
        int mercenaryMaxDurationDays,
        IReadOnlyList<BankInterestDurationMultiplierPointDto>? interestDurationMultiplierCurve,
        int penaltyIntervalDays,
        float penaltyRaidPointsPerSilver,
        IReadOnlyList<BankOverduePenaltyStageDto> overduePenaltyStages,
        BankLoanSummaryDto? activeLoan,
        IReadOnlyList<BankDebtSummaryDto>? openDebts = null)
    {
        Result = result;
        ColonyWealth = colonyWealth;
        MinLoanSilver = minLoanSilver;
        ConfiguredMaxLoanSilver = configuredMaxLoanSilver;
        MaxLoanSilver = maxLoanSilver;
        MaxLoanWealthRatio = maxLoanWealthRatio;
        BaseAnnualInterestRate = baseAnnualInterestRate;
        MinDurationDays = minDurationDays;
        MaxDurationDays = maxDurationDays;
        BankLoansEnabled = bankLoansEnabled;
        MercenariesEnabled = mercenariesEnabled;
        MercenaryMinDurationDays = mercenaryMinDurationDays;
        MercenaryMaxDurationDays = mercenaryMaxDurationDays;
        InterestDurationMultiplierCurve = interestDurationMultiplierCurve ?? Array.Empty<BankInterestDurationMultiplierPointDto>();
        PenaltyIntervalDays = penaltyIntervalDays;
        PenaltyRaidPointsPerSilver = penaltyRaidPointsPerSilver;
        OverduePenaltyStages = overduePenaltyStages;
        ActiveLoan = activeLoan;
        OpenDebts = openDebts ?? Array.Empty<BankDebtSummaryDto>();
        TotalOpenDebtSilver = OpenDebts.Sum(debt => debt.AmountSilver);
    }

    public ProtocolResponse Result { get; }

    public int ColonyWealth { get; }

    public int MinLoanSilver { get; }

    public int ConfiguredMaxLoanSilver { get; }

    public int MaxLoanSilver { get; }

    public float MaxLoanWealthRatio { get; }

    public float BaseAnnualInterestRate { get; }

    public int MinDurationDays { get; }

    public int MaxDurationDays { get; }

    public bool BankLoansEnabled { get; }

    public bool MercenariesEnabled { get; }

    public int MercenaryMinDurationDays { get; }

    public int MercenaryMaxDurationDays { get; }

    public IReadOnlyList<BankInterestDurationMultiplierPointDto> InterestDurationMultiplierCurve { get; }

    public int PenaltyIntervalDays { get; }

    public float PenaltyRaidPointsPerSilver { get; }

    public IReadOnlyList<BankOverduePenaltyStageDto> OverduePenaltyStages { get; }

    public BankLoanSummaryDto? ActiveLoan { get; }

    public IReadOnlyList<BankDebtSummaryDto> OpenDebts { get; }

    public int TotalOpenDebtSilver { get; }
}

public sealed class BankInterestDurationMultiplierPointDto
{
    public BankInterestDurationMultiplierPointDto(int durationDays, float multiplier)
    {
        DurationDays = durationDays;
        Multiplier = multiplier;
    }

    public int DurationDays { get; }

    public float Multiplier { get; }
}

public sealed class BankDebtSummaryDto
{
    public BankDebtSummaryDto(
        string debtId,
        int amountSilver,
        string sourceKind,
        string reason,
        string sourceId,
        long createdAtGameTicks,
        string status)
    {
        DebtId = debtId;
        AmountSilver = amountSilver;
        SourceKind = sourceKind;
        Reason = reason;
        SourceId = sourceId;
        CreatedAtGameTicks = createdAtGameTicks;
        Status = status;
    }

    public string DebtId { get; }

    public int AmountSilver { get; }

    public string SourceKind { get; }

    public string Reason { get; }

    public string SourceId { get; }

    public long CreatedAtGameTicks { get; }

    public string Status { get; }
}

public sealed class BankOverduePenaltyStageDto
{
    public BankOverduePenaltyStageDto(
        int triggerPenaltyCount,
        string kind,
        float severity)
    {
        TriggerPenaltyCount = triggerPenaltyCount;
        Kind = kind;
        Severity = severity;
    }

    public int TriggerPenaltyCount { get; }

    public string Kind { get; }

    public float Severity { get; }
}

public sealed class BankLoanResponse
{
    public BankLoanResponse(
        ProtocolResponse result,
        BankLoanSummaryDto? loan,
        int silverDelta,
        BankStatusResponse? status,
        string? appliedSnapshotId = null,
        string? nextLineageToken = null)
    {
        Result = result;
        Loan = loan;
        SilverDelta = silverDelta;
        Status = status;
        AppliedSnapshotId = appliedSnapshotId;
        NextLineageToken = nextLineageToken;
    }

    public ProtocolResponse Result { get; }

    public BankLoanSummaryDto? Loan { get; }

    public int SilverDelta { get; }

    public BankStatusResponse? Status { get; }

    public string? AppliedSnapshotId { get; }

    public string? NextLineageToken { get; }
}

public sealed class BankDebtResponse
{
    public BankDebtResponse(
        ProtocolResponse result,
        BankDebtSummaryDto? debt,
        int silverDelta,
        BankStatusResponse? status,
        string? appliedSnapshotId = null,
        string? nextLineageToken = null)
    {
        Result = result;
        Debt = debt;
        SilverDelta = silverDelta;
        Status = status;
        AppliedSnapshotId = appliedSnapshotId;
        NextLineageToken = nextLineageToken;
    }

    public ProtocolResponse Result { get; }

    public BankDebtSummaryDto? Debt { get; }

    public int SilverDelta { get; }

    public BankStatusResponse? Status { get; }

    public string? AppliedSnapshotId { get; }

    public string? NextLineageToken { get; }
}
