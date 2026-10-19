using AIRsLight.ClashOfRim.Modules.Multiplayer;
using AIRsLight.ClashOfRim.Multiplayer.Pages;
using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace AIRsLight.ClashOfRim.Multiplayer;

public sealed partial class ClashOfRimMultiplayerWindow : MainTabWindow
{
    private ClientPlayerPanel? playersPanel;
    private MultiplayerDiplomacyPage? diplomacyPage;
    private MultiplayerAchievementsPage? achievementsPage;
    private MultiplayerTradePage? tradePage;
    private ClientShopPanel? shopPanel;
    private MultiplayerShopPage? shopPage;
    private MultiplayerAdminShopPage? adminShopPage;
    private MultiplayerAdminPlayersPage? adminPlayersPage;
    private ClientBankPanel? bankPanel;
    private MultiplayerBankPage? bankPage;
    private MultiplayerMercenaryPage? mercenaryPage;
    private MultiplayerAccountPage? accountPage;
    private MultiplayerDiagnosticsPage? diagnosticsPage;
    private ClientAdminConfigurationEditor? adminConfiguration;
    private MultiplayerAdminConfigurationPage? adminConfigurationPage;

    private const float PanelRefreshIntervalSeconds = PanelRefreshSchedule.IntervalSeconds;

    private Vector2 adminScrollPosition;
    private Vector2 adminManifestScrollPosition;
    private MultiplayerTab selectedTab = MultiplayerTab.Trade;
    private AdminPanelTab selectedAdminPanelTab = AdminPanelTab.Trade;
    private float nextAdminRefreshAt;
    private string adminBroadcastMessage = string.Empty;
    private string adminBroadcastSeverity = "Info";
    private string adminBroadcastTargetUserId = string.Empty;
    private string adminBroadcastTargetColonyId = string.Empty;
    private bool adminBroadcastPersistent = true;
    private string adminMaintenanceReason = string.Empty;
    private MultiplayerTab cachedMainTabsSelectedTab;
    private bool cachedMainTabsDevMode;
    private bool cachedMainTabsAdministrator;
    private List<TabRecord>? cachedMainTabs;
    private AdminPanelTab cachedAdminTabsSelectedTab;
    private List<TabRecord>? cachedAdminTabs;

    public ClashOfRimMultiplayerWindow()
    {
        closeOnClickedOutside = false;
        absorbInputAroundWindow = false;
        forcePause = false;
    }

    public override Vector2 RequestedTabSize => new(
        Mathf.Min(1040f, UI.screenWidth - 40f),
        Mathf.Min(720f, UI.screenHeight - 80f));

    public override void DoWindowContents(Rect inRect)
    {
        ClashOfRimMod? mod = LoadedModManager.GetMod<ClashOfRimMod>();
        if (mod is null)
        {
            Widgets.Label(inRect, ClashOfRimText.Key("ClashOfRim.RuntimeConfigurationMissing"));
            return;
        }

        if (playersPanel is null) (playersPanel, diplomacyPage) = mod.CreatePlayerPages();
        achievementsPage ??= mod.CreateAchievementsPage(playersPanel);
        tradePage ??= mod.CreateTradePage();
        if (shopPanel is null) (shopPanel, shopPage) = mod.CreateShopPage();
        adminShopPage ??= mod.CreateAdminShopPage(shopPanel);
        adminPlayersPage ??= mod.CreateAdminPlayersPage();
        if (bankPanel is null) (bankPanel, bankPage) = mod.CreateBankPage();
        mercenaryPage ??= mod.CreateMercenaryPage(bankPanel);
        accountPage ??= mod.CreateAccountPage();
        diagnosticsPage ??= mod.CreateDiagnosticsPage();
        if (adminConfiguration is null) (adminConfiguration, adminConfigurationPage) = mod.CreateAdminConfigurationPage();
        TryRefreshSelectedTab(mod);
        Rect tabRect = new(inRect.x, inRect.y + 34f, inRect.width, inRect.height - 34f);
        if (!mod.IsAdministrator && selectedTab == MultiplayerTab.Admin)
        {
            selectedTab = MultiplayerTab.Trade;
        }

        TabDrawer.DrawTabs(tabRect, MainTabs(mod));
        Rect contentRect = tabRect;
        switch (selectedTab)
        {
            case MultiplayerTab.Diplomacy:
                diplomacyPage!.Draw(contentRect);
                break;
            case MultiplayerTab.Achievements:
                if (achievementsPage!.Draw(contentRect)) Close(doCloseSound: false);
                break;
            case MultiplayerTab.Bank:
                bankPage!.Draw(contentRect);
                break;
            case MultiplayerTab.Shop:
                shopPage!.Draw(contentRect);
                break;
            case MultiplayerTab.Mercenary:
                mercenaryPage!.Draw(contentRect);
                break;
            case MultiplayerTab.Diagnostics:
                diagnosticsPage!.Draw(contentRect);
                break;
            case MultiplayerTab.Account:
                accountPage!.Draw(contentRect);
                break;
            case MultiplayerTab.Admin:
                DrawAdminTab(contentRect, mod);
                break;
            default:
                tradePage!.Draw(contentRect);
                break;
        }
    }

