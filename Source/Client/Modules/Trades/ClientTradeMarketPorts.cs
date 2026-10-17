using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Synchronization;

namespace AIRsLight.ClashOfRim.Modules.Trades;

internal interface IClientTradeMarketTransport
{
    Task<ClashOfRimClientNetworkResult<ModListTradeOrdersResponseDto>> ListAsync(ClashOfRimClientNetworkContext context, string scope, int offset, int limit, CancellationToken token);
    Task<ClashOfRimClientNetworkResult<ModAcceptTradeOrderResponseDto>> AcceptAsync(ClashOfRimClientNetworkContext context, string key, string eventId, bool postage, CancellationToken token);
    Task<ClashOfRimClientNetworkResult<ModCloseTradeOrderResponseDto>> CancelAsync(ClashOfRimClientNetworkContext context, string key, string eventId, string reason, CancellationToken token);
}

internal interface IClientTradeMarketPresentation
{
    string InitialStatus { get; }
    string Busy { get; }
    string Text(string key, params (string Name, object? Value)[] arguments);
    string Admission(LocalMutationAdmission admission, LocalMutationState? pending);
    void PreparePreviews(IReadOnlyList<ModTradeOrderSummaryDto> orders);
    void Diagnose(Exception exception);
}
