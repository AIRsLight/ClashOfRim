using System;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Transport;
using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Modules.Compatibility;

internal interface IClientCompatibilityOverrideTransport
{
    Task<ClashOfRimClientNetworkResult<ModOverrideCompatibilityBaselineResponseDto>> SubmitAsync(
        ClashOfRimClientNetworkContext context, string manifest, CancellationToken token);
}

internal sealed class ClientCompatibilityOverrideProtocol : IClientCompatibilityOverrideTransport
{
    private readonly ClientProtocolTransport transport;
    private readonly Func<string> notConfigured;
    internal ClientCompatibilityOverrideProtocol(ClientProtocolTransport transport, Func<string> notConfigured)
    { this.transport = transport; this.notConfigured = notConfigured; }
    public Task<ClashOfRimClientNetworkResult<ModOverrideCompatibilityBaselineResponseDto>> SubmitAsync(
        ClashOfRimClientNetworkContext context, string manifest, CancellationToken token)
    {
        if (!context.HasServerIdentity)
            return Task.FromResult(ClashOfRimClientNetworkResult<ModOverrideCompatibilityBaselineResponseDto>.Failed("NotConfigured", notConfigured()));
        return transport.ForContext(context).PostAsync<ModOverrideCompatibilityBaselineRequestDto, ModOverrideCompatibilityBaselineResponseDto>(
            ProtocolContractManifest.Find(ProtocolMessageKind.OverrideCompatibilityBaseline).Route, new()
            {
                UserId = context.UserId, ColonyId = null, CompatibilityManifestJson = manifest ?? "",
                SteamAuthTicket = string.IsNullOrWhiteSpace(context.SteamAuthTicket) ? context.UserId : context.SteamAuthTicket,
                Password = context.OfflinePassword
            }, token);
    }
}
