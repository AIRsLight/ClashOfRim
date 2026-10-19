using AIRsLight.ClashOfRim.Multiplayer.Pages;
using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace AIRsLight.ClashOfRim.Multiplayer;

public sealed class ClashOfRimMultiplayerWindow : MainTabWindow
{
    private static MultiplayerPageFactory? factory;
    internal static void Attach(MultiplayerPageFactory? value) => factory = value;
    private MultiplayerPageFactory? composition;
    private MultiplayerWindowPages? pages;
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
        var current = factory;
        if (current is null)
        {
            Widgets.Label(inRect, ClashOfRimText.Key("ClashOfRim.RuntimeConfigurationMissing"));
            return;
        }
        if (!ReferenceEquals(composition, current))
        {
            pages?.Close(); mainTabs.Clear();
            composition = current; pages = current.Create();
        }
        var displayed = pages!;
        object owner = displayed.Players.Ownership;
        bool CurrentPage() => ReferenceEquals(factory, current) && ReferenceEquals(pages, displayed)
            && ReferenceEquals(owner, displayed.Players.Ownership);
        if (!CanSelect(selectedTab)) selectedTab = MultiplayerTab.Trade;
        TryRefreshSelectedTab();
        if (!CurrentPage()) return;
        Rect tabRect = new(inRect.x, inRect.y + 34f, inRect.width, inRect.height - 34f);
        var tabs = MainTabs(owner, CurrentPage);
        if (!CurrentPage()) return;
        TabDrawer.DrawTabs(tabRect, tabs);
        if (!CurrentPage()) return;
        if (!CanSelect(selectedTab)) selectedTab = MultiplayerTab.Trade;
        Rect contentRect = tabRect;
        switch (selectedTab)
        {
            case MultiplayerTab.Diplomacy:
                pages!.Diplomacy.Draw(contentRect);
                break;
            case MultiplayerTab.Achievements:
                if (pages!.Achievements.Draw(contentRect)) Close(doCloseSound: false);
                break;
            case MultiplayerTab.Bank:
                pages!.Bank.Draw(contentRect);
                break;
            case MultiplayerTab.Shop:
                pages!.Shop.Draw(contentRect);
                break;
            case MultiplayerTab.Mercenary:
                pages!.Mercenary.Draw(contentRect);
                break;
            case MultiplayerTab.Diagnostics:
                pages!.Diagnostics.Draw(contentRect);
                break;
            case MultiplayerTab.Account:
                pages!.Account.Draw(contentRect);
                break;
            case MultiplayerTab.Admin:
                pages!.Admin.Draw(contentRect);
                break;
            default:
                pages!.Trade.Draw(contentRect);
                break;
        }
    }

    public override void PostClose()
    {
        pages?.Close();
        mainTabs.Clear();
        base.PostClose();
    }

    private List<TabRecord> MainTabs(object owner, Func<bool> current)
    {
        bool devMode = Prefs.DevMode, administrator = pages!.Admin.IsAvailable;
        return mainTabs.Read(owner, (selectedTab, devMode, administrator), LanguageDatabase.activeLanguage,
            () => BuildMainTabs(devMode, administrator, current));
    }

    private List<TabRecord> BuildMainTabs(bool devMode, bool administrator, Func<bool> current)
    {
        var tabs = new List<TabRecord>
        {
            MainTab(current, MultiplayerTab.Trade, ClashOfRimText.Key("ClashOfRim.Multiplayer.TabTrade")),
            MainTab(current, MultiplayerTab.Shop, ClashOfRimText.Key("ClashOfRim.Multiplayer.TabShop")),
            MainTab(current, MultiplayerTab.Bank, ClashOfRimText.Key("ClashOfRim.Multiplayer.TabBank")),
            MainTab(current, MultiplayerTab.Mercenary, ClashOfRimText.Key("ClashOfRim.Multiplayer.TabMercenary")),
            MainTab(current, MultiplayerTab.Diplomacy, ClashOfRimText.Key("ClashOfRim.Multiplayer.TabDiplomacy")),
            MainTab(current, MultiplayerTab.Achievements, ClashOfRimText.Key("ClashOfRim.Multiplayer.TabAchievements")),
            MainTab(current, MultiplayerTab.Account, ClashOfRimText.Key("ClashOfRim.Multiplayer.TabAccount"))
        };
        if (devMode)
        {
            tabs.Add(MainTab(current, MultiplayerTab.Diagnostics, ClashOfRimText.Key("ClashOfRim.Multiplayer.TabDiagnostics")));
        }

        if (administrator)
        {
            tabs.Add(MainTab(current, MultiplayerTab.Admin, ClashOfRimText.Key("ClashOfRim.Multiplayer.TabAdmin")));
        }

        return tabs;
    }

    private TabRecord MainTab(Func<bool> current, MultiplayerTab tab, string label)
    {
        return new TabRecord(label, () => { if (current()) SelectMainTab(tab); }, selectedTab == tab);
    }

    private bool CanSelect(MultiplayerTab tab) => tab switch
    {
        MultiplayerTab.Admin => pages!.Admin.IsAvailable,
        MultiplayerTab.Diagnostics => Prefs.DevMode,
        _ => true
    };

    private void SelectMainTab(MultiplayerTab tab)
    {
        if (selectedTab == tab || !CanSelect(tab))
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
                pages?.Players.RequestRefresh();
                break;
            case MultiplayerTab.Achievements:
                pages?.Players.RequestRefresh();
                pages?.Achievements.RequestRefresh();
                break;
            case MultiplayerTab.Bank:
            case MultiplayerTab.Mercenary:
                pages?.BankModel.RequestRefresh();
                break;
            case MultiplayerTab.Trade:
                pages?.Trade.RequestRefresh();
                break;
            case MultiplayerTab.Shop:
                pages?.ShopModel.RequestRefresh();
                break;
            case MultiplayerTab.Admin:
                pages?.Admin.RequestRefresh();
                break;
        }
    }

    private void TryRefreshSelectedTab()
    {
        switch (selectedTab)
        {
            case MultiplayerTab.Diplomacy:
                _ = pages!.Players.RefreshAsync(Time.realtimeSinceStartup);
                break;
            case MultiplayerTab.Achievements:
                _ = pages!.Players.RefreshAsync(Time.realtimeSinceStartup);
                pages!.Achievements.Tick(Time.realtimeSinceStartup);
                break;
            case MultiplayerTab.Bank:
            case MultiplayerTab.Mercenary:
                pages!.Bank.Tick(Time.realtimeSinceStartup);
                break;
            case MultiplayerTab.Trade:
                pages!.Trade.Tick(Time.realtimeSinceStartup);
                break;
            case MultiplayerTab.Shop:
                pages!.Shop.Tick(Time.realtimeSinceStartup);
                break;
            case MultiplayerTab.Admin:
                pages!.Admin.Tick(Time.realtimeSinceStartup);
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
