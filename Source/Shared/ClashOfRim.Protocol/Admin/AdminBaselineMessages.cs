using System;
using System.Collections.Generic;

namespace AIRsLight.ClashOfRim.Protocol;

public sealed class GetAdminBaselineRequirementsRequest
{
    public GetAdminBaselineRequirementsRequest(
        string protocolVersion,
        string userId,
        string? colonyId = null)
    {
        ProtocolVersion = protocolVersion;
        UserId = userId;
        ColonyId = colonyId;
    }

    public string ProtocolVersion { get; }

    public string UserId { get; }

    public string? ColonyId { get; }
}

public sealed class GetAdminBaselineRequirementsResponse
{
    public GetAdminBaselineRequirementsResponse(
        ProtocolResponse result,
        bool isAdministrator,
        bool baselineConfigured,
        string? administratorUserId,
        IReadOnlyList<AdminBaselineExtensionRequirementDto>? baselineExtensions = null)
    {
        Result = result;
        IsAdministrator = isAdministrator;
        BaselineConfigured = baselineConfigured;
        AdministratorUserId = administratorUserId;
        BaselineExtensions = baselineExtensions ?? Array.Empty<AdminBaselineExtensionRequirementDto>();
    }

    public ProtocolResponse Result { get; }

    public bool IsAdministrator { get; }

    public bool BaselineConfigured { get; }

    public string? AdministratorUserId { get; }

    public IReadOnlyList<AdminBaselineExtensionRequirementDto> BaselineExtensions { get; }
}

public sealed class AdminBaselineExtensionRequirementDto
{
    public AdminBaselineExtensionRequirementDto(
        string providerId,
        string kind,
        string? requiredPackageId = null,
        string? displayName = null)
    {
        ProviderId = providerId;
        Kind = kind;
        RequiredPackageId = requiredPackageId;
        DisplayName = displayName;
    }

    public string ProviderId { get; }

    public string Kind { get; }

    public string? RequiredPackageId { get; }

    public string? DisplayName { get; }
}

public sealed class SubmitAdminBaselineRequest
{
    public SubmitAdminBaselineRequest(
        string protocolVersion,
        string userId,
        string? colonyId,
        DateTimeOffset generatedAtUtc,
        IReadOnlyList<StandardMarketValueDto>? standardMarketValues,
        IReadOnlyList<TrapClassificationDto>? trapClassifications,
        IReadOnlyList<PackableBuildingDto>? packableBuildings = null,
        IReadOnlyList<BuildingBaselineDto>? buildings = null,
        IReadOnlyList<AdminBaselineExtensionDto>? baselineExtensions = null,
        IReadOnlyList<StuffHitPointModifierDto>? stuffHitPointModifiers = null,
        IReadOnlyList<StuffMarketValueDto>? stuffMarketValues = null,
        IReadOnlyList<QualityMarketValueModifierDto>? qualityMarketValueModifiers = null)
    {
        ProtocolVersion = protocolVersion;
        UserId = userId;
        ColonyId = colonyId;
        GeneratedAtUtc = generatedAtUtc;
        StandardMarketValues = standardMarketValues ?? Array.Empty<StandardMarketValueDto>();
        TrapClassifications = trapClassifications ?? Array.Empty<TrapClassificationDto>();
        PackableBuildings = packableBuildings ?? Array.Empty<PackableBuildingDto>();
        Buildings = buildings ?? Array.Empty<BuildingBaselineDto>();
        BaselineExtensions = baselineExtensions ?? Array.Empty<AdminBaselineExtensionDto>();
        StuffHitPointModifiers = stuffHitPointModifiers ?? Array.Empty<StuffHitPointModifierDto>();
        StuffMarketValues = stuffMarketValues ?? Array.Empty<StuffMarketValueDto>();
        QualityMarketValueModifiers = qualityMarketValueModifiers ?? Array.Empty<QualityMarketValueModifierDto>();
    }

    public string ProtocolVersion { get; }

    public string UserId { get; }

    public string? ColonyId { get; }

    public DateTimeOffset GeneratedAtUtc { get; }

    public IReadOnlyList<StandardMarketValueDto> StandardMarketValues { get; }

    public IReadOnlyList<TrapClassificationDto> TrapClassifications { get; }

    public IReadOnlyList<PackableBuildingDto> PackableBuildings { get; }

    public IReadOnlyList<BuildingBaselineDto> Buildings { get; }

    public IReadOnlyList<AdminBaselineExtensionDto> BaselineExtensions { get; }

    public IReadOnlyList<StuffHitPointModifierDto> StuffHitPointModifiers { get; }

    public IReadOnlyList<StuffMarketValueDto> StuffMarketValues { get; }

    public IReadOnlyList<QualityMarketValueModifierDto> QualityMarketValueModifiers { get; }
}

public sealed class SubmitAdminBaselineResponse
{
    public SubmitAdminBaselineResponse(
        ProtocolResponse result,
        bool isAdministrator,
        bool baselineConfigured,
        string? administratorUserId,
        int standardMarketValueCount,
        int trapAutoApprovedCount,
        int trapCandidateCount,
        int trapApprovedCount,
        int packableBuildingCount = 0,
        int buildingCount = 0,
        int baselineExtensionCount = 0)
    {
        Result = result;
        IsAdministrator = isAdministrator;
        BaselineConfigured = baselineConfigured;
        AdministratorUserId = administratorUserId;
        StandardMarketValueCount = standardMarketValueCount;
        TrapAutoApprovedCount = trapAutoApprovedCount;
        TrapCandidateCount = trapCandidateCount;
        TrapApprovedCount = trapApprovedCount;
        PackableBuildingCount = packableBuildingCount;
        BuildingCount = buildingCount;
        BaselineExtensionCount = baselineExtensionCount;
    }

