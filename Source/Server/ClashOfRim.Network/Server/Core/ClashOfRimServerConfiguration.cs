using AIRsLight.ClashOfRim.Compatibility;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;

namespace AIRsLight.ClashOfRim.Network;

public sealed class ClashOfRimServerConfiguration
{
    public static readonly TimeSpan DefaultTradeOrderExpiration = TimeSpan.FromDays(7);
    public static readonly TimeSpan DefaultDiplomacyRelationChangeCooldown = TimeSpan.FromDays(1);
    public static readonly TimeSpan DefaultDiplomacySupportRequestCooldown = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan DefaultForcedGiftDeliveryCooldown = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan DefaultPendingConfirmationTimeout = TimeSpan.FromMinutes(10);

    public const int DefaultMaxOpenTradeOrdersPerOwner = 20;
    public const bool DefaultAuthenticationDebugMode = false;
    public const uint DefaultSteamAppId = 294100U;
    public const bool DefaultTradeMarketplaceEnabled = true;
    public const bool DefaultGiftsEnabled = true;
    public const bool DefaultPvpEnabled = true;
    public const int DefaultTradePostageBaseSilver = 60;
    public const int DefaultTradePostageSilverPerTile = 2;
    public const int DefaultTradePostageCrossLayerOverheadDistanceTiles = 120;
    public const int DefaultBankMinLoanSilver = 1;
    public const bool DefaultBankLoansEnabled = true;
    public const int DefaultBankMaxLoanSilver = 0;
    public const float DefaultBankMaxLoanWealthRatio = 0.25f;
    public const float DefaultBankBaseAnnualInterestRate = 0.08f;
    public const int DefaultBankMinDurationDays = 7;
    public const int BankMinimumDurationDaysHardLimit = 1;
    public const int BankMaximumDurationDaysHardLimit = BankLoanPolicy.RimWorldYearDays * 3;
    public const int DefaultBankMaxDurationDays = BankMaximumDurationDaysHardLimit;
    public static readonly IReadOnlyList<BankInterestDurationMultiplierPointDto> DefaultBankInterestDurationMultiplierCurve =
        new[]
        {
            new BankInterestDurationMultiplierPointDto(1, 1.25f),
            new BankInterestDurationMultiplierPointDto(7, 1.1f),
            new BankInterestDurationMultiplierPointDto(30, 1f),
            new BankInterestDurationMultiplierPointDto(60, 0.9f),
            new BankInterestDurationMultiplierPointDto(BankMaximumDurationDaysHardLimit, 0.8f)
        };
    public const int DefaultBankPenaltyIntervalDays = 3;
    public const float DefaultBankPenaltyRaidPointsPerSilver = 0.35f;
    public const int DefaultMercenaryApprenticeDailySilver = 80;
    public const bool DefaultMercenariesEnabled = true;
    public const int DefaultMercenarySkilledDailySilver = 180;
    public const int DefaultMercenaryMasterDailySilver = 400;
    public const int MercenaryMinimumDurationDaysHardLimit = 1;
    public const int MercenaryMaximumDurationDaysHardLimit = BankLoanPolicy.RimWorldYearDays * 3;
    public const int DefaultMercenaryMinDurationDays = MercenaryMinimumDurationDaysHardLimit;
    public const int DefaultMercenaryMaxDurationDays = MercenaryMaximumDurationDaysHardLimit;
    public static readonly IReadOnlyList<BankInterestDurationMultiplierPointDto> DefaultMercenaryDurationMultiplierCurve =
        new[]
        {
            new BankInterestDurationMultiplierPointDto(1, 1.15f),
            new BankInterestDurationMultiplierPointDto(7, 1f),
            new BankInterestDurationMultiplierPointDto(30, 0.9f),
            new BankInterestDurationMultiplierPointDto(60, 0.85f),
            new BankInterestDurationMultiplierPointDto(MercenaryMaximumDurationDaysHardLimit, 0.75f)
        };
    public const int DefaultMaxActiveMercenariesPerColony = 3;
    public const int DefaultMercenaryHarmfulSurgeryFineSilver = 1000;
    public const int DefaultMercenaryApprenticeDeathFineSilver = 1500;
    public const int DefaultMercenarySkilledDeathFineSilver = 3000;
    public const int DefaultMercenaryMasterDeathFineSilver = 6000;
    public const bool DefaultMercenaryGuardsEnabled = true;
    public const int DefaultMercenaryGuardApprenticeSilver = 600;
    public const int DefaultMercenaryGuardSkilledSilver = 1400;
    public const int DefaultMercenaryGuardMasterSilver = 3000;
    public const float DefaultMercenaryGuardApprenticePointsRatio = 0.35f;
    public const float DefaultMercenaryGuardSkilledPointsRatio = 0.7f;
    public const float DefaultMercenaryGuardMasterPointsRatio = 1.1f;
    public static readonly TimeSpan DefaultRaidProtectionDuration = RaidCooldownPolicy.Default.SettlementCooldown;
    public static readonly TimeSpan DefaultRaidMaxDuration = RaidDefenseLockPolicy.Default.MaxRaidDuration;
    public static readonly TimeSpan DefaultRaidTimeoutGracePeriod = RaidDefenseLockPolicy.Default.ServerTimeoutGracePeriod;
    public const int DefaultRaidMinimumDefenderWealth = 0;
    public const double DefaultRaidSettlementLossRatio = 0.5;
    public const double DefaultRaidSettlementBuildingHitPointsLossRatio = RaidSettlementPolicy.DefaultBuildingHitPointsLossRatio;
    public const double DefaultRaidSettlementMinimumRemainingHitPointsRatio = 0.1;
    public static readonly IReadOnlyList<BankOverduePenaltyStageDto> DefaultBankOverduePenaltyStages =
        new[]
        {
            new BankOverduePenaltyStageDto(4, "PsychicWhisper", 1f),
            new BankOverduePenaltyStageDto(8, "PsychicWhisper", 1.5f),
            new BankOverduePenaltyStageDto(12, "PsychicWhisper", 2f)
        };

