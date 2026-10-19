using AIRsLight.ClashOfRim.Modules.Multiplayer;
using AIRsLight.ClashOfRim.Multiplayer.Pages;
using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace AIRsLight.ClashOfRim.Multiplayer;

public sealed class ClashOfRimMultiplayerWindow : MainTabWindow
{
    private ClientPlayerPanel? playersPanel;
    private MultiplayerDiplomacyPage? diplomacyPage;
    private MultiplayerAchievementsPage? achievementsPage;
    private MultiplayerTradePage? tradePage;
    private ClientShopPanel? shopPanel;
    private MultiplayerShopPage? shopPage;
    private ClientBankPanel? bankPanel;
    private MultiplayerBankPage? bankPage;
    private MultiplayerMercenaryPage? mercenaryPage;
    private MultiplayerAccountPage? accountPage;
    private MultiplayerDiagnosticsPage? diagnosticsPage;
    private MultiplayerAdminPage? adminPage;
    private readonly MultiplayerTabCache<(MultiplayerTab Selected, bool Developer, bool Administrator)> mainTabs = new();

    private MultiplayerTab selectedTab = MultiplayerTab.Trade;

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
        if (bankPanel is null) (bankPanel, bankPage) = mod.CreateBankPage();
        mercenaryPage ??= mod.CreateMercenaryPage(bankPanel);
        accountPage ??= mod.CreateAccountPage();
        diagnosticsPage ??= mod.CreateDiagnosticsPage();
        adminPage ??= mod.CreateAdminPage(shopPanel);
        TryRefreshSelectedTab();
        Rect tabRect = new(inRect.x, inRect.y + 34f, inRect.width, inRect.height - 34f);
        if (!adminPage.IsAvailable && selectedTab == MultiplayerTab.Admin)
        {
            selectedTab = MultiplayerTab.Trade;
        }

        TabDrawer.DrawTabs(tabRect, MainTabs());
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
                adminPage.Draw(contentRect);
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
        bankPage?.Close();
        mercenaryPage?.Close();
        accountPage?.Close();
        adminPage?.Close();
        mainTabs.Clear();
        base.PostClose();
    }

    private List<TabRecord> MainTabs()
    {
        bool devMode = Prefs.DevMode, administrator = adminPage!.IsAvailable;
        return mainTabs.Read(this, (selectedTab, devMode, administrator), LanguageDatabase.activeLanguage,
            () => BuildMainTabs(devMode, administrator));
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
                adminPage?.RequestRefresh();
                break;
        }
    }

    private void TryRefreshSelectedTab()
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
                adminPage!.Tick(Time.realtimeSinceStartup);
                break;
        }
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
}
