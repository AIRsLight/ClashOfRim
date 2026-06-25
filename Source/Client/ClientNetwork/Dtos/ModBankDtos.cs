using System.Runtime.Serialization;
using System.Collections.Generic;

namespace AIRsLight.ClashOfRim.ClientNetwork;

[DataContract]
public sealed class ModGetBankStatusRequestDto
{
    [DataMember(Name = "userId")]
    public string UserId { get; set; } = string.Empty;

    [DataMember(Name = "colonyId")]
    public string ColonyId { get; set; } = string.Empty;

    [DataMember(Name = "currentSnapshotId")]
    public string CurrentSnapshotId { get; set; } = string.Empty;

    [DataMember(Name = "authToken")]
    public string? AuthToken { get; set; }

    [DataMember(Name = "currentGameTicks")]
    public long CurrentGameTicks { get; set; }

    [DataMember(Name = "colonyWealth")]
    public int ColonyWealth { get; set; }
}

[DataContract]
public sealed class ModCreateBankLoanRequestDto
{
    [DataMember(Name = "idempotencyKey")]
    public string IdempotencyKey { get; set; } = string.Empty;

    [DataMember(Name = "userId")]
    public string UserId { get; set; } = string.Empty;

    [DataMember(Name = "colonyId")]
    public string ColonyId { get; set; } = string.Empty;

    [DataMember(Name = "currentSnapshotId")]
    public string CurrentSnapshotId { get; set; } = string.Empty;

    [DataMember(Name = "authToken")]
    public string? AuthToken { get; set; }

    [DataMember(Name = "currentGameTicks")]
    public long CurrentGameTicks { get; set; }

    [DataMember(Name = "colonyWealth")]
    public int ColonyWealth { get; set; }

    [DataMember(Name = "principalSilver")]
    public int PrincipalSilver { get; set; }

    [DataMember(Name = "durationDays")]
    public int DurationDays { get; set; }
}

[DataContract]
public sealed class ModCreateBankLoanWithSnapshotRequestDto
{
    [DataMember(Name = "idempotencyKey")]
    public string IdempotencyKey { get; set; } = string.Empty;

    [DataMember(Name = "userId")]
    public string UserId { get; set; } = string.Empty;

    [DataMember(Name = "colonyId")]
    public string ColonyId { get; set; } = string.Empty;

    [DataMember(Name = "currentSnapshotId")]
    public string CurrentSnapshotId { get; set; } = string.Empty;

    [DataMember(Name = "authToken")]
    public string? AuthToken { get; set; }

    [DataMember(Name = "currentGameTicks")]
    public long CurrentGameTicks { get; set; }

    [DataMember(Name = "colonyWealth")]
    public int ColonyWealth { get; set; }

    [DataMember(Name = "principalSilver")]
    public int PrincipalSilver { get; set; }

    [DataMember(Name = "durationDays")]
    public int DurationDays { get; set; }

    [DataMember(Name = "requestedLoanId")]
    public string RequestedLoanId { get; set; } = string.Empty;

    [DataMember(Name = "expectedInterestSilver")]
    public int ExpectedInterestSilver { get; set; }

    [DataMember(Name = "confirmedSnapshot")]
    public ModSnapshotPackageMetadataDto? ConfirmedSnapshot { get; set; }
}

[DataContract]
public sealed class ModRepayBankLoanRequestDto
{
    [DataMember(Name = "idempotencyKey")]
    public string IdempotencyKey { get; set; } = string.Empty;

    [DataMember(Name = "userId")]
    public string UserId { get; set; } = string.Empty;

    [DataMember(Name = "colonyId")]
    public string ColonyId { get; set; } = string.Empty;

    [DataMember(Name = "currentSnapshotId")]
    public string CurrentSnapshotId { get; set; } = string.Empty;

    [DataMember(Name = "authToken")]
    public string? AuthToken { get; set; }

    [DataMember(Name = "currentGameTicks")]
    public long CurrentGameTicks { get; set; }

    [DataMember(Name = "silverPaid")]
    public int SilverPaid { get; set; }
}

[DataContract]
public sealed class ModRepayBankLoanWithSnapshotRequestDto
{
    [DataMember(Name = "idempotencyKey")]
    public string IdempotencyKey { get; set; } = string.Empty;

    [DataMember(Name = "userId")]
    public string UserId { get; set; } = string.Empty;

    [DataMember(Name = "colonyId")]
    public string ColonyId { get; set; } = string.Empty;

