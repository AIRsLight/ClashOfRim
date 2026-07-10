using System;
using System.Collections.Generic;

namespace AIRsLight.ClashOfRim.Protocol;

public sealed class AdminStatusRequest
{
    public AdminStatusRequest(string userId, string colonyId, string? authToken)
    {
        UserId = userId;
        ColonyId = colonyId;
        AuthToken = authToken;
    }

    public string UserId { get; }

    public string ColonyId { get; }

    public string? AuthToken { get; }
}

public sealed class AdminStatusResponse
{
    public AdminStatusResponse(
        ProtocolResponse result,
        bool isAdministrator,
        AdminConfigurationDto? configuration,
        IReadOnlyList<AdminPlayerSummaryDto>? players,
        bool maintenanceLoginLocked,
        string? maintenanceReason,
        IReadOnlyList<AdminAuditRecordDto>? auditRecords)
    {
        Result = result;
        IsAdministrator = isAdministrator;
        Configuration = configuration;
        Players = players ?? Array.Empty<AdminPlayerSummaryDto>();
        MaintenanceLoginLocked = maintenanceLoginLocked;
        MaintenanceReason = maintenanceReason;
        AuditRecords = auditRecords ?? Array.Empty<AdminAuditRecordDto>();
    }

    public ProtocolResponse Result { get; }

    public bool IsAdministrator { get; }

    public AdminConfigurationDto? Configuration { get; }

    public IReadOnlyList<AdminPlayerSummaryDto> Players { get; }

    public bool MaintenanceLoginLocked { get; }

    public string? MaintenanceReason { get; }

    public IReadOnlyList<AdminAuditRecordDto> AuditRecords { get; }
}

public sealed class AdminUpdateConfigurationRequest
{
    public AdminUpdateConfigurationRequest(
        string userId,
        string colonyId,
        string? authToken,
        AdminConfigurationDto? configuration)
    {
        UserId = userId;
        ColonyId = colonyId;
        AuthToken = authToken;
        Configuration = configuration;
    }

    public string UserId { get; }

    public string ColonyId { get; }

    public string? AuthToken { get; }

    public AdminConfigurationDto? Configuration { get; }
}

public sealed class AdminUpdateConfigurationResponse
{
    public AdminUpdateConfigurationResponse(
        ProtocolResponse result,
        AdminConfigurationDto? configuration,
        DateTimeOffset? updatedAtUtc)
    {
        Result = result;
        Configuration = configuration;
        UpdatedAtUtc = updatedAtUtc;
    }

    public ProtocolResponse Result { get; }

    public AdminConfigurationDto? Configuration { get; }

    public DateTimeOffset? UpdatedAtUtc { get; }
}

public sealed class AdminActionRequest
{
    public AdminActionRequest(
        string userId,
        string colonyId,
        string? authToken,
        string actionKind,
        string? targetUserId,
        string? targetColonyId,
        string? message,
        string? notificationSeverity = null,
        bool persistentNotification = true)
    {
        UserId = userId;
        ColonyId = colonyId;
        AuthToken = authToken;
        ActionKind = actionKind;
        TargetUserId = targetUserId;
        TargetColonyId = targetColonyId;
        Message = message;
        NotificationSeverity = notificationSeverity;
        PersistentNotification = persistentNotification;
    }

    public string UserId { get; }

    public string ColonyId { get; }

    public string? AuthToken { get; }

    public string ActionKind { get; }

    public string? TargetUserId { get; }

    public string? TargetColonyId { get; }

    public string? Message { get; }

    public string? NotificationSeverity { get; }

    public bool PersistentNotification { get; }
}

public sealed class AdminActionResponse
{
    public AdminActionResponse(
        ProtocolResponse result,
        string actionKind,
        string? targetUserId,
        bool maintenanceLoginLocked,
        AdminAuditRecordDto? auditRecord,
        int affectedOnlineUsers)
    {
        Result = result;
        ActionKind = actionKind;
        TargetUserId = targetUserId;
        MaintenanceLoginLocked = maintenanceLoginLocked;
        AuditRecord = auditRecord;
        AffectedOnlineUsers = affectedOnlineUsers;
    }

    public ProtocolResponse Result { get; }

    public string ActionKind { get; }

    public string? TargetUserId { get; }

    public bool MaintenanceLoginLocked { get; }

    public AdminAuditRecordDto? AuditRecord { get; }

