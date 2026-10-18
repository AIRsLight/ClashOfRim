using AIRsLight.ClashOfRim.Compatibility;
using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Network.Modules.Admin;

internal static class AdminConfigurationMapping
{
    internal static AdminConfigurationDto ToDto(ClashOfRimServerConfiguration configuration, CompatibilityManifest? baseline)
    {
        return new AdminConfigurationDto(
            configuration.TradeMarketplaceEnabled,
            Math.Max(0, (int)Math.Round(configuration.TradeOrderExpiration.TotalDays)),
            configuration.MaxOpenTradeOrdersPerOwner,
            configuration.TradePostageBaseSilver,
            configuration.TradePostageSilverPerTile,
            configuration.TradePostageCrossLayerOverheadDistanceTiles,
            configuration.TradeFeePolicy.BaseFeeRate,
            configuration.DiplomacyRelationChangeCooldown.TotalHours,
            configuration.DiplomacySupportRequestCooldown.TotalMinutes,
            configuration.ForcedGiftDeliveryCooldown.TotalMinutes,
            configuration.BankLoansEnabled,
            configuration.BankMinLoanSilver,
            configuration.BankMaxLoanSilver,
            configuration.BankMaxLoanWealthRatio,
            configuration.BankBaseAnnualInterestRate,
            configuration.BankMinDurationDays,
            configuration.BankMaxDurationDays,
            FormatBankInterestDurationMultiplierCurve(configuration.BankInterestDurationMultiplierCurve),
            configuration.BankPenaltyIntervalDays,
            configuration.BankPenaltyRaidPointsPerSilver,
            FormatBankOverduePenaltyStages(configuration.BankOverduePenaltyStages),
            configuration.MercenariesEnabled,
            configuration.MercenaryApprenticeDailySilver,
            configuration.MercenarySkilledDailySilver,
            configuration.MercenaryMasterDailySilver,
            configuration.MercenaryMinDurationDays,
            configuration.MercenaryMaxDurationDays,
            FormatBankInterestDurationMultiplierCurve(configuration.MercenaryDurationMultiplierCurve),
            configuration.MaxActiveMercenariesPerColony,
            configuration.MercenaryHarmfulSurgeryFineSilver,
            configuration.MercenaryApprenticeDeathFineSilver,
            configuration.MercenarySkilledDeathFineSilver,
            configuration.MercenaryMasterDeathFineSilver,
            configuration.MercenaryGuardsEnabled,
            configuration.MercenaryGuardApprenticeSilver,
            configuration.MercenaryGuardSkilledSilver,
            configuration.MercenaryGuardMasterSilver,
            configuration.MercenaryGuardApprenticePointsRatio,
            configuration.MercenaryGuardSkilledPointsRatio,
            configuration.MercenaryGuardMasterPointsRatio,
            configuration.RaidProtectionDuration.TotalHours,
            configuration.PvpEnabled,
            configuration.RaidMinimumDefenderWealth,
            configuration.RaidSettlementLossRatio,
            configuration.RaidSettlementBuildingHitPointsLossRatio,
            configuration.RaidSettlementMinimumRemainingHitPointsRatio,
            configuration.PendingConfirmationTimeout.TotalMinutes,
            BuildAdminFixedTradeFees(configuration.TradeFeePolicy),
            BuildAdminCompatibilityMods(configuration, baseline),
            configuration.RaidMaxDuration.TotalMinutes,
            configuration.RaidTimeoutGracePeriod.TotalMinutes,
            configuration.TradeFeePolicy.FeeStrategy,
            configuration.GiftsEnabled);
    }

