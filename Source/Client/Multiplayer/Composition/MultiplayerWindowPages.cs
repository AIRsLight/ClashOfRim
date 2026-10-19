using AIRsLight.ClashOfRim.Modules.Multiplayer;
using AIRsLight.ClashOfRim.Multiplayer.Pages;

namespace AIRsLight.ClashOfRim.Multiplayer;

internal sealed record MultiplayerWindowPages(
    ClientPlayerPanel Players, MultiplayerDiplomacyPage Diplomacy, MultiplayerAchievementsPage Achievements,
    MultiplayerTradePage Trade, ClientShopPanel ShopModel, MultiplayerShopPage Shop,
    ClientBankPanel BankModel, MultiplayerBankPage Bank, MultiplayerMercenaryPage Mercenary,
    MultiplayerAccountPage Account, MultiplayerDiagnosticsPage Diagnostics, MultiplayerAdminPage Admin)
{
    internal void Close()
    {
        Players.Close(); Diplomacy.Reset(); Achievements.Close(); Trade.Close();
        Shop.Close(); Bank.Close(); Mercenary.Close(); Account.Close(); Admin.Close();
    }
}
