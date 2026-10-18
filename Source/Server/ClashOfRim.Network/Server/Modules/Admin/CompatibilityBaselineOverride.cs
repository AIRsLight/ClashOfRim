using AIRsLight.ClashOfRim.Network.Modules.Accounts;
using AIRsLight.ClashOfRim.Network.Modules.Compatibility;
using AIRsLight.ClashOfRim.Network.Modules.Snapshots;
using AIRsLight.ClashOfRim.Protocol;
using Microsoft.Extensions.Logging;
using static AIRsLight.ClashOfRim.Network.Modules.Compatibility.CompatibilityHandshakeContract;
using static AIRsLight.ClashOfRim.Network.ServerText;

namespace AIRsLight.ClashOfRim.Network.Modules.Admin;

internal sealed class CompatibilityBaselineOverride(AccountService accounts, WorldConfigurationRegistry world,
    CompatibilityBaselineRegistry baseline, CompatibilityHandshakeService handshake,
    SnapshotMutationCoordinator mutations, Func<ILogger> logger)
{
    internal OverrideCompatibilityBaselineResponse Replace(OverrideCompatibilityBaselineRequest request)
    {
        OverrideCompatibilityBaselineResponse Reject(ProtocolResponse failure) => new(failure, null, null);
        ProtocolResponse CredentialFailure(ProtocolResponse failure) => ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, failure.Message ?? Format("Steam.Failed"));
        var credentials = accounts.PrepareCredentials(request.UserId, request.SteamAuthTicket, request.Password, DateTimeOffset.UtcNow);
        if (credentials.Result.Rejection is { } initialFailure) return Reject(CredentialFailure(initialFailure));
        string userId = credentials.Result.AuthenticatedUserId!;
        ProtocolResponse? Authorize() => credentials.Revalidate() is { } invalidCredentials ? CredentialFailure(invalidCredentials)
            : world.IsAdministrator(userId) ? null : ProtocolResponse.Reject(ProtocolErrorCode.Unauthorized, Format("Compatibility.OverrideAdminOnly"));
        if (Authorize() is { } unauthorized) return Reject(unauthorized);
        if (!TryReadCompatibilityManifest(request.CompatibilityManifestJson, out var manifest, out string invalid) || manifest is null)
            return Reject(ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed, invalid));
        using var recovery = mutations.Enter(Authorize);
        if (recovery.Rejection is { } failure) return Reject(failure);
        var at = DateTimeOffset.UtcNow;
        if (credentials.Commit(at).Rejection is { } changed) return Reject(CredentialFailure(changed));
        var update = baseline.ReplaceBaseline(manifest, userId, at);
        logger().LogInformation("兼容清单基线已覆盖：user={UserId} manifest={ManifestId} mods={ModCount} dlcs={DlcCount}",
            userId, manifest.ManifestId, manifest.Mods.Count, manifest.DlcIds.Count);
        return new(ProtocolResponse.Ok(Format("Compatibility.OverrideSuccess")), handshake.Serialize(update.Baseline!), update.UpdatedAtUtc);
    }
}