    public ClashOfRimServerConfiguration(
        TradeFeePolicy? tradeFeePolicy = null,
        bool tradeMarketplaceEnabled = DefaultTradeMarketplaceEnabled,
        TimeSpan? tradeOrderExpiration = null,
        int maxOpenTradeOrdersPerOwner = DefaultMaxOpenTradeOrdersPerOwner,
        int tradePostageBaseSilver = DefaultTradePostageBaseSilver,
        int tradePostageSilverPerTile = DefaultTradePostageSilverPerTile,
        int tradePostageCrossLayerOverheadDistanceTiles = DefaultTradePostageCrossLayerOverheadDistanceTiles,
        TimeSpan? diplomacyRelationChangeCooldown = null,
        TimeSpan? diplomacySupportRequestCooldown = null,
        TimeSpan? forcedGiftDeliveryCooldown = null,
        bool giftsEnabled = DefaultGiftsEnabled,
        bool bankLoansEnabled = DefaultBankLoansEnabled,
        int bankMinLoanSilver = DefaultBankMinLoanSilver,
        int bankMaxLoanSilver = DefaultBankMaxLoanSilver,
        float bankMaxLoanWealthRatio = DefaultBankMaxLoanWealthRatio,
        float bankBaseAnnualInterestRate = DefaultBankBaseAnnualInterestRate,
        int bankMinDurationDays = DefaultBankMinDurationDays,
        int bankMaxDurationDays = DefaultBankMaxDurationDays,
        IReadOnlyList<BankInterestDurationMultiplierPointDto>? bankInterestDurationMultiplierCurve = null,
        int bankPenaltyIntervalDays = DefaultBankPenaltyIntervalDays,
        float bankPenaltyRaidPointsPerSilver = DefaultBankPenaltyRaidPointsPerSilver,
        IReadOnlyList<BankOverduePenaltyStageDto>? bankOverduePenaltyStages = null,
        bool mercenariesEnabled = DefaultMercenariesEnabled,
        int mercenaryApprenticeDailySilver = DefaultMercenaryApprenticeDailySilver,
        int mercenarySkilledDailySilver = DefaultMercenarySkilledDailySilver,
        int mercenaryMasterDailySilver = DefaultMercenaryMasterDailySilver,
        int mercenaryMinDurationDays = DefaultMercenaryMinDurationDays,
        int mercenaryMaxDurationDays = DefaultMercenaryMaxDurationDays,
        IReadOnlyList<BankInterestDurationMultiplierPointDto>? mercenaryDurationMultiplierCurve = null,
        int maxActiveMercenariesPerColony = DefaultMaxActiveMercenariesPerColony,
        int mercenaryHarmfulSurgeryFineSilver = DefaultMercenaryHarmfulSurgeryFineSilver,
        int mercenaryApprenticeDeathFineSilver = DefaultMercenaryApprenticeDeathFineSilver,
        int mercenarySkilledDeathFineSilver = DefaultMercenarySkilledDeathFineSilver,
        int mercenaryMasterDeathFineSilver = DefaultMercenaryMasterDeathFineSilver,
        bool mercenaryGuardsEnabled = DefaultMercenaryGuardsEnabled,
        int mercenaryGuardApprenticeSilver = DefaultMercenaryGuardApprenticeSilver,
        int mercenaryGuardSkilledSilver = DefaultMercenaryGuardSkilledSilver,
        int mercenaryGuardMasterSilver = DefaultMercenaryGuardMasterSilver,
        float mercenaryGuardApprenticePointsRatio = DefaultMercenaryGuardApprenticePointsRatio,
        float mercenaryGuardSkilledPointsRatio = DefaultMercenaryGuardSkilledPointsRatio,
        float mercenaryGuardMasterPointsRatio = DefaultMercenaryGuardMasterPointsRatio,
        TimeSpan? raidProtectionDuration = null,
        TimeSpan? raidMaxDuration = null,
        TimeSpan? raidTimeoutGracePeriod = null,
        bool pvpEnabled = DefaultPvpEnabled,
        int raidMinimumDefenderWealth = DefaultRaidMinimumDefenderWealth,
        double raidSettlementLossRatio = DefaultRaidSettlementLossRatio,
        double raidSettlementBuildingHitPointsLossRatio = DefaultRaidSettlementBuildingHitPointsLossRatio,
        double raidSettlementMinimumRemainingHitPointsRatio = DefaultRaidSettlementMinimumRemainingHitPointsRatio,
        TimeSpan? pendingConfirmationTimeout = null,
        bool authenticationDebugMode = DefaultAuthenticationDebugMode,
        string? steamWebApiKey = null,
        uint steamAppId = DefaultSteamAppId,
        CompatibilityComparisonOptions? compatibilityOptions = null)
    {
        TradeFeePolicy = tradeFeePolicy ?? TradeFeePolicy.Default;
        TradeOrderExpiration = tradeOrderExpiration ?? DefaultTradeOrderExpiration;
        MaxOpenTradeOrdersPerOwner = Math.Max(0, maxOpenTradeOrdersPerOwner);
        TradePostageBaseSilver = Math.Max(0, tradePostageBaseSilver);
        TradePostageSilverPerTile = Math.Max(0, tradePostageSilverPerTile);
        TradePostageCrossLayerOverheadDistanceTiles = Math.Max(0, tradePostageCrossLayerOverheadDistanceTiles);
        DiplomacyRelationChangeCooldown = diplomacyRelationChangeCooldown ?? DefaultDiplomacyRelationChangeCooldown;
        if (DiplomacyRelationChangeCooldown < TimeSpan.Zero)
        {
            DiplomacyRelationChangeCooldown = TimeSpan.Zero;
        }
        DiplomacySupportRequestCooldown = diplomacySupportRequestCooldown ?? DefaultDiplomacySupportRequestCooldown;
        if (DiplomacySupportRequestCooldown < TimeSpan.Zero)
        {
            DiplomacySupportRequestCooldown = TimeSpan.Zero;
        }
        ForcedGiftDeliveryCooldown = forcedGiftDeliveryCooldown ?? DefaultForcedGiftDeliveryCooldown;
        if (ForcedGiftDeliveryCooldown < TimeSpan.Zero)
        {
            ForcedGiftDeliveryCooldown = TimeSpan.Zero;
        }

        BankMinLoanSilver = Math.Max(1, bankMinLoanSilver);
        BankMaxLoanSilver = Math.Max(0, bankMaxLoanSilver);
        BankMaxLoanWealthRatio = Math.Max(0f, bankMaxLoanWealthRatio);
        BankBaseAnnualInterestRate = Math.Max(0f, bankBaseAnnualInterestRate);
        BankMinDurationDays = Math.Clamp(bankMinDurationDays, BankMinimumDurationDaysHardLimit, BankMaximumDurationDaysHardLimit);
        BankMaxDurationDays = Math.Clamp(bankMaxDurationDays, BankMinDurationDays, BankMaximumDurationDaysHardLimit);
        BankInterestDurationMultiplierCurve = NormalizeDurationMultiplierCurve(
            bankInterestDurationMultiplierCurve,
            DefaultBankInterestDurationMultiplierCurve);
        BankPenaltyIntervalDays = Math.Max(1, bankPenaltyIntervalDays);
        BankPenaltyRaidPointsPerSilver = Math.Max(0f, bankPenaltyRaidPointsPerSilver);
        BankOverduePenaltyStages = (bankOverduePenaltyStages ?? DefaultBankOverduePenaltyStages)
            .Where(stage => stage.TriggerPenaltyCount > 0 && !string.IsNullOrWhiteSpace(stage.Kind))
            .OrderBy(stage => stage.TriggerPenaltyCount)
            .ToList();
        MercenaryApprenticeDailySilver = Math.Max(0, mercenaryApprenticeDailySilver);
        MercenarySkilledDailySilver = Math.Max(0, mercenarySkilledDailySilver);
        MercenaryMasterDailySilver = Math.Max(0, mercenaryMasterDailySilver);
        MercenaryMinDurationDays = Math.Clamp(
            mercenaryMinDurationDays,
            MercenaryMinimumDurationDaysHardLimit,
            MercenaryMaximumDurationDaysHardLimit);
        MercenaryMaxDurationDays = Math.Clamp(
            mercenaryMaxDurationDays,
            MercenaryMinDurationDays,
            MercenaryMaximumDurationDaysHardLimit);
        MercenaryDurationMultiplierCurve = NormalizeDurationMultiplierCurve(
            mercenaryDurationMultiplierCurve,
            DefaultMercenaryDurationMultiplierCurve);
        MaxActiveMercenariesPerColony = Math.Max(0, maxActiveMercenariesPerColony);
        MercenaryHarmfulSurgeryFineSilver = Math.Max(0, mercenaryHarmfulSurgeryFineSilver);
        MercenaryApprenticeDeathFineSilver = Math.Max(0, mercenaryApprenticeDeathFineSilver);
        MercenarySkilledDeathFineSilver = Math.Max(0, mercenarySkilledDeathFineSilver);
        MercenaryMasterDeathFineSilver = Math.Max(0, mercenaryMasterDeathFineSilver);
        MercenaryGuardsEnabled = mercenaryGuardsEnabled;
        MercenaryGuardApprenticeSilver = Math.Max(0, mercenaryGuardApprenticeSilver);
        MercenaryGuardSkilledSilver = Math.Max(0, mercenaryGuardSkilledSilver);
        MercenaryGuardMasterSilver = Math.Max(0, mercenaryGuardMasterSilver);
        MercenaryGuardApprenticePointsRatio = Math.Max(0f, mercenaryGuardApprenticePointsRatio);
        MercenaryGuardSkilledPointsRatio = Math.Max(0f, mercenaryGuardSkilledPointsRatio);
        MercenaryGuardMasterPointsRatio = Math.Max(0f, mercenaryGuardMasterPointsRatio);
        RaidProtectionDuration = raidProtectionDuration ?? DefaultRaidProtectionDuration;
        if (RaidProtectionDuration < TimeSpan.Zero)
        {
            RaidProtectionDuration = TimeSpan.Zero;
        }
        RaidMaxDuration = raidMaxDuration ?? DefaultRaidMaxDuration;
        if (RaidMaxDuration < TimeSpan.Zero)
        {
            RaidMaxDuration = TimeSpan.Zero;
        }
        RaidTimeoutGracePeriod = raidTimeoutGracePeriod ?? DefaultRaidTimeoutGracePeriod;
        if (RaidTimeoutGracePeriod < TimeSpan.Zero)
        {
            RaidTimeoutGracePeriod = TimeSpan.Zero;
        }

        PvpEnabled = pvpEnabled;
        RaidMinimumDefenderWealth = Math.Max(0, raidMinimumDefenderWealth);
        RaidSettlementLossRatio = ClampRatio(raidSettlementLossRatio);
        RaidSettlementBuildingHitPointsLossRatio = ClampRatio(raidSettlementBuildingHitPointsLossRatio);
        RaidSettlementMinimumRemainingHitPointsRatio = ClampRatio(raidSettlementMinimumRemainingHitPointsRatio);
        PendingConfirmationTimeout = pendingConfirmationTimeout ?? DefaultPendingConfirmationTimeout;
        if (PendingConfirmationTimeout < TimeSpan.Zero)
        {
            PendingConfirmationTimeout = TimeSpan.Zero;
        }
        CompatibilityOptions = compatibilityOptions ?? CompatibilityComparisonOptions.Default;
        AuthenticationDebugMode = authenticationDebugMode;
        SteamWebApiKey = string.IsNullOrWhiteSpace(steamWebApiKey) ? null : steamWebApiKey.Trim();
        SteamAppId = steamAppId == 0 ? DefaultSteamAppId : steamAppId;
        TradeMarketplaceEnabled = tradeMarketplaceEnabled;
        GiftsEnabled = giftsEnabled;
        BankLoansEnabled = bankLoansEnabled;
        MercenariesEnabled = mercenariesEnabled;
    }

