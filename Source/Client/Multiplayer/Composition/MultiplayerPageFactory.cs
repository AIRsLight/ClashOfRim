using System;
using AIRsLight.ClashOfRim.Bank;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.ClientSnapshots;
using AIRsLight.ClashOfRim.CompatibilityClient;
using AIRsLight.ClashOfRim.Modules.Accounts;
using AIRsLight.ClashOfRim.Modules.Achievements;
using AIRsLight.ClashOfRim.Modules.Administration;
using AIRsLight.ClashOfRim.Modules.Bank;
using AIRsLight.ClashOfRim.Modules.Compatibility;
using AIRsLight.ClashOfRim.Modules.Diagnostics;
using AIRsLight.ClashOfRim.Modules.Diplomacy;
using AIRsLight.ClashOfRim.Modules.Mercenaries;
using AIRsLight.ClashOfRim.Modules.Multiplayer;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Shop;
using AIRsLight.ClashOfRim.Modules.Synchronization;
using AIRsLight.ClashOfRim.Modules.Trades;
using AIRsLight.ClashOfRim.Modules.World;
using AIRsLight.ClashOfRim.Multiplayer.Achievements;
using AIRsLight.ClashOfRim.Multiplayer.Pages;
using AIRsLight.ClashOfRim.Multiplayer.Players;
using AIRsLight.ClashOfRim.Trades;
using UnityEngine;

namespace AIRsLight.ClashOfRim.Multiplayer;

// Screen composition only: creates independent page instances, sharing their
// player/shop/bank models inside one screen. It exposes no business operations.
internal sealed class MultiplayerPageFactory
{
    private readonly ClientSessionLifecycle sessionLifecycle;
    private readonly ClientSynchronizationCoordinator synchronization;
    private readonly ClientPlayerDirectory playerDirectory;
    private readonly ClientDiplomacyActions diplomacyActions;
    private readonly ClientWorldMarkers worldMarkers;
    private readonly ClientWorldSynchronization worldSynchronization;
    private readonly ClientAchievementQueries achievementQueries;
    private readonly ClientTradeState tradeState;
    private readonly ClientTradeMarket tradeMarket;
    private readonly ClientTradeActions tradeActions;
    private readonly ClientShopState shopState;
    private readonly ClientShopCatalog shopCatalog;
    private readonly ClientShopOperations shopOperations;
    private readonly ClientBankState bankState;
    private readonly ClientBankOperations bankOperations;
    private readonly ClientMercenaryState mercenaryState;
    private readonly ClientMercenaryQuotes mercenaryQuotes;
    private readonly ClientMercenaryHiring mercenaryHiring;
    private readonly ClientAdminAccess adminAccess;
    private readonly ClientAdministration administration;
    private readonly ClientCompatibilityOverride compatibilityOverride;
    private readonly ClientWorldWrites worldWrites;
    private readonly ClientColonyAppearance colonyAppearance;
    private readonly ClientPasswordChange passwordChange;
    private readonly ClientRuntimeDiagnostics diagnostics;