    [DataMember(Name = "currentSnapshotId")]
    public string CurrentSnapshotId { get; set; } = string.Empty;

    [DataMember(Name = "authToken")]
    public string? AuthToken { get; set; }

    [DataMember(Name = "currentGameTicks")]
    public long CurrentGameTicks { get; set; }

    [DataMember(Name = "loanId")]
    public string LoanId { get; set; } = string.Empty;

    [DataMember(Name = "silverPaid")]
    public int SilverPaid { get; set; }

    [DataMember(Name = "confirmedSnapshot")]
    public ModSnapshotPackageMetadataDto? ConfirmedSnapshot { get; set; }
}

[DataContract]
public sealed class ModRepayBankDebtRequestDto
{
    [DataMember(Name = "idempotencyKey")]
    public string IdempotencyKey { get; set; } = string.Empty;

    [DataMember(Name = "userId")]
    public string UserId { get; set; } = string.Empty;

    [DataMember(Name = "colonyId")]
    public string ColonyId { get; set; } = string.Empty;

    [DataMember(Name = "currentSnapshotId")]
    public string CurrentSnapshotId { get; set; } = string.Empty;

    [DataMember(Name = "authToken")]
    public string? AuthToken { get; set; }

    [DataMember(Name = "currentGameTicks")]
    public long CurrentGameTicks { get; set; }

    [DataMember(Name = "debtId")]
    public string DebtId { get; set; } = string.Empty;

    [DataMember(Name = "silverPaid")]
    public int SilverPaid { get; set; }
}

[DataContract]
public sealed class ModRepayBankDebtWithSnapshotRequestDto
{
    [DataMember(Name = "idempotencyKey")]
    public string IdempotencyKey { get; set; } = string.Empty;

    [DataMember(Name = "userId")]
    public string UserId { get; set; } = string.Empty;

    [DataMember(Name = "colonyId")]
    public string ColonyId { get; set; } = string.Empty;

    [DataMember(Name = "currentSnapshotId")]
    public string CurrentSnapshotId { get; set; } = string.Empty;

    [DataMember(Name = "authToken")]
    public string? AuthToken { get; set; }

    [DataMember(Name = "currentGameTicks")]
    public long CurrentGameTicks { get; set; }

    [DataMember(Name = "debtId")]
    public string DebtId { get; set; } = string.Empty;

    [DataMember(Name = "silverPaid")]
    public int SilverPaid { get; set; }

    [DataMember(Name = "confirmedSnapshot")]
    public ModSnapshotPackageMetadataDto? ConfirmedSnapshot { get; set; }
}

[DataContract]
public sealed class ModBankLoanSummaryDto
{
    [DataMember(Name = "loanId")]
    public string LoanId { get; set; } = string.Empty;

    [DataMember(Name = "principalSilver")]
    public int PrincipalSilver { get; set; }

    [DataMember(Name = "interestSilver")]
    public int InterestSilver { get; set; }

    [DataMember(Name = "totalDueSilver")]
    public int TotalDueSilver { get; set; }

    [DataMember(Name = "durationDays")]
    public int DurationDays { get; set; }

    [DataMember(Name = "createdAtGameTicks")]
    public long CreatedAtGameTicks { get; set; }

    [DataMember(Name = "dueAtGameTicks")]
    public long DueAtGameTicks { get; set; }

    [DataMember(Name = "penaltyRaidPoints")]
    public int PenaltyRaidPoints { get; set; }

    [DataMember(Name = "status")]
    public string Status { get; set; } = string.Empty;

    [DataMember(Name = "sourceKind")]
    public string SourceKind { get; set; } = "Loan";
}

[DataContract]
public sealed class ModBankDebtSummaryDto
{
    [DataMember(Name = "debtId")]
    public string DebtId { get; set; } = string.Empty;

    [DataMember(Name = "amountSilver")]
    public int AmountSilver { get; set; }

    [DataMember(Name = "sourceKind")]
    public string SourceKind { get; set; } = string.Empty;

    [DataMember(Name = "reason")]
    public string Reason { get; set; } = string.Empty;

    [DataMember(Name = "sourceId")]
    public string SourceId { get; set; } = string.Empty;

    [DataMember(Name = "createdAtGameTicks")]
    public long CreatedAtGameTicks { get; set; }

    [DataMember(Name = "status")]
    public string Status { get; set; } = string.Empty;
}