    public TradeFeePolicy TradeFeePolicy { get; }

    public bool TradeMarketplaceEnabled { get; }

    public TimeSpan TradeOrderExpiration { get; }

    public int MaxOpenTradeOrdersPerOwner { get; }

    public int TradePostageBaseSilver { get; }

    public int TradePostageSilverPerTile { get; }

    public int TradePostageCrossLayerOverheadDistanceTiles { get; }

    public TimeSpan DiplomacyRelationChangeCooldown { get; }

    public TimeSpan DiplomacySupportRequestCooldown { get; }

    public TimeSpan ForcedGiftDeliveryCooldown { get; }

    public bool GiftsEnabled { get; }

    public bool BankLoansEnabled { get; }

    public int BankMinLoanSilver { get; }

    public int BankMaxLoanSilver { get; }

    public float BankMaxLoanWealthRatio { get; }

    public float BankBaseAnnualInterestRate { get; }

    public int BankMinDurationDays { get; }

    public int BankMaxDurationDays { get; }

    public IReadOnlyList<BankInterestDurationMultiplierPointDto> BankInterestDurationMultiplierCurve { get; }

    public int BankPenaltyIntervalDays { get; }

    public float BankPenaltyRaidPointsPerSilver { get; }

    public IReadOnlyList<BankOverduePenaltyStageDto> BankOverduePenaltyStages { get; }

