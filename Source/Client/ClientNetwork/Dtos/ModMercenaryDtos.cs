using System.Runtime.Serialization;
using System.Collections.Generic;

namespace AIRsLight.ClashOfRim.ClientNetwork;

[DataContract]
public sealed class ModQuoteMercenaryRequestDto
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

    [DataMember(Name = "skillDefName")]
    public string SkillDefName { get; set; } = string.Empty;

    [DataMember(Name = "skillLevel")]
    public int SkillLevel { get; set; }

    [DataMember(Name = "durationDays")]
    public int DurationDays { get; set; }
}

[DataContract]
public sealed class ModHireMercenaryRequestDto
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

    [DataMember(Name = "skillDefName")]
    public string SkillDefName { get; set; } = string.Empty;

    [DataMember(Name = "skillLevel")]
    public int SkillLevel { get; set; }

    [DataMember(Name = "durationDays")]
    public int DurationDays { get; set; }
}

[DataContract]
public sealed class ModHireMercenaryWithSnapshotRequestDto
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

    [DataMember(Name = "skillDefName")]
    public string SkillDefName { get; set; } = string.Empty;

    [DataMember(Name = "skillLevel")]
    public int SkillLevel { get; set; }

    [DataMember(Name = "durationDays")]
    public int DurationDays { get; set; }

    [DataMember(Name = "requestedContractId")]
    public string RequestedContractId { get; set; } = string.Empty;

    [DataMember(Name = "expectedPriceSilver")]
    public int ExpectedPriceSilver { get; set; }

    [DataMember(Name = "expectedHarmfulSurgeryFineSilver")]
    public int ExpectedHarmfulSurgeryFineSilver { get; set; }

    [DataMember(Name = "expectedDeathFineSilver")]
    public int ExpectedDeathFineSilver { get; set; }

    [DataMember(Name = "confirmedSnapshot")]
    public ModSnapshotPackageMetadataDto? ConfirmedSnapshot { get; set; }
}

[DataContract]
public sealed class ModQuoteMercenaryGuardRequestDto
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

    [DataMember(Name = "tier")]
    public string Tier { get; set; } = string.Empty;
}

[DataContract]
public sealed class ModHireMercenaryGuardWithSnapshotRequestDto
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

    [DataMember(Name = "requestedContractId")]
    public string RequestedContractId { get; set; } = string.Empty;

    [DataMember(Name = "tier")]
    public string Tier { get; set; } = string.Empty;

    [DataMember(Name = "expectedPriceSilver")]
    public int ExpectedPriceSilver { get; set; }

    [DataMember(Name = "expectedPointRatio")]
    public float ExpectedPointRatio { get; set; }

    [DataMember(Name = "confirmedSnapshot")]
    public ModSnapshotPackageMetadataDto? ConfirmedSnapshot { get; set; }
}

[DataContract]
public sealed class ModReportMercenaryIncidentRequestDto
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

    [DataMember(Name = "contractId")]
    public string ContractId { get; set; } = string.Empty;

    [DataMember(Name = "incidentKind")]
    public string IncidentKind { get; set; } = string.Empty;
}

[DataContract]
public sealed class ModMercenaryContractDto
{
    [DataMember(Name = "contractId")]
    public string ContractId { get; set; } = string.Empty;

    [DataMember(Name = "skillDefName")]
    public string SkillDefName { get; set; } = string.Empty;

    [DataMember(Name = "skillLevel")]
    public int SkillLevel { get; set; }

    [DataMember(Name = "durationDays")]
    public int DurationDays { get; set; }

    [DataMember(Name = "priceSilver")]
    public int PriceSilver { get; set; }

    [DataMember(Name = "harmfulSurgeryFineSilver")]
    public int HarmfulSurgeryFineSilver { get; set; }

    [DataMember(Name = "deathFineSilver")]
    public int DeathFineSilver { get; set; }

    [DataMember(Name = "createdAtGameTicks")]
    public long CreatedAtGameTicks { get; set; }

    [DataMember(Name = "expiresAtGameTicks")]
    public long ExpiresAtGameTicks { get; set; }
}

[DataContract]
public sealed class ModMercenaryHireResponseDto
{
    [DataMember(Name = "result")]
    public ModProtocolResponseDto? Result { get; set; }

    [DataMember(Name = "contract")]
    public ModMercenaryContractDto? Contract { get; set; }

    [DataMember(Name = "bankStatus")]
    public ModBankStatusResponseDto? BankStatus { get; set; }

    [DataMember(Name = "appliedSnapshotId")]
    public string? AppliedSnapshotId { get; set; }

    [DataMember(Name = "nextLineageToken")]
    public string? NextLineageToken { get; set; }
}

[DataContract]
public sealed class ModMercenaryGuardContractDto
{
    [DataMember(Name = "contractId")]
    public string ContractId { get; set; } = string.Empty;

    [DataMember(Name = "tier")]
    public string Tier { get; set; } = string.Empty;

    [DataMember(Name = "priceSilver")]
    public int PriceSilver { get; set; }

    [DataMember(Name = "pointRatio")]
    public float PointRatio { get; set; }

    [DataMember(Name = "createdAtGameTicks")]
    public long CreatedAtGameTicks { get; set; }
}

[DataContract]
public sealed class ModMercenaryGuardHireResponseDto
{
    [DataMember(Name = "result")]
    public ModProtocolResponseDto? Result { get; set; }

    [DataMember(Name = "contract")]
    public ModMercenaryGuardContractDto? Contract { get; set; }

    [DataMember(Name = "bankStatus")]
    public ModBankStatusResponseDto? BankStatus { get; set; }

    [DataMember(Name = "appliedSnapshotId")]
    public string? AppliedSnapshotId { get; set; }

    [DataMember(Name = "nextLineageToken")]
    public string? NextLineageToken { get; set; }
}

[DataContract]
public sealed class ModMercenaryQuoteResponseDto
{
    [DataMember(Name = "result")]
    public ModProtocolResponseDto? Result { get; set; }

    [DataMember(Name = "skillLevel")]
    public int SkillLevel { get; set; }

    [DataMember(Name = "durationDays")]
    public int DurationDays { get; set; }

    [DataMember(Name = "priceSilver")]
    public int PriceSilver { get; set; }

    [DataMember(Name = "harmfulSurgeryFineSilver")]
    public int HarmfulSurgeryFineSilver { get; set; }

    [DataMember(Name = "deathFineSilver")]
    public int DeathFineSilver { get; set; }

    [DataMember(Name = "bankStatus")]
    public ModBankStatusResponseDto? BankStatus { get; set; }
}

[DataContract]
public sealed class ModMercenaryGuardQuoteResponseDto
{
    [DataMember(Name = "result")]
    public ModProtocolResponseDto? Result { get; set; }

    [DataMember(Name = "tier")]
    public string Tier { get; set; } = string.Empty;

    [DataMember(Name = "priceSilver")]
    public int PriceSilver { get; set; }

    [DataMember(Name = "pointRatio")]
    public float PointRatio { get; set; }

    [DataMember(Name = "bankStatus")]
    public ModBankStatusResponseDto? BankStatus { get; set; }
}

[DataContract]
public sealed class ModMercenaryIncidentResponseDto
{
    [DataMember(Name = "result")]
    public ModProtocolResponseDto? Result { get; set; }

    [DataMember(Name = "debt")]
    public ModBankDebtSummaryDto? Debt { get; set; }

    [DataMember(Name = "bankStatus")]
    public ModBankStatusResponseDto? BankStatus { get; set; }
}
