using AIRsLight.ClashOfRim.Compatibility;
using System.Runtime.Serialization;
using System.Collections.Generic;

namespace AIRsLight.ClashOfRim.ClientNetwork;

[DataContract]
public sealed class ModGetAdminBaselineRequirementsRequestDto
{
    [DataMember(Name = "protocolVersion")]
    public string ProtocolVersion { get; set; } = ClashOfRimVersion.ProtocolVersion;

    [DataMember(Name = "userId")]
    public string UserId { get; set; } = string.Empty;

    [DataMember(Name = "colonyId")]
    public string? ColonyId { get; set; }
}

[DataContract]
public sealed class ModGetAdminBaselineRequirementsResponseDto
{
    [DataMember(Name = "result")]
    public ModProtocolResponseDto? Result { get; set; }

    [DataMember(Name = "isAdministrator")]
    public bool IsAdministrator { get; set; }

    [DataMember(Name = "baselineConfigured")]
    public bool BaselineConfigured { get; set; }

    [DataMember(Name = "administratorUserId")]
    public string? AdministratorUserId { get; set; }

    [DataMember(Name = "baselineExtensions")]
    public List<ModAdminBaselineExtensionRequirementDto> BaselineExtensions { get; set; } = new();
}

[DataContract]
public sealed class ModAdminBaselineExtensionRequirementDto
{
    [DataMember(Name = "providerId")]
    public string ProviderId { get; set; } = string.Empty;

    [DataMember(Name = "kind")]
    public string Kind { get; set; } = string.Empty;

    [DataMember(Name = "requiredPackageId")]
    public string? RequiredPackageId { get; set; }

    [DataMember(Name = "displayName")]
    public string? DisplayName { get; set; }
}

[DataContract]
public sealed class ModSubmitAdminBaselineRequestDto
{
    [DataMember(Name = "protocolVersion")]
    public string ProtocolVersion { get; set; } = ClashOfRimVersion.ProtocolVersion;

    [DataMember(Name = "userId")]
    public string UserId { get; set; } = string.Empty;

    [DataMember(Name = "colonyId")]
    public string? ColonyId { get; set; }

    [DataMember(Name = "generatedAtUtc")]
    public string GeneratedAtUtc { get; set; } = string.Empty;

    [DataMember(Name = "standardMarketValues")]
    public List<ModStandardMarketValueDto> StandardMarketValues { get; set; } = new();

    [DataMember(Name = "trapClassifications")]
    public List<ModTrapClassificationDto> TrapClassifications { get; set; } = new();

    [DataMember(Name = "packableBuildings")]
    public List<ModPackableBuildingDto> PackableBuildings { get; set; } = new();

    [DataMember(Name = "buildings")]
    public List<ModBuildingBaselineDto> Buildings { get; set; } = new();

    [DataMember(Name = "baselineExtensions")]
    public List<ModAdminBaselineExtensionDto> BaselineExtensions { get; set; } = new();

    [DataMember(Name = "stuffHitPointModifiers")]
    public List<ModStuffHitPointModifierDto> StuffHitPointModifiers { get; set; } = new();

    [DataMember(Name = "stuffMarketValues")]
    public List<ModStuffMarketValueDto> StuffMarketValues { get; set; } = new();

    [DataMember(Name = "qualityMarketValueModifiers")]
    public List<ModQualityMarketValueModifierDto> QualityMarketValueModifiers { get; set; } = new();
}

[DataContract]
public sealed class ModSubmitAdminBaselineResponseDto
{
    [DataMember(Name = "result")]
    public ModProtocolResponseDto? Result { get; set; }

    [DataMember(Name = "isAdministrator")]
    public bool IsAdministrator { get; set; }

    [DataMember(Name = "baselineConfigured")]
    public bool BaselineConfigured { get; set; }

    [DataMember(Name = "administratorUserId")]
    public string? AdministratorUserId { get; set; }

    [DataMember(Name = "standardMarketValueCount")]
    public int StandardMarketValueCount { get; set; }

    [DataMember(Name = "trapAutoApprovedCount")]
    public int TrapAutoApprovedCount { get; set; }

    [DataMember(Name = "trapCandidateCount")]
    public int TrapCandidateCount { get; set; }

    [DataMember(Name = "trapApprovedCount")]
    public int TrapApprovedCount { get; set; }

    [DataMember(Name = "packableBuildingCount")]
    public int PackableBuildingCount { get; set; }

    [DataMember(Name = "buildingCount")]
    public int BuildingCount { get; set; }

