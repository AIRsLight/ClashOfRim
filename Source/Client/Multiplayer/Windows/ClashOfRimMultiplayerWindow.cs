using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AIRsLight.ClashOfRim.Achievements;
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
    private const float PanelRefreshIntervalSeconds = 30f;
    private const float PlayerLocationCacheSeconds = 2f;
    private const float DiplomacyRowHeight = 70f;
    private const float TradeOrderRowHeight = 96f;

    private Vector2 diplomacyScrollPosition;
    private Vector2 diplomacyScoreboardScrollPosition;
    private Vector2 achievementsScrollPosition;
    private Vector2 tradeOrderScrollPosition;
    private Vector2 tradeDetailScrollPosition;
    private Vector2 serverShopScrollPosition;
    private Vector2 bankScrollPosition;
    private Vector2 mercenaryScrollPosition;
    private Vector2 adminScrollPosition;
    private Vector2 adminManifestScrollPosition;
    private MultiplayerTab selectedTab = MultiplayerTab.Trade;
    private AdminPanelTab selectedAdminPanelTab = AdminPanelTab.Trade;
    private string selectedTradeScope = "Open";
    private string selectedShopListingKind = "SellToPlayer";
    private string selectedAchievementUserId = string.Empty;
    private string selectedAchievementColonyId = string.Empty;
    private string selectedAchievementOwnerUserId = string.Empty;
    private string selectedAchievementOwnerColonyId = string.Empty;
    private string? selectedTradeEventId;
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
    private float nextPlayerRefreshAt;
    private float nextAchievementRefreshAt;
    private float nextTradeRefreshAt;
    private float nextShopRefreshAt;
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
    private int cachedPlayersVersion = -1;
    private string cachedPlayersUserId = string.Empty;
    private string cachedPlayersColonyId = string.Empty;
    private IReadOnlyList<ModPlayerSummaryDto> cachedSortedPlayers = new List<ModPlayerSummaryDto>();
    private IReadOnlyList<ModPlayerSummaryDto> cachedWealthRankedPlayers = new List<ModPlayerSummaryDto>();
    private int cachedOwnAchievementsVersion = -1;
    private string cachedOwnAchievementsTargetUserId = string.Empty;
    private string cachedOwnAchievementsTargetColonyId = string.Empty;
    private IReadOnlyList<ModAchievementSummaryDto> cachedOwnAchievements = new List<ModAchievementSummaryDto>();
    private readonly Dictionary<string, PlayerLocationCacheEntry> playerLocationCache = new(StringComparer.Ordinal);
    private int playerLocationCachePlayersVersion = -1;
    private int cachedServerShopVersion = -1;
    private int cachedServerShopSellToPlayerCount;
    private int cachedServerShopBuyFromPlayerCount;
    private IReadOnlyList<ModServerShopListingDto> cachedServerShopSellToPlayerListings = new List<ModServerShopListingDto>();
    private IReadOnlyList<ModServerShopListingDto> cachedServerShopBuyFromPlayerListings = new List<ModServerShopListingDto>();
    private int cachedTradeOrdersVersion = -1;
    private IReadOnlyList<ModTradeOrderSummaryDto> cachedTradeOrders = new List<ModTradeOrderSummaryDto>();
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
                DrawDiplomacyTab(contentRect, mod);
                break;
            case MultiplayerTab.Achievements:
                DrawAchievementsTab(contentRect, mod);
                break;
            case MultiplayerTab.Bank:
                DrawBankTab(contentRect, mod);
                break;
            case MultiplayerTab.Shop:
                DrawServerShopTab(contentRect, mod);
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
                DrawTradeTab(contentRect, mod);
                break;
        }
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
                nextPlayerRefreshAt = 0f;
                break;
            case MultiplayerTab.Achievements:
                nextPlayerRefreshAt = 0f;
                nextAchievementRefreshAt = 0f;
                break;
            case MultiplayerTab.Bank:
                nextBankRefreshAt = 0f;
                break;
            case MultiplayerTab.Mercenary:
                nextBankRefreshAt = 0f;
                break;
            case MultiplayerTab.Trade:
                nextTradeRefreshAt = 0f;
                break;
            case MultiplayerTab.Shop:
                nextShopRefreshAt = 0f;
                break;
            case MultiplayerTab.Admin:
                nextAdminRefreshAt = 0f;
                break;
        }
    }

    private void DrawServerShopTab(Rect rect, ClashOfRimMod mod)
    {
        Widgets.DrawMenuSection(rect);
        Rect inner = rect.ContractedBy(10f);
        Text.Font = GameFont.Medium;
        Widgets.Label(new Rect(inner.x, inner.y, inner.width - 132f, 30f), ClashOfRimText.Key("ClashOfRim.Multiplayer.TabShop"));
        Text.Font = GameFont.Small;
        if (Widgets.ButtonText(new Rect(inner.xMax - 120f, inner.y, 120f, 30f), ClashOfRimText.Key("ClashOfRim.Refresh"), active: !mod.ServerShopInProgress))
        {
            mod.StartRefreshServerShop();
            nextShopRefreshAt = Time.realtimeSinceStartup + PanelRefreshIntervalSeconds;
        }

        Text.Font = GameFont.Tiny;
        Widgets.Label(new Rect(inner.x, inner.y + 32f, inner.width, 20f), mod.ServerShopStatus);
        Text.Font = GameFont.Small;

        RefreshServerShopListingCache(mod);
        DrawServerShopModeTabs(new Rect(inner.x, inner.y + 56f, 300f, 30f));

        IReadOnlyList<ModServerShopListingDto> visibleListings = string.Equals(
            selectedShopListingKind,
            "BuyFromPlayer",
            StringComparison.Ordinal)
            ? cachedServerShopBuyFromPlayerListings
            : cachedServerShopSellToPlayerListings;

        Rect outRect = new(inner.x, inner.y + 96f, inner.width, inner.height - 96f);
        const float tileWidth = 156f;
        const float tileHeight = 180f;
        const float gap = 10f;
        int columns = Math.Max(1, Mathf.FloorToInt((outRect.width - 16f + gap) / (tileWidth + gap)));
        int rows = Math.Max(1, Mathf.CeilToInt(visibleListings.Count / (float)columns));
        Rect viewRect = new(0f, 0f, outRect.width - 16f, Math.Max(outRect.height, rows * (tileHeight + gap)));
        Widgets.BeginScrollView(outRect, ref serverShopScrollPosition, viewRect);
        if (visibleListings.Count == 0)
        {
            Widgets.Label(new Rect(0f, 0f, viewRect.width, 40f), ClashOfRimText.Key("ClashOfRim.Shop.NoListings"));
        }
        else
        {
            for (int index = 0; index < visibleListings.Count; index++)
            {
                int column = index % columns;
                int row = index / columns;
                Rect tile = new(
                    column * (tileWidth + gap),
                    row * (tileHeight + gap),
                    tileWidth,
                    tileHeight);
                DrawServerShopTile(tile, visibleListings[index], mod);
            }
        }

        Widgets.EndScrollView();
    }

    private void DrawServerShopModeTabs(Rect rect)
    {
        float halfWidth = (rect.width - 8f) * 0.5f;
        DrawServerShopModeButton(
            new Rect(rect.x, rect.y, halfWidth, rect.height),
            "SellToPlayer",
            ClashOfRimText.Key("ClashOfRim.Shop.Buy") + " (" + cachedServerShopSellToPlayerCount + ")");
        DrawServerShopModeButton(
            new Rect(rect.x + halfWidth + 8f, rect.y, halfWidth, rect.height),
            "BuyFromPlayer",
            ClashOfRimText.Key("ClashOfRim.Shop.Sell") + " (" + cachedServerShopBuyFromPlayerCount + ")");
    }

    private void RefreshServerShopListingCache(ClashOfRimMod mod)
    {
        int version = mod.ServerShopListingsSnapshotVersion;
        if (version == cachedServerShopVersion)
        {
            return;
        }

        List<ModServerShopListingDto> listings = mod.ServerShopListingsSnapshot;
        cachedServerShopSellToPlayerListings = listings
            .Where(listing => !IsServerShopBuyOrder(listing))
            .ToList();
        cachedServerShopBuyFromPlayerListings = listings
            .Where(IsServerShopBuyOrder)
            .ToList();
        cachedServerShopSellToPlayerCount = cachedServerShopSellToPlayerListings.Count;
        cachedServerShopBuyFromPlayerCount = cachedServerShopBuyFromPlayerListings.Count;
        cachedServerShopVersion = version;
    }

    private void DrawServerShopModeButton(Rect rect, string listingKind, string label)
    {
        bool selected = string.Equals(selectedShopListingKind, listingKind, StringComparison.Ordinal);
        if (selected)
        {
            Widgets.DrawHighlightSelected(rect);
        }

        if (Widgets.ButtonText(rect, label, active: !selected))
        {
            selectedShopListingKind = listingKind;
            serverShopScrollPosition = Vector2.zero;
        }
    }

    private void DrawServerShopTile(Rect rect, ModServerShopListingDto listing, ClashOfRimMod mod)
    {
        Widgets.DrawMenuSection(rect);
        Rect inner = rect.ContractedBy(8f);
        ModThingReferenceDto? item = listing.Item;
        bool buyFromPlayer = IsServerShopBuyOrder(listing);
        Rect iconRect = new(inner.x, inner.y, 42f, 42f);
        if (item is not null)
        {
            TradeUiUtility.DrawThingIconWithInfo(iconRect, item);
            TooltipHandler.TipRegion(iconRect, TradeUiUtility.ThingLabel(
                item,
                asRequirement: buyFromPlayer,
                qualityRequirementMode: buyFromPlayer ? listing.QualityRequirementMode : null,
                hitPointsRequirementMode: buyFromPlayer ? listing.HitPointsRequirementMode : null));
        }

        Text.Font = GameFont.Tiny;
        Widgets.Label(new Rect(iconRect.xMax + 8f, inner.y, inner.width - 50f, 20f), ClashOfRimText.Key(
            buyFromPlayer ? "ClashOfRim.Shop.Demand" : "ClashOfRim.Shop.Stock",
            FormatServerShopStock(listing.StockCount).Named("COUNT")));
        Widgets.Label(new Rect(iconRect.xMax + 8f, inner.y + 20f, inner.width - 50f, 20f), ClashOfRimText.Key(
            buyFromPlayer ? "ClashOfRim.Shop.Payout" : "ClashOfRim.Shop.Price",
            FormatServerShopSilver(listing.PriceSilver).Named("PRICE")));
        Text.Font = GameFont.Small;

        string label = item is null
            ? ClashOfRimText.Key("ClashOfRim.UnknownThing")
            : TradeUiUtility.ThingLabel(
                item,
                asRequirement: buyFromPlayer,
                qualityRequirementMode: buyFromPlayer ? listing.QualityRequirementMode : null,
                hitPointsRequirementMode: buyFromPlayer ? listing.HitPointsRequirementMode : null);
        TradeUiUtility.DrawTruncatedLabel(new Rect(inner.x, inner.y + 50f, inner.width, 24f), label);

        const int purchaseCount = 1;
        bool validCount = listing.StockCount >= purchaseCount;
        int totalPrice = ClashOfRimMod.CalculateServerShopTotalPrice(listing, purchaseCount);
        Text.Font = GameFont.Tiny;
        Widgets.Label(new Rect(inner.x, inner.y + 88f, inner.width, 20f), validCount
            ? ClashOfRimText.Key(buyFromPlayer ? "ClashOfRim.Shop.TotalPayout" : "ClashOfRim.Shop.TotalPrice", FormatServerShopSilver(totalPrice).Named("PRICE"))
            : ClashOfRimText.Key(buyFromPlayer ? "ClashOfRim.Shop.NoDemand" : "ClashOfRim.Shop.OutOfStock"));
        Text.Font = GameFont.Small;

        bool canBuy = item is not null
            && validCount
            && !mod.ServerShopInProgress
            && !mod.ManualSyncInProgress;
        string actionLabel = ClashOfRimText.Key(buyFromPlayer ? "ClashOfRim.Shop.SellBatch" : "ClashOfRim.Shop.BuyOne");
        if (Widgets.ButtonText(new Rect(inner.x, inner.yMax - 30f, inner.width, 30f), actionLabel, active: canBuy))
        {
            Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                ClashOfRimText.Key(
                    buyFromPlayer ? "ClashOfRim.Shop.ConfirmSell" : "ClashOfRim.Shop.ConfirmBuy",
                    purchaseCount.Named("COUNT"),
                    label.Named("THING"),
                    totalPrice.Named("PRICE")),
                () => mod.StartPurchaseServerShopListing(listing, purchaseCount)));
        }
    }

    private static bool IsServerShopBuyOrder(ModServerShopListingDto listing)
    {
        return string.Equals(NormalizeServerShopListingKind(listing.ListingKind), "BuyFromPlayer", StringComparison.Ordinal);
    }

    private static string NormalizeServerShopListingKind(string? listingKind)
    {
        return string.Equals(listingKind, "BuyFromPlayer", StringComparison.Ordinal)
            ? "BuyFromPlayer"
            : "SellToPlayer";
    }

    private static string FormatServerShopStock(int stockCount)
    {
        return ColoredText(stockCount.ToString(), stockCount <= 0 ? "#ff6b6b" : "#d7dadb");
    }

    private static string FormatServerShopSilver(int silver)
    {
        return ColoredText(Math.Max(0, silver).ToString(), silver <= 0 ? "#ff6b6b" : "#f1d07a");
    }

    private static string ColoredText(string text, string color)
    {
        return "<color=" + color + ">" + text + "</color>";
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
                () => mod.StartHireMercenary(selectedMercenarySkillDefName, selectedMercenarySkillLevel, days)));
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
                () => mod.StartHireMercenaryGuard(selectedMercenaryGuardTier)));
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

    private void DrawTradeTab(Rect rect, ClashOfRimMod mod)
    {
        Widgets.DrawMenuSection(rect);
        Rect inner = rect.ContractedBy(10f);
        Text.Font = GameFont.Medium;
        Widgets.Label(new Rect(inner.x, inner.y, inner.width - 150f, 30f), ClashOfRimText.Key("ClashOfRim.Trade.MarketTitle"));
        Text.Font = GameFont.Small;
        if (Widgets.ButtonText(new Rect(inner.xMax - 140f, inner.y, 140f, 30f), ClashOfRimText.Key("ClashOfRim.Trade.CreateOrderTitle"), active: mod.TradeMarketplaceEnabled))
        {
            mod.OpenTradeOrderDialog();
        }

        DrawTradeScopeTabs(new Rect(inner.x, inner.y + 38f, inner.width, 30f), mod);

        Rect listRect = new(inner.x, inner.y + 78f, Mathf.Min(430f, inner.width * 0.46f), inner.height - 78f);
        Rect detailRect = new(listRect.xMax + 10f, listRect.y, inner.xMax - listRect.xMax - 10f, listRect.height);
        IReadOnlyList<ModTradeOrderSummaryDto> orders = TradeOrders(mod);
        DrawTradeOrderList(listRect, orders, mod);
        ModTradeOrderSummaryDto? selected = FindSelectedTradeOrder(orders, selectedTradeEventId)
            ?? (orders.Count > 0 ? orders[0] : null);
        DrawTradeOrderDetail(detailRect, selected, mod);
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

    private static ModTradeOrderSummaryDto? FindSelectedTradeOrder(
        IReadOnlyList<ModTradeOrderSummaryDto> orders,
        string? eventId)
    {
        if (string.IsNullOrWhiteSpace(eventId))
        {
            return null;
        }

        for (int index = 0; index < orders.Count; index++)
        {
            ModTradeOrderSummaryDto order = orders[index];
            if (string.Equals(order.EventId, eventId, StringComparison.Ordinal))
            {
                return order;
            }
        }

        return null;
    }

    private IReadOnlyList<ModTradeOrderSummaryDto> TradeOrders(ClashOfRimMod mod)
    {
        int version = mod.TradeOrdersSnapshotVersion;
        if (version != cachedTradeOrdersVersion)
        {
            cachedTradeOrders = mod.TradeOrdersSnapshot;
            cachedTradeOrdersVersion = version;
        }

        return cachedTradeOrders;
    }

    private void DrawTradeScopeTabs(Rect rect, ClashOfRimMod mod)
    {
        const float width = 86f;
        DrawTradeScopeButton(new Rect(rect.x, rect.y, width, rect.height), "Open", ClashOfRimText.Key("ClashOfRim.Trade.ScopeOpen"), mod);
        DrawTradeScopeButton(new Rect(rect.x + 92f, rect.y, width, rect.height), "AcceptedByMe", ClashOfRimText.Key("ClashOfRim.Trade.ScopeAcceptedByMe"), mod);
        DrawTradeScopeButton(new Rect(rect.x + 184f, rect.y, width, rect.height), "Mine", ClashOfRimText.Key("ClashOfRim.Trade.ScopeMine"), mod);
        DrawTradeScopeButton(new Rect(rect.x + 276f, rect.y, width, rect.height), "History", ClashOfRimText.Key("ClashOfRim.Trade.ScopeHistory"), mod);
    }

    private void DrawTradeScopeButton(Rect rect, string scope, string label, ClashOfRimMod mod)
    {
        if (string.Equals(selectedTradeScope, scope, StringComparison.Ordinal))
        {
            Widgets.DrawHighlightSelected(rect);
        }

        if (Widgets.ButtonText(rect, label))
        {
            selectedTradeScope = scope;
            selectedTradeEventId = null;
            mod.StartRefreshTradeOrders(selectedTradeScope);
            nextTradeRefreshAt = Time.realtimeSinceStartup + PanelRefreshIntervalSeconds;
        }
    }

    private void DrawTradeOrderList(Rect rect, IReadOnlyList<ModTradeOrderSummaryDto> orders, ClashOfRimMod mod)
    {
        Widgets.DrawMenuSection(rect);
        Rect inner = rect.ContractedBy(8f);
        Widgets.Label(new Rect(inner.x, inner.y, inner.width, 24f), TradeScopeTitle());
        Text.Font = GameFont.Tiny;
        Widgets.Label(new Rect(inner.x, inner.y + 24f, inner.width, 20f), mod.TradeStatus);
        Text.Font = GameFont.Small;

        Rect outRect = new(inner.x, inner.y + 50f, inner.width, inner.height - 50f);
        float contentHeight = orders.Count * TradeOrderRowHeight + (mod.TradeOrdersHasMore || mod.TradeOrdersPageLoadInProgress ? 40f : 0f);
        Rect viewRect = new(0f, 0f, outRect.width - 16f, Math.Max(outRect.height, contentHeight));
        Widgets.BeginScrollView(outRect, ref tradeOrderScrollPosition, viewRect);
        if (orders.Count == 0)
        {
            Widgets.Label(new Rect(0f, 0f, viewRect.width, 40f), ClashOfRimText.Key("ClashOfRim.Trade.NoOrdersInScope"));
        }
        else
        {
            for (int index = 0; index < orders.Count; index++)
            {
                DrawTradeOrderSummaryRow(new Rect(0f, index * TradeOrderRowHeight, viewRect.width, TradeOrderRowHeight - 4f), orders[index]);
            }
        }

        DrawTradePagingControls(new Rect(0f, orders.Count * TradeOrderRowHeight + 4f, viewRect.width, 32f), mod);
        Widgets.EndScrollView();

        if (ShouldAutoLoadMoreTradeOrders(outRect, viewRect, tradeOrderScrollPosition, mod))
        {
            mod.StartLoadMoreTradeOrders(selectedTradeScope);
        }
    }

    private static bool ShouldAutoLoadMoreTradeOrders(Rect outRect, Rect viewRect, Vector2 scrollPosition, ClashOfRimMod mod)
    {
        return mod.TradeOrdersHasMore
            && !mod.TradeOrdersPageLoadInProgress
            && viewRect.height > outRect.height
            && scrollPosition.y >= viewRect.height - outRect.height - 80f;
    }

    private void DrawTradePagingControls(Rect rect, ClashOfRimMod mod)
    {
        if (mod.TradeOrdersPageLoadInProgress)
        {
            Text.Anchor = TextAnchor.MiddleCenter;
            Widgets.Label(rect, ClashOfRimText.Key("ClashOfRim.Trade.LoadingMore"));
            Text.Anchor = TextAnchor.UpperLeft;
            return;
        }

        if (!mod.TradeOrdersHasMore)
        {
            return;
        }

        if (Widgets.ButtonText(rect, ClashOfRimText.Key("ClashOfRim.Trade.LoadMore")))
        {
            mod.StartLoadMoreTradeOrders(selectedTradeScope);
        }
    }

    private void DrawTradeOrderSummaryRow(Rect row, ModTradeOrderSummaryDto order)
    {
        bool selected = string.Equals(order.EventId, selectedTradeEventId, StringComparison.Ordinal);
        if (selected)
        {
            Widgets.DrawHighlightSelected(row);
        }
        else
        {
            Widgets.DrawHighlightIfMouseover(row);
        }

        bool openedInfo = DrawThingStrip(new Rect(row.x + 6f, row.y + 5f, 112f, 30f), order.OfferedThings, asRequirement: false);
        Text.Anchor = TextAnchor.MiddleCenter;
        Widgets.Label(new Rect(row.x + 122f, row.y + 5f, 24f, 30f), ClashOfRimText.Key("ClashOfRim.Trade.ExchangeVerb"));
        Text.Anchor = TextAnchor.UpperLeft;
        openedInfo |= DrawThingStrip(new Rect(row.x + 150f, row.y + 5f, 112f, 30f), order.RequestedThings, asRequirement: true);

        string memo = order.AcceptedMemoCount > 0
            ? ClashOfRimText.Key("ClashOfRim.Trade.AcceptedMemoSuffix", order.AcceptedMemoCount.Named("COUNT"))
            : string.Empty;
        string accepted = order.ViewerHasAccepted ? ClashOfRimText.Key("ClashOfRim.Trade.ViewerAcceptedSuffix") : string.Empty;
        Widgets.Label(new Rect(row.x + 6f, row.y + 40f, row.width - 12f, 22f), TradePartiesLine(order, includeColony: false) + memo + accepted);
        Text.Font = GameFont.Tiny;
        Widgets.Label(new Rect(row.x + 6f, row.y + 63f, row.width - 12f, 18f), ClashOfRimText.Key(
            "ClashOfRim.Trade.StatusLine",
            TradeUiUtility.FormatOrderStatus(order.Status).Named("STATUS")));
        Text.Font = GameFont.Small;

        if (!openedInfo && Widgets.ButtonInvisible(row))
        {
            selectedTradeEventId = order.EventId;
        }
    }

    private void DrawTradeOrderDetail(Rect rect, ModTradeOrderSummaryDto? order, ClashOfRimMod mod)
    {
        Widgets.DrawMenuSection(rect);
        Rect inner = rect.ContractedBy(8f);
        if (order is null)
        {
            Widgets.Label(inner, ClashOfRimText.Key("ClashOfRim.Trade.SelectOrderForDetails"));
            return;
        }

        if (string.IsNullOrWhiteSpace(selectedTradeEventId))
        {
            selectedTradeEventId = order.EventId;
        }

        float headerY = inner.y;
        Widgets.Label(new Rect(inner.x, headerY, inner.width, 24f), ClashOfRimText.Key("ClashOfRim.Trade.OwnerLine", FormatTradeParty(order.Owner, includeColony: true).Named("OWNER")));
        headerY += 24f;
        if (HasTradeCounterparty(order))
        {
            Widgets.Label(new Rect(inner.x, headerY, inner.width, 24f), ClashOfRimText.Key("ClashOfRim.Trade.CounterpartyLine", FormatTradeParty(order.Counterparty, includeColony: true).Named("COUNTERPARTY")));
            headerY += 24f;
        }

        Widgets.Label(new Rect(inner.x, headerY, inner.width, 24f), ClashOfRimText.Key("ClashOfRim.Trade.StatusLine", TradeUiUtility.FormatOrderStatus(order.Status).Named("STATUS")));
        headerY += 24f;
        Widgets.Label(new Rect(inner.x, headerY, inner.width, 24f), ClashOfRimText.Key(
            "ClashOfRim.Trade.FeeAndMemoLine",
            order.FeeSilver.Named("FEE"),
            order.AcceptedMemoCount.Named("COUNT")));
        headerY += 24f;
        Widgets.Label(new Rect(inner.x, headerY, inner.width, 24f), ClashOfRimText.Key("ClashOfRim.Trade.DropPodPostageLine", TradeUiUtility.FormatPostage(order.ServerDropPodPostage).Named("POSTAGE")));
        headerY += 30f;

        Rect contentRect = new(inner.x, headerY, inner.width, inner.yMax - headerY - 54f);
        Rect viewRect = new(0f, 0f, contentRect.width - 16f, Math.Max(contentRect.height, TradeDetailHeight(order)));
        Widgets.BeginScrollView(contentRect, ref tradeDetailScrollPosition, viewRect);
        float y = 0f;
        y = DrawThingSection(new Rect(0f, y, viewRect.width, 24f), ClashOfRimText.Key("ClashOfRim.Trade.OtherOffers"), order.OfferedThings, asRequirement: false);
        DrawThingSection(new Rect(0f, y + 8f, viewRect.width, 24f), ClashOfRimText.Key("ClashOfRim.Trade.OtherRequests"), order.RequestedThings, asRequirement: true);
        Widgets.EndScrollView();

        Rect acceptRect = new(inner.xMax - 132f, inner.yMax - 42f, 132f, 34f);
        Rect dropPodRect = new(acceptRect.x - 142f, acceptRect.y, 132f, 34f);
        bool canCancel = string.Equals(selectedTradeScope, "Mine", StringComparison.Ordinal)
            && mod.IsOwnOpenTradeOrder(order);
        bool canAct = mod.CanActOnOpenTradeOrder(order);
        bool canAccept = canAct && !order.ViewerHasAccepted && string.Equals(selectedTradeScope, "Open", StringComparison.Ordinal);
        bool canCancelAcceptedMemo = mod.CanCancelAcceptedTradeMemo(order);
        if (canAct && order.AllowServerDropPod)
        {
            bool canDropPod = order.ServerDropPodPostage?.Reachable == true
                && order.ServerDropPodPostage.PostageSilver.HasValue;
            if (canDropPod && Widgets.ButtonText(dropPodRect, ClashOfRimText.Key("ClashOfRim.Trade.SendByDropPod")))
            {
                string summary = ClashOfRimText.Key("ClashOfRim.Trade.ConfirmDropPodFulfillTitle")
                    + "\n\n"
                    + ClashOfRimText.Key("ClashOfRim.Trade.DeliverLine", TradeUiUtility.FormatThingList(order.RequestedThings, asRequirement: true).Named("THINGS"))
                    + "\n"
                    + ClashOfRimText.Key("ClashOfRim.Trade.ReceiveLine", TradeUiUtility.FormatThingList(order.OfferedThings, asRequirement: false).Named("THINGS"))
                    + "\n"
                    + ClashOfRimText.Key("ClashOfRim.Trade.DropPodPostageLine", TradeUiUtility.FormatPostage(order.ServerDropPodPostage).Named("POSTAGE"));
                Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                    summary,
                    () => mod.ConfirmTradeAcceptorWillReceiveGoods(
                        order,
                        () => mod.StartFulfillTradeOrderByDropPod(order))));
            }
            else if (!canDropPod)
            {
                Text.Anchor = TextAnchor.MiddleCenter;
                Widgets.Label(dropPodRect, ClashOfRimText.Key("ClashOfRim.Trade.DropPodUnreachableShort"));
                Text.Anchor = TextAnchor.UpperLeft;
            }
        }

        if (canAccept && Widgets.ButtonText(acceptRect, ClashOfRimText.Key("ClashOfRim.Trade.AddToAcceptedList")))
        {
            mod.ConfirmTradeAcceptorWillReceiveGoods(
                order,
                () => mod.StartAcceptTradeOrder(order, postagePaidByAcceptor: false));
        }
        else if (canCancelAcceptedMemo)
        {
            if (Widgets.ButtonText(acceptRect, ClashOfRimText.Key("ClashOfRim.Trade.CancelAcceptedMemo")))
            {
                Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                    ClashOfRimText.Key("ClashOfRim.Trade.ConfirmCancelAcceptedMemo"),
                    () => mod.StartCancelAcceptedTradeMemo(order)));
            }
        }
        else if (canCancel)
        {
            if (Widgets.ButtonText(acceptRect, ClashOfRimText.Key("ClashOfRim.Trade.CancelOrder")))
            {
                Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                    ClashOfRimText.Key("ClashOfRim.Trade.ConfirmCancelOrder"),
                    () => mod.StartCancelTradeOrder(order)));
            }
        }
        else if (!canAccept)
        {
            Text.Anchor = TextAnchor.MiddleRight;
            Widgets.Label(new Rect(inner.x, inner.yMax - 40f, inner.width, 30f), order.ViewerHasAccepted ? ClashOfRimText.Key("ClashOfRim.Trade.Accepted") : ClashOfRimText.Key("ClashOfRim.Trade.ViewOnly"));
            Text.Anchor = TextAnchor.UpperLeft;
        }
    }

    private void DrawDiplomacyTab(Rect rect, ClashOfRimMod mod)
    {
        Widgets.DrawMenuSection(rect);
        Rect inner = rect.ContractedBy(10f);
        Text.Font = GameFont.Medium;
        Widgets.Label(new Rect(inner.x, inner.y, inner.width, 30f), ClashOfRimText.Key("ClashOfRim.Multiplayer.TabDiplomacy"));
        Text.Font = GameFont.Small;

        IReadOnlyList<ModPlayerSummaryDto> players = SortedPlayers(mod);
        float contentY = inner.y + 42f;
        float contentHeight = inner.height - 42f;
        const float gap = 10f;
        float scoreboardWidth = Mathf.Min(340f, Mathf.Max(260f, inner.width * 0.34f));
        Rect listPanelRect = new(inner.x, contentY, inner.width - scoreboardWidth - gap, contentHeight);
        Rect scoreboardPanelRect = new(listPanelRect.xMax + gap, contentY, scoreboardWidth, contentHeight);

        Widgets.DrawMenuSection(listPanelRect);
        Rect listInner = listPanelRect.ContractedBy(8f);
        Rect outRect = new(listInner.x, listInner.y, listInner.width, listInner.height);
        Rect viewRect = new(0f, 0f, outRect.width - 16f, Math.Max(outRect.height, players.Count * DiplomacyRowHeight));
        Widgets.BeginScrollView(outRect, ref diplomacyScrollPosition, viewRect);
        if (players.Count == 0)
        {
            Widgets.Label(new Rect(0f, 0f, viewRect.width, 32f), ClashOfRimText.Key("ClashOfRim.Multiplayer.NoPlayers"));
        }
        else
        {
            for (int index = 0; index < players.Count; index++)
            {
                DrawDiplomacyRow(new Rect(0f, index * DiplomacyRowHeight, viewRect.width, DiplomacyRowHeight - 4f), players[index], mod);
            }
        }

        Widgets.EndScrollView();

        DrawWealthScoreboard(scoreboardPanelRect, mod);
    }

    private void DrawWealthScoreboard(Rect rect, ClashOfRimMod mod)
    {
        Widgets.DrawMenuSection(rect);
        Rect inner = rect.ContractedBy(8f);
        Text.Font = GameFont.Small;
        Widgets.Label(new Rect(inner.x, inner.y, inner.width, 24f), ClashOfRimText.Key("ClashOfRim.Scoreboard.Title"));

        Rect outRect = new(inner.x, inner.y + 30f, inner.width, inner.height - 30f);
        IReadOnlyList<ModPlayerSummaryDto> rankedPlayers = SortedPlayersByWealth(mod);
        const float rowHeight = 28f;
        Rect viewRect = new(0f, 0f, outRect.width - 16f, Math.Max(outRect.height, rowHeight * (rankedPlayers.Count + 1) + 18f));

        Widgets.BeginScrollView(outRect, ref diplomacyScoreboardScrollPosition, viewRect);
        Text.Font = GameFont.Tiny;
        float y = 0f;
        Widgets.Label(new Rect(0f, y, 34f, 22f), ClashOfRimText.Key("ClashOfRim.Scoreboard.RankHeader"));
        Widgets.Label(new Rect(38f, y, viewRect.width - 130f, 22f), ClashOfRimText.Key("ClashOfRim.Scoreboard.PlayerHeader"));
        Text.Anchor = TextAnchor.UpperRight;
        Widgets.Label(new Rect(viewRect.width - 90f, y, 90f, 22f), ClashOfRimText.Key("ClashOfRim.Scoreboard.WealthHeader"));
        Text.Anchor = TextAnchor.UpperLeft;
        Widgets.DrawLineHorizontal(0f, y + 24f, viewRect.width);

        if (rankedPlayers.Count == 0)
        {
            Text.Font = GameFont.Small;
            Widgets.Label(new Rect(0f, y + rowHeight, viewRect.width, 32f), ClashOfRimText.Key("ClashOfRim.Multiplayer.NoPlayers"));
            y += rowHeight * 2f;
        }
        else
        {
            for (int index = 0; index < rankedPlayers.Count; index++)
            {
                DrawWealthScoreboardRow(new Rect(0f, y + rowHeight * (index + 1), viewRect.width, rowHeight), index + 1, rankedPlayers[index], mod);
            }

            y += rowHeight * (rankedPlayers.Count + 1);
        }

        Text.Font = GameFont.Small;
        Widgets.EndScrollView();
    }

    private void DrawAchievementsTab(Rect rect, ClashOfRimMod mod)
    {
        Widgets.DrawMenuSection(rect);
        Rect inner = rect.ContractedBy(10f);
        EnsureAchievementTarget(mod);
        IReadOnlyList<ModPlayerSummaryDto> selectablePlayers = AchievementSelectablePlayers(mod);

        Text.Font = GameFont.Medium;
        Widgets.Label(new Rect(inner.x, inner.y, 110f, 30f), ClashOfRimText.Key("ClashOfRim.Multiplayer.TabAchievements"));
        Text.Font = GameFont.Small;
        Rect selectorRect = new(inner.x + 118f, inner.y, Mathf.Max(120f, Mathf.Min(260f, inner.width - 250f)), 30f);
        if (ClashOfRimUiUtility.SelectionButton(
                selectorRect,
                AchievementTargetDisplayName(mod, selectablePlayers),
                tooltip: ClashOfRimText.Key("ClashOfRim.Achievement.SelectPlayer")))
        {
            List<FloatMenuOption> options = selectablePlayers
                .Select(player => new FloatMenuOption(
                    PlayerDisplayName(player),
                    () => SelectAchievementTarget(player, mod)))
                .ToList();
            if (options.Count == 0)
            {
                options.Add(new FloatMenuOption(ClashOfRimText.Key("ClashOfRim.Multiplayer.NoPlayers"), null));
            }

            Find.WindowStack.Add(new FloatMenu(options));
        }

        if (Widgets.ButtonText(new Rect(inner.xMax - 120f, inner.y, 120f, 30f), ClashOfRimText.Key("ClashOfRim.Refresh"), active: !mod.ManualSyncInProgress))
        {
            mod.StartRefreshAchievements(selectedAchievementUserId, selectedAchievementColonyId);
            nextAchievementRefreshAt = Time.realtimeSinceStartup + PanelRefreshIntervalSeconds;
        }

        Text.Font = GameFont.Tiny;
        Widgets.Label(new Rect(inner.x, inner.y + 32f, inner.width, 20f), mod.AchievementStatus);
        Text.Font = GameFont.Small;

        IReadOnlyList<ModAchievementSummaryDto> achievements = OwnAchievements(mod);
        Rect outRect = new(inner.x, inner.y + 60f, inner.width, inner.height - 60f);
        const float tileWidth = 220f;
        bool canCreateTrophy = IsSelectedAchievementTargetSelf(mod);
        const float tileHeight = 116f;
        const float gap = 10f;
        int columns = Math.Max(1, Mathf.FloorToInt((outRect.width - 16f + gap) / (tileWidth + gap)));
        int rows = Math.Max(1, Mathf.CeilToInt(achievements.Count / (float)columns));
        Rect viewRect = new(0f, 0f, outRect.width - 16f, Math.Max(outRect.height, rows * (tileHeight + gap)));
        Widgets.BeginScrollView(outRect, ref achievementsScrollPosition, viewRect);
        if (achievements.Count == 0)
        {
            Widgets.Label(new Rect(0f, 0f, viewRect.width, 28f), ClashOfRimText.Key("ClashOfRim.Achievement.OwnEmpty"));
        }
        else
        {
            for (int index = 0; index < achievements.Count; index++)
            {
                int column = index % columns;
                int row = index / columns;
                Rect tile = new(
                    column * (tileWidth + gap),
                    row * (tileHeight + gap),
                    tileWidth,
                    tileHeight);
                DrawOwnAchievementTile(tile, achievements[index], mod, selectablePlayers, canCreateTrophy);
            }
        }

        Widgets.EndScrollView();
        Text.Font = GameFont.Small;
    }

    private void DrawOwnAchievementTile(
        Rect rect,
        ModAchievementSummaryDto achievement,
        ClashOfRimMod mod,
        IReadOnlyList<ModPlayerSummaryDto> selectablePlayers,
        bool canCreateTrophy)
    {
        Widgets.DrawMenuSection(rect);
        DrawAchievementColorBorder(rect, achievement.Color, 2);
        Widgets.DrawHighlightIfMouseover(rect);
        Rect iconRect = new(rect.x + 10f, rect.y + 16f, 48f, 48f);
        Widgets.DrawBoxSolid(iconRect, new Color(0.16f, 0.18f, 0.19f, 0.85f));
        GUI.DrawTexture(iconRect.ContractedBy(6f), AchievementIcon(achievement.IconId), ScaleMode.ScaleToFit);

        Text.Font = GameFont.Small;
        Rect textRect = new(rect.x + 68f, rect.y + 12f, rect.width - 78f, 24f);
        TradeUiUtility.DrawTruncatedLabel(textRect, FormatAchievementLabel(achievement));
        Text.Font = GameFont.Tiny;
        Rect descriptionRect = new(rect.x + 68f, rect.y + 38f, rect.width - 78f, 22f);
        TradeUiUtility.DrawTruncatedLabel(descriptionRect, FormatAchievementDescription(achievement));
        Text.Anchor = TextAnchor.UpperRight;
        Widgets.Label(new Rect(rect.xMax - 76f, rect.y + 62f, 66f, 20f), FormatAchievementValue(achievement.Value));
        Text.Anchor = TextAnchor.UpperLeft;
        if (canCreateTrophy)
        {
            Rect buttonRect = new(rect.x + 68f, rect.yMax - 30f, rect.width - 78f, 24f);
            if (Widgets.ButtonText(buttonRect, ClashOfRimText.Key("ClashOfRim.Achievement.GetTrophyBlueprint")))
            {
                StartPlaceAchievementTrophy(achievement, mod, AchievementTargetDisplayName(mod, selectablePlayers));
            }
        }

        Text.Font = GameFont.Small;
    }

    private static void DrawWealthScoreboardRow(Rect rect, int rank, ModPlayerSummaryDto player, ClashOfRimMod mod)
    {
        bool isSelf = IsSelfPlayer(player, mod);
        if (isSelf)
        {
            Widgets.DrawHighlightSelected(rect);
        }
        else
        {
            Widgets.DrawHighlightIfMouseover(rect);
        }

        Text.Font = GameFont.Tiny;
        Color previousColor = GUI.color;
        if (isSelf)
        {
            GUI.color = new Color(1f, 0.86f, 0.35f);
        }

        Widgets.Label(new Rect(rect.x, rect.y + 4f, 34f, 22f), rank.ToString(CultureInfo.InvariantCulture));
        Widgets.Label(new Rect(rect.x + 38f, rect.y + 4f, rect.width - 132f, 22f), PlayerDisplayName(player));
        Text.Anchor = TextAnchor.UpperRight;
        Widgets.Label(new Rect(rect.xMax - 90f, rect.y + 4f, 90f, 22f), FormatPlayerWealth(player.LatestSnapshotWealth));
        Text.Anchor = TextAnchor.UpperLeft;
        GUI.color = previousColor;
        Text.Font = GameFont.Small;
    }

    private void DrawDiplomacyRow(Rect rect, ModPlayerSummaryDto player, ClashOfRimMod mod)
    {
        Widgets.DrawHighlightIfMouseover(rect);
        bool isSelf = IsSelfPlayer(player, mod);
        string online = IsPlayerOnlineForDisplay(player, mod)
            ? ClashOfRimText.Key("ClashOfRim.Multiplayer.Online")
            : ClashOfRimText.Key("ClashOfRim.Multiplayer.Offline");
        string self = isSelf ? "  " + ClashOfRimText.Key("ClashOfRim.Multiplayer.CurrentPlayer") : string.Empty;
        string relationKind = NormalizeDiplomacyRelationKind(player.RelationKind);
        string relation = FormatDiplomacyRelationKind(relationKind);

        const float buttonWidth = 112f;
        const float buttonHeight = 30f;
        const float buttonGap = 8f;
        int diplomacyButtonCount = !isSelf && !mod.ManualSyncInProgress
            ? relationKind is "Neutral" or "Ally" ? 2 : 1
            : 0;
        int buttonCount = !isSelf && !mod.ManualSyncInProgress
            ? diplomacyButtonCount + 1
            : 0;
        float buttonAreaWidth = buttonCount > 0
            ? buttonCount * buttonWidth + Math.Max(0, buttonCount - 1) * buttonGap
            : 0f;
        float textWidth = Math.Max(120f, rect.width - buttonAreaWidth - 24f);
        Rect nameRect = new(rect.x + 8f, rect.y + 5f, textWidth, 22f);
        Rect statusRect = new(rect.x + 8f, rect.y + 28f, textWidth, 20f);
        TradeUiUtility.DrawTruncatedLabel(nameRect, PlayerDisplayName(player));
        Text.Font = GameFont.Tiny;
        TradeUiUtility.DrawTruncatedLabel(statusRect, $"{online}  {relation}{self}");
        if (Prefs.DevMode)
        {
            string debugLine = $"{player.UserId}/{player.ColonyId}  "
                + (string.IsNullOrWhiteSpace(player.CurrentSnapshotId) ? ClashOfRimText.Key("ClashOfRim.CurrentSnapshotMissing") : player.CurrentSnapshotId!);
            TradeUiUtility.DrawTruncatedLabel(new Rect(rect.x + 8f, rect.y + 48f, textWidth, 18f), debugLine);
        }
        Text.Font = GameFont.Small;

        if (isSelf || mod.ManualSyncInProgress)
        {
            return;
        }

        float buttonY = rect.y + (rect.height - buttonHeight) * 0.5f;
        float buttonX = rect.xMax - buttonCount * buttonWidth - Math.Max(0, buttonCount - 1) * buttonGap;
        if (relationKind == "Neutral")
        {
            if (Widgets.ButtonText(new Rect(buttonX, buttonY, buttonWidth, buttonHeight), ClashOfRimText.Key("ClashOfRim.Diplomacy.RequestAlliance")))
            {
                ConfirmDiplomacyEvent(mod, "AllianceRequest", player);
            }

            buttonX += buttonWidth + buttonGap;
        }

        if (relationKind == "Ally")
        {
            if (Widgets.ButtonText(new Rect(buttonX, buttonY, buttonWidth, buttonHeight), ClashOfRimText.Key("ClashOfRim.Diplomacy.CancelAlliance")))
            {
                ConfirmDiplomacyEvent(mod, "AllianceCancellation", player);
            }

            buttonX += buttonWidth + buttonGap;

            if (Widgets.ButtonText(new Rect(buttonX, buttonY, buttonWidth, buttonHeight), ClashOfRimText.Key("ClashOfRim.Diplomacy.RequestSupport")))
            {
                Find.WindowStack.Add(new AllySupportRequestDialogWindow(mod, player));
            }

            buttonX += buttonWidth + buttonGap;
        }
        else if (relationKind == "Neutral")
        {
            if (Widgets.ButtonText(new Rect(buttonX, buttonY, buttonWidth, buttonHeight), ClashOfRimText.Key("ClashOfRim.Diplomacy.DeclareWar")))
            {
                ConfirmDiplomacyEvent(mod, "WarDeclaration", player);
            }

            buttonX += buttonWidth + buttonGap;
        }

        if (relationKind == "Hostile")
        {
            if (Widgets.ButtonText(new Rect(buttonX, buttonY, buttonWidth, buttonHeight), ClashOfRimText.Key("ClashOfRim.Diplomacy.RequestPeace")))
            {
                ConfirmDiplomacyEvent(mod, "PeaceRequest", player);
            }

            buttonX += buttonWidth + buttonGap;
        }

        Rect locateRect = new(buttonX, buttonY, buttonWidth, buttonHeight);
        bool canLocate = CanLocatePlayerColony(player, mod);
        if (Widgets.ButtonText(locateRect, ClashOfRimText.Key("ClashOfRim.Multiplayer.LocatePlayer"), active: canLocate))
        {
            TryLocatePlayerColony(player, mod);
        }

        if (!canLocate)
        {
            TooltipHandler.TipRegion(locateRect, ClashOfRimText.Key("ClashOfRim.Multiplayer.PlayerLocationUnavailable"));
        }
    }

    private static void ConfirmDiplomacyEvent(ClashOfRimMod mod, string kind, ModPlayerSummaryDto player)
    {
        string key = kind switch
        {
            "AllianceRequest" => "ClashOfRim.Diplomacy.ConfirmAllianceRequest",
            "AllianceCancellation" => "ClashOfRim.Diplomacy.ConfirmAllianceCancellation",
            "WarDeclaration" => "ClashOfRim.Diplomacy.ConfirmWarDeclaration",
            "PeaceRequest" => "ClashOfRim.Diplomacy.ConfirmPeaceRequest",
            _ => "ClashOfRim.Diplomacy.ConfirmEvent"
        };

        string message = ClashOfRimText.Key(
            key,
            PlayerDisplayName(player).Named("PLAYER"),
            kind.Named("KIND"));
        Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
            message,
            () => mod.StartCreateDiplomacyEvent(kind, player)));
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
                TryRefreshPlayers(mod);
                break;
            case MultiplayerTab.Achievements:
                TryRefreshPlayers(mod);
                TryRefreshAchievements(mod);
                break;
            case MultiplayerTab.Bank:
                TryRefreshBank(mod);
                break;
            case MultiplayerTab.Mercenary:
                TryRefreshBank(mod);
                break;
            case MultiplayerTab.Trade:
                TryRefreshTradeOrders(mod);
                break;
            case MultiplayerTab.Shop:
                TryRefreshServerShop(mod);
                break;
            case MultiplayerTab.Admin:
                TryRefreshAdmin(mod);
                break;
        }
    }

    private void TryRefreshPlayers(ClashOfRimMod mod)
    {
        if (mod.ManualSyncInProgress || Time.realtimeSinceStartup < nextPlayerRefreshAt)
        {
            return;
        }

        nextPlayerRefreshAt = Time.realtimeSinceStartup + PanelRefreshIntervalSeconds;
        mod.StartRefreshPlayers();
    }

    private void TryRefreshAchievements(ClashOfRimMod mod)
    {
        if (mod.ManualSyncInProgress || Time.realtimeSinceStartup < nextAchievementRefreshAt)
        {
            return;
        }

        EnsureAchievementTarget(mod);
        nextAchievementRefreshAt = Time.realtimeSinceStartup + PanelRefreshIntervalSeconds;
        mod.StartRefreshAchievements(selectedAchievementUserId, selectedAchievementColonyId);
    }

    private void TryRefreshTradeOrders(ClashOfRimMod mod)
    {
        if (mod.ManualSyncInProgress || Time.realtimeSinceStartup < nextTradeRefreshAt)
        {
            return;
        }

        nextTradeRefreshAt = Time.realtimeSinceStartup + PanelRefreshIntervalSeconds;
        mod.StartRefreshTradeOrders(selectedTradeScope);
    }

    private void TryRefreshServerShop(ClashOfRimMod mod)
    {
        if (mod.ManualSyncInProgress || mod.ServerShopInProgress || Time.realtimeSinceStartup < nextShopRefreshAt)
        {
            return;
        }

        nextShopRefreshAt = Time.realtimeSinceStartup + PanelRefreshIntervalSeconds;
        mod.StartRefreshServerShop();
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

    private IReadOnlyList<ModPlayerSummaryDto> SortedPlayers(ClashOfRimMod mod)
    {
        RefreshPlayerSortCache(mod);
        return cachedSortedPlayers;
    }

    private IReadOnlyList<ModPlayerSummaryDto> SortedPlayersByWealth(ClashOfRimMod mod)
    {
        RefreshPlayerSortCache(mod);
        return cachedWealthRankedPlayers;
    }

    private void EnsureAchievementTarget(ClashOfRimMod mod)
    {
        if (!string.Equals(selectedAchievementOwnerUserId, mod.UserId, StringComparison.Ordinal)
            || !string.Equals(selectedAchievementOwnerColonyId, mod.ColonyId, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(selectedAchievementUserId)
            || string.IsNullOrWhiteSpace(selectedAchievementColonyId))
        {
            selectedAchievementUserId = mod.UserId;
            selectedAchievementColonyId = mod.ColonyId;
            selectedAchievementOwnerUserId = mod.UserId;
            selectedAchievementOwnerColonyId = mod.ColonyId;
        }
    }

    private IReadOnlyList<ModPlayerSummaryDto> AchievementSelectablePlayers(ClashOfRimMod mod)
    {
        List<ModPlayerSummaryDto> players = SortedPlayers(mod)
            .Where(player => !string.IsNullOrWhiteSpace(player.UserId)
                && !string.IsNullOrWhiteSpace(player.ColonyId))
            .ToList();
        if (!players.Any(player => IsSelfPlayer(player, mod))
            && !string.IsNullOrWhiteSpace(mod.UserId)
            && !string.IsNullOrWhiteSpace(mod.ColonyId))
        {
            players.Insert(0, new ModPlayerSummaryDto
            {
                UserId = mod.UserId,
                ColonyId = mod.ColonyId,
                Online = true,
                DisplayName = mod.UserId
            });
        }

        return players;
    }

    private string AchievementTargetDisplayName(ClashOfRimMod mod, IReadOnlyList<ModPlayerSummaryDto> players)
    {
        ModPlayerSummaryDto? selected = players.FirstOrDefault(player =>
            string.Equals(player.UserId, selectedAchievementUserId, StringComparison.Ordinal)
            && string.Equals(player.ColonyId, selectedAchievementColonyId, StringComparison.Ordinal));
        if (selected is not null)
        {
            return PlayerDisplayName(selected);
        }

        return string.Equals(selectedAchievementUserId, mod.UserId, StringComparison.Ordinal)
            && string.Equals(selectedAchievementColonyId, mod.ColonyId, StringComparison.Ordinal)
                ? mod.UserId
                : string.IsNullOrWhiteSpace(selectedAchievementUserId)
                    ? ClashOfRimText.Key("ClashOfRim.Achievement.SelectPlayer")
                    : selectedAchievementUserId;
    }

    private void SelectAchievementTarget(ModPlayerSummaryDto player, ClashOfRimMod mod)
    {
        if (string.Equals(selectedAchievementUserId, player.UserId, StringComparison.Ordinal)
            && string.Equals(selectedAchievementColonyId, player.ColonyId, StringComparison.Ordinal))
        {
            return;
        }

        selectedAchievementUserId = player.UserId;
        selectedAchievementColonyId = player.ColonyId;
        achievementsScrollPosition = Vector2.zero;
        cachedOwnAchievementsVersion = -1;
        nextAchievementRefreshAt = Time.realtimeSinceStartup + PanelRefreshIntervalSeconds;
        mod.StartRefreshAchievements(selectedAchievementUserId, selectedAchievementColonyId);
    }

    private bool IsSelectedAchievementTargetSelf(ClashOfRimMod mod)
    {
        return string.Equals(selectedAchievementUserId, mod.UserId, StringComparison.Ordinal)
            && string.Equals(selectedAchievementColonyId, mod.ColonyId, StringComparison.Ordinal);
    }

    private void StartPlaceAchievementTrophy(
        ModAchievementSummaryDto achievement,
        ClashOfRimMod mod,
        string ownerDisplayName)
    {
        if (Find.CurrentMap is null)
        {
            Messages.Message(ClashOfRimText.Key("ClashOfRim.Achievement.TrophyNoMap"), MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        AchievementTrophyData trophyData = AchievementTrophyData.FromAchievement(
            achievement,
            mod.UserId,
            mod.ColonyId,
            ownerDisplayName);
        bool created = AchievementTrophyUtility.TryCreatePlanOnCurrentMap(trophyData, out string message);
        Messages.Message(message, created ? MessageTypeDefOf.TaskCompletion : MessageTypeDefOf.RejectInput, historical: false);
        if (!created)
        {
            return;
        }

        Close(doCloseSound: false);
    }

    private IReadOnlyList<ModAchievementSummaryDto> OwnAchievements(ClashOfRimMod mod)
    {
        int version = mod.AchievementLeaderboardsSnapshotVersion;
        if (version != cachedOwnAchievementsVersion
            || !string.Equals(cachedOwnAchievementsTargetUserId, selectedAchievementUserId, StringComparison.Ordinal)
            || !string.Equals(cachedOwnAchievementsTargetColonyId, selectedAchievementColonyId, StringComparison.Ordinal))
        {
            if (string.Equals(mod.AchievementTargetUserId, selectedAchievementUserId, StringComparison.Ordinal)
                && string.Equals(mod.AchievementTargetColonyId, selectedAchievementColonyId, StringComparison.Ordinal))
            {
                cachedOwnAchievements = mod.OwnAchievementsSnapshot
                    .Where(achievement => achievement.Value != 0)
                    .OrderBy(achievement => achievement.Category, StringComparer.Ordinal)
                    .ThenBy(achievement => achievement.AchievementId, StringComparer.Ordinal)
                    .ToList();
            }
            else
            {
                cachedOwnAchievements = Array.Empty<ModAchievementSummaryDto>();
            }

            cachedOwnAchievementsVersion = version;
            cachedOwnAchievementsTargetUserId = selectedAchievementUserId;
            cachedOwnAchievementsTargetColonyId = selectedAchievementColonyId;
        }

        return cachedOwnAchievements;
    }

    private void RefreshPlayerSortCache(ClashOfRimMod mod)
    {
        int version = mod.PlayersSnapshotVersion;
        if (version == cachedPlayersVersion
            && string.Equals(mod.UserId, cachedPlayersUserId, StringComparison.Ordinal)
            && string.Equals(mod.ColonyId, cachedPlayersColonyId, StringComparison.Ordinal))
        {
            return;
        }

        List<ModPlayerSummaryDto> snapshot = mod.PlayersSnapshot
            .Where(HasVisiblePlayerColony)
            .ToList();
        cachedSortedPlayers = snapshot
            .OrderBy(player => IsSelfPlayer(player, mod) ? 0 : 1)
            .ThenByDescending(player => IsPlayerOnlineForDisplay(player, mod))
            .ThenBy(player => player.UserId, StringComparer.Ordinal)
            .ThenBy(player => player.ColonyId, StringComparer.Ordinal)
            .ToList();
        cachedWealthRankedPlayers = snapshot
            .OrderByDescending(player => player.LatestSnapshotWealth.HasValue)
            .ThenByDescending(player => player.LatestSnapshotWealth ?? 0)
            .ThenBy(PlayerDisplayName, StringComparer.Ordinal)
            .ThenBy(player => player.ColonyId, StringComparer.Ordinal)
            .ToList();
        cachedPlayersVersion = version;
        cachedPlayersUserId = mod.UserId;
        cachedPlayersColonyId = mod.ColonyId;
    }

    private static string FormatPlayerWealth(int? wealth)
    {
        return wealth.HasValue
            ? wealth.Value.ToString("N0", CultureInfo.InvariantCulture)
            : ClashOfRimText.Key("ClashOfRim.Scoreboard.WealthUnknown");
    }

    private static string FormatAchievementLabel(ModAchievementLeaderboardDto board)
    {
        return FormatAchievementLabel(board.LabelKey, board.AchievementId);
    }

    private static string FormatAchievementLabel(ModAchievementSummaryDto achievement)
    {
        return FormatAchievementLabel(achievement.LabelKey, achievement.AchievementId);
    }

    private static string FormatAchievementDescription(ModAchievementSummaryDto achievement)
    {
        if (!string.IsNullOrWhiteSpace(achievement.DescriptionKey))
        {
            string translated = ClashOfRimText.Key(achievement.DescriptionKey);
            if (!string.Equals(translated, achievement.DescriptionKey, StringComparison.Ordinal))
            {
                return translated;
            }
        }

        return string.Empty;
    }

    private static string FormatAchievementLabel(string labelKey, string achievementId)
    {
        if (!string.IsNullOrWhiteSpace(labelKey))
        {
            string translated = ClashOfRimText.Key(labelKey);
            if (!string.Equals(translated, labelKey, StringComparison.Ordinal))
            {
                return translated;
            }
        }

        return string.IsNullOrWhiteSpace(achievementId)
            ? ClashOfRimText.Key("ClashOfRim.Achievement.Unknown")
            : achievementId;
    }

    private static Texture2D AchievementIcon(string? iconId)
    {
        return ContentFinder<Texture2D>.Get("UI/ClashOfRim/AchievementDefault", reportFailure: false)
            ?? BaseContent.WhiteTex;
    }

    private static void DrawAchievementColorBorder(Rect rect, string? color, int thickness)
    {
        Color previousColor = GUI.color;
        GUI.color = AchievementBorderColor(color);
        Widgets.DrawBox(rect, thickness);
        GUI.color = previousColor;
    }

    private static Color AchievementBorderColor(string? color)
    {
        if (string.Equals(color, "Blue", StringComparison.OrdinalIgnoreCase))
        {
            return new Color(0.36f, 0.68f, 1f, 0.95f);
        }

        if (string.Equals(color, "Purple", StringComparison.OrdinalIgnoreCase))
        {
            return new Color(0.70f, 0.48f, 1f, 0.95f);
        }

        if (string.Equals(color, "Red", StringComparison.OrdinalIgnoreCase))
        {
            return new Color(1f, 0.35f, 0.30f, 0.95f);
        }

        string? trimmed = color?.Trim();
        if (!string.IsNullOrWhiteSpace(trimmed)
            && ColorUtility.TryParseHtmlString(trimmed, out Color parsed))
        {
            if (parsed.a <= 0f)
            {
                parsed.a = 0.95f;
            }

            return parsed;
        }

        return new Color(0.42f, 0.82f, 0.42f, 0.95f);
    }

    private static string FormatAchievementValue(long value)
    {
        return value.ToString("N0", CultureInfo.InvariantCulture);
    }

    private static bool IsSelfPlayer(ModPlayerSummaryDto player, ClashOfRimMod mod)
    {
        return string.Equals(player.UserId, mod.UserId, StringComparison.Ordinal)
            && string.Equals(player.ColonyId, mod.ColonyId, StringComparison.Ordinal);
    }

    private static bool HasVisiblePlayerColony(ModPlayerSummaryDto player)
    {
        return !string.IsNullOrWhiteSpace(player.ColonyId)
            && !string.IsNullOrWhiteSpace(player.CurrentSnapshotId);
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

    private static bool IsPlayerOnlineForDisplay(ModPlayerSummaryDto player, ClashOfRimMod mod)
    {
        return IsSelfPlayer(player, mod) || player.Online;
    }

    private bool CanLocatePlayerColony(ModPlayerSummaryDto player, ClashOfRimMod mod)
    {
        return TryResolveCachedPlayerColonyTarget(player, mod, out _, out _);
    }

    private void TryLocatePlayerColony(ModPlayerSummaryDto player, ClashOfRimMod mod)
    {
        if (!TryResolveCachedPlayerColonyTarget(player, mod, out WorldObject? worldObject, out PlanetTile tile))
        {
            Messages.Message(
                ClashOfRimText.Key("ClashOfRim.Multiplayer.PlayerLocationUnavailable"),
                MessageTypeDefOf.RejectInput,
                historical: false);
            playerLocationCache.Remove(PlayerLocationCacheKey(player));
            mod.RequestWorldMapMarkerRefresh(ClashOfRimText.Key("ClashOfRim.WorldMap.ReasonLocatePlayer"));
            return;
        }

        if (worldObject is not null && worldObject.Spawned)
        {
            CameraJumper.TryJump(new GlobalTargetInfo(worldObject), CameraJumper.MovementMode.Pan);
            return;
        }

        CameraJumper.TryJump(tile, CameraJumper.MovementMode.Pan);
    }

    private bool TryResolveCachedPlayerColonyTarget(
        ModPlayerSummaryDto player,
        ClashOfRimMod mod,
        out WorldObject? worldObject,
        out PlanetTile tile)
    {
        int playerVersion = mod.PlayersSnapshotVersion;
        if (playerLocationCachePlayersVersion != playerVersion)
        {
            playerLocationCache.Clear();
            playerLocationCachePlayersVersion = playerVersion;
        }

        string key = PlayerLocationCacheKey(player);
        float now = Time.realtimeSinceStartup;
        if (playerLocationCache.TryGetValue(key, out PlayerLocationCacheEntry cached)
            && cached.ExpiresAt > now
            && (cached.WorldObject is null || cached.WorldObject.Spawned))
        {
            worldObject = cached.WorldObject;
            tile = cached.Tile;
            return cached.Found;
        }

        bool found = TryResolvePlayerColonyTarget(player, mod, out worldObject, out tile);
        playerLocationCache[key] = new PlayerLocationCacheEntry
        {
            ExpiresAt = now + PlayerLocationCacheSeconds,
            Found = found,
            WorldObject = worldObject,
            Tile = tile
        };
        return found;
    }

    private static bool TryResolvePlayerColonyTarget(
        ModPlayerSummaryDto player,
        ClashOfRimMod mod,
        out WorldObject? worldObject,
        out PlanetTile tile)
    {
        worldObject = null;
        tile = PlanetTile.Invalid;

        if (Find.WorldGrid is null)
        {
            return false;
        }

        IRemoteWorldObjectView? bestView = null;
        List<WorldObject>? worldObjects = Find.WorldObjects?.AllWorldObjects;
        if (worldObjects is not null)
        {
            for (int index = 0; index < worldObjects.Count; index++)
            {
                if (worldObjects[index] is not IRemoteWorldObjectView view
                    || !IsPlayerColonyViewForPlayer(view, player)
                    || !IsValidWorldTile(view.Tile))
                {
                    continue;
                }

                if (bestView is null || IsBetterPlayerColonyView(view, bestView))
                {
                    bestView = view;
                }
            }
        }

        worldObject = bestView?.WorldObject;
        if (worldObject is not null)
        {
            tile = worldObject.Tile;
            return true;
        }

        ModWorldMapMarkerDto? marker = null;
        List<ModWorldMapMarkerDto> markers = mod.WorldMapMarkersSnapshot;
        for (int index = 0; index < markers.Count; index++)
        {
            ModWorldMapMarkerDto candidate = markers[index];
            if (!IsPlayerColonyMarkerForPlayer(candidate, player) || !IsValidTileId(candidate.Tile))
            {
                continue;
            }

            if (marker is null || IsBetterPlayerColonyMarker(candidate, marker))
            {
                marker = candidate;
            }
        }

        if (marker is null)
        {
            return false;
        }

        tile = new PlanetTile(marker.Tile, Math.Max(0, marker.TileLayerId));
        return IsValidWorldTile(tile);
    }

    private static bool IsBetterPlayerColonyView(IRemoteWorldObjectView candidate, IRemoteWorldObjectView current)
    {
        bool candidateIsMapParent = candidate.WorldObject is RemoteColonyMapParent;
        bool currentIsMapParent = current.WorldObject is RemoteColonyMapParent;
        if (candidateIsMapParent != currentIsMapParent)
        {
            return candidateIsMapParent;
        }

        return string.Compare(candidate.Label, current.Label, StringComparison.Ordinal) < 0;
    }

    private static bool IsBetterPlayerColonyMarker(ModWorldMapMarkerDto candidate, ModWorldMapMarkerDto current)
    {
        return string.Compare(candidate.Label, current.Label, StringComparison.Ordinal) < 0;
    }

    private static bool IsPlayerColonyViewForPlayer(IRemoteWorldObjectView view, ModPlayerSummaryDto player)
    {
        return view.RuntimeKind == "TradeableColony"
            && string.Equals(view.OwnerUserId, player.UserId, StringComparison.Ordinal)
            && (string.IsNullOrWhiteSpace(player.ColonyId)
                || string.Equals(view.OwnerColonyId, player.ColonyId, StringComparison.Ordinal));
    }

    private static bool IsPlayerColonyMarkerForPlayer(ModWorldMapMarkerDto marker, ModPlayerSummaryDto player)
    {
        return marker.Kind == "TradeableColony"
            && string.Equals(marker.OwnerUserId, player.UserId, StringComparison.Ordinal)
            && (string.IsNullOrWhiteSpace(player.ColonyId)
                || string.Equals(marker.OwnerColonyId, player.ColonyId, StringComparison.Ordinal));
    }

    private static bool IsValidTileId(int tileId)
    {
        return tileId >= 0 && Find.WorldGrid is not null && tileId < Find.WorldGrid.TilesCount;
    }

    private static bool IsValidWorldTile(PlanetTile tile)
    {
        return tile.Valid && IsValidTileId(tile);
    }

    private static string PlayerLocationCacheKey(ModPlayerSummaryDto player)
    {
        return (player.UserId ?? string.Empty) + "\u001f" + (player.ColonyId ?? string.Empty);
    }

    private sealed class PlayerLocationCacheEntry
    {
        public float ExpiresAt { get; set; }

        public bool Found { get; set; }

        public WorldObject? WorldObject { get; set; }

        public PlanetTile Tile { get; set; }
    }

    private static string PlayerDisplayName(ModPlayerSummaryDto player)
    {
        if (!string.IsNullOrWhiteSpace(player.DisplayName))
        {
            return player.DisplayName!;
        }

        return string.IsNullOrWhiteSpace(player.UserId)
            ? ClashOfRimText.Key("ClashOfRim.UnknownPlayer")
            : player.UserId;
    }

    private static string NormalizeDiplomacyRelationKind(string? relationKind)
    {
        if (string.Equals(relationKind, "Ally", StringComparison.OrdinalIgnoreCase))
        {
            return "Ally";
        }

        if (string.Equals(relationKind, "Hostile", StringComparison.OrdinalIgnoreCase))
        {
            return "Hostile";
        }

        return "Neutral";
    }

    private static string FormatDiplomacyRelationKind(string relationKind)
    {
        return relationKind switch
        {
            "Ally" => ClashOfRimText.Key("ClashOfRim.Diplomacy.RelationAlly"),
            "Hostile" => ClashOfRimText.Key("ClashOfRim.Diplomacy.RelationHostile"),
            _ => ClashOfRimText.Key("ClashOfRim.Diplomacy.RelationNeutral")
        };
    }

    private string TradeScopeTitle()
    {
        return selectedTradeScope switch
        {
            "AcceptedByMe" => ClashOfRimText.Key("ClashOfRim.Trade.ScopeTitleAcceptedByMe"),
            "Mine" => ClashOfRimText.Key("ClashOfRim.Trade.ScopeTitleMine"),
            "History" => ClashOfRimText.Key("ClashOfRim.Trade.ScopeTitleHistory"),
            _ => ClashOfRimText.Key("ClashOfRim.Trade.ScopeTitleOpen")
        };
    }

    private static float TradeDetailHeight(ModTradeOrderSummaryDto order)
    {
        int offeredRows = TradeUiUtility.BuildDisplayGroups(order.OfferedThings).Count;
        int requestedRows = TradeUiUtility.BuildDisplayGroups(order.RequestedThings).Count;
        return 56f
            + Math.Max(1, offeredRows) * TradeUiUtility.RowHeight
            + Math.Max(1, requestedRows) * TradeUiUtility.RowHeight;
    }

    private static string TradePartiesLine(ModTradeOrderSummaryDto order, bool includeColony)
    {
        string owner = FormatTradeParty(order.Owner, includeColony);
        return HasTradeCounterparty(order)
            ? ClashOfRimText.Key(
                "ClashOfRim.Trade.PartiesLine",
                owner.Named("OWNER"),
                FormatTradeParty(order.Counterparty, includeColony).Named("COUNTERPARTY"))
            : owner;
    }

    private static bool HasTradeCounterparty(ModTradeOrderSummaryDto order)
    {
        return !string.IsNullOrWhiteSpace(order.Counterparty?.UserId);
    }

    private static string FormatTradeParty(ModProtocolIdentityDto? identity, bool includeColony)
    {
        string? userId = identity?.UserId;
        if (string.IsNullOrWhiteSpace(userId))
        {
            return ClashOfRimText.Key("ClashOfRim.UnknownPlayer");
        }

        string? colonyId = identity?.ColonyId;
        return includeColony && !string.IsNullOrWhiteSpace(colonyId)
            ? userId! + "/" + colonyId
            : userId!;
    }

    private static float DrawThingSection(
        Rect headerRect,
        string title,
        IReadOnlyList<ModThingReferenceDto> things,
        bool asRequirement)
    {
        Widgets.Label(headerRect, title);
        float y = headerRect.yMax + 4f;
        if (things.Count == 0)
        {
            Widgets.Label(new Rect(headerRect.x, y, headerRect.width, 28f), ClashOfRimText.Key("ClashOfRim.None"));
            return y + 32f;
        }

        foreach (TradeUiUtility.TradeThingDisplayGroup group in TradeUiUtility.BuildDisplayGroups(things))
        {
            Rect row = new(headerRect.x, y, headerRect.width, TradeUiUtility.RowHeight);
            DrawThingReferenceRow(row, group, asRequirement);
            y += TradeUiUtility.RowHeight;
        }

        return y;
    }

    private static void DrawThingReferenceRow(Rect row, TradeUiUtility.TradeThingDisplayGroup group, bool asRequirement)
    {
        Widgets.DrawHighlightIfMouseover(row);
        Rect iconRect = new(row.x + 3f, row.y + 3f, TradeUiUtility.IconSize, TradeUiUtility.IconSize);
        TradeUiUtility.DrawThingDisplayGroupIconWithInfo(iconRect, group);
        Rect labelRect = new(iconRect.xMax + 8f, row.y, row.width - iconRect.width - 12f, row.height);
        TradeUiUtility.DrawTruncatedLabel(labelRect, TradeUiUtility.ThingDisplayGroupLabel(group, asRequirement));
    }

    private static bool DrawThingStrip(Rect rect, IReadOnlyList<ModThingReferenceDto> things, bool asRequirement)
    {
        bool openedInfo = false;
        IReadOnlyList<TradeUiUtility.TradeThingDisplayGroup> groups = TradeUiUtility.BuildDisplayGroups(things);
        int max = Math.Min(3, groups.Count);
        for (int index = 0; index < max; index++)
        {
            Rect iconRect = new(rect.x + index * 34f, rect.y, 30f, 30f);
            TradeUiUtility.TradeThingDisplayGroup group = groups[index];
            openedInfo |= TradeUiUtility.DrawThingDisplayGroupIconWithInfo(iconRect, group);
            TooltipHandler.TipRegion(iconRect, TradeUiUtility.ThingDisplayGroupLabel(group, asRequirement));
        }

        if (groups.Count > max)
        {
            Text.Anchor = TextAnchor.MiddleCenter;
            Widgets.Label(new Rect(rect.x + max * 34f, rect.y, 30f, 30f), "+" + (groups.Count - max));
            Text.Anchor = TextAnchor.UpperLeft;
        }

        return openedInfo;
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
