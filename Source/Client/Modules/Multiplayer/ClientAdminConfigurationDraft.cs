using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Transport;
using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Modules.Multiplayer;

internal sealed class ClientAdminConfigurationDraft
{
    private readonly List<ClientAdminTextInput> inputs = new();
    internal bool TradeMarketplaceEnabled;
    internal readonly ClientAdminTextInput TradeOrderExpirationDays;
    internal readonly ClientAdminTextInput MaxOpenTradeOrdersPerOwner;
    internal readonly ClientAdminTextInput TradePostageBaseSilver;
    internal readonly ClientAdminTextInput TradePostageSilverPerTile;
    internal readonly ClientAdminTextInput TradePostageCrossLayerOverheadDistanceTiles;
    internal readonly ClientAdminTextInput TradeBaseFeeRate;
    internal string TradeFeeStrategy;
    internal readonly ClientAdminTextInput DiplomacyRelationChangeCooldownHours;
    internal readonly ClientAdminTextInput DiplomacySupportRequestCooldownMinutes;
    internal readonly ClientAdminTextInput ForcedGiftDeliveryCooldownMinutes;
    internal bool GiftsEnabled;
    internal bool BankLoansEnabled;
    internal readonly ClientAdminTextInput BankMinLoanSilver;
    internal readonly ClientAdminTextInput BankMaxLoanSilver;
    internal readonly ClientAdminTextInput BankMaxLoanWealthRatio;
    internal readonly ClientAdminTextInput BankBaseAnnualInterestRate;
    internal readonly ClientAdminTextInput BankMinDurationDays;
    internal readonly ClientAdminTextInput BankMaxDurationDays;
    internal readonly ClientAdminTextInput BankInterestDurationMultiplierCurve;
    internal readonly ClientAdminTextInput BankPenaltyIntervalDays;
    internal readonly ClientAdminTextInput BankPenaltyRaidPointsPerSilver;
    internal bool MercenariesEnabled;
    internal readonly ClientAdminTextInput MercenaryApprenticeDailySilver;
    internal readonly ClientAdminTextInput MercenarySkilledDailySilver;
    internal readonly ClientAdminTextInput MercenaryMasterDailySilver;
    internal readonly ClientAdminTextInput MercenaryMinDurationDays;
    internal readonly ClientAdminTextInput MercenaryMaxDurationDays;
    internal readonly ClientAdminTextInput MercenaryDurationMultiplierCurve;
    internal readonly ClientAdminTextInput MaxActiveMercenariesPerColony;
    internal readonly ClientAdminTextInput MercenaryHarmfulSurgeryFineSilver;
    internal readonly ClientAdminTextInput MercenaryApprenticeDeathFineSilver;
    internal readonly ClientAdminTextInput MercenarySkilledDeathFineSilver;
    internal readonly ClientAdminTextInput MercenaryMasterDeathFineSilver;
    internal bool MercenaryGuardsEnabled;
    internal readonly ClientAdminTextInput MercenaryGuardApprenticeSilver;
    internal readonly ClientAdminTextInput MercenaryGuardSkilledSilver;
    internal readonly ClientAdminTextInput MercenaryGuardMasterSilver;
    internal readonly ClientAdminTextInput MercenaryGuardApprenticePointsRatio;
    internal readonly ClientAdminTextInput MercenaryGuardSkilledPointsRatio;
    internal readonly ClientAdminTextInput MercenaryGuardMasterPointsRatio;
    internal bool PvpEnabled;
    internal readonly ClientAdminTextInput RaidProtectionHours;
    internal readonly ClientAdminTextInput RaidMaxDurationMinutes;
    internal readonly ClientAdminTextInput RaidTimeoutGraceMinutes;
    internal readonly ClientAdminTextInput RaidMinimumDefenderWealth;
    internal readonly ClientAdminTextInput RaidSettlementLossRatio;
    internal readonly ClientAdminTextInput RaidSettlementBuildingHitPointsLossRatio;
    internal readonly ClientAdminTextInput RaidSettlementMinimumRemainingHitPointsRatio;
    internal readonly ClientAdminTextInput PendingConfirmationTimeoutMinutes;
    internal readonly List<ClientAdminFixedTradeFeeInput> FixedTradeFees;
    internal readonly List<ClientAdminPenaltyInput> PenaltyStages;
    internal readonly List<ModAdminCompatibilityModDto> CompatibilityMods;