    internal static ClashOfRimServerConfiguration Apply(
        AdminConfigurationDto dto,
        ClashOfRimServerConfiguration current)
    {
        return new ClashOfRimServerConfiguration(
            BuildAdminTradeFeePolicy(dto, current.TradeFeePolicy),
            dto.TradeMarketplaceEnabled,
            TimeSpan.FromDays(Math.Max(0, dto.TradeOrderExpirationDays)),
            dto.MaxOpenTradeOrdersPerOwner,
            dto.TradePostageBaseSilver,
            dto.TradePostageSilverPerTile,
            dto.TradePostageCrossLayerOverheadDistanceTiles,
            TimeSpan.FromHours(Math.Max(0, dto.DiplomacyRelationChangeCooldownHours)),
            TimeSpan.FromMinutes(Math.Max(0, dto.DiplomacySupportRequestCooldownMinutes)),
            TimeSpan.FromMinutes(Math.Max(0, dto.ForcedGiftDeliveryCooldownMinutes)),
            dto.GiftsEnabled,
            dto.BankLoansEnabled,
            dto.BankMinLoanSilver,
            dto.BankMaxLoanSilver,
            dto.BankMaxLoanWealthRatio,
            dto.BankBaseAnnualInterestRate,
            dto.BankMinDurationDays,
            dto.BankMaxDurationDays,
            ParseBankInterestDurationMultiplierCurve(dto.BankInterestDurationMultiplierCurve, current.BankInterestDurationMultiplierCurve),
            dto.BankPenaltyIntervalDays,
            dto.BankPenaltyRaidPointsPerSilver,
            ParseBankOverduePenaltyStages(dto.BankOverduePenaltyStages, current.BankOverduePenaltyStages),
            dto.MercenariesEnabled,
            dto.MercenaryApprenticeDailySilver,
            dto.MercenarySkilledDailySilver,
            dto.MercenaryMasterDailySilver,
            dto.MercenaryMinDurationDays,
            dto.MercenaryMaxDurationDays,
            ParseBankInterestDurationMultiplierCurve(
                dto.MercenaryDurationMultiplierCurve,
                current.MercenaryDurationMultiplierCurve),
            dto.MaxActiveMercenariesPerColony,
            dto.MercenaryHarmfulSurgeryFineSilver,
            dto.MercenaryApprenticeDeathFineSilver,
            dto.MercenarySkilledDeathFineSilver,
            dto.MercenaryMasterDeathFineSilver,
            dto.MercenaryGuardsEnabled,
            dto.MercenaryGuardApprenticeSilver,
            dto.MercenaryGuardSkilledSilver,
            dto.MercenaryGuardMasterSilver,
            dto.MercenaryGuardApprenticePointsRatio,
            dto.MercenaryGuardSkilledPointsRatio,
            dto.MercenaryGuardMasterPointsRatio,
            TimeSpan.FromHours(Math.Max(0, dto.RaidProtectionHours)),
            TimeSpan.FromMinutes(Math.Max(0, dto.RaidMaxDurationMinutes)),
            TimeSpan.FromMinutes(Math.Max(0, dto.RaidTimeoutGraceMinutes)),
            dto.PvpEnabled,
            dto.RaidMinimumDefenderWealth,
            dto.RaidSettlementLossRatio,
            dto.RaidSettlementBuildingHitPointsLossRatio,
            dto.RaidSettlementMinimumRemainingHitPointsRatio,
            TimeSpan.FromMinutes(Math.Max(0, dto.PendingConfirmationTimeoutMinutes)),
            current.AuthenticationDebugMode,
            current.SteamWebApiKey,
            current.SteamAppId,
            BuildAdminCompatibilityOptions(dto, current.CompatibilityOptions));
    }

    private static IReadOnlyList<AdminFixedTradeFeeDto> BuildAdminFixedTradeFees(TradeFeePolicy policy)
    {
        return policy.FixedFeePerThing
            .OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
            .Select(entry => new AdminFixedTradeFeeDto(entry.Key, Math.Max(0, entry.Value)))
            .ToList();
    }

    private static string FormatBankInterestDurationMultiplierCurve(
        IReadOnlyList<BankInterestDurationMultiplierPointDto> points)
    {
        return string.Join(
            ",",
            (points ?? Array.Empty<BankInterestDurationMultiplierPointDto>())
                .OrderBy(point => point.DurationDays)
                .Select(point => FormattableString.Invariant($"{point.DurationDays}:{point.Multiplier:R}")));
    }

    private static IReadOnlyList<BankInterestDurationMultiplierPointDto> ParseBankInterestDurationMultiplierCurve(
        string? text,
        IReadOnlyList<BankInterestDurationMultiplierPointDto> fallback)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        var points = new List<BankInterestDurationMultiplierPointDto>();
        foreach (string part in text.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string[] pieces = part.Split(new[] { ':', '=' }, StringSplitOptions.RemoveEmptyEntries);
            if (pieces.Length != 2
                || !int.TryParse(pieces[0], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int days)
                || days < 0
                || !float.TryParse(pieces[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float multiplier)
                || multiplier < 0f
                || float.IsNaN(multiplier))
            {
                continue;
            }

            points.Add(new BankInterestDurationMultiplierPointDto(days, multiplier));
        }

        return points.Count > 0 ? points : fallback;
    }

    private static string FormatBankOverduePenaltyStages(IReadOnlyList<BankOverduePenaltyStageDto> stages)
    {
        return string.Join(
            ",",
            (stages ?? Array.Empty<BankOverduePenaltyStageDto>())
                .OrderBy(stage => stage.TriggerPenaltyCount)
                .Select(stage => FormattableString.Invariant(
                    $"{stage.TriggerPenaltyCount}:{stage.Kind}:{stage.Severity:R}")));
    }

