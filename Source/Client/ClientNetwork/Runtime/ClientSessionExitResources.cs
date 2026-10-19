using AIRsLight.ClashOfRim.Modules.World;
using AIRsLight.ClashOfRim.Modules.Trades;
using AIRsLight.ClashOfRim.WorldObjects;

namespace AIRsLight.ClashOfRim.ClientNetwork;

internal sealed class ClientSessionExitResources : IClientSessionExitResources
{
    private readonly ClientPawnPreviews previews;
    internal ClientSessionExitResources(ClientPawnPreviews previews) => this.previews = previews;
    public void ClearPawnPreviews() => previews.Reset();
    public void ClearWorldIcons() => RemoteColonyWorldIconCache.Clear();
}
