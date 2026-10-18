using AIRsLight.ClashOfRim.Modules.World;
using AIRsLight.ClashOfRim.Trades;
using AIRsLight.ClashOfRim.WorldObjects;

namespace AIRsLight.ClashOfRim.ClientNetwork;

internal sealed class ClientSessionExitResources : IClientSessionExitResources
{
    public void ClearPawnPreviews() => TradePawnPreviewService.Reset();
    public void ClearWorldIcons() => RemoteColonyWorldIconCache.Clear();
}
