using System;
using System.Collections.Generic;
using System.Linq;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.ClientSnapshots;
using AIRsLight.ClashOfRim.Modules.Trades;
using AIRsLight.ClashOfRim.Modules.Synchronization;
using Verse;

namespace AIRsLight.ClashOfRim.Trades;

internal sealed class ClientTradeMarketPresentation : IClientTradeMarketPresentation
{
    public string InitialStatus => ClashOfRimText.Key("ClashOfRim.Status.TradeOrderNotCreated");
    public string Busy => ClashOfRimText.Key("ClashOfRim.Sync.StatusBusy");
    public string Text(string key, params (string Name, object? Value)[] arguments) =>
        ClashOfRimText.Key("ClashOfRim.Trade." + key, arguments.Select(a => (a.Value ?? string.Empty).Named(a.Name)).ToArray());
    public string Admission(LocalMutationAdmission admission, LocalMutationState? pending) => ClientLocalMutationPresentation.AdmissionFailure(admission, pending);
    public void PreparePreviews(IReadOnlyList<ModTradeOrderSummaryDto> orders) => TradeUiUtility.PreparePawnPreviewsForTradeOrders(orders);
    public void Diagnose(Exception exception) => Log.Warning("[ClashOfRim] Trade market operation failed: " + exception);
}
