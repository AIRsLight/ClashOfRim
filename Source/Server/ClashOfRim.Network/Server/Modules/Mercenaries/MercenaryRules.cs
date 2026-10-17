using AIRsLight.ClashOfRim.Protocol;
using static AIRsLight.ClashOfRim.Network.Modules.Bank.BankLoanTerms;
using static AIRsLight.ClashOfRim.Network.ServerText;
using System.Globalization;

namespace AIRsLight.ClashOfRim.Network.Modules.Mercenaries;

internal static class MercenaryRules
{
    internal const int RecallShuttleDestroyedFineSilver = 6000;

    internal static bool IsAllowedMercenarySkill(string? skillDefName)
    {
        return skillDefName is
            "Construction" or
            "Plants" or
            "Intellectual" or
            "Mining" or
            "Shooting" or
            "Melee" or
            "Social" or
            "Animals" or
            "Cooking" or
            "Medicine" or
            "Artistic" or
            "Crafting" or
            "Hauling";
    }

    internal static ProtocolResponse? ValidateMercenaryRequestShape(
        string? skillDefName,
        int skillLevel,
        int durationDays,
        ClashOfRimServerConfiguration configuration)
    {
        if (!IsAllowedMercenarySkill(skillDefName))
        {
            return ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, Format("Mercenary.InvalidSkill"));
        }