    internal MultiplayerPageFactory(ClientSessionLifecycle sessionLifecycle, ClientSynchronizationCoordinator synchronization,
        ClientPlayerDirectory playerDirectory, ClientDiplomacyActions diplomacyActions,
        ClientWorldMarkers worldMarkers, ClientWorldSynchronization worldSynchronization, ClientAchievementQueries achievementQueries,
        ClientTradeState tradeState, ClientTradeMarket tradeMarket, ClientTradeActions tradeActions,
        ClientShopState shopState, ClientShopCatalog shopCatalog, ClientShopOperations shopOperations,
        ClientBankState bankState, ClientBankOperations bankOperations,
        ClientMercenaryState mercenaryState, ClientMercenaryQuotes mercenaryQuotes, ClientMercenaryHiring mercenaryHiring,
        ClientAdminAccess adminAccess, ClientAdministration administration, ClientCompatibilityOverride compatibilityOverride,
        ClientWorldWrites worldWrites, ClientColonyAppearance colonyAppearance, ClientPasswordChange passwordChange, ClientRuntimeDiagnostics diagnostics)
    {
        this.sessionLifecycle = sessionLifecycle;
        this.synchronization = synchronization;
        this.playerDirectory = playerDirectory;
        this.diplomacyActions = diplomacyActions;
        this.worldMarkers = worldMarkers;
        this.worldSynchronization = worldSynchronization;
        this.achievementQueries = achievementQueries;
        this.tradeState = tradeState;
        this.tradeMarket = tradeMarket;
        this.tradeActions = tradeActions;
        this.shopState = shopState;
        this.shopCatalog = shopCatalog;
        this.shopOperations = shopOperations;
        this.bankState = bankState;
        this.bankOperations = bankOperations;
        this.mercenaryState = mercenaryState;
        this.mercenaryQuotes = mercenaryQuotes;
        this.mercenaryHiring = mercenaryHiring;
        this.adminAccess = adminAccess;
        this.administration = administration;
        this.compatibilityOverride = compatibilityOverride;
        this.worldWrites = worldWrites;
        this.colonyAppearance = colonyAppearance;
        this.passwordChange = passwordChange;
        this.diagnostics = diagnostics;
    }

    internal MultiplayerWindowPages Create()
    {
        var players = new ClientPlayerPanel(sessionLifecycle, new ClientSnapshotGame(), playerDirectory, diplomacyActions, synchronization);
        var locations = new PlayerColonyLocator(() => worldMarkers.Snapshot,
            reason => _ = worldSynchronization.RequestAsync(ClientWorldSynchronizationKind.Markers, reason), () => Time.realtimeSinceStartup);
        var shop = CreateShopPanel();
        var bank = new ClientBankPanel(sessionLifecycle, new ClientSnapshotGame(), bankState, bankOperations, synchronization, new ClientBankRuntime());
        var configuration = new ClientAdminConfigurationEditor(sessionLifecycle, adminAccess, administration);
        var adminPlayers = new ClientAdminPlayerPanel(sessionLifecycle, adminAccess, administration);
        var admin = new MultiplayerAdminPage(new ClientAdminPanel(sessionLifecycle, adminAccess, administration), configuration, new(configuration),
            new(CreateAdminShopPanel(shop)), new(adminPlayers),
            new(new ClientAdminMaintenancePanel(sessionLifecycle, adminPlayers, administration)),
            new(new ClientAdminManifestPanel(configuration, new ClientLocalCompatibilityManifest(), compatibilityOverride, worldWrites, colonyAppearance.Capture)),
            new(new ClientAdminAuditPanel(sessionLifecycle, adminAccess, administration)));
        return new(players, new(players, locations),
            new(new ClientAchievementPanel(sessionLifecycle, new ClientSnapshotGame(), achievementQueries, players, synchronization, new ClientAchievementTrophyPlacement())),
            CreateTradePage(), shop, new(shop), bank, new(bank),
            new(new ClientMercenaryPanel(sessionLifecycle, new ClientSnapshotGame(), mercenaryState, mercenaryQuotes,
                mercenaryHiring, synchronization, bank.Read, () => worldMarkers.PvpEnabled)),
            new(new ClientAccountPanel(sessionLifecycle, passwordChange)), new(diagnostics), admin);
    }
    internal MultiplayerTradePage CreateTradePage() =>
        new(new ClientTradePanel(sessionLifecycle, new ClientSnapshotGame(), tradeState, tradeMarket, synchronization, tradeActions));
    private ClientShopPanel CreateShopPanel() =>
        new(sessionLifecycle, new ClientSnapshotGame(), shopState, shopCatalog, shopOperations, synchronization);
    private ClientAdminShopPanel CreateAdminShopPanel(ClientShopPanel shop) =>
        new(sessionLifecycle, adminAccess, shopState, shopCatalog, shop);
    internal ClientAdminShopPanel.Edit CreateAdminShopEdit(ModServerShopListingDto? listing)
    {
        var panel = CreateAdminShopPanel(CreateShopPanel());
        return listing is null ? panel.PrepareEdit() : panel.PrepareExistingEdit(listing);
    }
}