    public int AffectedOnlineUsers { get; }
}

public sealed class AdminConfigurationDto
{
    public AdminConfigurationDto(
        bool tradeMarketplaceEnabled,
        int tradeOrderExpirationDays,
        int maxOpenTradeOrdersPerOwner,
        int tradePostageBaseSilver,
        int tradePostageSilverPerTile,
        int tradePostageCrossLayerOverheadDistanceTiles,
        float tradeBaseFeeRate,
        double diplomacyRelationChangeCooldownHours,
        double diplomacySupportRequestCooldownMinutes,
        double forcedGiftDeliveryCooldownMinutes,
        bool bankLoansEnabled,
        int bankMinLoanSilver,
        int bankMaxLoanSilver,
        float bankMaxLoanWealthRatio,
        float bankBaseAnnualInterestRate,
        int bankMinDurationDays,
        int bankMaxDurationDays,
        string? bankInterestDurationMultiplierCurve,
        int bankPenaltyIntervalDays,
        float bankPenaltyRaidPointsPerSilver,
        string? bankOverduePenaltyStages,
        bool mercenariesEnabled,
        int mercenaryApprenticeDailySilver,
        int mercenarySkilledDailySilver,
        int mercenaryMasterDailySilver,
        int mercenaryMinDurationDays,
        int mercenaryMaxDurationDays,
        string? mercenaryDurationMultiplierCurve,
        int maxActiveMercenariesPerColony,
        int mercenaryHarmfulSurgeryFineSilver,
        int mercenaryApprenticeDeathFineSilver,
        int mercenarySkilledDeathFineSilver,
        int mercenaryMasterDeathFineSilver,
        bool mercenaryGuardsEnabled,
        int mercenaryGuardApprenticeSilver,
        int mercenaryGuardSkilledSilver,
        int mercenaryGuardMasterSilver,
        float mercenaryGuardApprenticePointsRatio,
        float mercenaryGuardSkilledPointsRatio,
        float mercenaryGuardMasterPointsRatio,
        double raidProtectionHours,
        bool pvpEnabled,
        int raidMinimumDefenderWealth,
        double raidSettlementLossRatio,
        double raidSettlementBuildingHitPointsLossRatio,
        double raidSettlementMinimumRemainingHitPointsRatio,
        double pendingConfirmationTimeoutMinutes,
        IReadOnlyList<AdminFixedTradeFeeDto>? fixedTradeFees = null,
        IReadOnlyList<AdminCompatibilityModDto>? compatibilityMods = null,
        double raidMaxDurationMinutes = 15,
        double raidTimeoutGraceMinutes = 2,
        string? tradeFeeStrategy = null,
        bool giftsEnabled = true)
    {
        TradeMarketplaceEnabled = tradeMarketplaceEnabled;
        TradeOrderExpirationDays = tradeOrderExpirationDays;
        MaxOpenTradeOrdersPerOwner = maxOpenTradeOrdersPerOwner;
        TradePostageBaseSilver = tradePostageBaseSilver;
        TradePostageSilverPerTile = tradePostageSilverPerTile;
        TradePostageCrossLayerOverheadDistanceTiles = tradePostageCrossLayerOverheadDistanceTiles;
        TradeBaseFeeRate = tradeBaseFeeRate;
        TradeFeeStrategy = TradeFeeStrategies.Normalize(tradeFeeStrategy);
        DiplomacyRelationChangeCooldownHours = diplomacyRelationChangeCooldownHours;
        DiplomacySupportRequestCooldownMinutes = diplomacySupportRequestCooldownMinutes;
        ForcedGiftDeliveryCooldownMinutes = forcedGiftDeliveryCooldownMinutes;
        GiftsEnabled = giftsEnabled;
        BankLoansEnabled = bankLoansEnabled;
        BankMinLoanSilver = bankMinLoanSilver;
        BankMaxLoanSilver = bankMaxLoanSilver;
        BankMaxLoanWealthRatio = bankMaxLoanWealthRatio;
        BankBaseAnnualInterestRate = bankBaseAnnualInterestRate;
        BankMinDurationDays = bankMinDurationDays;
        BankMaxDurationDays = bankMaxDurationDays;
        BankInterestDurationMultiplierCurve = bankInterestDurationMultiplierCurve ?? string.Empty;
        BankPenaltyIntervalDays = bankPenaltyIntervalDays;
        BankPenaltyRaidPointsPerSilver = bankPenaltyRaidPointsPerSilver;
        BankOverduePenaltyStages = bankOverduePenaltyStages ?? string.Empty;
        MercenariesEnabled = mercenariesEnabled;
        MercenaryApprenticeDailySilver = mercenaryApprenticeDailySilver;
        MercenarySkilledDailySilver = mercenarySkilledDailySilver;
        MercenaryMasterDailySilver = mercenaryMasterDailySilver;
        MercenaryMinDurationDays = mercenaryMinDurationDays;
        MercenaryMaxDurationDays = mercenaryMaxDurationDays;
        MercenaryDurationMultiplierCurve = mercenaryDurationMultiplierCurve ?? string.Empty;
        MaxActiveMercenariesPerColony = maxActiveMercenariesPerColony;
        MercenaryHarmfulSurgeryFineSilver = mercenaryHarmfulSurgeryFineSilver;
        MercenaryApprenticeDeathFineSilver = mercenaryApprenticeDeathFineSilver;
        MercenarySkilledDeathFineSilver = mercenarySkilledDeathFineSilver;
        MercenaryMasterDeathFineSilver = mercenaryMasterDeathFineSilver;
        MercenaryGuardsEnabled = mercenaryGuardsEnabled;
        MercenaryGuardApprenticeSilver = mercenaryGuardApprenticeSilver;
        MercenaryGuardSkilledSilver = mercenaryGuardSkilledSilver;
        MercenaryGuardMasterSilver = mercenaryGuardMasterSilver;
        MercenaryGuardApprenticePointsRatio = mercenaryGuardApprenticePointsRatio;
        MercenaryGuardSkilledPointsRatio = mercenaryGuardSkilledPointsRatio;
        MercenaryGuardMasterPointsRatio = mercenaryGuardMasterPointsRatio;
        RaidProtectionHours = raidProtectionHours;
        PvpEnabled = pvpEnabled;
        RaidMinimumDefenderWealth = raidMinimumDefenderWealth;
        RaidSettlementLossRatio = raidSettlementLossRatio;
        RaidSettlementBuildingHitPointsLossRatio = raidSettlementBuildingHitPointsLossRatio;
        RaidSettlementMinimumRemainingHitPointsRatio = raidSettlementMinimumRemainingHitPointsRatio;
        PendingConfirmationTimeoutMinutes = pendingConfirmationTimeoutMinutes;
        FixedTradeFees = fixedTradeFees ?? Array.Empty<AdminFixedTradeFeeDto>();
        CompatibilityMods = compatibilityMods ?? Array.Empty<AdminCompatibilityModDto>();
        RaidMaxDurationMinutes = raidMaxDurationMinutes;
        RaidTimeoutGraceMinutes = raidTimeoutGraceMinutes;
    }

