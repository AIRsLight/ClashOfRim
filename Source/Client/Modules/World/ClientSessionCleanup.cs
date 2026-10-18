using AIRsLight.ClashOfRim.Modules.Administration;
using AIRsLight.ClashOfRim.Modules.Bank;
using AIRsLight.ClashOfRim.Modules.Chat;
using AIRsLight.ClashOfRim.Modules.Diplomacy;
using AIRsLight.ClashOfRim.Modules.Events;
using AIRsLight.ClashOfRim.Modules.Mercenaries;
using AIRsLight.ClashOfRim.Modules.Presence;
using AIRsLight.ClashOfRim.Modules.Shop;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Synchronization;
using AIRsLight.ClashOfRim.Modules.Trades;

namespace AIRsLight.ClashOfRim.Modules.World;

internal interface IClientSessionExitStore
{
    // Stage settings only; the lifecycle persists them with the cleared authentication.
    void ClearWorld();
    void ClearSnapshot();
}
internal interface IClientSessionExitResources
{
    void ClearPawnPreviews();
    void ClearWorldIcons();
}

// One application-lifetime composition owns explicit-exit ordering. Each domain
// is a separate lifecycle subscriber so a reentrant login stops the remaining work.
// Expiry deliberately preserves the world and does not dispatch these operations.
internal static class ClientSessionCleanup
{
    internal static void Attach(ClientSessionLifecycle session, ClientSynchronizationCoordinator synchronization,
        ClientLocalMutationCoordinator mutations, ClientEventInbox inbox, ClientEventState events, ClientWorldSourceGame sourceGame,
        IClientSessionExitResources resources, ClientColonyRegistration registration, ClientBankState bank,
        ClientMercenaryState mercenaries, ClientMercenaryIncidents incidents, ClientShopState shop, ClientTradeState trade,
        ClientChat chat, ClientWorldMarkers markers, ClientWorldSynchronization worldSynchronization, IClientSessionExitStore store,
        ClientAdministration administration, ClientSessionNotifications notifications, ClientWorldSetupState setup,
        ClientPlayerDirectory players, ClientOccupiedColonySites occupied)
    {
        session.Ended += synchronization.Reset;
        session.Ended += mutations.Reset;
        session.Ended += inbox.Reset;
        session.Ended += events.Reset;
        session.Ended += sourceGame.BlockCurrent;
        session.Ended += resources.ClearPawnPreviews;
        session.Ended += registration.Reset;
        session.Ended += bank.Reset;
        session.Ended += mercenaries.Reset;
        session.Ended += incidents.Reset;
        session.Ended += shop.Reset;
        session.Ended += trade.Reset;
        session.Ended += chat.Reset;
        session.Ended += resources.ClearWorldIcons;
        session.Ended += markers.Reset;
        session.Ended += worldSynchronization.Reset;
        session.Ended += store.ClearWorld;
        session.Ended += administration.Reset;
        session.Ended += notifications.Reset;
        session.Ended += setup.Reset;
        session.Ended += store.ClearSnapshot;
        session.Ended += players.ClearSelection;
        session.Ended += players.Reset;
        session.Ended += occupied.Reset;
    }
}