    [DataMember(Name = "baselineExtensionCount")]
    public int BaselineExtensionCount { get; set; }
}

[DataContract]
public sealed class ModStandardMarketValueDto
{
    [DataMember(Name = "defName")]
    public string DefName { get; set; } = string.Empty;

    [DataMember(Name = "marketValue")]
    public float MarketValue { get; set; }
}

[DataContract]
public sealed class ModStuffMarketValueDto
{
    [DataMember(Name = "thingDefName")]
    public string ThingDefName { get; set; } = string.Empty;

    [DataMember(Name = "stuffDefName")]
    public string StuffDefName { get; set; } = string.Empty;

    [DataMember(Name = "marketValue")]
    public float MarketValue { get; set; }
}

[DataContract]
public sealed class ModQualityMarketValueModifierDto
{
    [DataMember(Name = "quality")]
    public string Quality { get; set; } = string.Empty;

    [DataMember(Name = "factor")]
    public float Factor { get; set; }

    [DataMember(Name = "maxGain")]
    public float MaxGain { get; set; }
}

[DataContract]
public sealed class ModTrapClassificationDto
{
    [DataMember(Name = "defName")]
    public string DefName { get; set; } = string.Empty;

    [DataMember(Name = "thingClass")]
    public string? ThingClass { get; set; }

    [DataMember(Name = "modPackageId")]
    public string? ModPackageId { get; set; }

    [DataMember(Name = "modName")]
    public string? ModName { get; set; }

    [DataMember(Name = "scanStatus")]
    public string ScanStatus { get; set; } = string.Empty;

    [DataMember(Name = "scanReason")]
    public string ScanReason { get; set; } = string.Empty;

    [DataMember(Name = "inheritsBuildingTrap")]
    public bool InheritsBuildingTrap { get; set; }

    [DataMember(Name = "adminApproved")]
    public bool AdminApproved { get; set; }
}

[DataContract]
public sealed class ModPackableBuildingDto
{
    [DataMember(Name = "defName")]
    public string DefName { get; set; } = string.Empty;

    [DataMember(Name = "minifiedDefName")]
    public string? MinifiedDefName { get; set; }

    [DataMember(Name = "label")]
    public string? Label { get; set; }

    [DataMember(Name = "modPackageId")]
    public string? ModPackageId { get; set; }

    [DataMember(Name = "modName")]
    public string? ModName { get; set; }
}

[DataContract]
public sealed class ModBuildingBaselineDto
{
    [DataMember(Name = "defName")]
    public string DefName { get; set; } = string.Empty;

    [DataMember(Name = "label")]
    public string? Label { get; set; }

    [DataMember(Name = "modPackageId")]
    public string? ModPackageId { get; set; }

    [DataMember(Name = "modName")]
    public string? ModName { get; set; }

    [DataMember(Name = "useHitPoints")]
    public bool UseHitPoints { get; set; }

    [DataMember(Name = "estimatedMaxHitPoints")]
    public int EstimatedMaxHitPoints { get; set; }

    [DataMember(Name = "minifiable")]
    public bool Minifiable { get; set; }

    [DataMember(Name = "minifiedDefName")]
    public string? MinifiedDefName { get; set; }
}

[DataContract]
public sealed class ModAdminBaselineExtensionDto
{
    [DataMember(Name = "providerId")]
    public string ProviderId { get; set; } = string.Empty;

    [DataMember(Name = "kind")]
    public string Kind { get; set; } = string.Empty;

    [DataMember(Name = "records")]
    public List<ModAdminBaselineExtensionRecordDto> Records { get; set; } = new();
}

public sealed class ModAdminBaselineExtensionRecordDto
{
    [DataMember(Name = "key")]
    public string Key { get; set; } = string.Empty;

    [DataMember(Name = "values")]
    public Dictionary<string, string> Values { get; set; } = new();
}

[DataContract]
public sealed class ModStuffHitPointModifierDto
{
    [DataMember(Name = "defName")]
    public string DefName { get; set; } = string.Empty;

    [DataMember(Name = "label")]
    public string? Label { get; set; }

    [DataMember(Name = "modPackageId")]
    public string? ModPackageId { get; set; }

    [DataMember(Name = "modName")]
    public string? ModName { get; set; }

    [DataMember(Name = "maxHitPointsFactor")]
    public float MaxHitPointsFactor { get; set; }

    [DataMember(Name = "maxHitPointsOffset")]
    public float MaxHitPointsOffset { get; set; }
}

