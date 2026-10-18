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
    private readonly Action<string?, string> expired;
    internal ClientSessionNotificationEffects(ClientChat chat, ClientEventRefresh ledger, ClientWorldCatalog world, Action<string?, string> expired)
    { this.chat = chat; this.ledger = ledger; this.world = world; this.expired = expired; }
    public void RefreshChat(bool initial) => _ = chat.RefreshAsync(initial);
    public void RefreshLedger(string reason) => _ = ledger.StartAutomatic(reason);
    public void RefreshWorld(string reason) => _ = world.RefreshAsync(reason, generation: true);
    public void Expired(ClashOfRimClientNetworkContext context, string sessionId) => expired(context.AuthToken, sessionId);
}
