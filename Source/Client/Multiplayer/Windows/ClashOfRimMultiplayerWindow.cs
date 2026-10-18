using AIRsLight.ClashOfRim.Modules.Multiplayer;
using AIRsLight.ClashOfRim.Multiplayer.Pages;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Mercenaries;
using AIRsLight.ClashOfRim.Trades;
using AIRsLight.ClashOfRim.Visuals;
using AIRsLight.ClashOfRim.WorldObjects;
using RimWorld;
using RimWorld.Planet;
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

    private const float PanelRefreshIntervalSeconds = PanelRefreshSchedule.IntervalSeconds;

    private Vector2 mercenaryScrollPosition;
    private Vector2 adminScrollPosition;
    private Vector2 adminManifestScrollPosition;
    private MultiplayerTab selectedTab = MultiplayerTab.Trade;
    private AdminPanelTab selectedAdminPanelTab = AdminPanelTab.Trade;
    private string selectedMercenarySkillDefName = "Shooting";
    private int selectedMercenarySkillLevel = 7;
    private float selectedMercenaryDurationDays = 7f;
    private string lastMercenaryQuoteRequestKey = string.Empty;
    private string pendingMercenaryQuoteRequestKey = string.Empty;
    private string mercenaryQuoteStatus = string.Empty;
    private ModMercenaryQuoteResponseDto? mercenaryQuote;
    private bool mercenaryQuoteInProgress;
    private string selectedMercenaryGuardTier = "Apprentice";
    private string lastMercenaryGuardQuoteRequestKey = string.Empty;
    private string pendingMercenaryGuardQuoteRequestKey = string.Empty;
    private string mercenaryGuardQuoteStatus = string.Empty;
    private ModMercenaryGuardQuoteResponseDto? mercenaryGuardQuote;
    private bool mercenaryGuardQuoteInProgress;
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
    private string accountCurrentPassword = string.Empty;
    private string accountNewPassword = string.Empty;
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
                DrawMercenaryTab(contentRect, mod);
                break;
            case MultiplayerTab.Diagnostics:
                DrawDiagnosticsTab(contentRect, mod);
                break;
            case MultiplayerTab.Account:
                DrawAccountTab(contentRect, mod);
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

    private void DrawMercenaryTab(Rect rect, ClashOfRimMod mod)
    {
        Widgets.DrawMenuSection(rect);
        Rect inner = rect.ContractedBy(10f);
        Text.Font = GameFont.Medium;
        Widgets.Label(new Rect(inner.x, inner.y, inner.width, 30f), ClashOfRimText.Key("ClashOfRim.Multiplayer.TabMercenary"));
        Text.Font = GameFont.Small;

        Rect outRect = new(inner.x, inner.y + 40f, inner.width, inner.height - 40f);
        Rect viewRect = new(0f, 0f, outRect.width - 16f, Math.Max(outRect.height, 560f));
        Widgets.BeginScrollView(outRect, ref mercenaryScrollPosition, viewRect);
        float y = 0f;
        Text.Font = GameFont.Tiny;
        Widgets.Label(new Rect(0f, y, viewRect.width, 22f), mod.MercenaryStatus);
        Text.Font = GameFont.Small;
        y += 34f;

        IReadOnlyList<MercenaryProfession> professions = MercenarySkillUtility.AvailableProfessions();
        MercenaryProfession? selected = FindMercenaryProfession(professions, selectedMercenarySkillDefName)
            ?? (professions.Count > 0 ? professions[0] : null);
        if (selected is not null)
        {
            selectedMercenarySkillDefName = selected.Key;
        }

        float selectorColumnWidth = Math.Min(360f, (viewRect.width - 18f) / 2f);
        Widgets.Label(new Rect(0f, y + 4f, 72f, 24f), ClashOfRimText.Key("ClashOfRim.Mercenary.Profession"));
        if (ClashOfRimUiUtility.SelectionButton(
                new Rect(78f, y, selectorColumnWidth - 78f, 28f),
                selected?.Label ?? selectedMercenarySkillDefName))
        {
            List<FloatMenuOption> options = professions
                .Select(profession => new FloatMenuOption(profession.Label, () =>
                {
                    selectedMercenarySkillDefName = profession.Key;
                    InvalidateMercenaryQuote();
                }))
                .ToList();
            Find.WindowStack.Add(new FloatMenu(options));
        }

        float tierX = selectorColumnWidth + 18f;
        Widgets.Label(new Rect(tierX, y + 4f, 72f, 24f), ClashOfRimText.Key("ClashOfRim.Mercenary.Tier"));
        DrawMercenaryTierDropdown(new Rect(tierX + 78f, y, selectorColumnWidth - 78f, 28f));

        y += 42f;
        bool hasMercenaryConfig = HasMercenaryDurationRange();
        (int minMercenaryDays, int maxMercenaryDays) = GetMercenaryDurationRange();
        selectedMercenaryDurationDays = ClampFloat(selectedMercenaryDurationDays, minMercenaryDays, maxMercenaryDays);
        int days = Mathf.RoundToInt(selectedMercenaryDurationDays);
        Widgets.Label(new Rect(0f, y, 180f, 24f), MercenaryUiUtility.FormatDurationLine(days));
        float previousDuration = selectedMercenaryDurationDays;
        selectedMercenaryDurationDays = maxMercenaryDays > minMercenaryDays
            ? Widgets.HorizontalSlider(
                new Rect(190f, y + 2f, 260f, 24f),
                selectedMercenaryDurationDays,
                minMercenaryDays,
                maxMercenaryDays,
                roundTo: 1f)
            : minMercenaryDays;
        if (Mathf.RoundToInt(previousDuration) != Mathf.RoundToInt(selectedMercenaryDurationDays))
        {
            InvalidateMercenaryQuote();
        }

        y += 42f;
        if (hasMercenaryConfig)
        {
            EnsureMercenaryQuote(mod, days);
        }
        else if (!mercenaryQuoteInProgress)
        {
            mercenaryQuoteStatus = ClashOfRimText.Key("ClashOfRim.Mercenary.PriceLoading");
        }

        bool quoteAccepted = mercenaryQuote is not null && (mercenaryQuote.Result is null || mercenaryQuote.Result.Accepted);
        bool mercenariesEnabled = mercenaryQuote?.BankStatus?.MercenariesEnabled ?? true;
        bool canHire = selected is not null
            && hasMercenaryConfig
            && mercenariesEnabled
            && quoteAccepted
            && !mercenaryQuoteInProgress
            && !mod.MercenaryInProgress
            && !mod.ManualSyncInProgress
            && !mod.SnapshotUploadInProgress;
        DrawMercenaryQuote(new Rect(0f, y, viewRect.width, 76f), canHire, () =>
        {
            int price = mercenaryQuote?.PriceSilver ?? 0;
            Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                MercenaryUiUtility.FormatConfirmHire(selected!, selectedMercenarySkillDefName, selectedMercenarySkillLevel, days, price),
                mod.MercenaryHiring.BindHire(selectedMercenarySkillDefName, selectedMercenarySkillLevel, days)));
        });
        y += 88f;

        Text.Font = GameFont.Tiny;
        Widgets.Label(new Rect(0f, y, viewRect.width, 24f), ClashOfRimText.Key("ClashOfRim.Mercenary.HireHint"));
        Text.Font = GameFont.Small;

        y += 36f;
        DrawMercenaryGuardSection(new Rect(0f, y, viewRect.width, 176f), mod);

        Widgets.EndScrollView();
    }

    private void DrawMercenaryGuardSection(Rect rect, ClashOfRimMod mod)
    {
        Widgets.DrawMenuSection(rect);
        Rect inner = rect.ContractedBy(8f);
        float y = inner.y;
        Text.Font = GameFont.Small;
        Widgets.Label(new Rect(inner.x, y, inner.width, 24f), ClashOfRimText.Key("ClashOfRim.Mercenary.GuardTitle"));
        y += 34f;

        Widgets.Label(new Rect(inner.x, y, 120f, 24f), ClashOfRimText.Key("ClashOfRim.Mercenary.GuardTier"));
        DrawMercenaryGuardTierDropdown(new Rect(inner.x + 130f, y, 220f, 28f));
        y += 42f;

        if (mod.PvpEnabled)
        {
            EnsureMercenaryGuardQuote(mod);
        }
        else
        {
            pendingMercenaryGuardQuoteRequestKey = string.Empty;
            mercenaryGuardQuoteInProgress = false;
            mercenaryGuardQuote = null;
            mercenaryGuardQuoteStatus = ClashOfRimText.Key("ClashOfRim.Raid.PvpDisabled");
        }

        bool quoteAccepted = mercenaryGuardQuote is not null && (mercenaryGuardQuote.Result is null || mercenaryGuardQuote.Result.Accepted);
        bool mercenariesEnabled = mercenaryGuardQuote?.BankStatus?.MercenariesEnabled ?? true;
        bool canHire = mod.PvpEnabled
            && mercenariesEnabled
            && quoteAccepted
            && !mercenaryGuardQuoteInProgress
            && !mod.MercenaryInProgress
            && !mod.ManualSyncInProgress
            && !mod.SnapshotUploadInProgress;
        DrawMercenaryGuardQuote(new Rect(inner.x, y, inner.width, 66f), canHire, () =>
        {
            int price = mercenaryGuardQuote?.PriceSilver ?? 0;
            Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                ClashOfRimText.Key(
                    "ClashOfRim.Mercenary.GuardConfirmHire",
                    MercenaryGuardTierLabel(selectedMercenaryGuardTier).Named("TIER"),
                    price.Named("PRICE")),
                mod.MercenaryHiring.BindGuard(selectedMercenaryGuardTier)));
        });
        y += 78f;

        Text.Font = GameFont.Tiny;
        Widgets.Label(new Rect(inner.x, y, inner.width, 34f), ClashOfRimText.Key("ClashOfRim.Mercenary.GuardHint"));
        Text.Font = GameFont.Small;
    }

    private void DrawMercenaryTierDropdown(Rect rect)
    {
        if (ClashOfRimUiUtility.SelectionButton(
                rect,
                MercenarySkillUtility.TierLabel(selectedMercenarySkillLevel)))
        {
            Find.WindowStack.Add(new FloatMenu(new List<FloatMenuOption>
            {
                new(MercenarySkillUtility.TierLabel(7), () => SelectMercenaryTier(7)),
                new(MercenarySkillUtility.TierLabel(14), () => SelectMercenaryTier(14)),
                new(MercenarySkillUtility.TierLabel(20), () => SelectMercenaryTier(20))
            }));
        }
    }

    private void SelectMercenaryTier(int level)
    {
        if (selectedMercenarySkillLevel == level)
        {
            return;
        }

        selectedMercenarySkillLevel = level;
        InvalidateMercenaryQuote();
    }

    private void EnsureMercenaryQuote(ClashOfRimMod mod, int durationDays)
    {
        string requestKey = MercenaryUiUtility.BuildQuoteRequestKey(selectedMercenarySkillDefName, selectedMercenarySkillLevel, durationDays, mod.CurrentSnapshotId);
        if (string.Equals(lastMercenaryQuoteRequestKey, requestKey, StringComparison.Ordinal)
            || string.Equals(pendingMercenaryQuoteRequestKey, requestKey, StringComparison.Ordinal))
        {
            return;
        }

        if (mercenaryQuoteInProgress)
        {
            return;
        }

        pendingMercenaryQuoteRequestKey = requestKey;
        mercenaryQuoteInProgress = true;
        mercenaryQuote = null;
        mercenaryQuoteStatus = ClashOfRimText.Key("ClashOfRim.Mercenary.PriceLoading");
        mod.StartQuoteMercenaryPrice(
            requestKey,
            selectedMercenarySkillDefName,
            selectedMercenarySkillLevel,
            durationDays,
            OnMercenaryQuoteReceived);
    }

    private void OnMercenaryQuoteReceived(string requestKey, string status, ModMercenaryQuoteResponseDto? quote)
    {
        if (!string.Equals(pendingMercenaryQuoteRequestKey, requestKey, StringComparison.Ordinal))
        {
            return;
        }

        pendingMercenaryQuoteRequestKey = string.Empty;
        lastMercenaryQuoteRequestKey = requestKey;
        mercenaryQuoteInProgress = false;
        mercenaryQuote = quote;
        mercenaryQuoteStatus = status;
    }

    private void DrawMercenaryQuote(Rect rect, bool canHire, Action hireAction)
    {
        Widgets.DrawMenuSection(rect);
        Rect inner = rect.ContractedBy(8f);
        const float buttonWidth = 140f;
        Widgets.Label(new Rect(inner.x, inner.y, inner.width - buttonWidth - 12f, 24f), ClashOfRimText.Key("ClashOfRim.Mercenary.PriceTitle"));
        Text.Font = GameFont.Tiny;
        Widgets.Label(new Rect(inner.x, inner.y + 26f, inner.width - buttonWidth - 12f, inner.height - 26f), MercenaryUiUtility.FormatQuoteStatus(mercenaryQuoteStatus));
        Text.Font = GameFont.Small;
        if (Widgets.ButtonText(new Rect(inner.xMax - buttonWidth, inner.yMax - 34f, buttonWidth, 30f), ClashOfRimText.Key("ClashOfRim.Mercenary.Hire"), active: canHire))
        {
            hireAction();
        }
    }

    private void InvalidateMercenaryQuote()
    {
        lastMercenaryQuoteRequestKey = string.Empty;
        mercenaryQuote = null;
    }

    private void DrawMercenaryGuardTierDropdown(Rect rect)
    {
        if (ClashOfRimUiUtility.SelectionButton(
                rect,
                MercenaryGuardTierLabel(selectedMercenaryGuardTier)))
        {
            Find.WindowStack.Add(new FloatMenu(new List<FloatMenuOption>
            {
                new(MercenaryGuardTierLabel("Apprentice"), () => SelectMercenaryGuardTier("Apprentice")),
                new(MercenaryGuardTierLabel("Skilled"), () => SelectMercenaryGuardTier("Skilled")),
                new(MercenaryGuardTierLabel("Master"), () => SelectMercenaryGuardTier("Master"))
            }));
        }
    }

    private void SelectMercenaryGuardTier(string tier)
    {
        string normalized = NormalizeMercenaryGuardTier(tier);
        if (string.Equals(selectedMercenaryGuardTier, normalized, StringComparison.Ordinal))
        {
            return;
        }

        selectedMercenaryGuardTier = normalized;
        InvalidateMercenaryGuardQuote();
    }

    private void EnsureMercenaryGuardQuote(ClashOfRimMod mod)
    {
        string requestKey = selectedMercenaryGuardTier + "|" + (mod.CurrentSnapshotId ?? string.Empty);
        if (string.Equals(lastMercenaryGuardQuoteRequestKey, requestKey, StringComparison.Ordinal)
            || string.Equals(pendingMercenaryGuardQuoteRequestKey, requestKey, StringComparison.Ordinal)
            || mercenaryGuardQuoteInProgress)
        {
            return;
        }

        pendingMercenaryGuardQuoteRequestKey = requestKey;
        mercenaryGuardQuoteInProgress = true;
        mercenaryGuardQuote = null;
        mercenaryGuardQuoteStatus = ClashOfRimText.Key("ClashOfRim.Mercenary.PriceLoading");
        mod.StartQuoteMercenaryGuardPrice(
            requestKey,
            selectedMercenaryGuardTier,
            OnMercenaryGuardQuoteReceived);
    }

    private void OnMercenaryGuardQuoteReceived(string requestKey, string status, ModMercenaryGuardQuoteResponseDto? quote)
    {
        if (!string.Equals(pendingMercenaryGuardQuoteRequestKey, requestKey, StringComparison.Ordinal))
        {
            return;
        }

        pendingMercenaryGuardQuoteRequestKey = string.Empty;
        lastMercenaryGuardQuoteRequestKey = requestKey;
        mercenaryGuardQuoteInProgress = false;
        mercenaryGuardQuote = quote;
        mercenaryGuardQuoteStatus = status;
    }

    private void DrawMercenaryGuardQuote(Rect rect, bool canHire, Action hireAction)
    {
        Widgets.DrawMenuSection(rect);
        Rect inner = rect.ContractedBy(8f);
        const float buttonWidth = 140f;
        Widgets.Label(new Rect(inner.x, inner.y, inner.width - buttonWidth - 12f, 24f), ClashOfRimText.Key("ClashOfRim.Mercenary.PriceTitle"));
        Text.Font = GameFont.Tiny;
        Widgets.Label(new Rect(inner.x, inner.y + 26f, inner.width - buttonWidth - 12f, inner.height - 26f), MercenaryUiUtility.FormatQuoteStatus(mercenaryGuardQuoteStatus));
        Text.Font = GameFont.Small;
        if (Widgets.ButtonText(new Rect(inner.xMax - buttonWidth, inner.yMax - 34f, buttonWidth, 30f), ClashOfRimText.Key("ClashOfRim.Mercenary.GuardHire"), active: canHire))
        {
            hireAction();
        }
    }

    private void InvalidateMercenaryGuardQuote()
    {
        lastMercenaryGuardQuoteRequestKey = string.Empty;
        mercenaryGuardQuote = null;
    }

    private static string NormalizeMercenaryGuardTier(string? tier)
    {
        return tier switch
        {
            "Skilled" => "Skilled",
            "Master" => "Master",
            _ => "Apprentice"
        };
    }

    private static string MercenaryGuardTierLabel(string tier)
    {
        return NormalizeMercenaryGuardTier(tier) switch
        {
            "Skilled" => ClashOfRimText.Key("ClashOfRim.Mercenary.GuardScaleStandard"),
            "Master" => ClashOfRimText.Key("ClashOfRim.Mercenary.GuardScaleLarge"),
            _ => ClashOfRimText.Key("ClashOfRim.Mercenary.GuardScaleSmall")
        };
    }

    private static MercenaryProfession? FindMercenaryProfession(
        IReadOnlyList<MercenaryProfession> professions,
        string? professionKey)
    {
        if (string.IsNullOrWhiteSpace(professionKey))
        {
            return null;
        }

        for (int index = 0; index < professions.Count; index++)
        {
            MercenaryProfession profession = professions[index];
            if (string.Equals(profession.Key, professionKey, StringComparison.Ordinal))
            {
                return profession;
            }
        }

        return null;
    }

    private static void DrawDiagnosticsTab(Rect rect, ClashOfRimMod mod)
    {
        Widgets.DrawMenuSection(rect);
        Rect inner = rect.ContractedBy(10f);
        Listing_Standard listing = new();
        listing.Begin(inner);
        listing.Label($"{mod.UserId}/{mod.ColonyId}");
        listing.GapLine();
        listing.Label(mod.FormatCurrentMapRuntimeSummary());
        listing.Label(mod.SnapshotUploadStatus);
        listing.Label(mod.LoginStatus);
        listing.Label(mod.WorldMapStatus);
        listing.Label(mod.PresenceStatus);
        listing.GapLine();
        listing.Label(mod.EventQueueStatus);
        listing.Label(mod.EventDetailsStatus);
        listing.End();
    }

    private void DrawAccountTab(Rect rect, ClashOfRimMod mod)
    {
        Widgets.DrawMenuSection(rect);
        Rect inner = rect.ContractedBy(10f);
        Text.Font = GameFont.Medium;
        Widgets.Label(new Rect(inner.x, inner.y, inner.width, 30f), ClashOfRimText.Key("ClashOfRim.Multiplayer.TabAccount"));
        Text.Font = GameFont.Small;

        float y = inner.y + 44f;
        Widgets.Label(new Rect(inner.x, y, inner.width, 24f), ClashOfRimText.Key(
            "ClashOfRim.Account.CurrentUser",
            mod.CurrentUserId.Named("USER")));
        y += 38f;

        Widgets.Label(new Rect(inner.x, y, 130f, 24f), ClashOfRimText.Key("ClashOfRim.Account.CurrentPassword"));
        accountCurrentPassword = GUI.PasswordField(new Rect(inner.x + 140f, y - 2f, 260f, 28f), accountCurrentPassword ?? string.Empty, '*');
        y += 36f;

        Widgets.Label(new Rect(inner.x, y, 130f, 24f), ClashOfRimText.Key("ClashOfRim.Account.NewPassword"));
        accountNewPassword = GUI.PasswordField(new Rect(inner.x + 140f, y - 2f, 260f, 28f), accountNewPassword ?? string.Empty, '*');
        y += 42f;

        if (Widgets.ButtonText(new Rect(inner.x + 140f, y, 160f, 30f), ClashOfRimText.Key("ClashOfRim.Account.ChangePassword"), active: !mod.AccountPasswordInProgress))
        {
            mod.StartChangeOfflinePassword(accountCurrentPassword ?? string.Empty, accountNewPassword ?? string.Empty);
            accountCurrentPassword = string.Empty;
            accountNewPassword = string.Empty;
        }

        Text.Font = GameFont.Tiny;
        Widgets.Label(new Rect(inner.x, y + 44f, inner.width, 40f), mod.AccountPasswordStatus);
        Text.Font = GameFont.Small;
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

    private (int Min, int Max) GetMercenaryDurationRange()
    {
        ModBankStatusResponseDto? status = mercenaryQuote?.BankStatus ?? bankPanel!.Read();
        var range = status is null ? new PanelIntegerRange(1, 180)
            : PanelIntegerRange.Normalize(status.MercenaryMinDurationDays, status.MercenaryMaxDurationDays, 1, 180);
        return (range.Min, range.Max);
    }

    private bool HasMercenaryDurationRange()
    {
        ModBankStatusResponseDto? status = mercenaryQuote?.BankStatus ?? bankPanel!.Read();
        return status is not null && status.MercenaryMaxDurationDays > 0;
    }

    private static float ClampFloat(float value, int min, int max)
    {
        return value < min ? min : value > max ? max : value;
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
