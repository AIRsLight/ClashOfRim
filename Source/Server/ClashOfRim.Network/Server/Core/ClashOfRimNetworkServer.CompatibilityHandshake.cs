using AIRsLight.ClashOfRim.Network.Modules.Accounts;
using AIRsLight.ClashOfRim.Compatibility;
using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Network;

public static partial class ClashOfRimNetworkServer
{
    private sealed record CompatibilityHandshakeInput(bool HasFullManifest, string ManifestId,
        CompatibilityManifest? Manifest, string? ManifestFailure, CompatibilityManifestSummary? Summary, string SummaryFailure);

    private static CompatibilityHandshakeInput PrepareCompatibilityHandshakeInput(string? json, string? id, string? summaryJson)
    {
        bool full = !string.IsNullOrWhiteSpace(json);
        CompatibilityManifest? manifest = null;
        CompatibilityManifestSummary? summary = null;
        string? failure = null;
        string summaryFailure = string.Empty;
        if (full)
        {
            if (!TryReadCompatibilityManifest(json, out manifest, out string reason)) failure = reason;
            else if (manifest is not null && !ValidCompatibilityManifestShape(manifest))
                failure = T("Compatibility.InvalidManifestJson", ("MESSAGE", "Manifest contains missing or duplicate fields."));
        }
        else
        {
            TryReadCompatibilityManifestSummary(summaryJson, out summary, out summaryFailure);
            if (summary is not null && (!ValidCompatibilityHeader(summary.SchemaVersion, summary.ManifestId, summary.ProtocolVersion, summary.RimWorldVersion)
                || !UniqueCompatibilityKeys(summary.DlcIds, x => x) || !UniqueCompatibilityKeys(summary.Mods, x => x.PackageId)
                || !UniqueCompatibilityKeys(summary.DefSummaries, x => x.Name)))
            {
                summary = null;
                summaryFailure = T("Compatibility.InvalidManifestJson", ("MESSAGE", "Summary contains missing or duplicate fields."));
            }
        }
        return new(full, (id ?? string.Empty).Trim(), manifest, failure, summary, summaryFailure);
    }

    private static bool ValidCompatibilityHeader(int schema, string? id, string? protocol, string? game)
        => schema > 0 && !string.IsNullOrWhiteSpace(id) && protocol == ProtocolApiVersion.Current && !string.IsNullOrWhiteSpace(game);

    private static bool UniqueCompatibilityKeys<T>(IReadOnlyList<T>? values, Func<T, string?> key) where T : class
    {
        if (values is null) return false;
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return values.All(value => value is not null && key(value) is { } name && !string.IsNullOrWhiteSpace(name) && keys.Add(name.Trim()));
    }

    private static bool ValidCompatibilityManifestShape(CompatibilityManifest manifest)
        => ValidCompatibilityHeader(manifest.SchemaVersion, manifest.ManifestId, manifest.ProtocolVersion, manifest.RimWorldVersion)
            && UniqueCompatibilityKeys(manifest.DlcIds, x => x) && UniqueCompatibilityKeys(manifest.DefSummaries, x => x.Name)
            && manifest.ModConfigRules is not null && manifest.ModConfigRules.All(x => x is not null)
            && UniqueCompatibilityKeys(manifest.Mods, x => x.PackageId)
            && manifest.Mods.All(mod => UniqueCompatibilityKeys(mod.Files, x => x.RelativePath?.Replace('\\', '/').TrimStart('/'))
                && UniqueCompatibilityKeys(mod.Configs, x => x.FileName));