        if (skillLevel is not (7 or 14 or 20))
        {
            return ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, Format("Mercenary.InvalidTier"));
        }

        if (durationDays < configuration.MercenaryMinDurationDays
            || durationDays > configuration.MercenaryMaxDurationDays)
        {
            return ProtocolResponse.Reject(
                ProtocolErrorCode.ValidationFailed,
                Format(
                    "Mercenary.InvalidDuration",
                    ("MIN", configuration.MercenaryMinDurationDays.ToString(CultureInfo.InvariantCulture)),
                    ("MAX", configuration.MercenaryMaxDurationDays.ToString(CultureInfo.InvariantCulture))));
        }

        return null;
    }

    internal static int CalculateMercenaryDeathFineSilver(int skillLevel, ClashOfRimServerConfiguration configuration)
    {
        return skillLevel switch
        {
            7 => configuration.MercenaryApprenticeDeathFineSilver,
            14 => configuration.MercenarySkilledDeathFineSilver,
            20 => configuration.MercenaryMasterDeathFineSilver,
            _ => 0
        };
    }

    internal static int CalculateMercenaryOvertimeFineSilver(MercenaryContractRecord contract)
    {
        int durationDays = Math.Max(1, contract.DurationDays);
        return Math.Max(1, (int)Math.Ceiling(contract.PriceSilver / (double)durationDays * 3d));
    }

    internal static string NormalizeMercenaryGuardTier(string? tier)
    {
        if (string.Equals(tier, "Skilled", StringComparison.OrdinalIgnoreCase))
        {
            return "Skilled";
        }

        if (string.Equals(tier, "Master", StringComparison.OrdinalIgnoreCase))
        {
            return "Master";
        }

        return "Apprentice";
    }

    internal static int CalculateMercenaryGuardPriceSilver(
        string tier,
        ClashOfRimServerConfiguration configuration)
    {
        return Math.Max(0, tier switch
        {
            "Skilled" => configuration.MercenaryGuardSkilledSilver,
            "Master" => configuration.MercenaryGuardMasterSilver,
            _ => configuration.MercenaryGuardApprenticeSilver
        });
    }

    internal static float CalculateMercenaryGuardPointRatio(
        string tier,
        ClashOfRimServerConfiguration configuration)
    {
        return Math.Max(0f, tier switch
        {
            "Skilled" => configuration.MercenaryGuardSkilledPointsRatio,
            "Master" => configuration.MercenaryGuardMasterPointsRatio,
            _ => configuration.MercenaryGuardApprenticePointsRatio
        });
    }

    internal static ProtocolResponse? ValidateMercenaryActiveLimit(
        MercenaryContractRegistry contracts, ClashOfRimServerConfiguration configuration,
        string userId,
        string colonyId)
    {
        int limit = configuration.MaxActiveMercenariesPerColony;
        if (limit <= 0)
        {
            return ProtocolResponse.Reject(
                ProtocolErrorCode.ServerRejected,
                Format("Mercenary.ActiveLimitReached", ("MAX", "0")));
        }

        int active = contracts.CountOpenForColony(userId, colonyId);
        return active >= limit
            ? ProtocolResponse.Reject(
                ProtocolErrorCode.ServerRejected,
                Format("Mercenary.ActiveLimitReached", ("MAX", limit.ToString(CultureInfo.InvariantCulture))))
            : null;
    }

    internal static int CalculateMercenaryPriceSilver(
        int skillLevel,
        int durationDays,
        ClashOfRimServerConfiguration configuration)
    {
        int daily = skillLevel switch
        {
            7 => configuration.MercenaryApprenticeDailySilver,
            14 => configuration.MercenarySkilledDailySilver,
            20 => configuration.MercenaryMasterDailySilver,
            _ => 0
        };
        double price = Math.Max(0, daily)
            * Math.Max(0, durationDays)
            * InterpolateBankInterestDurationMultiplier(
                durationDays,
                configuration.MercenaryDurationMultiplierCurve);
        return (int)Math.Ceiling(price);
    }

    internal static ProtocolResponse? ValidateMercenaryIncidentShape(ReportMercenaryIncidentRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey) || string.IsNullOrWhiteSpace(request.ContractId))
            return ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, Format("Mercenary.IncidentMissingFields"));
        return request.IncidentKind is "Completed" or "Death" or "HostileBehavior" or "HarmfulSurgery" or "OvertimeService" or "ShuttleDestroyed"
            ? null : ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, Format("Mercenary.InvalidIncidentKind"));
    }

    internal static ProtocolResponse? ValidateMercenaryAvailability(MercenaryContractRegistry contracts,
        string userId, string colonyId, string skill, int level, int days, ClashOfRimServerConfiguration configuration)
    {
        if (!configuration.MercenariesEnabled)
            return ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, Format("Mercenary.Disabled"));
        return ValidateMercenaryRequestShape(skill, level, days, configuration)
            ?? ValidateMercenaryActiveLimit(contracts, configuration, userId, colonyId);
    }

    internal static ProtocolResponse? ValidateGuardAvailability(MercenaryGuardContractRegistry guards,
        string userId, string colonyId, ClashOfRimServerConfiguration configuration)
    {
        if (!configuration.PvpEnabled)
            return ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, Format("Mercenary.GuardPvpDisabled"));
        if (!configuration.MercenariesEnabled || !configuration.MercenaryGuardsEnabled)
            return ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, Format("Mercenary.GuardDisabled"));
        return guards.HasActiveForColony(userId, colonyId)
            ? ProtocolResponse.Reject(ProtocolErrorCode.Conflict, Format("Mercenary.GuardAlreadyActive")) : null;
    }

    internal static bool MatchesMercenarySnapshot(MercenaryContractRecord? contract, HireMercenaryWithSnapshotRequest request) =>
        contract is not null && contract.Status is MercenaryContractRegistry.StatusActive or MercenaryContractRegistry.StatusCompleted
        && contract.ActivatedAtUtc is not null && contract.UserId == request.UserId && contract.ColonyId == request.ColonyId
        && contract.SnapshotId == request.ConfirmedSnapshot?.SnapshotId && contract.ContractId == request.RequestedContractId
        && contract.SkillDefName == request.SkillDefName && contract.SkillLevel == request.SkillLevel && contract.DurationDays == request.DurationDays
        && contract.PriceSilver == request.ExpectedPriceSilver && contract.HarmfulSurgeryFineSilver == request.ExpectedHarmfulSurgeryFineSilver
        && contract.DeathFineSilver == request.ExpectedDeathFineSilver;

    internal static bool MatchesGuardSnapshot(MercenaryGuardContractRecord? contract, HireMercenaryGuardWithSnapshotRequest request) =>
        contract is not null && contract.Status is MercenaryGuardContractRegistry.StatusActive or MercenaryGuardContractRegistry.StatusConsumed
        && contract.UserId == request.UserId && contract.ColonyId == request.ColonyId
        && contract.SnapshotId == request.ConfirmedSnapshot?.SnapshotId && contract.ContractId == request.RequestedContractId
        && contract.Tier == NormalizeMercenaryGuardTier(request.Tier) && contract.PriceSilver == request.ExpectedPriceSilver
        && float.IsFinite(request.ExpectedPointRatio) && Math.Abs(contract.PointRatio - request.ExpectedPointRatio) <= 0.0001f;

    internal static MercenaryContractDto ToMercenaryContractDto(MercenaryContractRecord contract)
    {
        return new MercenaryContractDto(
            contract.ContractId,
            contract.SkillDefName,
            contract.SkillLevel,
            contract.DurationDays,
            contract.PriceSilver,
            contract.HarmfulSurgeryFineSilver,
            contract.DeathFineSilver,
            contract.CreatedAtGameTicks,
            contract.ExpiresAtGameTicks);
    }

    internal static MercenaryGuardContractDto ToMercenaryGuardContractDto(MercenaryGuardContractRecord contract)
    {
        return new MercenaryGuardContractDto(
            contract.ContractId,
            contract.Tier,
            contract.PriceSilver,
            contract.PointRatio,
            contract.CreatedAtGameTicks);
    }
}
