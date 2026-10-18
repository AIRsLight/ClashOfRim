using AIRsLight.ClashOfRim.Network.Modules.World;
using static AIRsLight.ClashOfRim.Network.Modules.Compatibility.CompatibilityHandshakeContract;
using AIRsLight.ClashOfRim.Network.Modules.Accounts;
using static AIRsLight.ClashOfRim.Network.Modules.World.WorldTileGeometry;
using AIRsLight.ClashOfRim.Compatibility;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network.Plugins;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Globalization;

namespace AIRsLight.ClashOfRim.Network;

public static partial class ClashOfRimNetworkServer
{
    private static IResult GetAdminBaselineRequirements(GetAdminBaselineRequirementsRequest request, ClashOfRimNetworkState state)
    {
        IResult Reject(ProtocolResponse failure) => Results.Ok(new GetAdminBaselineRequirementsResponse(
            failure, false, state.AdminBaseline.Current is not null, null));
        if (!string.Equals(request.ProtocolVersion, ProtocolApiVersion.Current, StringComparison.Ordinal))
            return Reject(ProtocolResponse.Reject(ProtocolErrorCode.IncompatibleProtocolVersion, T("Protocol.IncompatibleVersion")));
        if (string.IsNullOrWhiteSpace(request.UserId) || string.IsNullOrWhiteSpace(request.ColonyId))
            return Reject(ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("AdminBaseline.MissingIdentity")));
        using (var recovery = EnterRecoveredSnapshotMutation(state,
            () => AuthorizeAdministratorRequest(state, request.UserId, request.ColonyId, request.AuthToken)))
        {
            if (recovery.Rejection is { } failure) return Reject(failure);
            WorldSessionState session = state.WorldConfiguration.GetSessionState(request.UserId);
            IReadOnlySet<string> packageIds = BuildServerPackageIdSet(state.CompatibilityBaseline.Current);
            var context = new AdminBaselineRequirementContext(packageIds);
            List<AdminBaselineExtensionRequirementDto> requirements = state.Plugins.AdminBaselineRequirementProviders
                .SelectMany(provider => SafeAdminBaselineRequirements(provider, context))
                .Where(requirement => !string.IsNullOrWhiteSpace(requirement.ProviderId)
                    && !string.IsNullOrWhiteSpace(requirement.Kind))
                .GroupBy(requirement => requirement.ProviderId + "\u001f" + requirement.Kind, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.Last())
                .OrderBy(requirement => requirement.ProviderId, StringComparer.Ordinal)
                .ThenBy(requirement => requirement.Kind, StringComparer.Ordinal)
                .Select(requirement => new AdminBaselineExtensionRequirementDto(
                    requirement.ProviderId,
                    requirement.Kind,
                    requirement.RequiredPackageId,
                    requirement.DisplayName))
                .ToList();

            return Results.Ok(new GetAdminBaselineRequirementsResponse(
                ProtocolResponse.Ok(T("AdminBaseline.RequirementsPrepared")),
                isAdministrator: true,
                baselineConfigured: state.AdminBaseline.Current is not null,
                session.AdministratorUserId,
                requirements));
        }
    }

    private static IEnumerable<AdminBaselineExtensionRequirement> SafeAdminBaselineRequirements(
        IAdminBaselineRequirementProvider provider,
        AdminBaselineRequirementContext context)
    {
        try
        {
            return provider.GetRequirements(context) ?? Array.Empty<AdminBaselineExtensionRequirement>();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                "[ClashOfRim][ServerPlugin][Error] AdminBaselineRequirementProviderCallbackException: "
                + provider.GetType().FullName
                + " "
                + ex.GetType().Name
                + " "
                + ex.Message);
            return Array.Empty<AdminBaselineExtensionRequirement>();
        }
    }

    private static IReadOnlySet<string> BuildServerPackageIdSet(CompatibilityManifest? manifest)
    {
        return new HashSet<string>(
            (manifest?.Mods ?? Array.Empty<ModManifestEntry>())
            .Select(mod => mod.PackageId)
            .Where(packageId => !string.IsNullOrWhiteSpace(packageId)),
            StringComparer.OrdinalIgnoreCase);
    }

    private static IResult SubmitAdminBaseline(SubmitAdminBaselineRequest request, ClashOfRimNetworkState state)
    {
        IResult Reject(ProtocolResponse failure) => Results.Ok(new SubmitAdminBaselineResponse(
            failure, false, state.AdminBaseline.Current is not null, null, 0, 0, 0, 0, 0, 0, 0));
        if (!string.Equals(request.ProtocolVersion, ProtocolApiVersion.Current, StringComparison.Ordinal))
            return Reject(ProtocolResponse.Reject(ProtocolErrorCode.IncompatibleProtocolVersion, T("Protocol.IncompatibleVersion")));
        if (string.IsNullOrWhiteSpace(request.UserId) || string.IsNullOrWhiteSpace(request.ColonyId))
            return Reject(ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("AdminBaseline.MissingIdentity")));
        ProtocolResponse? Authorize() => AuthorizeAdministratorRequest(state, request.UserId, request.ColonyId, request.AuthToken);
        if (Authorize() is { } initialFailure) return Reject(initialFailure);
        // Normalize potentially large price/definition collections without owning the mutation gate.
        AdminBaselineSnapshot normalized = AdminBaselineSnapshot.FromRequest(request);
        using (var recovery = EnterRecoveredSnapshotMutation(state, Authorize))
        {
            if (recovery.Rejection is { } failure) return Reject(failure);
            WorldSessionState session = state.WorldConfiguration.GetSessionState(request.UserId);
            DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
            var invalidations = state.TradeLifecycle.PrepareTradeOrderBaselineInvalidations(normalized, nowUtc);
            AdminBaselineSnapshot baseline = state.AdminBaseline.SubmitPrepared(normalized, invalidations);
            state.TradeLifecycle.RecoverAdminBaselineTradeInvalidations();
            int cancelledTradeOrderCount = invalidations.Count;
            if (!string.IsNullOrWhiteSpace(request.ColonyId))
            {
                state.PlayerActivity.Record(request.UserId, request.ColonyId!, currentSnapshotId: null, nowUtc);
            }

            return Results.Ok(new SubmitAdminBaselineResponse(
                ProtocolResponse.Ok(cancelledTradeOrderCount > 0
                    ? T("AdminBaseline.RegisteredWithCancelledTrades", ("COUNT", cancelledTradeOrderCount.ToString(CultureInfo.InvariantCulture)))
                    : T("AdminBaseline.Registered")),
                isAdministrator: true,
                baselineConfigured: true,
                session.AdministratorUserId,
                baseline.StandardMarketValuePerThing.Count,
                baseline.TrapAutoApprovedCount,
                baseline.TrapCandidateCount,
                baseline.TrapApprovedCount,
                baseline.PackableBuildingCount,
                baseline.BuildingCount,
                baseline.BaselineExtensionCount));
        }
    }

    private static IResult OverrideCompatibilityBaseline(
        OverrideCompatibilityBaselineRequest request,
        ClashOfRimNetworkState state,
        ILoggerFactory loggerFactory)
    {
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        AuthenticationValidationResult auth = state.Accounts.Authenticate(request.UserId, request.SteamAuthTicket, request.Password, nowUtc);
        if (!auth.Accepted || string.IsNullOrWhiteSpace(auth.AuthenticatedUserId))
        {
            return Results.Ok(new OverrideCompatibilityBaselineResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, auth.Message ?? T("Steam.Failed")),
                state.CompatibilityBaseline.Current is null ? null : state.CompatibilityHandshake.Serialize(state.CompatibilityBaseline.Current),
                updatedAtUtc: null));
        }

        string authenticatedUserId = auth.AuthenticatedUserId!.Trim();
        if (!state.WorldConfiguration.IsAdministrator(authenticatedUserId))
        {
            return Results.Ok(new OverrideCompatibilityBaselineResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.Unauthorized, T("Compatibility.OverrideAdminOnly")),
                state.CompatibilityBaseline.Current is null ? null : state.CompatibilityHandshake.Serialize(state.CompatibilityBaseline.Current),
                updatedAtUtc: null));
        }

        if (!TryReadCompatibilityManifest(
                request.CompatibilityManifestJson,
                out CompatibilityManifest? manifest,
                out string failure)
            || manifest is null)
        {
            return Results.Ok(new OverrideCompatibilityBaselineResponse(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, failure),
                state.CompatibilityBaseline.Current is null ? null : state.CompatibilityHandshake.Serialize(state.CompatibilityBaseline.Current),
                updatedAtUtc: null));
        }

        CompatibilityBaselineUpdateResult update = state.CompatibilityBaseline.ReplaceBaseline(
            manifest,
            authenticatedUserId,
            nowUtc);
        RuntimeLogger(loggerFactory).LogInformation(
            "兼容清单基线已覆盖：user={UserId} manifest={ManifestId} mods={ModCount} dlcs={DlcCount}",
            authenticatedUserId,
            manifest.ManifestId,
            manifest.Mods.Count,
            manifest.DlcIds.Count);
        return Results.Ok(new OverrideCompatibilityBaselineResponse(
            ProtocolResponse.Ok(T("Compatibility.OverrideSuccess")),
            update.Baseline is null ? null : state.CompatibilityHandshake.Serialize(update.Baseline),
            update.UpdatedAtUtc));
    }

}
