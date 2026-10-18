using AIRsLight.ClashOfRim.Network.Modules.Accounts;
using static AIRsLight.ClashOfRim.Network.Modules.Compatibility.CompatibilityHandshakeComparison;
using static AIRsLight.ClashOfRim.Network.Modules.Compatibility.CompatibilityHandshakeContract;
using AIRsLight.ClashOfRim.Compatibility;
using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Network.Modules.Compatibility;

internal sealed class CompatibilityHandshakeService(CompatibilityBaselineRegistry baseline,
    WorldConfigurationRegistry world, Func<CompatibilityComparisonOptions> readOptions)
{
    internal string Serialize(CompatibilityManifest manifest) => CompatibilityHandshakeContract.Serialize(manifest, readOptions());

    // Compare against current policy without registering an account or assigning an administrator.
    internal CompatibilityHandshakeResult Evaluate(
        AuthenticationValidationResult auth, CompatibilityHandshakeInput input, DateTimeOffset nowUtc, bool publishBaseline)
    {
        CompatibilityManifest? serverCompatibilityManifest = baseline.Current;
        if (!auth.Accepted || string.IsNullOrWhiteSpace(auth.AuthenticatedUserId))
        {
            return new CompatibilityHandshakeResult(
                ProtocolResponse.Reject(auth.ErrorCode, auth.Message ?? ServerLocalization.Text("Steam.Failed")),
                AuthenticatedUserId: null,
                DisplayName: null,
                serverCompatibilityManifest is null ? null : Serialize(serverCompatibilityManifest),
                Array.Empty<CompatibilityIssueDto>(),
                CanOverrideCompatibilityBaseline: false,
                RequiresFullCompatibilityManifest: false,
                RequestedCompatibilityPackageIds: Array.Empty<string>());
        }

        string authenticatedUserId = auth.AuthenticatedUserId!.Trim();
        bool isAdministrator = world.IsAdministrator(authenticatedUserId);
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
                    ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, ServerLocalization.Text("Compatibility.ManifestMissing")),
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
                    world.Current,
                    serverCompatibilityManifest.GameLanguage);
                return new CompatibilityHandshakeResult(
                    ProtocolResponse.Ok(ServerLocalization.Text("Compatibility.Validated")),
                    authenticatedUserId,
                    auth.DisplayName,
                    ServerCompatibilityManifestJson: CompatibilityManifestDeliveryPolicy.ShouldIncludeFullManifest(worldLanguageIssues)
                        ? Serialize(serverCompatibilityManifest)
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
                    Serialize(serverCompatibilityManifest),
                    Array.Empty<CompatibilityIssueDto>(),
                    isAdministrator,
                    RequiresFullCompatibilityManifest: true,
                    RequestedCompatibilityPackageIds: new[] { "*" });
            }

            CompatibilitySummaryHandshake summaryHandshake = CompareCompatibilitySummary(
                serverCompatibilityManifest,
                summary,
                readOptions());
            IReadOnlyList<CompatibilityIssue> summaryWorldLanguageIssues = BuildWorldLanguageIssues(
                world.Current,
                summary.GameLanguage);
            IReadOnlyList<CompatibilityIssue> summaryAcceptedIssues =
                summaryHandshake.Issues.Concat(summaryWorldLanguageIssues).ToList();
            if (summaryHandshake.Accepted)
            {
                return new CompatibilityHandshakeResult(
                    ProtocolResponse.Ok(ServerLocalization.Text("Compatibility.Validated")),
                    authenticatedUserId,
                    auth.DisplayName,
                    ServerCompatibilityManifestJson: CompatibilityManifestDeliveryPolicy.ShouldIncludeFullManifest(summaryAcceptedIssues)
                        ? Serialize(serverCompatibilityManifest)
                        : null,
                    ToCompatibilityIssueDtos(summaryAcceptedIssues),
                    isAdministrator,
                    RequiresFullCompatibilityManifest: false,
                    RequestedCompatibilityPackageIds: Array.Empty<string>());
            }

            return new CompatibilityHandshakeResult(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, ServerLocalization.Text("Compatibility.ManifestMismatch")),
                authenticatedUserId,
                auth.DisplayName,
                summaryHandshake.RequiresFullManifest ? null : Serialize(serverCompatibilityManifest),
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
                serverCompatibilityManifest is null ? null : Serialize(serverCompatibilityManifest),
                Array.Empty<CompatibilityIssueDto>(),
                isAdministrator,
                RequiresFullCompatibilityManifest: false,
                RequestedCompatibilityPackageIds: Array.Empty<string>());
        }

        if (clientCompatibilityManifest is null && serverCompatibilityManifest is not null)
        {
            return new CompatibilityHandshakeResult(
                ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, ServerLocalization.Text("Compatibility.ManifestMissing")),
                authenticatedUserId,
                auth.DisplayName,
                Serialize(serverCompatibilityManifest),
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
                CompatibilityBaselineUpdateResult update = baseline.EnsureBaseline(
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
                        readOptions())
                    : CompatibilityManifestComparer.Compare(
                        serverCompatibilityManifest,
                        clientCompatibilityManifest,
                        readOptions());
                IReadOnlyList<CompatibilityIssue> issues = comparison.Issues
                    .Concat(BuildWorldLanguageIssues(world.Current, clientCompatibilityManifest.GameLanguage))
                    .ToList();
                compatibilityIssues = ToCompatibilityIssueDtos(issues);
                if (CompatibilityManifestDeliveryPolicy.ShouldIncludeFullManifest(issues))
                {
                    acceptedServerCompatibilityManifestJson = Serialize(serverCompatibilityManifest);
                }
                if (!comparison.Accepted)
                {
                    return new CompatibilityHandshakeResult(
                        ProtocolResponse.Reject(
                            ProtocolErrorCode.ServerRejected,
                            ServerLocalization.Text("Compatibility.ManifestMismatch")),
                        authenticatedUserId,
                        auth.DisplayName,
                        Serialize(serverCompatibilityManifest),
                        compatibilityIssues,
                        isAdministrator,
                        RequiresFullCompatibilityManifest: false,
                        RequestedCompatibilityPackageIds: Array.Empty<string>());
                }
            }
        }

        return new CompatibilityHandshakeResult(
            ProtocolResponse.Ok(ServerLocalization.Text("Compatibility.Validated")),
            authenticatedUserId,
            auth.DisplayName,
            ServerCompatibilityManifestJson: acceptedServerCompatibilityManifestJson,
            compatibilityIssues,
            isAdministrator,
            RequiresFullCompatibilityManifest: false,
            RequestedCompatibilityPackageIds: Array.Empty<string>());
    }
    private static IReadOnlyList<CompatibilityIssue> BuildWorldLanguageIssues(
        WorldConfigurationDto? configuration,
        string? clientLanguage)
    {
        var issues = new List<CompatibilityIssue>();
        CompatibilityManifestComparer.CompareGameLanguage(
            issues,
            configuration?.GameLanguage,
            clientLanguage);
        return issues;
    }

}

internal sealed record CompatibilityHandshakeResult(
        ProtocolResponse Result,
        string? AuthenticatedUserId,
        string? DisplayName,
        string? ServerCompatibilityManifestJson,
        IReadOnlyList<CompatibilityIssueDto> CompatibilityIssues,
        bool CanOverrideCompatibilityBaseline,
        bool RequiresFullCompatibilityManifest,
        IReadOnlyList<string> RequestedCompatibilityPackageIds);