    internal ClientAdminConfigurationDraft(ModAdminConfigurationDto source)
    {
        TradeMarketplaceEnabled = source.TradeMarketplaceEnabled;
        TradeOrderExpirationDays = Add(ClientAdminTextInput.Number(source.TradeOrderExpirationDays, static (c, v) => c.TradeOrderExpirationDays = v));
        MaxOpenTradeOrdersPerOwner = Add(ClientAdminTextInput.Number(source.MaxOpenTradeOrdersPerOwner, static (c, v) => c.MaxOpenTradeOrdersPerOwner = v));
        TradePostageBaseSilver = Add(ClientAdminTextInput.Number(source.TradePostageBaseSilver, static (c, v) => c.TradePostageBaseSilver = v));
        TradePostageSilverPerTile = Add(ClientAdminTextInput.Number(source.TradePostageSilverPerTile, static (c, v) => c.TradePostageSilverPerTile = v));
        TradePostageCrossLayerOverheadDistanceTiles = Add(ClientAdminTextInput.Number(source.TradePostageCrossLayerOverheadDistanceTiles, static (c, v) => c.TradePostageCrossLayerOverheadDistanceTiles = v));
        TradeBaseFeeRate = Add(ClientAdminTextInput.Number(source.TradeBaseFeeRate, static (c, v) => c.TradeBaseFeeRate = v));
        TradeFeeStrategy = TradeFeeStrategies.Normalize(source.TradeFeeStrategy);
        DiplomacyRelationChangeCooldownHours = Add(ClientAdminTextInput.Number(source.DiplomacyRelationChangeCooldownHours, static (c, v) => c.DiplomacyRelationChangeCooldownHours = v));
        DiplomacySupportRequestCooldownMinutes = Add(ClientAdminTextInput.Number(source.DiplomacySupportRequestCooldownMinutes, static (c, v) => c.DiplomacySupportRequestCooldownMinutes = v));
        ForcedGiftDeliveryCooldownMinutes = Add(ClientAdminTextInput.Number(source.ForcedGiftDeliveryCooldownMinutes, static (c, v) => c.ForcedGiftDeliveryCooldownMinutes = v));
        GiftsEnabled = source.GiftsEnabled;
        BankLoansEnabled = source.BankLoansEnabled;
        BankMinLoanSilver = Add(ClientAdminTextInput.Number(source.BankMinLoanSilver, static (c, v) => c.BankMinLoanSilver = v));
        BankMaxLoanSilver = Add(ClientAdminTextInput.Number(source.BankMaxLoanSilver, static (c, v) => c.BankMaxLoanSilver = v));
        BankMaxLoanWealthRatio = Add(ClientAdminTextInput.Number(source.BankMaxLoanWealthRatio, static (c, v) => c.BankMaxLoanWealthRatio = v));
        BankBaseAnnualInterestRate = Add(ClientAdminTextInput.Number(source.BankBaseAnnualInterestRate, static (c, v) => c.BankBaseAnnualInterestRate = v));
        BankMinDurationDays = Add(ClientAdminTextInput.Number(source.BankMinDurationDays, static (c, v) => c.BankMinDurationDays = v));
        BankMaxDurationDays = Add(ClientAdminTextInput.Number(source.BankMaxDurationDays, static (c, v) => c.BankMaxDurationDays = v));
        BankInterestDurationMultiplierCurve = Add(ClientAdminTextInput.Curve(source.BankInterestDurationMultiplierCurve, static (c, v) => c.BankInterestDurationMultiplierCurve = v));
        BankPenaltyIntervalDays = Add(ClientAdminTextInput.Number(source.BankPenaltyIntervalDays, static (c, v) => c.BankPenaltyIntervalDays = v));
        BankPenaltyRaidPointsPerSilver = Add(ClientAdminTextInput.Number(source.BankPenaltyRaidPointsPerSilver, static (c, v) => c.BankPenaltyRaidPointsPerSilver = v));
        MercenariesEnabled = source.MercenariesEnabled;
        MercenaryApprenticeDailySilver = Add(ClientAdminTextInput.Number(source.MercenaryApprenticeDailySilver, static (c, v) => c.MercenaryApprenticeDailySilver = v));
        MercenarySkilledDailySilver = Add(ClientAdminTextInput.Number(source.MercenarySkilledDailySilver, static (c, v) => c.MercenarySkilledDailySilver = v));
        MercenaryMasterDailySilver = Add(ClientAdminTextInput.Number(source.MercenaryMasterDailySilver, static (c, v) => c.MercenaryMasterDailySilver = v));
        MercenaryMinDurationDays = Add(ClientAdminTextInput.Number(source.MercenaryMinDurationDays, static (c, v) => c.MercenaryMinDurationDays = v));
        MercenaryMaxDurationDays = Add(ClientAdminTextInput.Number(source.MercenaryMaxDurationDays, static (c, v) => c.MercenaryMaxDurationDays = v));
        MercenaryDurationMultiplierCurve = Add(ClientAdminTextInput.Curve(source.MercenaryDurationMultiplierCurve, static (c, v) => c.MercenaryDurationMultiplierCurve = v));
        MaxActiveMercenariesPerColony = Add(ClientAdminTextInput.Number(source.MaxActiveMercenariesPerColony, static (c, v) => c.MaxActiveMercenariesPerColony = v));
        MercenaryHarmfulSurgeryFineSilver = Add(ClientAdminTextInput.Number(source.MercenaryHarmfulSurgeryFineSilver, static (c, v) => c.MercenaryHarmfulSurgeryFineSilver = v));
        MercenaryApprenticeDeathFineSilver = Add(ClientAdminTextInput.Number(source.MercenaryApprenticeDeathFineSilver, static (c, v) => c.MercenaryApprenticeDeathFineSilver = v));
        MercenarySkilledDeathFineSilver = Add(ClientAdminTextInput.Number(source.MercenarySkilledDeathFineSilver, static (c, v) => c.MercenarySkilledDeathFineSilver = v));
        MercenaryMasterDeathFineSilver = Add(ClientAdminTextInput.Number(source.MercenaryMasterDeathFineSilver, static (c, v) => c.MercenaryMasterDeathFineSilver = v));
        MercenaryGuardsEnabled = source.MercenaryGuardsEnabled;
        MercenaryGuardApprenticeSilver = Add(ClientAdminTextInput.Number(source.MercenaryGuardApprenticeSilver, static (c, v) => c.MercenaryGuardApprenticeSilver = v));
        MercenaryGuardSkilledSilver = Add(ClientAdminTextInput.Number(source.MercenaryGuardSkilledSilver, static (c, v) => c.MercenaryGuardSkilledSilver = v));
        MercenaryGuardMasterSilver = Add(ClientAdminTextInput.Number(source.MercenaryGuardMasterSilver, static (c, v) => c.MercenaryGuardMasterSilver = v));
        MercenaryGuardApprenticePointsRatio = Add(ClientAdminTextInput.Number(source.MercenaryGuardApprenticePointsRatio, static (c, v) => c.MercenaryGuardApprenticePointsRatio = v));
        MercenaryGuardSkilledPointsRatio = Add(ClientAdminTextInput.Number(source.MercenaryGuardSkilledPointsRatio, static (c, v) => c.MercenaryGuardSkilledPointsRatio = v));
        MercenaryGuardMasterPointsRatio = Add(ClientAdminTextInput.Number(source.MercenaryGuardMasterPointsRatio, static (c, v) => c.MercenaryGuardMasterPointsRatio = v));
        PvpEnabled = source.PvpEnabled;
        RaidProtectionHours = Add(ClientAdminTextInput.Number(source.RaidProtectionHours, static (c, v) => c.RaidProtectionHours = v));
        RaidMaxDurationMinutes = Add(ClientAdminTextInput.Number(source.RaidMaxDurationMinutes, static (c, v) => c.RaidMaxDurationMinutes = v));
        RaidTimeoutGraceMinutes = Add(ClientAdminTextInput.Number(source.RaidTimeoutGraceMinutes, static (c, v) => c.RaidTimeoutGraceMinutes = v));
        RaidMinimumDefenderWealth = Add(ClientAdminTextInput.Number(source.RaidMinimumDefenderWealth, static (c, v) => c.RaidMinimumDefenderWealth = v));
        RaidSettlementLossRatio = Add(ClientAdminTextInput.Number(source.RaidSettlementLossRatio, static (c, v) => c.RaidSettlementLossRatio = v));
        RaidSettlementBuildingHitPointsLossRatio = Add(ClientAdminTextInput.Number(source.RaidSettlementBuildingHitPointsLossRatio, static (c, v) => c.RaidSettlementBuildingHitPointsLossRatio = v));
        RaidSettlementMinimumRemainingHitPointsRatio = Add(ClientAdminTextInput.Number(source.RaidSettlementMinimumRemainingHitPointsRatio, static (c, v) => c.RaidSettlementMinimumRemainingHitPointsRatio = v));
        PendingConfirmationTimeoutMinutes = Add(ClientAdminTextInput.Number(source.PendingConfirmationTimeoutMinutes, static (c, v) => c.PendingConfirmationTimeoutMinutes = v));
        FixedTradeFees = source.FixedTradeFees
            .Select(f => new ClientAdminFixedTradeFeeInput(f.ThingDefName, f.SilverPerUnit.ToString(CultureInfo.InvariantCulture))).ToList();
        PenaltyStages = ParsePenaltyStages(source.BankOverduePenaltyStages).ToList();
        CompatibilityMods = ClientProtocolJson.Copy(source.CompatibilityMods);
    }
    private ClientAdminTextInput Add(ClientAdminTextInput input) { inputs.Add(input); return input; }
    internal bool IsValid => inputs.All(f => f.TryApply(null)) && FixedTradeFees.All(f => f.TryRead(out _)) && PenaltyStages.All(f => f.TryRead(out _));

