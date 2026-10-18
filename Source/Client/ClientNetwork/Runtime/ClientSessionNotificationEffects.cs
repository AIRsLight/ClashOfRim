using System;
using AIRsLight.ClashOfRim.Modules.Chat;
using AIRsLight.ClashOfRim.Modules.Events;
using AIRsLight.ClashOfRim.Modules.Presence;
using AIRsLight.ClashOfRim.Modules.World;

namespace AIRsLight.ClashOfRim.ClientNetwork;

internal sealed class ClientSessionNotificationEffects : IClientSessionNotificationEffects
{
    private readonly ClientChat chat;
    private readonly ClientEventRefresh ledger;
    private readonly ClientWorldCatalog world;
    internal event Action<string?, string>? SessionExpired;
    internal ClientSessionNotificationEffects(ClientChat chat, ClientEventRefresh ledger, ClientWorldCatalog world)
    { this.chat = chat; this.ledger = ledger; this.world = world; }
    public void RefreshChat(bool initial) => _ = chat.RefreshAsync(initial);
    public void RefreshLedger(string reason) => _ = ledger.StartAutomatic(reason);
    public void RefreshWorld(string reason) => _ = world.RefreshAsync(reason, generation: true);
    public void Expired(ClashOfRimClientNetworkContext context, string sessionId) => SessionExpired?.Invoke(context.AuthToken, sessionId);
}