    private static IReadOnlyList<BankOverduePenaltyStageDto> ParseBankOverduePenaltyStages(
        string? text,
        IReadOnlyList<BankOverduePenaltyStageDto> fallback)
    {
        if (text is null)
        {
            return fallback;
        }

        var stages = new List<BankOverduePenaltyStageDto>();
        foreach (string rawEntry in text.Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = rawEntry.Trim().Split(':');
            if (parts.Length < 2
                || !int.TryParse(parts[0], out int triggerPenaltyCount)
                || triggerPenaltyCount <= 0
                || string.IsNullOrWhiteSpace(parts[1]))
            {
                continue;
            }

            float severity = 1f;
            if (parts.Length >= 3
                && !float.TryParse(
                    parts[2],
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out severity))
            {
                severity = 1f;
            }

            stages.Add(new BankOverduePenaltyStageDto(
                triggerPenaltyCount,
                parts[1].Trim(),
                Math.Max(0f, severity)));
        }

        return stages.OrderBy(stage => stage.TriggerPenaltyCount).ToList();
    }

    private static TradeFeePolicy BuildAdminTradeFeePolicy(AdminConfigurationDto dto, TradeFeePolicy current)
    {
        Dictionary<string, int> fixedFees = (dto.FixedTradeFees ?? Array.Empty<AdminFixedTradeFeeDto>())
            .Where(entry => !string.IsNullOrWhiteSpace(entry.ThingDefName))
            .GroupBy(entry => entry.ThingDefName.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => Math.Max(0, group.Last().SilverPerUnit),
                StringComparer.OrdinalIgnoreCase);

        return new TradeFeePolicy(
            Math.Max(0f, dto.TradeBaseFeeRate),
            fixedFees,
            current.StandardMarketValuePerThing,
            current.StuffMarketValuePerThingAndStuff,
            current.QualityMarketValueModifiers,
            current.WeaponTraitMarketValueOffsets,
            current.ItemHealthValueCurve,
            current.RepairableBuildingHealthValueCurve,
            dto.TradeFeeStrategy);
    }

    private static IReadOnlyList<AdminCompatibilityModDto> BuildAdminCompatibilityMods(ClashOfRimServerConfiguration configuration, CompatibilityManifest? baseline)
    {
        if (baseline is null)
        {
            return Array.Empty<AdminCompatibilityModDto>();
        }

        return baseline.Mods
            .OrderBy(mod => mod.LoadOrder)
            .Select(mod => new AdminCompatibilityModDto(
                mod.PackageId,
                string.IsNullOrWhiteSpace(mod.Name) ? mod.PackageId : mod.Name,
                mod.LoadOrder,
                mod.Role.ToString(),
                mod.Configs
                    .OrderBy(config => config.FileName, StringComparer.OrdinalIgnoreCase)
                    .Select(config => new AdminCompatibilityConfigDto(
                        config.FileName,
                        configuration.CompatibilityOptions.ResolveConfigMode(mod.PackageId, config.FileName).ToString(),
                        config.HasSavedFile))
                    .ToList()))
            .ToList();
    }

    private static CompatibilityComparisonOptions BuildAdminCompatibilityOptions(
        AdminConfigurationDto dto,
        CompatibilityComparisonOptions current)
    {
        var rules = new List<ModConfigComparisonRule>();
        foreach (AdminCompatibilityModDto mod in dto.CompatibilityMods ?? Array.Empty<AdminCompatibilityModDto>())
        {
            if (string.IsNullOrWhiteSpace(mod.PackageId))
            {
                continue;
            }

            foreach (AdminCompatibilityConfigDto config in mod.Configs ?? Array.Empty<AdminCompatibilityConfigDto>())
            {
                if (string.IsNullOrWhiteSpace(config.FileName))
                {
                    continue;
                }

                rules.Add(new ModConfigComparisonRule
                {
                    PackageId = mod.PackageId,
                    FileName = config.FileName,
                    Mode = ParseConfigMode(config.Mode)
                });
            }
        }

        return current with { ModConfigRules = rules };
    }

    internal static CompatibilityManifest? ApplyRoles(CompatibilityManifest? baseline, AdminConfigurationDto dto)
    {
        if (baseline is null)
        {
            return null;
        }

        Dictionary<string, AdminCompatibilityModDto> byPackage = (dto.CompatibilityMods ?? Array.Empty<AdminCompatibilityModDto>())
            .Where(mod => !string.IsNullOrWhiteSpace(mod.PackageId))
            .GroupBy(mod => mod.PackageId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<ModManifestEntry> mods = baseline.Mods
            .Select(mod => byPackage.TryGetValue(mod.PackageId, out AdminCompatibilityModDto? edited)
                ? mod with { Role = ParseModRole(edited.Role) }
                : mod)
            .ToList();
        return baseline with { Mods = mods };
    }

    private static ModCompatibilityRole ParseModRole(string? value)
    {
        return Enum.TryParse(value, ignoreCase: true, out ModCompatibilityRole role)
            ? role
            : ModCompatibilityRole.Required;
    }

    private static ModConfigComparisonMode ParseConfigMode(string? value)
    {
        return Enum.TryParse(value, ignoreCase: true, out ModConfigComparisonMode mode)
            ? mode
            : ModConfigComparisonMode.Enforce;
    }

}
