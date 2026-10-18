using AIRsLight.ClashOfRim.Modules.Multiplayer;
using AIRsLight.ClashOfRim.Multiplayer.Pages;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AIRsLight.ClashOfRim.Bank;
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

    private const float PanelRefreshIntervalSeconds = PanelRefreshSchedule.IntervalSeconds;

    private Vector2 bankScrollPosition;
    private Vector2 mercenaryScrollPosition;
    private Vector2 adminScrollPosition;
    private Vector2 adminManifestScrollPosition;
    private MultiplayerTab selectedTab = MultiplayerTab.Trade;
    private AdminPanelTab selectedAdminPanelTab = AdminPanelTab.Trade;
    private string bankPrincipalText = "1000";
    private string bankDurationText = "30";
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
    private float nextBankRefreshAt;
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
                DrawBankTab(contentRect, mod);
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
                nextBankRefreshAt = 0f;
                break;
            case MultiplayerTab.Mercenary:
                nextBankRefreshAt = 0f;
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

    private void DrawBankTab(Rect rect, ClashOfRimMod mod)
    {
        Widgets.DrawMenuSection(rect);
        Rect inner = rect.ContractedBy(10f);
        Text.Font = GameFont.Medium;
        Widgets.Label(new Rect(inner.x, inner.y, inner.width - 132f, 30f), ClashOfRimText.Key("ClashOfRim.Multiplayer.TabBank"));
        Text.Font = GameFont.Small;
        if (Widgets.ButtonText(new Rect(inner.xMax - 120f, inner.y, 120f, 30f), ClashOfRimText.Key("ClashOfRim.Refresh")))
        {
            mod.StartRefreshBankStatus();
            nextBankRefreshAt = Time.realtimeSinceStartup + PanelRefreshIntervalSeconds;
        }

        ModBankStatusResponseDto? status = mod.BankStatusSnapshot;
        Rect outRect = new(inner.x, inner.y + 40f, inner.width, inner.height - 40f);
        float contentHeight = status is null ? 360f : 560f + status.OpenDebts.Count * 90f;
        Rect viewRect = new(0f, 0f, outRect.width - 16f, Math.Max(outRect.height, contentHeight));
        Widgets.BeginScrollView(outRect, ref bankScrollPosition, viewRect);
        float y = 0f;
        Text.Font = GameFont.Tiny;
        Widgets.Label(new Rect(0f, y, viewRect.width, 22f), mod.BankStatus);
        Text.Font = GameFont.Small;
        y += 32f;

        if (status is null)
        {
            Widgets.Label(new Rect(0f, y, viewRect.width, 30f), ClashOfRimText.Key("ClashOfRim.Bank.NoStatus"));
            Widgets.EndScrollView();
            return;
        }

        Widgets.Label(new Rect(0f, y, viewRect.width, 24f), ClashOfRimText.Key(
            "ClashOfRim.Bank.WealthLine",
            status.ColonyWealth.Named("WEALTH"),
            status.MinLoanSilver.Named("MIN"),
            status.MaxLoanSilver.Named("MAX")));
        y += 26f;
        Widgets.Label(new Rect(0f, y, viewRect.width, 24f), ClashOfRimText.Key(
            "ClashOfRim.Bank.ConfigLine",
            (status.BaseAnnualInterestRate * 100f).ToString("0.#").Named("RATE"),
            status.MinDurationDays.Named("MIN"),
            status.MaxDurationDays.Named("MAX")));
        y += 26f;
        if (status.TotalOpenDebtSilver > 0)
        {
            Widgets.Label(new Rect(0f, y, viewRect.width, 24f), ClashOfRimText.Key(
                "ClashOfRim.Bank.DebtLine",
                status.TotalOpenDebtSilver.Named("TOTAL"),
                status.OpenDebts.Count.Named("COUNT")));
            y += 30f;
        }
        else
        {
            y += 10f;
        }

        if (status.ActiveLoan is not null)
        {
            ModBankLoanSummaryDto loan = status.ActiveLoan;
            int loanTotalDue = Math.Max(
                loan.TotalDueSilver,
                ClashBankLoanQuestUtility.FindLoanTotalDueSilver(loan.LoanId) ?? loan.TotalDueSilver);
            Widgets.DrawMenuSection(new Rect(0f, y, viewRect.width, 122f));
            Rect box = new(8f, y + 8f, viewRect.width - 16f, 106f);
            Widgets.Label(new Rect(box.x, box.y, box.width, 24f), ClashOfRimText.Key(
                "ClashOfRim.Bank.ActiveLoanTitleWithSource",
                BankUiUtility.FormatDebtSource(loan.SourceKind).Named("SOURCE")));
            Widgets.Label(new Rect(box.x, box.y + 26f, box.width, 24f), ClashOfRimText.Key("ClashOfRim.Bank.ActiveLoanLine", loan.PrincipalSilver.Named("PRINCIPAL"), loan.InterestSilver.Named("INTEREST"), loanTotalDue.Named("TOTAL")));
            Widgets.Label(new Rect(box.x, box.y + 52f, box.width, 24f), ClashOfRimText.Key("ClashOfRim.Bank.ActiveLoanStatus", BankUiUtility.FormatLoanStatus(loan.Status).Named("STATUS")));
            Text.Font = GameFont.Tiny;
            Widgets.Label(new Rect(box.x, box.y + 78f, box.width - 130f, 20f), ClashOfRimText.Key("ClashOfRim.Bank.LoanDueLine", BankUiUtility.FormatLoanDue(loan).Named("DUE")));
            Text.Font = GameFont.Small;
            bool canRepayLoan = string.Equals(loan.Status, "Active", StringComparison.Ordinal)
                && !mod.BankInProgress
                && !mod.ManualSyncInProgress;
            if (Widgets.ButtonText(new Rect(box.xMax - 120f, box.yMax - 32f, 120f, 30f), ClashOfRimText.Key("ClashOfRim.Bank.Repay"), active: canRepayLoan))
            {
                Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                    ClashOfRimText.Key("ClashOfRim.Bank.ConfirmRepay", loanTotalDue.Named("TOTAL")),
                    mod.StartRepayBankLoan));
            }

            y += 132f;
        }

        if (status.OpenDebts.Count > 0)
        {
            Widgets.Label(new Rect(0f, y, viewRect.width, 24f), ClashOfRimText.Key("ClashOfRim.Bank.DebtListTitle"));
            y += 28f;
            foreach (ModBankDebtSummaryDto debt in status.OpenDebts)
            {
                Widgets.DrawMenuSection(new Rect(0f, y, viewRect.width, 82f));
                Rect box = new(8f, y + 8f, viewRect.width - 16f, 66f);
                Widgets.Label(new Rect(box.x, box.y, box.width - 130f, 24f), ClashOfRimText.Key(
                    "ClashOfRim.Bank.DebtItemLine",
                    debt.AmountSilver.Named("TOTAL"),
                    BankUiUtility.FormatDebtSource(debt.SourceKind).Named("SOURCE"),
                    BankUiUtility.FormatDebtStatus(debt.Status).Named("STATUS")));
                Text.Font = GameFont.Tiny;
                Widgets.Label(new Rect(box.x, box.y + 28f, box.width - 130f, 24f), ClashOfRimText.Key(
                    "ClashOfRim.Bank.DebtDetailLine",
                    BankUiUtility.FormatDebtReason(debt.Reason).Named("REASON"),
                    BankUiUtility.FormatDebtDue(debt).Named("DUE")));
                Text.Font = GameFont.Small;
                bool canRepayDebt = string.Equals(debt.Status, "Active", StringComparison.Ordinal)
                    && !mod.BankInProgress
                    && !mod.ManualSyncInProgress;
                if (Widgets.ButtonText(new Rect(box.xMax - 120f, box.yMax - 32f, 120f, 30f), ClashOfRimText.Key("ClashOfRim.Bank.Repay"), active: canRepayDebt))
                {
                    Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                        ClashOfRimText.Key("ClashOfRim.Bank.ConfirmRepayDebt", debt.AmountSilver.Named("TOTAL"), BankUiUtility.FormatDebtReason(debt.Reason).Named("REASON")),
                        () => mod.StartRepayBankDebt(debt)));
                }

                y += 92f;
            }
        }

        if (status.ActiveLoan is not null)
        {
            Widgets.EndScrollView();
            return;
        }

        Widgets.DrawMenuSection(new Rect(0f, y, viewRect.width, 132f));
        Rect form = new(8f, y + 8f, viewRect.width - 16f, 116f);
        (int minPrincipal, int maxPrincipal) = NormalizeRange(status.MinLoanSilver, status.MaxLoanSilver, 1, 1);
        (int minDuration, int maxDuration) = NormalizeRange(status.MinDurationDays, status.MaxDurationDays, 1, 1);
        int principalValue = ClampInt(ParseIntOrDefault(bankPrincipalText, minPrincipal), minPrincipal, maxPrincipal);
        int durationValue = ClampInt(ParseIntOrDefault(bankDurationText, minDuration), minDuration, maxDuration);
        Widgets.Label(new Rect(form.x, form.y, 140f, 24f), ClashOfRimText.Key("ClashOfRim.Bank.Principal"));
        bankPrincipalText = Widgets.TextField(new Rect(form.x + 150f, form.y, 92f, 24f), bankPrincipalText);
        int sliderPrincipal = maxPrincipal > minPrincipal
            ? Mathf.RoundToInt(Widgets.HorizontalSlider(
                new Rect(form.x + 252f, form.y + 2f, Math.Max(80f, form.width - 380f), 24f),
                principalValue,
                minPrincipal,
                maxPrincipal,
                roundTo: 1f))
            : minPrincipal;
        if (sliderPrincipal != principalValue)
        {
            bankPrincipalText = sliderPrincipal.ToString();
            principalValue = sliderPrincipal;
        }

        Widgets.Label(new Rect(form.x, form.y + 34f, 140f, 24f), ClashOfRimText.Key("ClashOfRim.Bank.Duration"));
        bankDurationText = Widgets.TextField(new Rect(form.x + 150f, form.y + 34f, 92f, 24f), bankDurationText);
        int sliderDuration = maxDuration > minDuration
            ? Mathf.RoundToInt(Widgets.HorizontalSlider(
                new Rect(form.x + 252f, form.y + 36f, Math.Max(80f, form.width - 380f), 24f),
                durationValue,
                minDuration,
                maxDuration,
                roundTo: 1f))
            : minDuration;
        if (sliderDuration != durationValue)
        {
            bankDurationText = sliderDuration.ToString();
            durationValue = sliderDuration;
        }

        bool validPrincipal = int.TryParse(bankPrincipalText, out int principal)
            && principal >= status.MinLoanSilver
            && principal <= status.MaxLoanSilver;
        bool validDuration = int.TryParse(bankDurationText, out int duration) && duration >= status.MinDurationDays && duration <= status.MaxDurationDays;
        bool canCreate = status.BankLoansEnabled && validPrincipal && validDuration && !mod.BankInProgress && !mod.ManualSyncInProgress;
        if (validPrincipal && validDuration)
        {
            int interest = BankUiUtility.CalculateInterestSilver(principal, duration, status);
            int totalDue = principal + interest;
            Text.Font = GameFont.Tiny;
            Widgets.Label(new Rect(form.x, form.y + 68f, form.width - 132f, 22f), ClashOfRimText.Key(
                "ClashOfRim.Bank.LoanPreview",
                principal.Named("PRINCIPAL"),
                interest.Named("INTEREST"),
                totalDue.Named("TOTAL")));
            Text.Font = GameFont.Small;
        }
        else if (!canCreate)
        {
            Text.Font = GameFont.Tiny;
            Widgets.Label(new Rect(form.x, form.y + 68f, form.width - 132f, 22f), status.BankLoansEnabled
                ? ClashOfRimText.Key("ClashOfRim.Bank.CreateHint")
                : ClashOfRimText.Key("ClashOfRim.Bank.Disabled"));
            Text.Font = GameFont.Small;
        }

        if (Widgets.ButtonText(new Rect(form.xMax - 120f, form.yMax - 32f, 120f, 30f), ClashOfRimText.Key("ClashOfRim.Bank.CreateLoan"), active: canCreate))
        {
            Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                ClashOfRimText.Key("ClashOfRim.Bank.ConfirmCreate", principal.Named("PRINCIPAL"), duration.Named("DAYS")),
                () => mod.StartCreateBankLoan(principal, duration)));
        }

        Widgets.EndScrollView();
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
        bool hasMercenaryConfig = HasMercenaryDurationRange(mod);
        (int minMercenaryDays, int maxMercenaryDays) = GetMercenaryDurationRange(mod);
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
                TryRefreshBank(mod);
                break;
            case MultiplayerTab.Mercenary:
                TryRefreshBank(mod);
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

    private void TryRefreshBank(ClashOfRimMod mod)
    {
        if (mod.ManualSyncInProgress || mod.BankInProgress || Time.realtimeSinceStartup < nextBankRefreshAt)
        {
            return;
        }

        nextBankRefreshAt = Time.realtimeSinceStartup + PanelRefreshIntervalSeconds;
        mod.StartRefreshBankStatus();
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

    private (int Min, int Max) GetMercenaryDurationRange(ClashOfRimMod mod)
    {
        ModBankStatusResponseDto? status = mercenaryQuote?.BankStatus ?? mod.BankStatusSnapshot;
        return status is null
            ? (1, 180)
            : NormalizeRange(status.MercenaryMinDurationDays, status.MercenaryMaxDurationDays, 1, 180);
    }

    private bool HasMercenaryDurationRange(ClashOfRimMod mod)
    {
        ModBankStatusResponseDto? status = mercenaryQuote?.BankStatus ?? mod.BankStatusSnapshot;
        return status is not null && status.MercenaryMaxDurationDays > 0;
    }

    private static (int Min, int Max) NormalizeRange(int min, int max, int fallbackMin, int fallbackMax)
    {
        int normalizedMin = min > 0 ? min : Math.Max(1, fallbackMin);
        int normalizedMax = max >= normalizedMin ? max : Math.Max(normalizedMin, fallbackMax);
        return (normalizedMin, normalizedMax);
    }

    private static int ParseIntOrDefault(string text, int fallback)
    {
        return int.TryParse(text, out int value) ? value : fallback;
    }

    private static int ClampInt(int value, int min, int max)
    {
        return value < min ? min : value > max ? max : value;
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