[DataContract]
public sealed class ModAdminStatusRequestDto
{
    [DataMember(Name = "userId")]
    public string UserId { get; set; } = string.Empty;

    [DataMember(Name = "colonyId")]
    public string ColonyId { get; set; } = string.Empty;

    [DataMember(Name = "authToken")]
    public string? AuthToken { get; set; }
}

[DataContract]
public sealed class ModAdminStatusResponseDto
{
    [DataMember(Name = "result")]
    public ModProtocolResponseDto? Result { get; set; }

    [DataMember(Name = "isAdministrator")]
    public bool IsAdministrator { get; set; }

    [DataMember(Name = "configuration")]
    public ModAdminConfigurationDto? Configuration { get; set; }

    [DataMember(Name = "players")]
    public List<ModAdminPlayerSummaryDto> Players { get; set; } = new();

    [DataMember(Name = "maintenanceLoginLocked")]
    public bool MaintenanceLoginLocked { get; set; }

    [DataMember(Name = "maintenanceReason")]
    public string? MaintenanceReason { get; set; }

    [DataMember(Name = "auditRecords")]
    public List<ModAdminAuditRecordDto> AuditRecords { get; set; } = new();
}

[DataContract]
public sealed class ModAdminUpdateConfigurationRequestDto
{
    [DataMember(Name = "userId")]
    public string UserId { get; set; } = string.Empty;

    [DataMember(Name = "colonyId")]
    public string ColonyId { get; set; } = string.Empty;

    [DataMember(Name = "authToken")]
    public string? AuthToken { get; set; }

    [DataMember(Name = "configuration")]
    public ModAdminConfigurationDto? Configuration { get; set; }
}

[DataContract]
public sealed class ModAdminUpdateConfigurationResponseDto
{
    [DataMember(Name = "result")]
    public ModProtocolResponseDto? Result { get; set; }

    [DataMember(Name = "configuration")]
    public ModAdminConfigurationDto? Configuration { get; set; }

    [DataMember(Name = "updatedAtUtc")]
    public string? UpdatedAtUtc { get; set; }
}

[DataContract]
public sealed class ModAdminActionRequestDto
{
    [DataMember(Name = "userId")]
    public string UserId { get; set; } = string.Empty;

    [DataMember(Name = "colonyId")]
    public string ColonyId { get; set; } = string.Empty;

    [DataMember(Name = "authToken")]
    public string? AuthToken { get; set; }

    [DataMember(Name = "actionKind")]
    public string ActionKind { get; set; } = string.Empty;

    [DataMember(Name = "targetUserId")]
    public string? TargetUserId { get; set; }

    [DataMember(Name = "targetColonyId")]
    public string? TargetColonyId { get; set; }

    [DataMember(Name = "message")]
    public string? Message { get; set; }

    [DataMember(Name = "notificationSeverity")]
    public string? NotificationSeverity { get; set; }

    [DataMember(Name = "persistentNotification")]
    public bool PersistentNotification { get; set; } = true;
}

[DataContract]
public sealed class ModAdminActionResponseDto
{
    [DataMember(Name = "result")]
    public ModProtocolResponseDto? Result { get; set; }

    [DataMember(Name = "actionKind")]
    public string ActionKind { get; set; } = string.Empty;

    [DataMember(Name = "targetUserId")]
    public string? TargetUserId { get; set; }

    [DataMember(Name = "maintenanceLoginLocked")]
    public bool MaintenanceLoginLocked { get; set; }

    [DataMember(Name = "auditRecord")]
    public ModAdminAuditRecordDto? AuditRecord { get; set; }

    [DataMember(Name = "affectedOnlineUsers")]
    public int AffectedOnlineUsers { get; set; }
}

[DataContract]
public sealed class ModAdminConfigurationDto
{
    [DataMember(Name = "tradeMarketplaceEnabled")]
    public bool TradeMarketplaceEnabled { get; set; } = true;

    [DataMember(Name = "tradeOrderExpirationDays")]
    public int TradeOrderExpirationDays { get; set; }

    [DataMember(Name = "maxOpenTradeOrdersPerOwner")]
    public int MaxOpenTradeOrdersPerOwner { get; set; }

    [DataMember(Name = "tradePostageBaseSilver")]
    public int TradePostageBaseSilver { get; set; }

    [DataMember(Name = "tradePostageSilverPerTile")]
    public int TradePostageSilverPerTile { get; set; }

