using AIRsLight.ClashOfRim.Modules.Chat;
using AIRsLight.ClashOfRim.Modules.Diplomacy;
using AIRsLight.ClashOfRim.Modules.Events;
using AIRsLight.ClashOfRim.Modules.Presence;
using AIRsLight.ClashOfRim.Modules.Raids;
using AIRsLight.ClashOfRim.Modules.World;
using AIRsLight.ClashOfRim.Raids;

namespace AIRsLight.ClashOfRim.ClientNetwork;

internal sealed class ClientSessionRecoveryEffects : IClientSessionRecoveryEffects
{
    private readonly ClientEventRefresh ledger;
    private readonly ClientPlayerDirectory players;
    private readonly ClientChat chat;
    private readonly ClientSessionNotifications notifications;
    private readonly ClientRaidRecovery raids;
    internal ClientSessionRecoveryEffects(ClientEventRefresh ledger, ClientPlayerDirectory players, ClientChat chat,
        ClientSessionNotifications notifications, ClientRaidRecovery raids)
    { this.ledger = ledger; this.players = players; this.chat = chat; this.notifications = notifications; this.raids = raids; }
    public void AdoptQueue(ModEventQueueSummaryDto? queue) => ledger.AdoptQueue(queue);
    public void RefreshLedger(string reason) => _ = ledger.StartAutomatic(reason);
    public void RefreshPlayers() => _ = players.RefreshAsync(manual: false);
    public void RefreshChat() => _ = chat.RefreshAsync();
    public void StartPresence() => _ = notifications.Start();
    public void ReconcileRaid(ModActiveRaidRecoveryDto? recovery) => raids.Reconcile(new ClientRaidRecoveryRuntime(), recovery);
}
