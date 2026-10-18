using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Transport;
using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Modules.World;

// Protocol and setup credential ownership are shared by entry, refresh and reconnect.
// No settings or engine state is reread once the caller supplies its context.
internal sealed class ClientWorldProtocol
{
    private readonly ClientProtocolTransport transport;
    private readonly ClientSetupAuthenticationLease setup;
    private readonly IClientCompatibilityManifestSource manifests;
    private readonly IClientAuthenticationDiagnostics diagnostics;
    private readonly Action logEntry;
    internal ClientWorldProtocol(ClientProtocolTransport transport, ClientSetupAuthenticationLease setup,
        IClientCompatibilityManifestSource manifests, IClientAuthenticationDiagnostics diagnostics, Action logEntry)
    { this.transport = transport; this.setup = setup; this.manifests = manifests; this.diagnostics = diagnostics; this.logEntry = logEntry; }

    internal Task<ClashOfRimClientNetworkResult<ModServerHelloResponseDto>> HelloAsync(ClashOfRimClientNetworkContext context, CancellationToken token)
        => string.IsNullOrWhiteSpace(context.ServerBaseUrl) ? NotConfigured<ModServerHelloResponseDto>()
            : transport.ForContext(context).GetAsync<ModServerHelloResponseDto>(Route(ProtocolMessageKind.ServerHello), token);

    internal async Task<ClashOfRimClientNetworkResult<ModPrepareWorldSessionResponseDto>> PrepareAsync(
        ClashOfRimClientNetworkContext context, bool createAccount, CancellationToken token)
    {
        if (!context.HasServerIdentity) return await NotConfigured<ModPrepareWorldSessionResponseDto>();
        logEntry();
        var request = new ModPrepareWorldSessionRequestDto
        {
            UserId = context.UserId, CompatibilityManifestId = manifests.Id, CompatibilityManifestSummaryJson = manifests.SummaryJson,
            SteamAuthTicket = Ticket(context), Password = context.OfflinePassword, CreateAccountIfMissing = createAccount
        };
        var owned = transport.ForContext(context);
        var result = await owned.PostAsync<ModPrepareWorldSessionRequestDto, ModPrepareWorldSessionResponseDto>(
            Route(ProtocolMessageKind.PrepareWorldSession), request, token).ConfigureAwait(false);
        if (!ClientCompatibilityNegotiation.NeedsDetails(result.Response?.Result?.Accepted,
            result.Response?.RequiresFullCompatibilityManifest == true, request.CompatibilityManifestJson)) return result;
        request.CompatibilityManifestJson = manifests.BuildJson(result.Response?.RequestedCompatibilityPackageIds);
        if (string.IsNullOrWhiteSpace(request.CompatibilityManifestJson))
            return ClashOfRimClientNetworkResult<ModPrepareWorldSessionResponseDto>.Failed("CompatibilityManifestBuildFailed", diagnostics.ManifestBuildFailedMessage);
        return await owned.PostAsync<ModPrepareWorldSessionRequestDto, ModPrepareWorldSessionResponseDto>(
            Route(ProtocolMessageKind.PrepareWorldSession), request, token).ConfigureAwait(false);
    }

    internal Task<ClashOfRimClientNetworkResult<ModGetWorldConfigurationResponseDto>> ReadAsync(
        ClashOfRimClientNetworkContext context, bool generation, bool colonySites, bool extensions, CancellationToken token)
        => !context.HasServerIdentity ? NotConfigured<ModGetWorldConfigurationResponseDto>()
            : transport.ForContext(context).PostAsync<ModGetWorldConfigurationRequestDto, ModGetWorldConfigurationResponseDto>(
                Route(ProtocolMessageKind.GetWorldConfiguration), new()
                {
                    UserId = context.UserId, ColonyId = context.ColonyId, SteamAuthTicket = Ticket(context), Password = context.OfflinePassword,
                    IncludeGenerationBaseline = generation, IncludePlayerColonySites = colonySites, IncludeWorldExtensions = extensions
                }, token);