    public ProtocolResponse Result { get; }

    public bool IsAdministrator { get; }

    public bool BaselineConfigured { get; }

    public string? AdministratorUserId { get; }

    public int StandardMarketValueCount { get; }

    public int TrapAutoApprovedCount { get; }

    public int TrapCandidateCount { get; }

    public int TrapApprovedCount { get; }

    public int PackableBuildingCount { get; }

    public int BuildingCount { get; }

    public int BaselineExtensionCount { get; }
}

public sealed class StandardMarketValueDto
{
    public StandardMarketValueDto(string defName, float marketValue)
    {
        DefName = defName;
        MarketValue = marketValue;
    }

    public string DefName { get; }

    public float MarketValue { get; }
}

public sealed class StuffMarketValueDto
{
    public StuffMarketValueDto(string thingDefName, string stuffDefName, float marketValue)
    {
        ThingDefName = thingDefName;
        StuffDefName = stuffDefName;
        MarketValue = marketValue;
    }

    public string ThingDefName { get; }

    public string StuffDefName { get; }

    public float MarketValue { get; }
}

public sealed class QualityMarketValueModifierDto
{
    public QualityMarketValueModifierDto(string quality, float factor, float maxGain)
    {
        Quality = quality;
        Factor = factor;
        MaxGain = maxGain;
    }

    public string Quality { get; }

    public float Factor { get; }

    public float MaxGain { get; }
}

public sealed class TrapClassificationDto
{
    public TrapClassificationDto(
        string defName,
        string? thingClass,
        string? modPackageId,
        string? modName,
        string scanStatus,
        string scanReason,
        bool inheritsBuildingTrap,
        bool adminApproved)
    {
        DefName = defName;
        ThingClass = thingClass;
        ModPackageId = modPackageId;
        ModName = modName;
        ScanStatus = scanStatus;
        ScanReason = scanReason;
        InheritsBuildingTrap = inheritsBuildingTrap;
        AdminApproved = adminApproved;
    }

    public string DefName { get; }

    public string? ThingClass { get; }

    public string? ModPackageId { get; }

    public string? ModName { get; }

    public string ScanStatus { get; }

    public string ScanReason { get; }

    public bool InheritsBuildingTrap { get; }

    public bool AdminApproved { get; }

    public bool IncludedInApprovedManifest => InheritsBuildingTrap || AdminApproved;
}

public sealed class PackableBuildingDto
{
    public PackableBuildingDto(
        string defName,
        string? minifiedDefName,
        string? label,
        string? modPackageId,
        string? modName)
    {
        DefName = defName;
        MinifiedDefName = minifiedDefName;
        Label = label;
        ModPackageId = modPackageId;
        ModName = modName;
    }

    public string DefName { get; }

    public string? MinifiedDefName { get; }

    public string? Label { get; }

    public string? ModPackageId { get; }

    public string? ModName { get; }
}

public sealed class BuildingBaselineDto
{
    public BuildingBaselineDto(
        string defName,
        string? label,
        string? modPackageId,
        string? modName,
        bool useHitPoints,
        int estimatedMaxHitPoints,
        bool minifiable,
        string? minifiedDefName)
    {
        DefName = defName;
        Label = label;
        ModPackageId = modPackageId;
        ModName = modName;
        UseHitPoints = useHitPoints;
        EstimatedMaxHitPoints = estimatedMaxHitPoints;
        Minifiable = minifiable;
        MinifiedDefName = minifiedDefName;
    }

    public string DefName { get; }

    public string? Label { get; }

    public string? ModPackageId { get; }

    public string? ModName { get; }

    public bool UseHitPoints { get; }

    public int EstimatedMaxHitPoints { get; }

    public bool Minifiable { get; }

    public string? MinifiedDefName { get; }
}

public sealed class AdminBaselineExtensionDto
{
    public AdminBaselineExtensionDto(
        string providerId,
        string kind,
        IReadOnlyList<AdminBaselineExtensionRecordDto>? records = null)
    {
        ProviderId = providerId;
        Kind = kind;
        Records = records ?? Array.Empty<AdminBaselineExtensionRecordDto>();
    }

    public string ProviderId { get; }

    public string Kind { get; }

    public IReadOnlyList<AdminBaselineExtensionRecordDto> Records { get; }
}

public sealed class AdminBaselineExtensionRecordDto
{
    public AdminBaselineExtensionRecordDto(
        string key,
        IReadOnlyDictionary<string, string>? values = null)
    {
        Key = key;
        Values = values ?? new Dictionary<string, string>(StringComparer.Ordinal);
    }

    public string Key { get; }

    public IReadOnlyDictionary<string, string> Values { get; }
}

public sealed class StuffHitPointModifierDto
{
    public StuffHitPointModifierDto(
        string defName,
        string? label,
        string? modPackageId,
        string? modName,
        float maxHitPointsFactor,
        float maxHitPointsOffset)
    {
        DefName = defName;
        Label = label;
        ModPackageId = modPackageId;
        ModName = modName;
        MaxHitPointsFactor = maxHitPointsFactor;
        MaxHitPointsOffset = maxHitPointsOffset;
    }

    public string DefName { get; }

    public string? Label { get; }

    public string? ModPackageId { get; }

    public string? ModName { get; }

    public float MaxHitPointsFactor { get; }

    public float MaxHitPointsOffset { get; }
}