[DataContract]
public sealed class ModBankOverduePenaltyStageDto
{
    [DataMember(Name = "triggerPenaltyCount")]
    public int TriggerPenaltyCount { get; set; }

    [DataMember(Name = "kind")]
    public string? Kind { get; set; }

    [DataMember(Name = "severity")]
    public float Severity { get; set; }
}

[DataContract]
public sealed class ModBankInterestDurationMultiplierPointDto
{
    [DataMember(Name = "durationDays")]
    public int DurationDays { get; set; }

    [DataMember(Name = "multiplier")]
    public float Multiplier { get; set; }
}

[DataContract]
public sealed class ModBankStatusResponseDto
{
    [DataMember(Name = "result")]
    public ModProtocolResponseDto? Result { get; set; }

    [DataMember(Name = "colonyWealth")]
    public int ColonyWealth { get; set; }

    [DataMember(Name = "minLoanSilver")]
    public int MinLoanSilver { get; set; }

    [DataMember(Name = "configuredMaxLoanSilver")]
    public int ConfiguredMaxLoanSilver { get; set; }

    [DataMember(Name = "maxLoanSilver")]
    public int MaxLoanSilver { get; set; }

    [DataMember(Name = "maxLoanWealthRatio")]
    public float MaxLoanWealthRatio { get; set; }

    [DataMember(Name = "baseAnnualInterestRate")]
    public float BaseAnnualInterestRate { get; set; }

    [DataMember(Name = "minDurationDays")]
    public int MinDurationDays { get; set; }

    [DataMember(Name = "maxDurationDays")]
    public int MaxDurationDays { get; set; }

    [DataMember(Name = "bankLoansEnabled")]
    public bool BankLoansEnabled { get; set; } = true;

    [DataMember(Name = "mercenariesEnabled")]
    public bool MercenariesEnabled { get; set; } = true;

    [DataMember(Name = "mercenaryMinDurationDays")]
    public int MercenaryMinDurationDays { get; set; }

    [DataMember(Name = "mercenaryMaxDurationDays")]
    public int MercenaryMaxDurationDays { get; set; }

    [DataMember(Name = "interestDurationMultiplierCurve")]
    public List<ModBankInterestDurationMultiplierPointDto> InterestDurationMultiplierCurve { get; set; } = new();

    [DataMember(Name = "penaltyIntervalDays")]
    public int PenaltyIntervalDays { get; set; }

    [DataMember(Name = "penaltyRaidPointsPerSilver")]
    public float PenaltyRaidPointsPerSilver { get; set; }

    [DataMember(Name = "overduePenaltyStages")]
    public List<ModBankOverduePenaltyStageDto> OverduePenaltyStages { get; set; } = new();

    [DataMember(Name = "activeLoan")]
    public ModBankLoanSummaryDto? ActiveLoan { get; set; }

    [DataMember(Name = "openDebts")]
    public List<ModBankDebtSummaryDto> OpenDebts { get; set; } = new();

    [DataMember(Name = "totalOpenDebtSilver")]
    public int TotalOpenDebtSilver { get; set; }
}

[DataContract]
public sealed class ModBankLoanResponseDto
{
    [DataMember(Name = "result")]
    public ModProtocolResponseDto? Result { get; set; }

    [DataMember(Name = "loan")]
    public ModBankLoanSummaryDto? Loan { get; set; }

    [DataMember(Name = "silverDelta")]
    public int SilverDelta { get; set; }

    [DataMember(Name = "status")]
    public ModBankStatusResponseDto? Status { get; set; }

    [DataMember(Name = "appliedSnapshotId")]
    public string? AppliedSnapshotId { get; set; }

    [DataMember(Name = "nextLineageToken")]
    public string? NextLineageToken { get; set; }
}

[DataContract]
public sealed class ModBankDebtResponseDto
{
    [DataMember(Name = "result")]
    public ModProtocolResponseDto? Result { get; set; }

    [DataMember(Name = "debt")]
    public ModBankDebtSummaryDto? Debt { get; set; }

    [DataMember(Name = "silverDelta")]
    public int SilverDelta { get; set; }

    [DataMember(Name = "status")]
    public ModBankStatusResponseDto? Status { get; set; }

    [DataMember(Name = "appliedSnapshotId")]
    public string? AppliedSnapshotId { get; set; }

    [DataMember(Name = "nextLineageToken")]
    public string? NextLineageToken { get; set; }
}