    public bool TradeMarketplaceEnabled { get; }

    public int TradeOrderExpirationDays { get; }

    public int MaxOpenTradeOrdersPerOwner { get; }

    public int TradePostageBaseSilver { get; }

    public int TradePostageSilverPerTile { get; }

    public int TradePostageCrossLayerOverheadDistanceTiles { get; }

    public float TradeBaseFeeRate { get; }

    public string TradeFeeStrategy { get; }

    public double DiplomacyRelationChangeCooldownHours { get; }

    public double DiplomacySupportRequestCooldownMinutes { get; }

    public double ForcedGiftDeliveryCooldownMinutes { get; }

    public bool GiftsEnabled { get; }

    public bool BankLoansEnabled { get; }

    public int BankMinLoanSilver { get; }

    public int BankMaxLoanSilver { get; }

    public float BankMaxLoanWealthRatio { get; }

    public float BankBaseAnnualInterestRate { get; }

    public int BankMinDurationDays { get; }

    public int BankMaxDurationDays { get; }

    public string BankInterestDurationMultiplierCurve { get; }

    public int BankPenaltyIntervalDays { get; }

    public float BankPenaltyRaidPointsPerSilver { get; }

    public string BankOverduePenaltyStages { get; }

    public bool MercenariesEnabled { get; }

    public int MercenaryApprenticeDailySilver { get; }

    public int MercenarySkilledDailySilver { get; }

    public int MercenaryMasterDailySilver { get; }

    public int MercenaryMinDurationDays { get; }

    public int MercenaryMaxDurationDays { get; }

