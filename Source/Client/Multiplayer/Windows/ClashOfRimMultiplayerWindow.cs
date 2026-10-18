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
    private ClientBankPanel? bankPanel;
    private MultiplayerBankPage? bankPage;
    private MultiplayerMercenaryPage? mercenaryPage;
    private MultiplayerAccountPage? accountPage;
    private MultiplayerDiagnosticsPage? diagnosticsPage;

    private const float PanelRefreshIntervalSeconds = PanelRefreshSchedule.IntervalSeconds;

    private Vector2 adminScrollPosition;
    private Vector2 adminManifestScrollPosition;
    private MultiplayerTab selectedTab = MultiplayerTab.Trade;
    private AdminPanelTab selectedAdminPanelTab = AdminPanelTab.Trade;
    private float nextAdminRefreshAt;
    private string adminConfigSignature = string.Empty;
    private bool adminTradeMarketplaceEnabled = true;
    private string adminTradeOrderExpirationDaysText = string.Empty;
    private string adminMaxOpenTradeOrdersText = string.Empty;
    private string adminPostageBaseText = string.Empty;
    private string adminPostagePerTileText = string.Empty;
    private string adminPostageCrossLayerText = string.Empty;
    private string adminTradeBaseFeeRateText = string.Empty;
    private string adminTradeFeeStrategy = "Publisher";
    private string adminRelationCooldownHoursText = string.Empty;
    private string adminSupportCooldownMinutesText = string.Empty;
    private string adminForcedGiftCooldownMinutesText = string.Empty;
    private bool adminGiftsEnabled = true;
    private bool adminBankLoansEnabled = true;
    private string adminBankMinLoanSilverText = string.Empty;
    private string adminBankMaxLoanSilverText = string.Empty;
    private string adminBankLoanRatioText = string.Empty;
    private string adminBankInterestRateText = string.Empty;
    private string adminBankMinDaysText = string.Empty;
    private string adminBankMaxDaysText = string.Empty;
    private string adminBankInterestCurveText = string.Empty;
    private string adminBankPenaltyIntervalText = string.Empty;
    private string adminBankPenaltyPointsText = string.Empty;
    private bool adminMercenariesEnabled = true;
    private string adminMercApprenticeText = string.Empty;
    private string adminMercSkilledText = string.Empty;
    private string adminMercMasterText = string.Empty;
    private string adminMercMinDaysText = string.Empty;
    private string adminMercMaxDaysText = string.Empty;
    private string adminMercDurationCurveText = string.Empty;
    private string adminMercMaxActiveText = string.Empty;
    private string adminMercHarmfulSurgeryText = string.Empty;
    private string adminMercApprenticeDeathText = string.Empty;
    private string adminMercSkilledDeathText = string.Empty;
    private string adminMercMasterDeathText = string.Empty;
    private bool adminMercGuardsEnabled = true;
    private string adminMercGuardApprenticeText = string.Empty;
    private string adminMercGuardSkilledText = string.Empty;
    private string adminMercGuardMasterText = string.Empty;
    private string adminMercGuardApprenticeRatioText = string.Empty;
    private string adminMercGuardSkilledRatioText = string.Empty;
    private string adminMercGuardMasterRatioText = string.Empty;
    private bool adminPvpEnabled = true;
    private string adminRaidProtectionHoursText = string.Empty;
    private string adminRaidMaxDurationMinutesText = string.Empty;
    private string adminRaidTimeoutGraceMinutesText = string.Empty;
    private string adminRaidMinimumDefenderWealthText = string.Empty;
    private string adminRaidLossRatioText = string.Empty;
    private string adminRaidBuildingHpLossRatioText = string.Empty;
    private string adminRaidMinimumHpRatioText = string.Empty;
    private string adminPendingConfirmationMinutesText = string.Empty;
    private readonly List<AdminFixedTradeFeeRow> adminFixedTradeFeeRows = new();
    private readonly List<AdminBankOverduePenaltyStageRow> adminBankOverduePenaltyStageRows = new();
    private string adminBroadcastMessage = string.Empty;
    private string adminBroadcastSeverity = "Info";
    private string adminBroadcastTargetUserId = string.Empty;
    private string adminBroadcastTargetColonyId = string.Empty;
    private bool adminBroadcastPersistent = true;
    private string adminMaintenanceReason = string.Empty;
    private readonly Dictionary<string, string> adminShopStockBuffers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> adminShopPriceBuffers = new(StringComparer.Ordinal);
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
        if (bankPanel is null) (bankPanel, bankPage) = mod.CreateBankPage();
        mercenaryPage ??= mod.CreateMercenaryPage(bankPanel);
        accountPage ??= mod.CreateAccountPage();
        diagnosticsPage ??= mod.CreateDiagnosticsPage();
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
        bankPage?.Close();
        mercenaryPage?.Close();
        accountPage?.Close();
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