    // Compare against current policy without registering an account or assigning an administrator.
    private static CompatibilityHandshakeResult EvaluateCompatibilityHandshake(ClashOfRimNetworkState state,
        AuthenticationValidationResult auth, CompatibilityHandshakeInput input, DateTimeOffset nowUtc, bool publishBaseline)
    {
        CompatibilityManifest? serverCompatibilityManifest = state.CompatibilityBaseline.Current;
        if (!auth.Accepted || string.IsNullOrWhiteSpace(auth.AuthenticatedUserId))
        {
            return new CompatibilityHandshakeResult(
                ProtocolResponse.Reject(auth.ErrorCode, auth.Message ?? T("Steam.Failed")),
                SteamId: null,
                DisplayName: null,
                serverCompatibilityManifest is null ? null : SerializeCompatibilityManifest(serverCompatibilityManifest, state),
                Array.Empty<CompatibilityIssueDto>(),
                CanOverrideCompatibilityBaseline: false,
                RequiresFullCompatibilityManifest: false,
                RequestedCompatibilityPackageIds: Array.Empty<string>());
        }

        string authenticatedUserId = auth.AuthenticatedUserId!.Trim();
        bool isAdministrator = state.WorldConfiguration.IsAdministrator(authenticatedUserId);
        string clientManifestId = input.ManifestId;
        var clientCompatibilityManifest = input.Manifest;
        var summary = input.Summary;
        string summaryFailure = input.SummaryFailure;
        string compatibilityManifestFailure = input.ManifestFailure ?? string.Empty;
        if (!input.HasFullManifest)
        {
            if (serverCompatibilityManifest is null)
            {
                return new CompatibilityHandshakeResult(
                    ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Compatibility.ManifestMissing")),
                    authenticatedUserId,
                    auth.DisplayName,
                    ServerCompatibilityManifestJson: null,
                    Array.Empty<CompatibilityIssueDto>(),
                    isAdministrator,
                    RequiresFullCompatibilityManifest: true,
                    RequestedCompatibilityPackageIds: new[] { "*" });
            }

            if (!string.IsNullOrWhiteSpace(clientManifestId)
                && string.Equals(serverCompatibilityManifest.ManifestId, clientManifestId, StringComparison.Ordinal))
            {
                IReadOnlyList<CompatibilityIssue> worldLanguageIssues = BuildWorldLanguageIssues(
                    state.WorldConfiguration.Current,
                    serverCompatibilityManifest.GameLanguage);
                return new CompatibilityHandshakeResult(
                    ProtocolResponse.Ok(T("Compatibility.Validated")),
                    authenticatedUserId,
                    auth.DisplayName,
                    ServerCompatibilityManifestJson: CompatibilityManifestDeliveryPolicy.ShouldIncludeFullManifest(worldLanguageIssues)
                        ? SerializeCompatibilityManifest(serverCompatibilityManifest, state)
                        : null,
                    ToCompatibilityIssueDtos(worldLanguageIssues),
                    isAdministrator,
                    RequiresFullCompatibilityManifest: false,
                    RequestedCompatibilityPackageIds: Array.Empty<string>());
            }

            if (summary is null)
            {
                return new CompatibilityHandshakeResult(
                    ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, summaryFailure),
                    authenticatedUserId,
                    auth.DisplayName,
                    SerializeCompatibilityManifest(serverCompatibilityManifest, state),
                    Array.Empty<CompatibilityIssueDto>(),
                    isAdministrator,
                    RequiresFullCompatibilityManifest: true,
                    RequestedCompatibilityPackageIds: new[] { "*" });
            }

            CompatibilitySummaryHandshake summaryHandshake = CompareCompatibilitySummary(
                serverCompatibilityManifest,
                summary,
                state.ServerConfiguration.CompatibilityOptions);
            IReadOnlyList<CompatibilityIssue> summaryWorldLanguageIssues = BuildWorldLanguageIssues(
                state.WorldConfiguration.Current,
                summary.GameLanguage);
            IReadOnlyList<CompatibilityIssue> summaryAcceptedIssues =
                summaryHandshake.Issues.Concat(summaryWorldLanguageIssues).ToList();
            if (summaryHandshake.Accepted)
            {
                return new CompatibilityHandshakeResult(
                    ProtocolResponse.Ok(T("Compatibility.Validated")),
                    authenticatedUserId,
                    auth.DisplayName,
                    ServerCompatibilityManifestJson: CompatibilityManifestDeliveryPolicy.ShouldIncludeFullManifest(summaryAcceptedIssues)
                        ? SerializeCompatibilityManifest(serverCompatibilityManifest, state)
                        : null,
                    ToCompatibilityIssueDtos(summaryAcceptedIssues),
                    isAdministrator,
                    RequiresFullCompatibilityManifest: false,
                    RequestedCompatibilityPackageIds: Array.Empty<string>());
            }

            return new CompatibilityHandshakeResult(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Compatibility.ManifestMismatch")),
                authenticatedUserId,
                auth.DisplayName,
                summaryHandshake.RequiresFullManifest ? null : SerializeCompatibilityManifest(serverCompatibilityManifest, state),
                ToCompatibilityIssueDtos(summaryAcceptedIssues),
                isAdministrator,
                summaryHandshake.RequiresFullManifest,
                summaryHandshake.RequestedPackageIds);
        }

