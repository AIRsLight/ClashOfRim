using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.Modules.Events;
using AIRsLight.ClashOfRim.Modules.Transport;

namespace AIRsLight.ClashOfRim.ClientNetwork;

internal sealed class ClientEventDecisionTransport : IClientEventDecisionProtocol
{
    public Task<ClashOfRimClientNetworkResult<ModRejectGiftResponseDto>> RejectGiftAsync(ClashOfRimClientNetworkContext context, string eventId, string? reason, CancellationToken token) =>
        Exchange(context, p => p.RejectGiftAsync(context, eventId, reason, token));
    public Task<ClashOfRimClientNetworkResult<ModRejectSupportPawnResponseDto>> RejectSupportAsync(ClashOfRimClientNetworkContext context, string eventId, string? reason, CancellationToken token) =>
        Exchange(context, p => p.RejectSupportAsync(context, eventId, reason, token));
    public Task<ClashOfRimClientNetworkResult<ModDiplomacyEventResponseDto>> RespondDiplomacyAsync(ClashOfRimClientNetworkContext context, string eventId, bool accepted, string? reason, CancellationToken token) =>
        Exchange(context, p => p.RespondDiplomacyAsync(context, eventId, accepted, reason, token));
    public Task<ClashOfRimClientNetworkResult<ModReportEventApplicationFailureResponseDto>> ReportFailureAsync(ClashOfRimClientNetworkContext context, ClientEventFailureReport report, CancellationToken token) =>
        Exchange(context, p => p.ReportFailureAsync(context, report, token));

    private static async Task<ClashOfRimClientNetworkResult<T>> Exchange<T>(ClashOfRimClientNetworkContext context,
        Func<ClientEventDecisionProtocol, Task<ClashOfRimClientNetworkResult<T>>> send)
    {
        using var http = new HttpClient();
        var protocol = new ClientEventDecisionProtocol(new ClientProtocolTransport(http, context, new ClientProtocolEnvironment(
            (message, owner) => ClashOfRimModNetworkClient.SessionExpired?.Invoke(message, owner))),
            () => ClashOfRimText.Key("ClashOfRim.Network.StatusNotConfigured"));
        return await send(protocol);
    }
}
