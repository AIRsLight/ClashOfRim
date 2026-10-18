using System;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Transport;
using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Modules.Administration;

internal interface IClientAdminBaselineTransport
{
    Task<ClashOfRimClientNetworkResult<ModGetAdminBaselineRequirementsResponseDto>> RequirementsAsync(ClashOfRimClientNetworkContext context, CancellationToken token);
    Task<ClashOfRimClientNetworkResult<ModSubmitAdminBaselineResponseDto>> SubmitAsync(ClashOfRimClientNetworkContext context, ModSubmitAdminBaselineRequestDto request, CancellationToken token);
}

internal sealed class ClientAdminBaselineProtocol : IClientAdminBaselineTransport
{
    private readonly ClientSetupAuthenticationLease setup;
    private readonly Func<string> notConfigured;
    internal ClientAdminBaselineProtocol(ClientSetupAuthenticationLease setup, Func<string> notConfigured)
    { this.setup = setup; this.notConfigured = notConfigured; }

    public Task<ClashOfRimClientNetworkResult<ModGetAdminBaselineRequirementsResponseDto>> RequirementsAsync(
        ClashOfRimClientNetworkContext context, CancellationToken token)
        => setup.RunAsync<ModGetAdminBaselineRequirementsResponseDto>(context, "admin-baseline-requirements", (authenticated, transport, cancellation) =>
            transport.PostAsync<ModGetAdminBaselineRequirementsRequestDto, ModGetAdminBaselineRequirementsResponseDto>(
                ProtocolContractManifest.Find(ProtocolMessageKind.GetAdminBaselineRequirements).Route,
                new() { UserId = authenticated.UserId, ColonyId = authenticated.ColonyId, AuthToken = authenticated.AuthToken }, cancellation), token);

    public Task<ClashOfRimClientNetworkResult<ModSubmitAdminBaselineResponseDto>> SubmitAsync(
        ClashOfRimClientNetworkContext context, ModSubmitAdminBaselineRequestDto request, CancellationToken token)
    {
        if (!context.IsConfigured)
            return Task.FromResult(ClashOfRimClientNetworkResult<ModSubmitAdminBaselineResponseDto>.Failed("NotConfigured", notConfigured()));
        // Login may yield. Capture nested catalogs before acquiring a temporary credential.
        var captured = ClientProtocolJson.Copy(request);
        return setup.RunAsync<ModSubmitAdminBaselineResponseDto>(context, "admin-baseline-setup", (authenticated, transport, cancellation) =>
            transport.PostAsync<ModSubmitAdminBaselineRequestDto, ModSubmitAdminBaselineResponseDto>(
                ProtocolContractManifest.Find(ProtocolMessageKind.SubmitAdminBaseline).Route,
                captured.WithAuthentication(authenticated.UserId, authenticated.ColonyId, authenticated.AuthToken), cancellation), token);
    }
}