        if (input.ManifestFailure is not null)
        {
            return new CompatibilityHandshakeResult(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, compatibilityManifestFailure),
                authenticatedUserId,
                auth.DisplayName,
                serverCompatibilityManifest is null ? null : SerializeCompatibilityManifest(serverCompatibilityManifest, state),
                Array.Empty<CompatibilityIssueDto>(),
                isAdministrator,
                RequiresFullCompatibilityManifest: false,
                RequestedCompatibilityPackageIds: Array.Empty<string>());
        }

        if (clientCompatibilityManifest is null && serverCompatibilityManifest is not null)
        {
            return new CompatibilityHandshakeResult(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, T("Compatibility.ManifestMissing")),
                authenticatedUserId,
                auth.DisplayName,
                SerializeCompatibilityManifest(serverCompatibilityManifest, state),
                Array.Empty<CompatibilityIssueDto>(),
                isAdministrator,
                RequiresFullCompatibilityManifest: false,
                RequestedCompatibilityPackageIds: Array.Empty<string>());
        }

        IReadOnlyList<CompatibilityIssueDto> compatibilityIssues = Array.Empty<CompatibilityIssueDto>();
        string? acceptedServerCompatibilityManifestJson = null;
        if (clientCompatibilityManifest is not null)
        {
            if (serverCompatibilityManifest is null && publishBaseline)
            {
                CompatibilityBaselineUpdateResult update = state.CompatibilityBaseline.EnsureBaseline(
                    clientCompatibilityManifest,
                    authenticatedUserId,
                    nowUtc);
                serverCompatibilityManifest = update.Baseline;
            }
            if (serverCompatibilityManifest is not null)
            {
                CompatibilityComparisonResult comparison = IsPartialCompatibilityManifest(
                        serverCompatibilityManifest,
                        clientCompatibilityManifest)
                    ? ComparePartialCompatibilityManifest(
                        serverCompatibilityManifest,
                        clientCompatibilityManifest,
                        state.ServerConfiguration.CompatibilityOptions)
                    : CompatibilityManifestComparer.Compare(
                        serverCompatibilityManifest,
                        clientCompatibilityManifest,
                        state.ServerConfiguration.CompatibilityOptions);
                IReadOnlyList<CompatibilityIssue> issues = comparison.Issues
                    .Concat(BuildWorldLanguageIssues(state.WorldConfiguration.Current, clientCompatibilityManifest.GameLanguage))
                    .ToList();
                compatibilityIssues = ToCompatibilityIssueDtos(issues);
                if (CompatibilityManifestDeliveryPolicy.ShouldIncludeFullManifest(issues))
                {
                    acceptedServerCompatibilityManifestJson = SerializeCompatibilityManifest(serverCompatibilityManifest, state);
                }
                if (!comparison.Accepted)
                {
                    return new CompatibilityHandshakeResult(
                        ProtocolResponse.Reject(
                            ProtocolErrorCode.ServerRejected,
                            T("Compatibility.ManifestMismatch")),
                        authenticatedUserId,
                        auth.DisplayName,
                        SerializeCompatibilityManifest(serverCompatibilityManifest, state),
                        compatibilityIssues,
                        isAdministrator,
                        RequiresFullCompatibilityManifest: false,
                        RequestedCompatibilityPackageIds: Array.Empty<string>());
                }
            }
        }

        return new CompatibilityHandshakeResult(
            ProtocolResponse.Ok(T("Compatibility.Validated")),
            authenticatedUserId,
            auth.DisplayName,
            ServerCompatibilityManifestJson: acceptedServerCompatibilityManifestJson,
            compatibilityIssues,
            isAdministrator,
            RequiresFullCompatibilityManifest: false,
            RequestedCompatibilityPackageIds: Array.Empty<string>());
    }

}
