namespace AIRsLight.ClashOfRim.Protocol;

public sealed class QuoteMercenaryRequest
{
    public QuoteMercenaryRequest(
        string userId,
        string colonyId,
        string currentSnapshotId,
        string? authToken,
        long currentGameTicks,
        string skillDefName,
        int skillLevel,
        int durationDays)
    {
        UserId = userId;
        ColonyId = colonyId;
        CurrentSnapshotId = currentSnapshotId;
        AuthToken = authToken;
        CurrentGameTicks = currentGameTicks;
        SkillDefName = skillDefName;
        SkillLevel = skillLevel;
        DurationDays = durationDays;
    }

    public string UserId { get; }

    public string ColonyId { get; }

    public string CurrentSnapshotId { get; }

    public string? AuthToken { get; }

    public long CurrentGameTicks { get; }

    public string SkillDefName { get; }

    public int SkillLevel { get; }

    public int DurationDays { get; }
}

public sealed class HireMercenaryRequest
{
    public HireMercenaryRequest(
        string idempotencyKey,
        string userId,
        string colonyId,
        string currentSnapshotId,
        string? authToken,
        long currentGameTicks,
        string skillDefName,
        int skillLevel,
        int durationDays)
    {
        IdempotencyKey = idempotencyKey;
        UserId = userId;
        ColonyId = colonyId;
        CurrentSnapshotId = currentSnapshotId;
        AuthToken = authToken;
        CurrentGameTicks = currentGameTicks;
        SkillDefName = skillDefName;
        SkillLevel = skillLevel;
        DurationDays = durationDays;
    }

    public string IdempotencyKey { get; }

    public string UserId { get; }

    public string ColonyId { get; }

    public string CurrentSnapshotId { get; }

    public string? AuthToken { get; }

    public long CurrentGameTicks { get; }

    public string SkillDefName { get; }

    public int SkillLevel { get; }

    public int DurationDays { get; }
}

public sealed class HireMercenaryWithSnapshotRequest
{
    public HireMercenaryWithSnapshotRequest(
        string idempotencyKey,
        string userId,
        string colonyId,
        string currentSnapshotId,
        string? authToken,
        long currentGameTicks,
        string skillDefName,
        int skillLevel,
        int durationDays,
        string requestedContractId,
        int expectedPriceSilver,
        int expectedHarmfulSurgeryFineSilver,
        int expectedDeathFineSilver,
        SnapshotPackageMetadataDto confirmedSnapshot)
    {
        IdempotencyKey = idempotencyKey;
        UserId = userId;
        ColonyId = colonyId;
        CurrentSnapshotId = currentSnapshotId;
        AuthToken = authToken;
        CurrentGameTicks = currentGameTicks;
        SkillDefName = skillDefName;
        SkillLevel = skillLevel;
        DurationDays = durationDays;
        RequestedContractId = requestedContractId;
        ExpectedPriceSilver = expectedPriceSilver;
        ExpectedHarmfulSurgeryFineSilver = expectedHarmfulSurgeryFineSilver;
        ExpectedDeathFineSilver = expectedDeathFineSilver;
        ConfirmedSnapshot = confirmedSnapshot;
    }

    public string IdempotencyKey { get; }

    public string UserId { get; }

    public string ColonyId { get; }

    public string CurrentSnapshotId { get; }

    public string? AuthToken { get; }

    public long CurrentGameTicks { get; }

    public string SkillDefName { get; }

    public int SkillLevel { get; }

    public int DurationDays { get; }

    public string RequestedContractId { get; }

    public int ExpectedPriceSilver { get; }

    public int ExpectedHarmfulSurgeryFineSilver { get; }

    public int ExpectedDeathFineSilver { get; }

    public SnapshotPackageMetadataDto ConfirmedSnapshot { get; }
}