    public bool MercenariesEnabled { get; }

    public int MercenaryApprenticeDailySilver { get; }

    public int MercenarySkilledDailySilver { get; }

    public int MercenaryMasterDailySilver { get; }

    public int MercenaryMinDurationDays { get; }

    public int MercenaryMaxDurationDays { get; }

    public IReadOnlyList<BankInterestDurationMultiplierPointDto> MercenaryDurationMultiplierCurve { get; }

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

    public TimeSpan RaidProtectionDuration { get; }

    public TimeSpan RaidMaxDuration { get; }

    public TimeSpan RaidTimeoutGracePeriod { get; }

    public bool PvpEnabled { get; }

    public int RaidMinimumDefenderWealth { get; }

    public double RaidSettlementLossRatio { get; }

    public double RaidSettlementBuildingHitPointsLossRatio { get; }

    public double RaidSettlementMinimumRemainingHitPointsRatio { get; }

    public TimeSpan PendingConfirmationTimeout { get; }

    public CompatibilityComparisonOptions CompatibilityOptions { get; }

    public bool AuthenticationDebugMode { get; }

    public string? SteamWebApiKey { get; }

    public uint SteamAppId { get; }

    private static double ClampRatio(double value)
    {
        if (double.IsNaN(value))
        {
            return 0;
        }

        return Math.Clamp(value, 0, 1);
    }

    private static IReadOnlyList<BankInterestDurationMultiplierPointDto> NormalizeDurationMultiplierCurve(
        IReadOnlyList<BankInterestDurationMultiplierPointDto>? curve,
        IReadOnlyList<BankInterestDurationMultiplierPointDto> fallback)
    {
        IReadOnlyList<BankInterestDurationMultiplierPointDto> source =
            curve is { Count: > 0 } ? curve : fallback;
        List<BankInterestDurationMultiplierPointDto> normalized = source
            .Where(point => point.DurationDays >= 0 && point.Multiplier >= 0f && !float.IsNaN(point.Multiplier))
            .GroupBy(point => point.DurationDays)
            .Select(group => group.Last())
            .OrderBy(point => point.DurationDays)
            .ToList();
        return normalized.Count > 0 ? normalized : fallback;
    }
}
