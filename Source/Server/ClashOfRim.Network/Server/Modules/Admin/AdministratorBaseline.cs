using AIRsLight.ClashOfRim.Network.Modules.Authorization;
using AIRsLight.ClashOfRim.Network.Modules.Sessions;
using AIRsLight.ClashOfRim.Network.Modules.Snapshots;
using AIRsLight.ClashOfRim.Network.Modules.Trades;
using AIRsLight.ClashOfRim.Network.Plugins;
using AIRsLight.ClashOfRim.Protocol;
using System.Globalization;
using static AIRsLight.ClashOfRim.Network.ServerText;

namespace AIRsLight.ClashOfRim.Network.Modules.Admin;

internal sealed class AdministratorBaseline(AdministratorAuthorization authorization, WorldConfigurationRegistry world,
    CompatibilityBaselineRegistry compatibility, ServerPluginRegistry plugins, AdminBaselineRegistry baseline,
    TradeLifecycle trades, SnapshotMutationCoordinator mutations, PlayerSessionActivity activity)
{
    internal GetAdminBaselineRequirementsResponse Requirements(GetAdminBaselineRequirementsRequest request)
    {
        GetAdminBaselineRequirementsResponse Reject(ProtocolResponse failure) => new(
            failure, false, baseline.Current is not null, null);
        if (Validate(request.ProtocolVersion, request.UserId, request.ColonyId) is { } invalid) return Reject(invalid);
        using var recovery = mutations.Enter(() => authorization.Authorize(request.UserId, request.ColonyId, request.AuthToken));
        if (recovery.Rejection is { } failure) return Reject(failure);
        WorldSessionState session = world.GetSessionState(request.UserId);
        var packageIds = new HashSet<string>(
            (compatibility.Current?.Mods ?? []).Select(mod => mod.PackageId).Where(id => !string.IsNullOrWhiteSpace(id)),
            StringComparer.OrdinalIgnoreCase);
        var context = new AdminBaselineRequirementContext(packageIds);
        var requirements = plugins.AdminBaselineRequirementProviders
            .SelectMany(provider => ReadRequirements(provider, context))
            .Where(requirement => !string.IsNullOrWhiteSpace(requirement.ProviderId) && !string.IsNullOrWhiteSpace(requirement.Kind))
            .GroupBy(requirement => requirement.ProviderId, StringComparer.OrdinalIgnoreCase)
            .SelectMany(group => group.GroupBy(requirement => requirement.Kind, StringComparer.OrdinalIgnoreCase)
                .Select(kind => kind.Last()))
            .OrderBy(requirement => requirement.ProviderId, StringComparer.Ordinal)
            .ThenBy(requirement => requirement.Kind, StringComparer.Ordinal)
            .Select(requirement => new AdminBaselineExtensionRequirementDto(
                requirement.ProviderId, requirement.Kind, requirement.RequiredPackageId, requirement.DisplayName))
            .ToList();
        return new(ProtocolResponse.Ok(Format("AdminBaseline.RequirementsPrepared")), true,
            baseline.Current is not null, session.AdministratorUserId, requirements);
    }

    internal SubmitAdminBaselineResponse Submit(SubmitAdminBaselineRequest request)
    {
        SubmitAdminBaselineResponse Reject(ProtocolResponse failure) => new(
            failure, false, baseline.Current is not null, null, 0, 0, 0, 0, 0, 0, 0);
        if (Validate(request.ProtocolVersion, request.UserId, request.ColonyId) is { } invalid) return Reject(invalid);
        ProtocolResponse? Authorize() => authorization.Authorize(request.UserId, request.ColonyId, request.AuthToken);
        if (Authorize() is { } initialFailure) return Reject(initialFailure);
        // Large definition and price collections are normalized before taking the mutation gate.
        AdminBaselineSnapshot normalized = AdminBaselineSnapshot.FromRequest(request);
        using var recovery = mutations.Enter(Authorize);
        if (recovery.Rejection is { } failure) return Reject(failure);
        WorldSessionState session = world.GetSessionState(request.UserId);
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        var invalidations = trades.PrepareTradeOrderBaselineInvalidations(normalized, nowUtc);
        AdminBaselineSnapshot saved = baseline.SubmitPrepared(normalized, invalidations);
        trades.RecoverAdminBaselineTradeInvalidations();
        activity.Record(request.UserId, request.ColonyId!, currentSnapshotId: null, nowUtc);
        return new(ProtocolResponse.Ok(invalidations.Count > 0
                ? Format("AdminBaseline.RegisteredWithCancelledTrades", ("COUNT", invalidations.Count.ToString(CultureInfo.InvariantCulture)))
                : Format("AdminBaseline.Registered")),
            true, true, session.AdministratorUserId, saved.StandardMarketValuePerThing.Count,
            saved.TrapAutoApprovedCount, saved.TrapCandidateCount, saved.TrapApprovedCount,
            saved.PackableBuildingCount, saved.BuildingCount, saved.BaselineExtensionCount);
    }

    private static ProtocolResponse? Validate(string protocolVersion, string userId, string? colonyId)
    {
        if (!string.Equals(protocolVersion, ProtocolApiVersion.Current, StringComparison.Ordinal))
            return ProtocolResponse.Reject(ProtocolErrorCode.IncompatibleProtocolVersion, Format("Protocol.IncompatibleVersion"));
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(colonyId))
            return ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, Format("AdminBaseline.MissingIdentity"));
        return null;
    }

    private static IReadOnlyList<AdminBaselineExtensionRequirement> ReadRequirements(
        IAdminBaselineRequirementProvider provider, AdminBaselineRequirementContext context)
    {
        try
        {
            // Enumeration is part of the plugin call: a lazy iterator can fail after returning.
            return (provider.GetRequirements(context) ?? []).Where(requirement => requirement is not null).ToArray();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[ClashOfRim][ServerPlugin][Error] AdminBaselineRequirementProviderCallbackException: "
                + provider.GetType().FullName + " " + ex.GetType().Name + " " + ex.Message);
            return [];
        }
    }
}