public sealed class QuoteMercenaryGuardRequest
{
    public QuoteMercenaryGuardRequest(
        string userId,
        string colonyId,
        string currentSnapshotId,
        string? authToken,
        long currentGameTicks,
        string tier)
    {
        UserId = userId;
        ColonyId = colonyId;
        CurrentSnapshotId = currentSnapshotId;
        AuthToken = authToken;
        CurrentGameTicks = currentGameTicks;
        Tier = tier;
    }

    public string UserId { get; }

    public string ColonyId { get; }

    public string CurrentSnapshotId { get; }

    public string? AuthToken { get; }

    public long CurrentGameTicks { get; }

    public string Tier { get; }
}

public sealed class HireMercenaryGuardWithSnapshotRequest
{
    public HireMercenaryGuardWithSnapshotRequest(
        string idempotencyKey,
        string userId,
        string colonyId,
        string currentSnapshotId,
        string? authToken,
        long currentGameTicks,
        string requestedContractId,
        string tier,
        int expectedPriceSilver,
        float expectedPointRatio,
        SnapshotPackageMetadataDto confirmedSnapshot)
    {
        IdempotencyKey = idempotencyKey;
        UserId = userId;
        ColonyId = colonyId;
        CurrentSnapshotId = currentSnapshotId;
        AuthToken = authToken;
        CurrentGameTicks = currentGameTicks;
        RequestedContractId = requestedContractId;
        Tier = tier;
        ExpectedPriceSilver = expectedPriceSilver;
        ExpectedPointRatio = expectedPointRatio;
        ConfirmedSnapshot = confirmedSnapshot;
    }

    public string IdempotencyKey { get; }

    public string UserId { get; }

    public string ColonyId { get; }

    public string CurrentSnapshotId { get; }

    public string? AuthToken { get; }

    public long CurrentGameTicks { get; }

    public string RequestedContractId { get; }

    public string Tier { get; }

    public int ExpectedPriceSilver { get; }

    public float ExpectedPointRatio { get; }

    public SnapshotPackageMetadataDto ConfirmedSnapshot { get; }
}

public sealed class ReportMercenaryIncidentRequest
{
    public ReportMercenaryIncidentRequest(
        string idempotencyKey,
        string userId,
        string colonyId,
        string currentSnapshotId,
        string? authToken,
        long currentGameTicks,
        string contractId,
        string incidentKind)
    {
        IdempotencyKey = idempotencyKey;
        UserId = userId;
        ColonyId = colonyId;
        CurrentSnapshotId = currentSnapshotId;
        AuthToken = authToken;
        CurrentGameTicks = currentGameTicks;
        ContractId = contractId;
        IncidentKind = incidentKind;
    }

    public string IdempotencyKey { get; }

    public string UserId { get; }

    public string ColonyId { get; }

    public string CurrentSnapshotId { get; }

    public string? AuthToken { get; }

    public long CurrentGameTicks { get; }

    public string ContractId { get; }

    public string IncidentKind { get; }
}

public sealed class MercenaryContractDto
{
    public MercenaryContractDto(
        string contractId,
        string skillDefName,
        int skillLevel,
        int durationDays,
        int priceSilver,
        int harmfulSurgeryFineSilver,
        int deathFineSilver,
        long createdAtGameTicks,
        long expiresAtGameTicks)
    {
        ContractId = contractId;
        SkillDefName = skillDefName;
        SkillLevel = skillLevel;
        DurationDays = durationDays;
        PriceSilver = priceSilver;
        HarmfulSurgeryFineSilver = harmfulSurgeryFineSilver;
        DeathFineSilver = deathFineSilver;
        CreatedAtGameTicks = createdAtGameTicks;
        ExpiresAtGameTicks = expiresAtGameTicks;
    }

    public string ContractId { get; }

    public string SkillDefName { get; }

    public int SkillLevel { get; }

    public int DurationDays { get; }

    public int PriceSilver { get; }

    public int HarmfulSurgeryFineSilver { get; }

    public int DeathFineSilver { get; }

    public long CreatedAtGameTicks { get; }

    public long ExpiresAtGameTicks { get; }
}

