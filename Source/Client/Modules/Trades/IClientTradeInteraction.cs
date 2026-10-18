using System;
using AIRsLight.ClashOfRim.ClientNetwork;

namespace AIRsLight.ClashOfRim.Modules.Trades;

internal interface IClientTradeInteraction
{
    void OpenCreate();
    void ConfirmGoods(ModTradeOrderSummaryDto order, Action confirmed);
    void FulfillByDropPod(ModTradeOrderSummaryDto order);
}