    public string MercenaryDurationMultiplierCurve { get; }

    public int MaxActiveMercenariesPerColony { get; }

    public int MercenaryHarmfulSurgeryFineSilver { get; }

    public int MercenaryApprenticeDeathFineSilver { get; }

    public int MercenarySkilledDeathFineSilver { get; }

    public int MercenaryMasterDeathFineSilver { get; }

    public bool MercenaryGuardsEnabled { get; }

    public int MercenaryGuardApprenticeSilver { get; }

    public int MercenaryGuardSkilledSilver { get; }

    public int MercenaryGuardMasterSilver { get; }

    public float MercenaryGuardApprenticePointsRatio { get; }

    public float MercenaryGuardSkilledPointsRatio { get; }

    public float MercenaryGuardMasterPointsRatio { get; }

    public double RaidProtectionHours { get; }

    public double RaidMaxDurationMinutes { get; }

    public double RaidTimeoutGraceMinutes { get; }

    public bool PvpEnabled { get; }

    public int RaidMinimumDefenderWealth { get; }

    public double RaidSettlementLossRatio { get; }

    public double RaidSettlementBuildingHitPointsLossRatio { get; }

    public double RaidSettlementMinimumRemainingHitPointsRatio { get; }

    public double PendingConfirmationTimeoutMinutes { get; }

    public IReadOnlyList<AdminFixedTradeFeeDto> FixedTradeFees { get; }

    public IReadOnlyList<AdminCompatibilityModDto> CompatibilityMods { get; }
}

public sealed class AdminFixedTradeFeeDto
{
    public AdminFixedTradeFeeDto(string thingDefName, int silverPerUnit)
    {
        ThingDefName = thingDefName;
        SilverPerUnit = silverPerUnit;
    }

    public string ThingDefName { get; }

    public int SilverPerUnit { get; }
}

public sealed class AdminCompatibilityModDto
{
    public AdminCompatibilityModDto(
        string packageId,
        string name,
        int loadOrder,
        string role,
        IReadOnlyList<AdminCompatibilityConfigDto>? configs)
    {
        PackageId = packageId;
        Name = name;
        LoadOrder = loadOrder;
        Role = role;
        Configs = configs ?? Array.Empty<AdminCompatibilityConfigDto>();
    }

    public string PackageId { get; }

    public string Name { get; }

    public int LoadOrder { get; }

    public string Role { get; }

    public IReadOnlyList<AdminCompatibilityConfigDto> Configs { get; }
}

public sealed class AdminCompatibilityConfigDto
{
    public AdminCompatibilityConfigDto(string fileName, string mode, bool hasSavedFile)
    {
        FileName = fileName;
        Mode = mode;
        HasSavedFile = hasSavedFile;
    }

    public string FileName { get; }

    public string Mode { get; }

    public bool HasSavedFile { get; }
}

public sealed class AdminPlayerSummaryDto
{
    public AdminPlayerSummaryDto(
        string userId,
        string colonyId,
        string? currentSnapshotId,
        bool online,
        DateTimeOffset lastSeenAtUtc,
        string? displayName,
        bool isAdministrator,
        bool isBanned)
    {
        UserId = userId;
        ColonyId = colonyId;
        CurrentSnapshotId = currentSnapshotId;
        Online = online;
        LastSeenAtUtc = lastSeenAtUtc;
        DisplayName = displayName;
        IsAdministrator = isAdministrator;
        IsBanned = isBanned;
    }

    public string UserId { get; }

    public string ColonyId { get; }

    public string? CurrentSnapshotId { get; }

    public bool Online { get; }

    public DateTimeOffset LastSeenAtUtc { get; }

    public string? DisplayName { get; }

    public bool IsAdministrator { get; }

    public bool IsBanned { get; }
}

public sealed class AdminAuditRecordDto
{
    public AdminAuditRecordDto(
        string actionKind,
        string actorUserId,
        string? targetUserId,
        string? message,
        DateTimeOffset createdAtUtc)
    {
        ActionKind = actionKind;
        ActorUserId = actorUserId;
        TargetUserId = targetUserId;
        Message = message;
        CreatedAtUtc = createdAtUtc;
    }

    public string ActionKind { get; }

    public string ActorUserId { get; }

    public string? TargetUserId { get; }

    public string? Message { get; }

    public DateTimeOffset CreatedAtUtc { get; }
}