    [DataMember(Name = "tradePostageCrossLayerOverheadDistanceTiles")]
    public int TradePostageCrossLayerOverheadDistanceTiles { get; set; }

    [DataMember(Name = "tradeBaseFeeRate")]
    public float TradeBaseFeeRate { get; set; }

    [DataMember(Name = "tradeFeeStrategy")]
    public string TradeFeeStrategy { get; set; } = "Publisher";

    [DataMember(Name = "diplomacyRelationChangeCooldownHours")]
    public double DiplomacyRelationChangeCooldownHours { get; set; }

    [DataMember(Name = "diplomacySupportRequestCooldownMinutes")]
    public double DiplomacySupportRequestCooldownMinutes { get; set; }

    [DataMember(Name = "forcedGiftDeliveryCooldownMinutes")]
    public double ForcedGiftDeliveryCooldownMinutes { get; set; }

    [DataMember(Name = "giftsEnabled")]
    public bool GiftsEnabled { get; set; } = true;

    [DataMember(Name = "bankLoansEnabled")]
    public bool BankLoansEnabled { get; set; } = true;

    [DataMember(Name = "bankMinLoanSilver")]
    public int BankMinLoanSilver { get; set; }

    [DataMember(Name = "bankMaxLoanSilver")]
    public int BankMaxLoanSilver { get; set; }

    [DataMember(Name = "bankMaxLoanWealthRatio")]
    public float BankMaxLoanWealthRatio { get; set; }

    [DataMember(Name = "bankBaseAnnualInterestRate")]
    public float BankBaseAnnualInterestRate { get; set; }

    [DataMember(Name = "bankMinDurationDays")]
    public int BankMinDurationDays { get; set; }

    [DataMember(Name = "bankMaxDurationDays")]
    public int BankMaxDurationDays { get; set; }

    [DataMember(Name = "bankInterestDurationMultiplierCurve")]
    public string BankInterestDurationMultiplierCurve { get; set; } = string.Empty;

    [DataMember(Name = "bankPenaltyIntervalDays")]
    public int BankPenaltyIntervalDays { get; set; }

    [DataMember(Name = "bankPenaltyRaidPointsPerSilver")]
    public float BankPenaltyRaidPointsPerSilver { get; set; }

    [DataMember(Name = "bankOverduePenaltyStages")]
    public string BankOverduePenaltyStages { get; set; } = string.Empty;

    [DataMember(Name = "mercenariesEnabled")]
    public bool MercenariesEnabled { get; set; } = true;

    [DataMember(Name = "mercenaryApprenticeDailySilver")]
    public int MercenaryApprenticeDailySilver { get; set; }

    [DataMember(Name = "mercenarySkilledDailySilver")]
    public int MercenarySkilledDailySilver { get; set; }

    [DataMember(Name = "mercenaryMasterDailySilver")]
    public int MercenaryMasterDailySilver { get; set; }

    [DataMember(Name = "mercenaryMinDurationDays")]
    public int MercenaryMinDurationDays { get; set; }

    [DataMember(Name = "mercenaryMaxDurationDays")]
    public int MercenaryMaxDurationDays { get; set; }

    [DataMember(Name = "mercenaryDurationMultiplierCurve")]
    public string MercenaryDurationMultiplierCurve { get; set; } = string.Empty;

    [DataMember(Name = "maxActiveMercenariesPerColony")]
    public int MaxActiveMercenariesPerColony { get; set; }

    [DataMember(Name = "mercenaryHarmfulSurgeryFineSilver")]
    public int MercenaryHarmfulSurgeryFineSilver { get; set; }

    [DataMember(Name = "mercenaryApprenticeDeathFineSilver")]
    public int MercenaryApprenticeDeathFineSilver { get; set; }

    [DataMember(Name = "mercenarySkilledDeathFineSilver")]
    public int MercenarySkilledDeathFineSilver { get; set; }

    [DataMember(Name = "mercenaryMasterDeathFineSilver")]
    public int MercenaryMasterDeathFineSilver { get; set; }

    [DataMember(Name = "mercenaryGuardsEnabled")]
    public bool MercenaryGuardsEnabled { get; set; } = true;

    [DataMember(Name = "mercenaryGuardApprenticeSilver")]
    public int MercenaryGuardApprenticeSilver { get; set; }

    [DataMember(Name = "mercenaryGuardSkilledSilver")]
    public int MercenaryGuardSkilledSilver { get; set; }

    [DataMember(Name = "mercenaryGuardMasterSilver")]
    public int MercenaryGuardMasterSilver { get; set; }