    internal bool TryBuild(out ModAdminConfigurationDto config)
    {
        config = new();
        foreach (var input in inputs) if (!input.TryApply(config)) return false;
        foreach (var row in FixedTradeFees)
        {
            if (!row.TryRead(out var fee)) return false;
            if (fee is not null) config.FixedTradeFees.Add(fee);
        }
        var stages = new List<string>();
        foreach (var row in PenaltyStages)
        {
            if (!row.TryRead(out var stage)) return false;
            if (stage is not null) stages.Add(stage);
        }
        config.BankOverduePenaltyStages = string.Join(",", stages);
        config.TradeMarketplaceEnabled = TradeMarketplaceEnabled;
        config.TradeFeeStrategy = TradeFeeStrategies.Normalize(TradeFeeStrategy);
        config.GiftsEnabled = GiftsEnabled;
        config.BankLoansEnabled = BankLoansEnabled;
        config.MercenariesEnabled = MercenariesEnabled;
        config.MercenaryGuardsEnabled = MercenaryGuardsEnabled;
        config.PvpEnabled = PvpEnabled;
        config.CompatibilityMods = ClientProtocolJson.Copy(CompatibilityMods);
        return true;
    }

    private static IReadOnlyList<ClientAdminPenaltyInput> ParsePenaltyStages(string text)
    {
        var rows = new List<ClientAdminPenaltyInput>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return rows;
        }

        foreach (string part in text.Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string[] pieces = part.Trim().Split(':');
            if (pieces.Length < 2)
            {
                continue;
            }

            string trigger = pieces[0].Trim();
            string kind = pieces[1].Trim();
            string severity = pieces.Length >= 3 && !string.IsNullOrWhiteSpace(pieces[2])
                ? pieces[2].Trim()
                : "1";
            if (!string.IsNullOrWhiteSpace(trigger) && !string.IsNullOrWhiteSpace(kind))
            {
                rows.Add(new ClientAdminPenaltyInput(trigger, kind, severity));
            }
        }

        return rows;
    }
}