    internal Task<ClashOfRimClientNetworkResult<ModSubmitWorldConfigurationResponseDto>> SubmitAsync(
        ClashOfRimClientNetworkContext context, ModWorldConfigurationDto configuration, CancellationToken token)
    {
        if (!context.IsConfigured) return NotConfigured<ModSubmitWorldConfigurationResponseDto>();
        var captured = ClientProtocolJson.Copy(configuration);
        return setup.RunAsync<ModSubmitWorldConfigurationResponseDto>(context, "world-configuration-setup", (authenticated, owned, cancellation) =>
            owned.PostAsync<ModSubmitWorldConfigurationRequestDto, ModSubmitWorldConfigurationResponseDto>(
                Route(ProtocolMessageKind.SubmitWorldConfiguration), new()
                {
                    UserId = authenticated.UserId, ColonyId = authenticated.ColonyId, AuthToken = authenticated.AuthToken, Configuration = captured
                }, cancellation), token);
    }

    internal Task<ClashOfRimClientNetworkResult<ModSubmitWorldFeatureNamesResponseDto>> SubmitNamesAsync(
        ClashOfRimClientNetworkContext context, string language, string worldId, IReadOnlyList<ModWorldFeatureDto> features, CancellationToken token)
        => !context.IsConfigured ? NotConfigured<ModSubmitWorldFeatureNamesResponseDto>()
            : transport.ForContext(context).PostAsync<ModSubmitWorldFeatureNamesRequestDto, ModSubmitWorldFeatureNamesResponseDto>(
                Route(ProtocolMessageKind.SubmitWorldFeatureNames), new()
                {
                    UserId = context.UserId, ColonyId = context.ColonyId, Language = language, WorldConfigurationId = worldId,
                    Features = features?.ToList() ?? new(), SteamAuthTicket = Ticket(context), Password = context.OfflinePassword
                }, token);

    internal Task<ClashOfRimClientNetworkResult<ModUploadWorldSubstrateResponseDto>> UploadSubstrateAsync(
        ClashOfRimClientNetworkContext context, string worldId, byte[] payload, CancellationToken token)
    {
        if (!context.IsConfigured) return NotConfigured<ModUploadWorldSubstrateResponseDto>();
        byte[] captured = payload?.ToArray() ?? Array.Empty<byte>();
        return setup.RunAsync<ModUploadWorldSubstrateResponseDto>(context, "world-substrate-setup", (authenticated, owned, cancellation) =>
            owned.PostSnapshotMultipartAsync<ModUploadWorldSubstrateRequestDto, ModUploadWorldSubstrateResponseDto>(
                Route(ProtocolMessageKind.UploadWorldSubstrate), new()
                {
                    UserId = authenticated.UserId, ColonyId = authenticated.ColonyId, WorldConfigurationId = worldId ?? "",
                    SteamAuthTicket = Ticket(authenticated), Password = authenticated.OfflinePassword
                }, captured, cancellation), token);
    }

    internal Task<ClashOfRimClientNetworkResult<byte[]>> DownloadSubstrateAsync(ClashOfRimClientNetworkContext context, string worldId, CancellationToken token)
        => !context.IsConfigured ? NotConfigured<byte[]>()
            : transport.ForContext(context).PostForBytesAsync(Route(ProtocolMessageKind.DownloadWorldSubstrate), new ModDownloadWorldSubstrateRequestDto
            {
                UserId = context.UserId, ColonyId = context.ColonyId, WorldConfigurationId = worldId ?? "",
                SteamAuthTicket = Ticket(context), Password = context.OfflinePassword
            }, token);

    private Task<ClashOfRimClientNetworkResult<T>> NotConfigured<T>() =>
        Task.FromResult(ClashOfRimClientNetworkResult<T>.Failed("NotConfigured", diagnostics.NotConfiguredMessage));
    private static string Ticket(ClashOfRimClientNetworkContext context) => string.IsNullOrWhiteSpace(context.SteamAuthTicket) ? context.UserId : context.SteamAuthTicket;
    private static string Route(ProtocolMessageKind kind) => ProtocolContractManifest.Find(kind).Route;
}
