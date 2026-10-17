using System;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Modules.Transport;

internal sealed class ClientSessionProtocol : IClientSessionAuthentication
{
    private static readonly string LoginRoute = ProtocolContractManifest.Find(ProtocolMessageKind.Login).Route;
    private static readonly string LogoutRoute = ProtocolContractManifest.Find(ProtocolMessageKind.Logout).Route;
    private readonly ClientProtocolTransport transport;
    private readonly IClientCompatibilityManifestSource manifests;
    private readonly IClientAuthenticationDiagnostics diagnostics;

    internal ClientSessionProtocol(ClientProtocolTransport transport, IClientCompatibilityManifestSource manifests, IClientAuthenticationDiagnostics diagnostics)
    { this.transport = transport; this.manifests = manifests; this.diagnostics = diagnostics; }

    public async Task<ClashOfRimClientNetworkResult<ModLoginResponseDto>> LoginAsync(ClashOfRimClientNetworkContext context,
        string purpose, string? manifest, CancellationToken token, Action<ModLoginResponseDto>? captureReceipt = null)
    {
        if (!context.IsConfigured) return NotConfigured<ModLoginResponseDto>();
        var request = new ModLoginRequestDto
        {
            UserId = context.UserId, ColonyId = context.ColonyId, CurrentSnapshotId = context.CurrentSnapshotId,
            CompatibilityDigest = purpose, CompatibilityManifestJson = manifest,
            CompatibilityManifestId = manifests.Id, CompatibilityManifestSummaryJson = manifest is null ? manifests.SummaryJson : null,
            SteamAuthTicket = string.IsNullOrWhiteSpace(context.SteamAuthTicket) ? context.UserId : context.SteamAuthTicket,
            Password = context.OfflinePassword
        };
        ClientProtocolTransport ownedTransport = transport.ForContext(context);
        var result = await ownedTransport.PostLoginAsync(LoginRoute, request, token, captureReceipt).ConfigureAwait(false);
        // An accepted credential already owns a session. Never create another for a contradictory retry flag.
        if (!string.IsNullOrWhiteSpace(manifest) || result.Response?.RequiresFullCompatibilityManifest != true
            || result.Response.Result?.Accepted == true) return result;
        request.CompatibilityManifestJson = manifests.BuildJson(result.Response.RequestedCompatibilityPackageIds);
        if (string.IsNullOrWhiteSpace(request.CompatibilityManifestJson))
            return ClashOfRimClientNetworkResult<ModLoginResponseDto>.Failed("CompatibilityManifestBuildFailed", diagnostics.ManifestBuildFailedMessage);
        request.CompatibilityManifestSummaryJson = null;
        return await ownedTransport.PostLoginAsync(LoginRoute, request, token, captureReceipt).ConfigureAwait(false);
    }

    public Task<ClashOfRimClientNetworkResult<ModLogoutResponseDto>> LogoutAsync(ClashOfRimClientNetworkContext context,
        string? sessionId, CancellationToken token, bool bindToSession = true)
    {
        if (!context.IsConfigured) return Task.FromResult(NotConfigured<ModLogoutResponseDto>());
        return transport.ForContext(context).PostAsync<ModLogoutRequestDto, ModLogoutResponseDto>(LogoutRoute,
            new ModLogoutRequestDto { UserId = context.UserId, ColonyId = context.ColonyId, SessionId = sessionId }, token, bindToSession);
    }

    private ClashOfRimClientNetworkResult<T> NotConfigured<T>() =>
        ClashOfRimClientNetworkResult<T>.Failed("NotConfigured", diagnostics.NotConfiguredMessage);
}
