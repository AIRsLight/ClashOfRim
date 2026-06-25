using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Compatibility;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace AIRsLight.ClashOfRim.Network;

public static partial class ClashOfRimNetworkServer
{
    private static IResult AdminStatus(AdminStatusRequest request, ClashOfRimNetworkState state)
    {
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        if (!TryAuthorizeAdmin(state, request.UserId, request.ColonyId, request.AuthToken, nowUtc, out string failure))
        {
            return Results.Ok(new AdminStatusResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.Unauthorized, failure),
                isAdministrator: false,
                configuration: null,
                players: Array.Empty<AdminPlayerSummaryDto>(),
                state.AdminControl.MaintenanceLoginLocked,
                state.AdminControl.MaintenanceReason,
                Array.Empty<AdminAuditRecordDto>()));
        }

        return Results.Ok(new AdminStatusResponse(
            ProtocolResponse.Ok(T("Admin.Status.Success")),
            isAdministrator: true,
            ToAdminConfigurationDto(state),
            BuildAdminPlayerSummaries(state),
            state.AdminControl.MaintenanceLoginLocked,
            state.AdminControl.MaintenanceReason,
            state.AdminControl.ListAudit().Select(record => record.ToDto()).ToList()));
    }

    private static IResult AdminUpdateConfiguration(
        AdminUpdateConfigurationRequest request,
        ClashOfRimNetworkState state,
        ILoggerFactory loggerFactory)
    {
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        if (!TryAuthorizeAdmin(state, request.UserId, request.ColonyId, request.AuthToken, nowUtc, out string failure))
        {
            return Results.Ok(new AdminUpdateConfigurationResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.Unauthorized, failure),
                ToAdminConfigurationDto(state),
                updatedAtUtc: null));
        }

        if (request.Configuration is null)
        {
            return Results.Ok(new AdminUpdateConfigurationResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Admin.Configuration.Missing")),
                ToAdminConfigurationDto(state),
                updatedAtUtc: null));
        }

        ClashOfRimServerConfiguration updated = FromAdminConfigurationDto(
            request.Configuration,
            state.ServerConfiguration);
        ApplyAdminCompatibilityBaseline(state, request.Configuration, request.UserId, nowUtc);
        state.UpdateServerConfiguration(updated);
        AdminConfigurationDto persistedConfiguration = ToAdminConfigurationDto(state);
        state.ServerConfigurationOverrides.Replace(persistedConfiguration, request.UserId, nowUtc);
        state.AdminControl.AddAudit("UpdateConfiguration", request.UserId, targetUserId: null, message: null, nowUtc);
        RuntimeLogger(loggerFactory).LogInformation(
            "管理员更新服务器配置：user={UserId} trade={TradeEnabled} bank={BankEnabled} mercenaries={MercenariesEnabled} pvp={PvpEnabled} raidProtectionHours={RaidProtectionHours} raidMaxMinutes={RaidMaxMinutes} raidGraceMinutes={RaidGraceMinutes}",
            request.UserId,
            updated.TradeMarketplaceEnabled,
            updated.BankLoansEnabled,
            updated.MercenariesEnabled,
            updated.PvpEnabled,
            updated.RaidProtectionDuration.TotalHours,
            updated.RaidMaxDuration.TotalMinutes,
            updated.RaidTimeoutGracePeriod.TotalMinutes);

        return Results.Ok(new AdminUpdateConfigurationResponse(
            ProtocolResponse.Ok(T("Admin.Configuration.Updated")),
            persistedConfiguration,
            nowUtc));
    }

    private static IResult AdminAction(
        AdminActionRequest request,
        ClashOfRimNetworkState state,
        ILoggerFactory loggerFactory)
    {
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        if (!TryAuthorizeAdmin(state, request.UserId, request.ColonyId, request.AuthToken, nowUtc, out string failure))
        {
            return Results.Ok(new AdminActionResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.Unauthorized, failure),
                request.ActionKind ?? string.Empty,
                request.TargetUserId,
                state.AdminControl.MaintenanceLoginLocked,
                auditRecord: null,
                affectedOnlineUsers: 0));
        }

        string actionKind = NormalizeAdminActionKind(request.ActionKind);
        if (string.IsNullOrWhiteSpace(actionKind))
        {
            return Results.Ok(new AdminActionResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Admin.Action.Missing")),
                request.ActionKind ?? string.Empty,
                request.TargetUserId,
                state.AdminControl.MaintenanceLoginLocked,
                auditRecord: null,
                affectedOnlineUsers: 0));
        }

        int affected = 0;
        ProtocolResponse result;
        switch (actionKind)
        {
            case "Broadcast":
                affected = BroadcastAdminNotification(
                    state,
                    request.UserId,
                    request.TargetUserId,
                    request.Message,
                    ParseAdminBroadcastSeverity(request.NotificationSeverity),
                    request.PersistentNotification,
                    nowUtc);
                result = ProtocolResponse.Ok(T("Admin.Action.Broadcasted"));
                break;
            case "Kick":
                result = TryRequireTarget(request.TargetUserId, out string? kickTarget)
                    ? RejectSelfTarget(request.UserId, kickTarget!)
                        ?? KickUser(state, kickTarget!, request.TargetColonyId, out affected)
                    : ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Admin.Action.TargetMissing"));
                break;
            case "Ban":
                result = TryRequireTarget(request.TargetUserId, out string? banTarget)
                    ? RejectSelfTarget(request.UserId, banTarget!)
                        ?? BanUser(state, banTarget!, request.TargetColonyId, out affected)
                    : ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Admin.Action.TargetMissing"));
                break;
            case "Unban":
                result = TryRequireTarget(request.TargetUserId, out string? unbanTarget)
                    ? UnbanUser(state, unbanTarget!)
                    : ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Admin.Action.TargetMissing"));
                break;
            case "PromoteAdmin":
                result = TryRequireTarget(request.TargetUserId, out string? promoteTarget)
                    ? PromoteAdmin(state, promoteTarget!)
                    : ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Admin.Action.TargetMissing"));
                break;
            case "RevokeAdmin":
                result = TryRequireTarget(request.TargetUserId, out string? revokeTarget)
                    ? RejectSelfTarget(request.UserId, revokeTarget!)
                        ?? RevokeAdmin(state, revokeTarget!)
                    : ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Admin.Action.TargetMissing"));
                break;
            case "DeletePlayerSave":
                result = TryRequireTarget(request.TargetUserId, out string? deleteTarget)
                    ? RejectSelfTarget(request.UserId, deleteTarget!)
                        ?? DeletePlayerSave(state, deleteTarget!, request.TargetColonyId, nowUtc, out affected)
                    : ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Admin.Action.TargetMissing"));
                break;
            case "ResetOfflinePassword":
                result = TryRequireTarget(request.TargetUserId, out string? passwordTarget)
                    ? ResetOfflinePassword(state, passwordTarget!, request.Message, nowUtc)
                    : ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Admin.Action.TargetMissing"));
                break;
            case "LockMaintenance":
                state.AdminControl.SetMaintenanceLoginLocked(true, request.Message);
                result = ProtocolResponse.Ok(T("Admin.Action.MaintenanceLocked"));
                break;
            case "UnlockMaintenance":
                state.AdminControl.SetMaintenanceLoginLocked(false, null);
                result = ProtocolResponse.Ok(T("Admin.Action.MaintenanceUnlocked"));
                break;
            default:
                result = ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Admin.Action.Unknown"));
                break;
        }

        AdminAuditRecord? audit = null;
        if (result.Accepted)
        {
            audit = state.AdminControl.AddAudit(actionKind, request.UserId, request.TargetUserId, request.Message, nowUtc);
            if (string.Equals(actionKind, "PromoteAdmin", StringComparison.Ordinal)
                || string.Equals(actionKind, "RevokeAdmin", StringComparison.Ordinal))
            {
                SignalWorldConfigurationChanged(state, request.UserId, request.TargetUserId);
            }

            RuntimeLogger(loggerFactory).LogInformation(
                "管理员动作完成：action={ActionKind} actor={ActorUserId} target={TargetUserId} affectedOnline={AffectedOnlineUsers} maintenanceLocked={MaintenanceLocked}",
                actionKind,
                request.UserId,
                request.TargetUserId,
                affected,
                state.AdminControl.MaintenanceLoginLocked);
        }

        return Results.Ok(new AdminActionResponse(
            result,
            actionKind,
            request.TargetUserId,
            state.AdminControl.MaintenanceLoginLocked,
            audit?.ToDto(),
            affected));
    }

    private static bool TryAuthorizeAdmin(
        ClashOfRimNetworkState state,
        string userId,
        string colonyId,
        string? authToken,
        DateTimeOffset nowUtc,
        out string failure)
    {
        failure = T("Admin.Unauthorized");
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(colonyId))
        {
            failure = T("Admin.IdentityMissing");
            return false;
        }

        if (!state.AuthTokens.TryGetPrincipal(authToken, nowUtc, out AuthTokenPrincipal? principal)
            || principal is null
            || !string.Equals(principal.UserId, userId, StringComparison.Ordinal)
            || !string.Equals(principal.ColonyId, colonyId, StringComparison.Ordinal))
        {
            return false;
        }

        if (!state.LoginSessions.Refresh(principal.UserId, principal.ColonyId, principal.SessionId, nowUtc))
        {
            failure = T("Auth.SessionExpired");
            return false;
        }

        if (!state.WorldConfiguration.IsAdministrator(principal.UserId))
        {
            return false;
        }

        return true;
    }

    private static AdminConfigurationDto ToAdminConfigurationDto(ClashOfRimNetworkState state)
    {
        ClashOfRimServerConfiguration configuration = state.ServerConfiguration;
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
            BuildAdminCompatibilityMods(state),
            configuration.RaidMaxDuration.TotalMinutes,
            configuration.RaidTimeoutGracePeriod.TotalMinutes,
            configuration.TradeFeePolicy.FeeStrategy,
            configuration.GiftsEnabled);
    }

    private static ClashOfRimServerConfiguration FromAdminConfigurationDto(
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
                .Select(point => $"{point.DurationDays}:{point.Multiplier.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}"));
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
                    $"{stage.TriggerPenaltyCount}:{stage.Kind}:{stage.Severity:0.###}")));
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

    private static IReadOnlyList<AdminCompatibilityModDto> BuildAdminCompatibilityMods(ClashOfRimNetworkState state)
    {
        CompatibilityManifest? baseline = state.CompatibilityBaseline.Current;
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
                        state.ServerConfiguration.CompatibilityOptions.ResolveConfigMode(mod.PackageId, config.FileName).ToString()))
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

    private static void ApplyAdminCompatibilityBaseline(
        ClashOfRimNetworkState state,
        AdminConfigurationDto dto,
        string actorUserId,
        DateTimeOffset nowUtc)
    {
        CompatibilityManifest? baseline = state.CompatibilityBaseline.Current;
        if (baseline is null)
        {
            return;
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
        state.CompatibilityBaseline.ReplaceBaseline(baseline with { Mods = mods }, actorUserId, nowUtc);
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

    private static IReadOnlyList<AdminPlayerSummaryDto> BuildAdminPlayerSummaries(ClashOfRimNetworkState state)
    {
        return state.Players.List()
            .Select(player => new AdminPlayerSummaryDto(
                player.UserId,
                player.ColonyId,
                player.CurrentSnapshotId,
                state.OnlinePresence.IsUserOnline(player.UserId),
                player.LastSeenAtUtc,
                player.DisplayName,
                state.WorldConfiguration.IsAdministrator(player.UserId),
                state.AdminControl.IsBanned(player.UserId)))
            .OrderByDescending(player => player.Online)
            .ThenBy(player => player.UserId, StringComparer.Ordinal)
            .ToList();
    }

    private static string NormalizeAdminActionKind(string? actionKind)
    {
        return string.IsNullOrWhiteSpace(actionKind) ? string.Empty : actionKind.Trim();
    }

    private static bool TryRequireTarget(string? targetUserId, out string? target)
    {
        target = string.IsNullOrWhiteSpace(targetUserId) ? null : targetUserId.Trim();
        return !string.IsNullOrWhiteSpace(target);
    }

    private static ProtocolResponse? RejectSelfTarget(string actorUserId, string targetUserId)
    {
        return string.Equals(actorUserId, targetUserId, StringComparison.Ordinal)
            ? ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, T("Admin.Action.CannotTargetSelf"))
            : null;
    }

    private static ProtocolResponse KickUser(
        ClashOfRimNetworkState state,
        string targetUserId,
        string? targetColonyId,
        out int affected)
    {
        affected = 0;
        if (state.LoginSessions.EndUser(targetUserId))
        {
            affected++;
        }

        if (state.OnlinePresence.ForceDisconnect(targetUserId))
        {
            affected++;
        }

        if (!string.IsNullOrWhiteSpace(targetColonyId))
        {
            state.AuthTokens.RevokeForColony(targetUserId, targetColonyId!);
        }
        else
        {
            state.AuthTokens.RevokeForUser(targetUserId);
        }

        state.EventNotifications.SignalUser(targetUserId);
        return ProtocolResponse.Ok(T("Admin.Action.Kicked"));
    }

    private static ProtocolResponse BanUser(
        ClashOfRimNetworkState state,
        string targetUserId,
        string? targetColonyId,
        out int affected)
    {
        state.AdminControl.Ban(targetUserId);
        ProtocolResponse kicked = KickUser(state, targetUserId, targetColonyId, out affected);
        return kicked.Accepted
            ? ProtocolResponse.Ok(T("Admin.Action.Banned"))
            : kicked;
    }

    private static ProtocolResponse UnbanUser(ClashOfRimNetworkState state, string targetUserId)
    {
        state.AdminControl.Unban(targetUserId);
        return ProtocolResponse.Ok(T("Admin.Action.Unbanned"));
    }

    private static ProtocolResponse PromoteAdmin(ClashOfRimNetworkState state, string targetUserId)
    {
        state.WorldConfiguration.PromoteAdministrator(targetUserId);
        return ProtocolResponse.Ok(T("Admin.Action.Promoted"));
    }

    private static ProtocolResponse RevokeAdmin(ClashOfRimNetworkState state, string targetUserId)
    {
        return state.WorldConfiguration.RevokeAdministrator(targetUserId)
            ? ProtocolResponse.Ok(T("Admin.Action.Revoked"))
            : ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, T("Admin.Action.LastAdmin"));
    }

    private static ProtocolResponse DeletePlayerSave(
        ClashOfRimNetworkState state,
        string targetUserId,
        string? targetColonyId,
        DateTimeOffset nowUtc,
        out int affected)
    {
        affected = 0;
        string? colonyId = string.IsNullOrWhiteSpace(targetColonyId)
            ? state.Players.FindByUserId(targetUserId)?.ColonyId
            : targetColonyId.Trim();
        if (string.IsNullOrWhiteSpace(colonyId))
        {
            return ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Admin.Action.TargetColonyMissing"));
        }

        if (state.LoginSessions.EndUser(targetUserId))
        {
            affected++;
        }

        if (state.OnlinePresence.ForceDisconnect(targetUserId))
        {
            affected++;
        }

        LatestSnapshotRecord? latest = state.SnapshotStore.GetLatest(targetUserId, colonyId);
        CleanupAbandonedColony(state, targetUserId, colonyId, latest, nowUtc);
        return ProtocolResponse.Ok(T("Admin.Action.PlayerSaveDeleted"));
    }

    private static ProtocolResponse ResetOfflinePassword(
        ClashOfRimNetworkState state,
        string targetUserId,
        string? newPassword,
        DateTimeOffset nowUtc)
    {
        return state.OfflineAccounts.ResetPassword(targetUserId, newPassword, nowUtc, out string failure)
            ? ProtocolResponse.Ok(T("Admin.Action.PasswordReset"))
            : ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, LocalizeOfflineAccountFailure(failure));
    }

    private static int BroadcastAdminNotification(
        ClashOfRimNetworkState state,
        string actorUserId,
        string? targetUserId,
        string? message,
        ServerNotificationSeverity severity,
        bool persistentNotification,
        DateTimeOffset nowUtc)
    {
        string normalizedMessage = string.IsNullOrWhiteSpace(message) ? T("Admin.Broadcast.DefaultMessage") : message.Trim();
        IReadOnlyList<PlayerSessionRecord> players = state.Players.List()
            .Where(player => string.IsNullOrWhiteSpace(targetUserId)
                || string.Equals(player.UserId, targetUserId, StringComparison.Ordinal))
            .Where(player => persistentNotification || state.OnlinePresence.IsUserOnline(player.UserId))
            .ToList();

        foreach (PlayerSessionRecord player in players)
        {
            string idempotencyKey = $"admin-broadcast:{actorUserId}:{player.UserId}:{nowUtc:O}";
            AuthoritativeEvent notification = AuthoritativeEventFactory.Create(
                ServerEventType.ServerNotification,
                new EventParty("server"),
                new EventParty(player.UserId, player.ColonyId),
                idempotencyKey,
                state.OnlinePresence.IsUserOnline(player.UserId),
                new ServerNotificationEventPayload(
                    idempotencyKey,
                    T("Admin.Broadcast.Title"),
                    normalizedMessage,
                    severity,
                    FromAdministrator: true,
                    actorUserId,
                    OnlineOnly: !persistentNotification),
                nowUtc);
            LogEventAppend(state, state.Ledger.Append(notification), "admin-broadcast");
        }

        state.EventNotifications.SignalUsers(players.Select(player => player.UserId));
        return players.Count;
    }

    private static ServerNotificationSeverity ParseAdminBroadcastSeverity(string? severity)
    {
        return string.Equals(severity, "Warning", StringComparison.OrdinalIgnoreCase)
            ? ServerNotificationSeverity.Warning
            : string.Equals(severity, "Critical", StringComparison.OrdinalIgnoreCase)
                || string.Equals(severity, "Error", StringComparison.OrdinalIgnoreCase)
                    ? ServerNotificationSeverity.Critical
                    : ServerNotificationSeverity.Info;
    }
}