    public override void PostClose()
    {
        playersPanel?.Close();
        diplomacyPage?.Reset();
        achievementsPage?.Close();
        tradePage?.Close();
        shopPage?.Close();
        adminShopPage?.Close();
        adminPlayersPage?.Close();
        bankPage?.Close();
        mercenaryPage?.Close();
        accountPage?.Close();
        adminConfigurationPage?.Close();
        base.PostClose();
    }

    private List<TabRecord> MainTabs(ClashOfRimMod mod)
    {
        bool devMode = Prefs.DevMode;
        bool administrator = mod.IsAdministrator;
        if (cachedMainTabs is not null
            && cachedMainTabsSelectedTab == selectedTab
            && cachedMainTabsDevMode == devMode
            && cachedMainTabsAdministrator == administrator)
        {
            return cachedMainTabs;
        }

        cachedMainTabsSelectedTab = selectedTab;
        cachedMainTabsDevMode = devMode;
        cachedMainTabsAdministrator = administrator;
        cachedMainTabs = BuildMainTabs(devMode, administrator);
        return cachedMainTabs;
    }

    private List<TabRecord> BuildMainTabs(bool devMode, bool administrator)
    {
        var tabs = new List<TabRecord>
        {
            MainTab(MultiplayerTab.Trade, ClashOfRimText.Key("ClashOfRim.Multiplayer.TabTrade")),
            MainTab(MultiplayerTab.Shop, ClashOfRimText.Key("ClashOfRim.Multiplayer.TabShop")),
            MainTab(MultiplayerTab.Bank, ClashOfRimText.Key("ClashOfRim.Multiplayer.TabBank")),
            MainTab(MultiplayerTab.Mercenary, ClashOfRimText.Key("ClashOfRim.Multiplayer.TabMercenary")),
            MainTab(MultiplayerTab.Diplomacy, ClashOfRimText.Key("ClashOfRim.Multiplayer.TabDiplomacy")),
            MainTab(MultiplayerTab.Achievements, ClashOfRimText.Key("ClashOfRim.Multiplayer.TabAchievements")),
            MainTab(MultiplayerTab.Account, ClashOfRimText.Key("ClashOfRim.Multiplayer.TabAccount"))
        };
        if (devMode)
        {
            tabs.Add(MainTab(MultiplayerTab.Diagnostics, ClashOfRimText.Key("ClashOfRim.Multiplayer.TabDiagnostics")));
        }

        if (administrator)
        {
            tabs.Add(MainTab(MultiplayerTab.Admin, ClashOfRimText.Key("ClashOfRim.Multiplayer.TabAdmin")));
        }

        return tabs;
    }

    private TabRecord MainTab(MultiplayerTab tab, string label)
    {
        return new TabRecord(label, () => SelectMainTab(tab), selectedTab == tab);
    }

    private void SelectMainTab(MultiplayerTab tab)
    {
        if (selectedTab == tab)
        {
            return;
        }

        selectedTab = tab;
        cachedMainTabs = null;
        ResetRefreshTimer(tab);
    }

    private void ResetRefreshTimer(MultiplayerTab tab)
    {
        switch (tab)
        {
            case MultiplayerTab.Diplomacy:
                playersPanel?.RequestRefresh();
                break;
            case MultiplayerTab.Achievements:
                playersPanel?.RequestRefresh();
                achievementsPage?.RequestRefresh();
                break;
            case MultiplayerTab.Bank:
            case MultiplayerTab.Mercenary:
                bankPanel?.RequestRefresh();
                break;
            case MultiplayerTab.Trade:
                tradePage?.RequestRefresh();
                break;
            case MultiplayerTab.Shop:
                shopPanel?.RequestRefresh();
                break;
            case MultiplayerTab.Admin:
                nextAdminRefreshAt = 0f;
                break;
        }
    }

    private void TryRefreshSelectedTab(ClashOfRimMod mod)
    {
        switch (selectedTab)
        {
            case MultiplayerTab.Diplomacy:
                _ = playersPanel!.RefreshAsync(Time.realtimeSinceStartup);
                break;
            case MultiplayerTab.Achievements:
                _ = playersPanel!.RefreshAsync(Time.realtimeSinceStartup);
                achievementsPage!.Tick(Time.realtimeSinceStartup);
                break;
            case MultiplayerTab.Bank:
            case MultiplayerTab.Mercenary:
                bankPage!.Tick(Time.realtimeSinceStartup);
                break;
            case MultiplayerTab.Trade:
                tradePage!.Tick(Time.realtimeSinceStartup);
                break;
            case MultiplayerTab.Shop:
                shopPage!.Tick(Time.realtimeSinceStartup);
                break;
            case MultiplayerTab.Admin:
                TryRefreshAdmin(mod);
                break;
        }
    }

    private void TryRefreshAdmin(ClashOfRimMod mod)
    {
        if (!mod.IsAdministrator || mod.AdminInProgress || Time.realtimeSinceStartup < nextAdminRefreshAt)
        {
            return;
        }

        nextAdminRefreshAt = Time.realtimeSinceStartup + PanelRefreshIntervalSeconds;
        mod.StartRefreshAdminStatus();
    }

    private enum MultiplayerTab
    {
        Trade,
        Shop,
        Bank,
        Mercenary,
        Diplomacy,
        Achievements,
        Account,
        Admin,
        Diagnostics
    }

    private enum AdminPanelTab
    {
        Trade,
        Shop,
        Bank,
        Mercenary,
        Raid,
        Players,
        Maintenance,
        Manifest,
        Audit
    }
}