public sealed class MercenaryHireResponse
{
    public MercenaryHireResponse(
        ProtocolResponse result,
        MercenaryContractDto? contract,
        BankStatusResponse? bankStatus,
        string? appliedSnapshotId = null,
        string? nextLineageToken = null)
    {
        Result = result;
        Contract = contract;
        BankStatus = bankStatus;
        AppliedSnapshotId = appliedSnapshotId;
        NextLineageToken = nextLineageToken;
    }

    public ProtocolResponse Result { get; }

    public MercenaryContractDto? Contract { get; }

    public BankStatusResponse? BankStatus { get; }

    public string? AppliedSnapshotId { get; }

    public string? NextLineageToken { get; }
}

public sealed class MercenaryGuardContractDto
{
    public MercenaryGuardContractDto(
        string contractId,
        string tier,
        int priceSilver,
        float pointRatio,
        long createdAtGameTicks)
    {
        ContractId = contractId;
        Tier = tier;
        PriceSilver = priceSilver;
        PointRatio = pointRatio;
        CreatedAtGameTicks = createdAtGameTicks;
    }

    public string ContractId { get; }

    public string Tier { get; }

    public int PriceSilver { get; }

    public float PointRatio { get; }

    public long CreatedAtGameTicks { get; }
}

public sealed class MercenaryGuardHireResponse
{
    public MercenaryGuardHireResponse(
        ProtocolResponse result,
        MercenaryGuardContractDto? contract,
        BankStatusResponse? bankStatus,
        string? appliedSnapshotId = null,
        string? nextLineageToken = null)
    {
        Result = result;
        Contract = contract;
        BankStatus = bankStatus;
        AppliedSnapshotId = appliedSnapshotId;
        NextLineageToken = nextLineageToken;
    }

    public ProtocolResponse Result { get; }

    public MercenaryGuardContractDto? Contract { get; }

    public BankStatusResponse? BankStatus { get; }

    public string? AppliedSnapshotId { get; }

    public string? NextLineageToken { get; }
}

public sealed class MercenaryQuoteResponse
{
    public MercenaryQuoteResponse(
        ProtocolResponse result,
        int skillLevel,
        int durationDays,
        int priceSilver,
        int harmfulSurgeryFineSilver,
        int deathFineSilver,
        BankStatusResponse? bankStatus)
    {
        Result = result;
        SkillLevel = skillLevel;
        DurationDays = durationDays;
        PriceSilver = priceSilver;
        HarmfulSurgeryFineSilver = harmfulSurgeryFineSilver;
        DeathFineSilver = deathFineSilver;
        BankStatus = bankStatus;
    }

    public ProtocolResponse Result { get; }

    public int SkillLevel { get; }

    public int DurationDays { get; }

    public int PriceSilver { get; }

    public int HarmfulSurgeryFineSilver { get; }

    public int DeathFineSilver { get; }

    public BankStatusResponse? BankStatus { get; }
}

public sealed class MercenaryGuardQuoteResponse
{
    public MercenaryGuardQuoteResponse(
        ProtocolResponse result,
        string tier,
        int priceSilver,
        float pointRatio,
        BankStatusResponse? bankStatus)
    {
        Result = result;
        Tier = tier;
        PriceSilver = priceSilver;
        PointRatio = pointRatio;
        BankStatus = bankStatus;
    }

    public ProtocolResponse Result { get; }

    public string Tier { get; }

    public int PriceSilver { get; }

    public float PointRatio { get; }

    public BankStatusResponse? BankStatus { get; }
}

public sealed class MercenaryIncidentResponse
{
    public MercenaryIncidentResponse(
        ProtocolResponse result,
        BankDebtSummaryDto? debt,
        BankStatusResponse? bankStatus)
    {
        Result = result;
        Debt = debt;
        BankStatus = bankStatus;
    }

    public ProtocolResponse Result { get; }

    public BankDebtSummaryDto? Debt { get; }

    public BankStatusResponse? BankStatus { get; }
}