    [DataMember(Name = "mercenaryGuardApprenticePointsRatio")]
    public float MercenaryGuardApprenticePointsRatio { get; set; }

    [DataMember(Name = "mercenaryGuardSkilledPointsRatio")]
    public float MercenaryGuardSkilledPointsRatio { get; set; }

    [DataMember(Name = "mercenaryGuardMasterPointsRatio")]
    public float MercenaryGuardMasterPointsRatio { get; set; }

    [DataMember(Name = "raidProtectionHours")]
    public double RaidProtectionHours { get; set; }

    [DataMember(Name = "raidMaxDurationMinutes")]
    public double RaidMaxDurationMinutes { get; set; }

    [DataMember(Name = "raidTimeoutGraceMinutes")]
    public double RaidTimeoutGraceMinutes { get; set; }

    [DataMember(Name = "pvpEnabled")]
    public bool PvpEnabled { get; set; } = true;

    [DataMember(Name = "raidMinimumDefenderWealth")]
    public int RaidMinimumDefenderWealth { get; set; }

    [DataMember(Name = "raidSettlementLossRatio")]
    public double RaidSettlementLossRatio { get; set; }

    [DataMember(Name = "raidSettlementBuildingHitPointsLossRatio")]
    public double RaidSettlementBuildingHitPointsLossRatio { get; set; }

    [DataMember(Name = "raidSettlementMinimumRemainingHitPointsRatio")]
    public double RaidSettlementMinimumRemainingHitPointsRatio { get; set; }

    [DataMember(Name = "pendingConfirmationTimeoutMinutes")]
    public double PendingConfirmationTimeoutMinutes { get; set; }

    [DataMember(Name = "fixedTradeFees")]
    public List<ModAdminFixedTradeFeeDto> FixedTradeFees { get; set; } = new();

    [DataMember(Name = "compatibilityMods")]
    public List<ModAdminCompatibilityModDto> CompatibilityMods { get; set; } = new();
}

[DataContract]
public sealed class ModAdminFixedTradeFeeDto
{
    [DataMember(Name = "thingDefName")]
    public string ThingDefName { get; set; } = string.Empty;

    [DataMember(Name = "silverPerUnit")]
    public int SilverPerUnit { get; set; }
}

[DataContract]
public sealed class ModAdminCompatibilityModDto
{
    [DataMember(Name = "packageId")]
    public string PackageId { get; set; } = string.Empty;

    [DataMember(Name = "name")]
    public string Name { get; set; } = string.Empty;

    [DataMember(Name = "loadOrder")]
    public int LoadOrder { get; set; }

    [DataMember(Name = "role")]
    public string Role { get; set; } = "Required";

    [DataMember(Name = "configs")]
    public List<ModAdminCompatibilityConfigDto> Configs { get; set; } = new();
}

[DataContract]
public sealed class ModAdminCompatibilityConfigDto
{
    [DataMember(Name = "fileName")]
    public string FileName { get; set; } = string.Empty;

    [DataMember(Name = "mode")]
    public string Mode { get; set; } = "Enforce";
}

[DataContract]
public sealed class ModAdminPlayerSummaryDto
{
    [DataMember(Name = "userId")]
    public string UserId { get; set; } = string.Empty;

    [DataMember(Name = "colonyId")]
    public string ColonyId { get; set; } = string.Empty;

    [DataMember(Name = "currentSnapshotId")]
    public string? CurrentSnapshotId { get; set; }

    [DataMember(Name = "online")]
    public bool Online { get; set; }

    [DataMember(Name = "lastSeenAtUtc")]
    public string? LastSeenAtUtc { get; set; }

    [DataMember(Name = "displayName")]
    public string? DisplayName { get; set; }

    [DataMember(Name = "isAdministrator")]
    public bool IsAdministrator { get; set; }

    [DataMember(Name = "isBanned")]
    public bool IsBanned { get; set; }
}

[DataContract]
public sealed class ModAdminAuditRecordDto
{
    [DataMember(Name = "actionKind")]
    public string ActionKind { get; set; } = string.Empty;

    [DataMember(Name = "actorUserId")]
    public string ActorUserId { get; set; } = string.Empty;

    [DataMember(Name = "targetUserId")]
    public string? TargetUserId { get; set; }

    [DataMember(Name = "message")]
    public string? Message { get; set; }

    [DataMember(Name = "createdAtUtc")]
    public string? CreatedAtUtc { get; set; }
}
