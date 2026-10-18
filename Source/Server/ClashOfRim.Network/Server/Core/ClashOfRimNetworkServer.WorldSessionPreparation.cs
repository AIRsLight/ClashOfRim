using AIRsLight.ClashOfRim.Network.Modules.Sessions;
using AIRsLight.ClashOfRim.Network.Modules.Compatibility;
using AIRsLight.ClashOfRim.Protocol;
using Microsoft.AspNetCore.Http;

namespace AIRsLight.ClashOfRim.Network;

public static partial class ClashOfRimNetworkServer
{
    private static IResult PrepareAuthenticatedWorldSession(PrepareWorldSessionRequest request, ClashOfRimNetworkState state)
    {
        var prepared = state.Accounts.PrepareCredentials(request.UserId, request.SteamAuthTicket, request.Password, DateTimeOffset.UtcNow, request.CreateAccountIfMissing);
        if (prepared.Revalidate() is { } invalid) return RejectWorldSession(invalid);
        var input = CompatibilityHandshakeInput.Prepare(request.CompatibilityManifestJson, request.CompatibilityManifestId, request.CompatibilityManifestSummaryJson);
        var entry = state.EntryHandshakes.Prepare(prepared, input, EntryPurpose.WorldPreparation);
        using var mutation = EnterRecoveredSnapshotMutation(state, entry.Authorize);
        if (mutation.Rejection is { } denied) return RejectWorldSession(denied, entry.Compatibility);
        var nowUtc = DateTimeOffset.UtcNow;
        if (entry.CommitAccount(nowUtc) is { } uncommitted) return RejectWorldSession(uncommitted);
        if (entry.PublishBaseline(nowUtc) is { } incompatible) return RejectWorldSession(incompatible, entry.Compatibility);
        return PrepareWorldSessionLocked(request, state, entry.UserId, entry.Compatibility!, nowUtc);
    }

    private static IResult RejectWorldSession(ProtocolResponse rejection, CompatibilityHandshakeResult? compatibility = null)
        => Results.Ok(new PrepareWorldSessionResponse(rejection, false, false, false, null, null,
            serverCompatibilityManifestJson: compatibility?.ServerCompatibilityManifestJson,
            compatibilityIssues: compatibility?.CompatibilityIssues,
            requiresFullCompatibilityManifest: compatibility?.RequiresFullCompatibilityManifest ?? false,
            requestedCompatibilityPackageIds: compatibility?.RequestedCompatibilityPackageIds));
}
