using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Compatibility;
using AIRsLight.ClashOfRim.CompatibilityClient;
using AIRsLight.ClashOfRim.Trades;
using RimWorld;
using UnityEngine;
using Verse;

namespace AIRsLight.ClashOfRim.Multiplayer;

public sealed partial class ClashOfRimMultiplayerWindow
{
    private void DrawAdminTab(Rect rect, ClashOfRimMod mod)
    {
        Widgets.DrawMenuSection(rect);
        Rect inner = rect.ContractedBy(10f);
        Text.Font = GameFont.Medium;
        Widgets.Label(new Rect(inner.x, inner.y, inner.width - 120f, 30f), ClashOfRimText.Key("ClashOfRim.Multiplayer.TabAdmin"));
        Text.Font = GameFont.Small;
        if (Widgets.ButtonText(new Rect(inner.xMax - 110f, inner.y, 110f, 30f), ClashOfRimText.Key("ClashOfRim.Refresh"), active: !mod.AdminInProgress))
        {
            mod.StartRefreshAdminStatus();
            nextAdminRefreshAt = Time.realtimeSinceStartup + PanelRefreshIntervalSeconds;
        }

        const float headerHeight = 40f;
        const float tabHeight = 34f;
        Rect tabContentRect = new(
            inner.x,
            inner.y + headerHeight + tabHeight,
            inner.width,
            inner.height - headerHeight - tabHeight);
        TabDrawer.DrawTabs(tabContentRect, AdminTabs());
        Rect content = tabContentRect.ContractedBy(8f);
        Widgets.Label(new Rect(content.x, content.y, content.width, 24f), mod.AdminStatus);

        ModAdminStatusResponseDto? status = mod.AdminStatusSnapshot;
        if (status?.Configuration is not null)
        {
            EnsureAdminConfigFields(status.Configuration);
        }

        Rect body = new(content.x, content.y + 32f, content.width, content.height - 32f);
        switch (selectedAdminPanelTab)
        {
            case AdminPanelTab.Shop:
                DrawAdminShopPanel(body, mod);
                break;
            case AdminPanelTab.Bank:
                DrawAdminBankPanel(body, mod, status?.Configuration);
                break;
            case AdminPanelTab.Mercenary:
                DrawAdminMercenaryPanel(body, mod, status?.Configuration);
                break;
            case AdminPanelTab.Raid:
                DrawAdminRaidPanel(body, mod, status?.Configuration);
                break;
            case AdminPanelTab.Players:
                DrawAdminPlayersPanel(body, mod, status);
                break;
            case AdminPanelTab.Maintenance:
                DrawAdminMaintenancePanel(body, mod, status);
                break;
            case AdminPanelTab.Manifest:
                DrawAdminManifestPanel(body, mod, status?.Configuration);
                break;
            case AdminPanelTab.Audit:
                DrawAdminAuditPanel(body, status);
                break;
            default:
                DrawAdminTradePanel(body, mod, status?.Configuration);
                break;
        }
    }

    private List<TabRecord> AdminTabs()
    {
        if (cachedAdminTabs is not null && cachedAdminTabsSelectedTab == selectedAdminPanelTab)
        {
            return cachedAdminTabs;
        }

        cachedAdminTabsSelectedTab = selectedAdminPanelTab;
        cachedAdminTabs = BuildAdminTabs();
        return cachedAdminTabs;
    }

    private List<TabRecord> BuildAdminTabs()
    {
        return new List<TabRecord>
        {
            AdminTab(AdminPanelTab.Trade, ClashOfRimText.Key("ClashOfRim.Admin.TabTrade")),
            AdminTab(AdminPanelTab.Shop, ClashOfRimText.Key("ClashOfRim.Admin.TabShop")),
            AdminTab(AdminPanelTab.Bank, ClashOfRimText.Key("ClashOfRim.Admin.TabBank")),
            AdminTab(AdminPanelTab.Mercenary, ClashOfRimText.Key("ClashOfRim.Admin.TabMercenary")),
            AdminTab(AdminPanelTab.Raid, ClashOfRimText.Key("ClashOfRim.Admin.TabRaid")),
            AdminTab(AdminPanelTab.Players, ClashOfRimText.Key("ClashOfRim.Admin.TabPlayers")),
            AdminTab(AdminPanelTab.Maintenance, ClashOfRimText.Key("ClashOfRim.Admin.TabMaintenance")),
            AdminTab(AdminPanelTab.Manifest, ClashOfRimText.Key("ClashOfRim.Admin.TabManifest")),
            AdminTab(AdminPanelTab.Audit, ClashOfRimText.Key("ClashOfRim.Admin.TabAudit"))
        };
    }

    private TabRecord AdminTab(AdminPanelTab tab, string label)
    {
        return new TabRecord(label, () => SelectAdminTab(tab), selectedAdminPanelTab == tab);
    }

    private void SelectAdminTab(AdminPanelTab tab)
    {
        if (selectedAdminPanelTab == tab)
        {
            return;
        }

        selectedAdminPanelTab = tab;
        cachedAdminTabs = null;
    }

    private void DrawAdminTradePanel(Rect rect, ClashOfRimMod mod, ModAdminConfigurationDto? config)
    {
        if (config is null)
        {
            Widgets.Label(rect, ClashOfRimText.Key("ClashOfRim.Admin.NoStatus"));
            return;
        }

        Rect view = BeginAdminScrollWithSaveFooter(rect, 762f + adminFixedTradeFeeRows.Count * 34f);
        float y = 0f;
        DrawAdminCheckboxRow(view, ref y, ClashOfRimText.Key("ClashOfRim.Admin.TradeEnabled"), ref adminTradeMarketplaceEnabled);
        DrawAdminTextRow(view, ref y, ClashOfRimText.Key("ClashOfRim.Admin.TradeExpirationDays"), ref adminTradeOrderExpirationDaysText);
        DrawAdminTextRow(view, ref y, ClashOfRimText.Key("ClashOfRim.Admin.MaxOpenTradeOrders"), ref adminMaxOpenTradeOrdersText);
        DrawAdminTextRow(view, ref y, ClashOfRimText.Key("ClashOfRim.Admin.TradeBaseFeeRate"), ref adminTradeBaseFeeRateText);
        DrawAdminTradeFeeStrategyRow(view, ref y);
        DrawAdminFixedTradeFeeRows(view, ref y, mod);
        DrawAdminTextRow(view, ref y, ClashOfRimText.Key("ClashOfRim.Admin.PostageBase"), ref adminPostageBaseText);
        DrawAdminTextRow(view, ref y, ClashOfRimText.Key("ClashOfRim.Admin.PostagePerTile"), ref adminPostagePerTileText);
        DrawAdminTextRow(view, ref y, ClashOfRimText.Key("ClashOfRim.Admin.PostageCrossLayer"), ref adminPostageCrossLayerText);
        Widgets.EndScrollView();
        DrawAdminSaveFooter(rect, mod);
    }

    private void DrawAdminShopPanel(Rect rect, ClashOfRimMod mod)
    {
        IReadOnlyList<ModServerShopListingDto> listings = mod.ServerShopListingsSnapshot;
        Rect view = BeginAdminScroll(rect, Math.Max(rect.height, 80f + listings.Count * 54f));
        float y = 0f;
        DrawAdminSectionTitle(view, ref y, ClashOfRimText.Key("ClashOfRim.Admin.SectionShop"));
        Text.Font = GameFont.Tiny;
        Widgets.Label(new Rect(0f, y, view.width - 260f, 20f), mod.ServerShopStatus);
        Text.Font = GameFont.Small;
        if (Widgets.ButtonText(new Rect(view.width - 250f, y, 110f, 28f), ClashOfRimText.Key("ClashOfRim.Refresh"), active: !mod.ServerShopInProgress))
        {
            mod.StartRefreshServerShop();
        }

        if (Widgets.ButtonText(new Rect(view.width - 130f, y, 120f, 28f), ClashOfRimText.Key("ClashOfRim.Shop.AddListing"), active: !mod.ServerShopInProgress))
        {
            Find.WindowStack.Add(new ServerShopListingDialogWindow(mod, listing: null));
        }

        y += 38f;
        if (listings.Count == 0)
        {
            Widgets.Label(new Rect(0f, y, view.width, 30f), ClashOfRimText.Key("ClashOfRim.Shop.NoListings"));
            Widgets.EndScrollView();
            return;
        }

        foreach (ModServerShopListingDto listing in listings)
        {
            DrawAdminShopListingRow(new Rect(0f, y, view.width, 48f), listing, mod);
            y += 54f;
        }

        Widgets.EndScrollView();
    }

    private void DrawAdminShopListingRow(Rect row, ModServerShopListingDto listing, ClashOfRimMod mod)
    {
        Widgets.DrawMenuSection(row);
        Rect inner = row.ContractedBy(5f);
        ModThingReferenceDto? item = listing.Item;
        bool buyFromPlayer = IsAdminServerShopBuyOrder(listing);
        Rect iconRect = new(inner.x, inner.y + 3f, 32f, 32f);
        if (item is not null)
        {
            TradeUiUtility.DrawThingIconWithInfo(iconRect, item);
        }

        string label = item is null
            ? ClashOfRimText.Key("ClashOfRim.UnknownThing")
            : TradeUiUtility.ThingLabel(
                item,
                asRequirement: buyFromPlayer,
                qualityRequirementMode: buyFromPlayer ? listing.QualityRequirementMode : null,
                hitPointsRequirementMode: buyFromPlayer ? listing.HitPointsRequirementMode : null);
        string kindLabel = ClashOfRimText.Key(buyFromPlayer ? "ClashOfRim.Shop.KindBuy" : "ClashOfRim.Shop.KindSell");
        TradeUiUtility.DrawTruncatedLabel(new Rect(iconRect.xMax + 8f, inner.y + 7f, inner.width - 520f, 24f), kindLabel + " - " + label);

        string stockText = adminShopStockBuffers.TryGetValue(listing.ListingId, out string bufferedStock)
            ? bufferedStock
            : listing.StockCount.ToString();
        string priceText = adminShopPriceBuffers.TryGetValue(listing.ListingId, out string bufferedPrice)
            ? bufferedPrice
            : (listing.BasePriceSilver > 0 ? listing.BasePriceSilver : listing.PriceSilver).ToString();
        Widgets.Label(new Rect(inner.xMax - 510f, inner.y + 7f, 42f, 24f), ClashOfRimText.Key(buyFromPlayer ? "ClashOfRim.Shop.DemandShort" : "ClashOfRim.Shop.StockShort"));
        adminShopStockBuffers[listing.ListingId] = Widgets.TextField(new Rect(inner.xMax - 468f, inner.y + 5f, 70f, 26f), stockText);
        Widgets.Label(new Rect(inner.xMax - 388f, inner.y + 7f, 42f, 24f), ClashOfRimText.Key(buyFromPlayer ? "ClashOfRim.Shop.PayoutShort" : "ClashOfRim.Shop.PriceShort"));
        adminShopPriceBuffers[listing.ListingId] = Widgets.TextField(new Rect(inner.xMax - 346f, inner.y + 5f, 70f, 26f), priceText);

        int stock = 0;
        int price = 0;
        bool parsed = item is not null
            && int.TryParse(adminShopStockBuffers[listing.ListingId], out stock)
            && int.TryParse(adminShopPriceBuffers[listing.ListingId], out price)
            && stock >= 0
            && price >= 1;
        if (Widgets.ButtonText(new Rect(inner.xMax - 266f, inner.y + 5f, 78f, 28f), ClashOfRimText.Key("ClashOfRim.Admin.Save"), active: parsed && !mod.ServerShopInProgress))
        {
            mod.StartUpsertServerShopListing(
                listing.ListingId,
                listing.ListingKind,
                item!,
                price,
                stock,
                listing.PriceIncreaseRatio,
                listing.QualityRequirementMode,
                listing.HitPointsRequirementMode);
        }

        if (Widgets.ButtonText(new Rect(inner.xMax - 182f, inner.y + 5f, 78f, 28f), ClashOfRimText.Key("ClashOfRim.Edit"), active: item is not null && !mod.ServerShopInProgress))
        {
            Find.WindowStack.Add(new ServerShopListingDialogWindow(mod, listing));
        }

        if (Widgets.ButtonText(new Rect(inner.xMax - 98f, inner.y + 5f, 88f, 28f), ClashOfRimText.Key("ClashOfRim.Admin.Remove"), active: !mod.ServerShopInProgress))
        {
            Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                ClashOfRimText.Key("ClashOfRim.Shop.ConfirmRemove", label.Named("THING")),
                () => mod.StartRemoveServerShopListing(listing)));
        }
    }

    private static bool IsAdminServerShopBuyOrder(ModServerShopListingDto listing)
    {
        return string.Equals(listing.ListingKind, "BuyFromPlayer", StringComparison.Ordinal);
    }

    private void DrawAdminBankPanel(Rect rect, ClashOfRimMod mod, ModAdminConfigurationDto? config)
    {
        if (config is null)
        {
            Widgets.Label(rect, ClashOfRimText.Key("ClashOfRim.Admin.NoStatus"));
            return;
        }

        Rect view = BeginAdminScrollWithSaveFooter(rect, 510f + adminBankOverduePenaltyStageRows.Count * 34f);
        float y = 0f;
        DrawAdminCheckboxRow(view, ref y, ClashOfRimText.Key("ClashOfRim.Admin.BankEnabled"), ref adminBankLoansEnabled);
        DrawAdminTextRow(view, ref y, ClashOfRimText.Key("ClashOfRim.Admin.BankMinLoanSilver"), ref adminBankMinLoanSilverText);
        DrawAdminTextRow(view, ref y, ClashOfRimText.Key("ClashOfRim.Admin.BankMaxLoanSilver"), ref adminBankMaxLoanSilverText);
        DrawAdminTextRow(view, ref y, ClashOfRimText.Key("ClashOfRim.Admin.BankLoanRatio"), ref adminBankLoanRatioText);
        DrawAdminTextRow(view, ref y, ClashOfRimText.Key("ClashOfRim.Admin.BankInterestRate"), ref adminBankInterestRateText);
        DrawAdminTextRow(view, ref y, ClashOfRimText.Key("ClashOfRim.Admin.BankMinDays"), ref adminBankMinDaysText);
        DrawAdminTextRow(view, ref y, ClashOfRimText.Key("ClashOfRim.Admin.BankMaxDays"), ref adminBankMaxDaysText);
        DrawAdminTextRow(view, ref y, ClashOfRimText.Key("ClashOfRim.Admin.BankInterestCurve"), ref adminBankInterestCurveText);
        DrawAdminTextRow(view, ref y, ClashOfRimText.Key("ClashOfRim.Admin.BankPenaltyInterval"), ref adminBankPenaltyIntervalText);
        DrawAdminTextRow(view, ref y, ClashOfRimText.Key("ClashOfRim.Admin.BankPenaltyPoints"), ref adminBankPenaltyPointsText);
        DrawAdminBankOverduePenaltyRows(view, ref y, mod);
        Widgets.EndScrollView();
        DrawAdminSaveFooter(rect, mod);
    }

    private void DrawAdminMercenaryPanel(Rect rect, ClashOfRimMod mod, ModAdminConfigurationDto? config)
    {
        if (config is null)
        {
            Widgets.Label(rect, ClashOfRimText.Key("ClashOfRim.Admin.NoStatus"));
            return;
        }

        Rect view = BeginAdminScrollWithSaveFooter(rect, 690f);
        float y = 0f;
        DrawAdminSectionTitle(view, ref y, ClashOfRimText.Key("ClashOfRim.Admin.SectionMercenaryContracts"));
        DrawAdminCheckboxRow(view, ref y, ClashOfRimText.Key("ClashOfRim.Admin.MercEnabled"), ref adminMercenariesEnabled);
        DrawAdminTextRow(view, ref y, ClashOfRimText.Key("ClashOfRim.Admin.MercApprentice"), ref adminMercApprenticeText);
        DrawAdminTextRow(view, ref y, ClashOfRimText.Key("ClashOfRim.Admin.MercSkilled"), ref adminMercSkilledText);
        DrawAdminTextRow(view, ref y, ClashOfRimText.Key("ClashOfRim.Admin.MercMaster"), ref adminMercMasterText);
        DrawAdminTextRow(view, ref y, ClashOfRimText.Key("ClashOfRim.Admin.MercMinDays"), ref adminMercMinDaysText);
        DrawAdminTextRow(view, ref y, ClashOfRimText.Key("ClashOfRim.Admin.MercMaxDays"), ref adminMercMaxDaysText);
        DrawAdminTextRow(view, ref y, ClashOfRimText.Key("ClashOfRim.Admin.MercDurationCurve"), ref adminMercDurationCurveText);
        DrawAdminTextRow(view, ref y, ClashOfRimText.Key("ClashOfRim.Admin.MercMaxActive"), ref adminMercMaxActiveText);
        DrawAdminTextRow(view, ref y, ClashOfRimText.Key("ClashOfRim.Admin.MercHarmfulSurgery"), ref adminMercHarmfulSurgeryText);
        DrawAdminTextRow(view, ref y, ClashOfRimText.Key("ClashOfRim.Admin.MercApprenticeDeath"), ref adminMercApprenticeDeathText);
        DrawAdminTextRow(view, ref y, ClashOfRimText.Key("ClashOfRim.Admin.MercSkilledDeath"), ref adminMercSkilledDeathText);
        DrawAdminTextRow(view, ref y, ClashOfRimText.Key("ClashOfRim.Admin.MercMasterDeath"), ref adminMercMasterDeathText);
        DrawAdminSectionTitle(view, ref y, ClashOfRimText.Key("ClashOfRim.Admin.SectionMercenaryGuards"));
        DrawAdminCheckboxRow(view, ref y, ClashOfRimText.Key("ClashOfRim.Admin.MercGuardEnabled"), ref adminMercGuardsEnabled);
        DrawAdminTextRow(view, ref y, ClashOfRimText.Key("ClashOfRim.Admin.MercGuardApprentice"), ref adminMercGuardApprenticeText);
        DrawAdminTextRow(view, ref y, ClashOfRimText.Key("ClashOfRim.Admin.MercGuardSkilled"), ref adminMercGuardSkilledText);
        DrawAdminTextRow(view, ref y, ClashOfRimText.Key("ClashOfRim.Admin.MercGuardMaster"), ref adminMercGuardMasterText);
        DrawAdminTextRow(view, ref y, ClashOfRimText.Key("ClashOfRim.Admin.MercGuardApprenticeRatio"), ref adminMercGuardApprenticeRatioText);
        DrawAdminTextRow(view, ref y, ClashOfRimText.Key("ClashOfRim.Admin.MercGuardSkilledRatio"), ref adminMercGuardSkilledRatioText);
        DrawAdminTextRow(view, ref y, ClashOfRimText.Key("ClashOfRim.Admin.MercGuardMasterRatio"), ref adminMercGuardMasterRatioText);
        Widgets.EndScrollView();
        DrawAdminSaveFooter(rect, mod);
    }

    private void DrawAdminManifestPanel(Rect rect, ClashOfRimMod mod, ModAdminConfigurationDto? config)
    {
        if (config is null)
        {
            Widgets.Label(rect, ClashOfRimText.Key("ClashOfRim.Admin.NoStatus"));
            return;
        }

        IReadOnlyList<ModAdminCompatibilityModDto> mods = config.CompatibilityMods;
        float height = 136f + mods.Sum(modEntry => 42f + Math.Max(1, modEntry.Configs.Count) * 32f);
        Rect scrollRect = AdminScrollRectWithSaveFooter(rect);
        Rect view = new(0f, 0f, scrollRect.width - 16f, Math.Max(scrollRect.height, height));
        Widgets.BeginScrollView(scrollRect, ref adminManifestScrollPosition, view);
        float y = 0f;
        DrawAdminSectionTitle(view, ref y, ClashOfRimText.Key("ClashOfRim.Admin.SectionManifest"));
        Rect overrideBaselineRect = new(0f, y, 160f, 30f);
        if (ClashOfRimUiUtility.DangerButton(
                overrideBaselineRect,
                ClashOfRimText.Key("ClashOfRim.Compatibility.OverrideBaseline"),
                ClashOfRimText.Key("ClashOfRim.Compatibility.OverrideBaselineDesc"),
                active: !mod.AdminInProgress))
        {
            OpenCompatibilityBaselineOverride(mod, config);
        }

        Rect updateWorldBaselineRect = new(170f, y, 160f, 30f);
        if (ClashOfRimUiUtility.DangerButton(
                updateWorldBaselineRect,
                ClashOfRimText.Key("ClashOfRim.Admin.UpdateWorldBaseline"),
                ClashOfRimText.Key("ClashOfRim.Admin.UpdateWorldBaselineDesc"),
                active: !mod.AdminInProgress))
        {
            mod.StartUpdateServerWorldBaseline();
        }

        y += 38f;

        if (mods.Count == 0)
        {
            Widgets.Label(new Rect(0f, y, view.width, 30f), ClashOfRimText.Key("ClashOfRim.Admin.NoManifest"));
            Widgets.EndScrollView();
            DrawAdminSaveFooter(rect, mod);
            return;
        }

        foreach (ModAdminCompatibilityModDto modEntry in mods.OrderBy(entry => entry.LoadOrder))
        {
            Rect modRow = new(0f, y, view.width, 34f);
            Widgets.DrawHighlightIfMouseover(modRow);
            Widgets.Label(new Rect(0f, y + 4f, view.width - 260f, 24f), $"{modEntry.LoadOrder + 1}. {DisplayAdminModName(modEntry)}");
            if (DrawAdminDropdownButton(
                    new Rect(view.width - 240f, y, 110f, 28f),
                    AdminModRoleLabel(modEntry.Role),
                    !mod.AdminInProgress))
            {
                OpenAdminModRoleMenu(modEntry);
            }

            Text.Font = GameFont.Tiny;
            Widgets.Label(new Rect(view.width - 124f, y + 5f, 120f, 20f), modEntry.PackageId);
            Text.Font = GameFont.Small;
            y += 38f;

            if (modEntry.Configs.Count == 0)
            {
                Text.Font = GameFont.Tiny;
                Widgets.Label(new Rect(18f, y, view.width - 18f, 22f), ClashOfRimText.Key("ClashOfRim.Admin.NoConfigFiles"));
                Text.Font = GameFont.Small;
                y += 28f;
                continue;
            }

            foreach (ModAdminCompatibilityConfigDto configEntry in modEntry.Configs.OrderBy(entry => entry.FileName, StringComparer.OrdinalIgnoreCase))
            {
                string configLabel = configEntry.FileName
                    + (configEntry.HasSavedFile ? string.Empty : " " + ClashOfRimText.Key("ClashOfRim.Admin.ConfigNotSaved"));
                Widgets.Label(new Rect(18f, y + 4f, view.width - 180f, 24f), configLabel);
                if (DrawAdminDropdownButton(
                        new Rect(view.width - 160f, y, 140f, 28f),
                        AdminConfigModeLabel(configEntry.Mode),
                        !mod.AdminInProgress))
                {
                    OpenAdminConfigModeMenu(configEntry);
                }

                y += 32f;
            }
        }

        Widgets.EndScrollView();
        DrawAdminSaveFooter(rect, mod);
    }

    private void DrawAdminRaidPanel(Rect rect, ClashOfRimMod mod, ModAdminConfigurationDto? config)
    {
        if (config is null)
        {
            Widgets.Label(rect, ClashOfRimText.Key("ClashOfRim.Admin.NoStatus"));
            return;
        }

        const float raidAdminContentHeight = (2f * 34f) + (13f * 32f) + 16f;
        Rect view = BeginAdminScrollWithSaveFooter(rect, raidAdminContentHeight);
        float y = 0f;
        DrawAdminSectionTitle(view, ref y, ClashOfRimText.Key("ClashOfRim.Admin.SectionRaid"));
        DrawAdminCheckboxRow(view, ref y, ClashOfRimText.Key("ClashOfRim.Admin.PvpEnabled"), ref adminPvpEnabled);
        DrawAdminTextRow(view, ref y, ClashOfRimText.Key("ClashOfRim.Admin.RaidProtectionHours"), ref adminRaidProtectionHoursText);
        DrawAdminTextRow(view, ref y, ClashOfRimText.Key("ClashOfRim.Admin.RaidMaxDurationMinutes"), ref adminRaidMaxDurationMinutesText);
        DrawAdminTextRow(view, ref y, ClashOfRimText.Key("ClashOfRim.Admin.RaidTimeoutGraceMinutes"), ref adminRaidTimeoutGraceMinutesText);
        DrawAdminTextRow(view, ref y, ClashOfRimText.Key("ClashOfRim.Admin.RaidMinimumDefenderWealth"), ref adminRaidMinimumDefenderWealthText);
        DrawAdminTextRow(view, ref y, ClashOfRimText.Key("ClashOfRim.Admin.RaidLossRatio"), ref adminRaidLossRatioText);
        DrawAdminTextRow(view, ref y, ClashOfRimText.Key("ClashOfRim.Admin.RaidBuildingHpLossRatio"), ref adminRaidBuildingHpLossRatioText);
        DrawAdminTextRow(view, ref y, ClashOfRimText.Key("ClashOfRim.Admin.RaidMinimumHpRatio"), ref adminRaidMinimumHpRatioText);
        DrawAdminTextRow(view, ref y, ClashOfRimText.Key("ClashOfRim.Admin.PendingConfirmationMinutes"), ref adminPendingConfirmationMinutesText);

        DrawAdminSectionTitle(view, ref y, ClashOfRimText.Key("ClashOfRim.Admin.SectionCooldown"));
        DrawAdminTextRow(view, ref y, ClashOfRimText.Key("ClashOfRim.Admin.RelationCooldownHours"), ref adminRelationCooldownHoursText);
        DrawAdminTextRow(view, ref y, ClashOfRimText.Key("ClashOfRim.Admin.SupportCooldownMinutes"), ref adminSupportCooldownMinutesText);
        DrawAdminCheckboxRow(view, ref y, ClashOfRimText.Key("ClashOfRim.Admin.GiftsEnabled"), ref adminGiftsEnabled);
        DrawAdminTextRow(view, ref y, ClashOfRimText.Key("ClashOfRim.Admin.ForcedGiftCooldownMinutes"), ref adminForcedGiftCooldownMinutesText);
        Widgets.EndScrollView();
        DrawAdminSaveFooter(rect, mod);
    }

    private void DrawAdminPlayersPanel(Rect rect, ClashOfRimMod mod, ModAdminStatusResponseDto? status)
    {
        IReadOnlyList<ModAdminPlayerSummaryDto> players = status?.Players ?? new List<ModAdminPlayerSummaryDto>();
        Rect view = BeginAdminScroll(rect, Math.Max(rect.height, players.Count * 70f + 20f));
        float y = 0f;
        if (players.Count == 0)
        {
            Widgets.Label(new Rect(0f, y, view.width, 32f), ClashOfRimText.Key("ClashOfRim.Admin.NoPlayers"));
        }

        foreach (ModAdminPlayerSummaryDto player in players)
        {
            Rect row = new(0f, y, view.width, 64f);
            Widgets.DrawMenuSection(row);
            Rect inner = row.ContractedBy(6f);
            string flags = (player.Online ? ClashOfRimText.Key("ClashOfRim.Multiplayer.Online") : ClashOfRimText.Key("ClashOfRim.Multiplayer.Offline"))
                + (player.IsAdministrator ? "  " + ClashOfRimText.Key("ClashOfRim.Admin.AdminFlag") : string.Empty)
                + (player.IsBanned ? "  " + ClashOfRimText.Key("ClashOfRim.Admin.BannedFlag") : string.Empty);
            string displayName = string.IsNullOrWhiteSpace(player.DisplayName) ? player.UserId : player.DisplayName!;
            Widgets.Label(new Rect(inner.x, inner.y, inner.width - 520f, 24f), displayName);
            Text.Font = GameFont.Tiny;
            Widgets.Label(new Rect(inner.x, inner.y + 26f, inner.width - 520f, 18f), flags + "  " + player.UserId);
            Text.Font = GameFont.Small;

            float x = inner.xMax - 150f;
            bool isCurrentPlayer = string.Equals(player.UserId, mod.CurrentUserId, StringComparison.Ordinal);
            if (isCurrentPlayer)
            {
                Text.Font = GameFont.Tiny;
                Widgets.Label(new Rect(x - 120f, inner.y + 14f, 260f, 24f), ClashOfRimText.Key("ClashOfRim.Multiplayer.CurrentPlayer"));
                Text.Font = GameFont.Small;
                y += 70f;
                continue;
            }

            if (DrawAdminDropdownButton(
                    new Rect(x, inner.y + 10f, 140f, 30f),
                    ClashOfRimText.Key("ClashOfRim.Admin.PlayerManage"),
                    !mod.AdminInProgress))
            {
                OpenAdminPlayerActionMenu(mod, player);
            }

            y += 70f;
        }

        Widgets.EndScrollView();
    }

    private void DrawAdminMaintenancePanel(Rect rect, ClashOfRimMod mod, ModAdminStatusResponseDto? status)
    {
        Rect view = BeginAdminScroll(rect, 300f);
        float y = 0f;
        DrawAdminSectionTitle(view, ref y, ClashOfRimText.Key("ClashOfRim.Admin.SectionBroadcast"));
        Widgets.Label(new Rect(0f, y, 160f, 24f), ClashOfRimText.Key("ClashOfRim.Admin.BroadcastMessage"));
        adminBroadcastMessage = Widgets.TextField(new Rect(170f, y, view.width - 430f, 28f), adminBroadcastMessage);
        if (DrawAdminDropdownButton(
                new Rect(view.width - 250f, y, 110f, 28f),
                AdminBroadcastSeverityLabel(adminBroadcastSeverity),
                !mod.AdminInProgress))
        {
            OpenAdminBroadcastSeverityMenu();
        }

        if (Widgets.ButtonText(new Rect(view.width - 120f, y, 110f, 28f), ClashOfRimText.Key("ClashOfRim.Admin.ActionBroadcast"), active: !mod.AdminInProgress))
        {
            string? targetUserId = string.IsNullOrWhiteSpace(adminBroadcastTargetUserId) ? null : adminBroadcastTargetUserId;
            string? targetColonyId = string.IsNullOrWhiteSpace(adminBroadcastTargetColonyId) ? null : adminBroadcastTargetColonyId;
            mod.StartAdminAction(
                "Broadcast",
                targetUserId: targetUserId,
                targetColonyId: targetColonyId,
                message: adminBroadcastMessage,
                notificationSeverity: adminBroadcastSeverity,
                persistentNotification: adminBroadcastPersistent);
        }

        y += 36f;
        Widgets.Label(new Rect(0f, y, 160f, 24f), ClashOfRimText.Key("ClashOfRim.Admin.BroadcastTarget"));
        if (DrawAdminDropdownButton(
                new Rect(170f, y, 220f, 28f),
                AdminBroadcastTargetLabel(status),
                !mod.AdminInProgress))
        {
            OpenAdminBroadcastTargetMenu(status);
        }

        Widgets.CheckboxLabeled(
            new Rect(410f, y, view.width - 410f, 28f),
            ClashOfRimText.Key("ClashOfRim.Admin.BroadcastPersistent"),
            ref adminBroadcastPersistent);

        y += 46f;
        DrawAdminSectionTitle(view, ref y, ClashOfRimText.Key("ClashOfRim.Admin.SectionMaintenance"));
        Widgets.Label(new Rect(0f, y, view.width, 24f), status?.MaintenanceLoginLocked == true
            ? ClashOfRimText.Key("ClashOfRim.Admin.MaintenanceLocked")
            : ClashOfRimText.Key("ClashOfRim.Admin.MaintenanceUnlocked"));
        y += 30f;
        Widgets.Label(new Rect(0f, y, 160f, 24f), ClashOfRimText.Key("ClashOfRim.Admin.MaintenanceReason"));
        adminMaintenanceReason = Widgets.TextField(new Rect(170f, y, view.width - 300f, 28f), adminMaintenanceReason);
        if (Widgets.ButtonText(new Rect(view.width - 120f, y, 110f, 28f), ClashOfRimText.Key("ClashOfRim.Admin.ActionLockLogin"), active: !mod.AdminInProgress))
        {
            mod.StartAdminAction("LockMaintenance", message: adminMaintenanceReason);
        }

        y += 38f;
        if (Widgets.ButtonText(new Rect(170f, y, 160f, 30f), ClashOfRimText.Key("ClashOfRim.Admin.ActionUnlockLogin"), active: !mod.AdminInProgress))
        {
            mod.StartAdminAction("UnlockMaintenance");
        }

        Widgets.EndScrollView();
    }

    private void OpenAdminBroadcastSeverityMenu()
    {
        Find.WindowStack.Add(new FloatMenu(new List<FloatMenuOption>
        {
            new(AdminBroadcastSeverityLabel("Info"), () => adminBroadcastSeverity = "Info"),
            new(AdminBroadcastSeverityLabel("Warning"), () => adminBroadcastSeverity = "Warning"),
            new(AdminBroadcastSeverityLabel("Critical"), () => adminBroadcastSeverity = "Critical")
        }));
    }

    private void OpenAdminBroadcastTargetMenu(ModAdminStatusResponseDto? status)
    {
        var options = new List<FloatMenuOption>
        {
            new(ClashOfRimText.Key("ClashOfRim.Admin.BroadcastTargetAll"), () =>
            {
                adminBroadcastTargetUserId = string.Empty;
                adminBroadcastTargetColonyId = string.Empty;
            })
        };

        foreach (ModAdminPlayerSummaryDto player in (status?.Players ?? new List<ModAdminPlayerSummaryDto>())
                     .OrderBy(AdminPlayerDisplayName, StringComparer.CurrentCultureIgnoreCase))
        {
            string userId = player.UserId;
            string colonyId = player.ColonyId;
            string label = AdminPlayerDisplayName(player);
            options.Add(new FloatMenuOption(label, () =>
            {
                adminBroadcastTargetUserId = userId;
                adminBroadcastTargetColonyId = colonyId;
            }));
        }

        Find.WindowStack.Add(new FloatMenu(options));
    }

    private string AdminBroadcastTargetLabel(ModAdminStatusResponseDto? status)
    {
        if (string.IsNullOrWhiteSpace(adminBroadcastTargetUserId))
        {
            return ClashOfRimText.Key("ClashOfRim.Admin.BroadcastTargetAll");
        }

        ModAdminPlayerSummaryDto? player = status?.Players?.FirstOrDefault(candidate =>
            string.Equals(candidate.UserId, adminBroadcastTargetUserId, StringComparison.Ordinal));
        return player is null ? adminBroadcastTargetUserId : AdminPlayerDisplayName(player);
    }

    private static string AdminPlayerDisplayName(ModAdminPlayerSummaryDto player)
    {
        return string.IsNullOrWhiteSpace(player.DisplayName) ? player.UserId : player.DisplayName!;
    }

    private static string AdminBroadcastSeverityLabel(string? severity)
    {
        return severity switch
        {
            "Warning" => ClashOfRimText.Key("ClashOfRim.Admin.BroadcastSeverityWarning"),
            "Critical" => ClashOfRimText.Key("ClashOfRim.Admin.BroadcastSeverityError"),
            _ => ClashOfRimText.Key("ClashOfRim.Admin.BroadcastSeverityInfo")
        };
    }

    private void DrawAdminAuditPanel(Rect rect, ModAdminStatusResponseDto? status)
    {
        IReadOnlyList<ModAdminAuditRecordDto> audits = status?.AuditRecords ?? new List<ModAdminAuditRecordDto>();
        Rect view = BeginAdminScroll(rect, Math.Max(rect.height, audits.Count * 42f + 20f));
        float y = 0f;
        foreach (ModAdminAuditRecordDto audit in audits)
        {
            Widgets.Label(new Rect(0f, y, view.width, 22f), $"{audit.CreatedAtUtc}  {audit.ActorUserId}  {audit.ActionKind}  {audit.TargetUserId}");
            if (!string.IsNullOrWhiteSpace(audit.Message))
            {
                Text.Font = GameFont.Tiny;
                Widgets.Label(new Rect(0f, y + 20f, view.width, 18f), audit.Message);
                Text.Font = GameFont.Small;
            }

            y += 42f;
        }

        if (audits.Count == 0)
        {
            Widgets.Label(new Rect(0f, 0f, view.width, 32f), ClashOfRimText.Key("ClashOfRim.Admin.NoAudit"));
        }

        Widgets.EndScrollView();
    }

    private Rect BeginAdminScroll(Rect rect, float minHeight)
    {
        Rect view = new(0f, 0f, rect.width - 16f, Math.Max(rect.height, minHeight));
        Widgets.BeginScrollView(rect, ref adminScrollPosition, view);
        return view;
    }

    private Rect BeginAdminScrollWithSaveFooter(Rect rect, float minHeight)
    {
        return BeginAdminScroll(AdminScrollRectWithSaveFooter(rect), minHeight);
    }

    private static Rect AdminScrollRectWithSaveFooter(Rect rect)
    {
        const float footerHeight = 44f;
        return new Rect(rect.x, rect.y, rect.width, Math.Max(0f, rect.height - footerHeight));
    }

    private static Rect AdminSaveFooterRect(Rect rect)
    {
        const float footerHeight = 44f;
        return new Rect(rect.x, rect.yMax - footerHeight + 6f, rect.width, footerHeight - 6f);
    }

    private static void DrawAdminSectionTitle(Rect view, ref float y, string title)
    {
        Text.Font = GameFont.Medium;
        Widgets.Label(new Rect(0f, y, view.width, 28f), title);
        Text.Font = GameFont.Small;
        y += 34f;
    }

    private static void DrawAdminTextRow(Rect view, ref float y, string label, ref string value)
    {
        Widgets.Label(new Rect(0f, y, 300f, 24f), label);
        value = Widgets.TextField(new Rect(310f, y, 180f, 26f), value);
        y += 32f;
    }

    private static void DrawAdminCheckboxRow(Rect view, ref float y, string label, ref bool value)
    {
        Widgets.CheckboxLabeled(new Rect(0f, y, Math.Min(view.width, 490f), 28f), label, ref value);
        y += 32f;
    }

    private static bool DrawAdminDropdownButton(Rect rect, string label, bool active = true)
    {
        return ClashOfRimUiUtility.DropdownButton(
            rect,
            label,
            ClashOfRimText.Key("ClashOfRim.Admin.OpenOptionsMenu"),
            active);
    }

    private void DrawAdminTradeFeeStrategyRow(Rect view, ref float y)
    {
        Widgets.Label(new Rect(0f, y, 300f, 24f), ClashOfRimText.Key("ClashOfRim.Admin.TradeFeeStrategy"));
        if (DrawAdminDropdownButton(
                new Rect(310f, y, 180f, 26f),
                AdminTradeFeeStrategyLabel(adminTradeFeeStrategy)))
        {
            OpenAdminTradeFeeStrategyMenu();
        }

        y += 32f;
    }

    private void DrawAdminFixedTradeFeeRows(Rect view, ref float y, ClashOfRimMod mod)
    {
        y += 6f;
        Widgets.Label(new Rect(0f, y, 300f, 24f), ClashOfRimText.Key("ClashOfRim.Admin.FixedTradeFees"));
        if (Widgets.ButtonText(new Rect(310f, y, 120f, 26f), ClashOfRimText.Key("ClashOfRim.Admin.AddFixedTradeFee"), active: !mod.AdminInProgress))
        {
            Find.WindowStack.Add(new AdminThingDefSelectionDialogWindow(def =>
            {
                adminFixedTradeFeeRows.Add(new AdminFixedTradeFeeRow(def.defName, "0"));
            }));
        }

        y += 32f;
        Text.Font = GameFont.Tiny;
        Widgets.Label(new Rect(20f, y, 300f, 20f), ClashOfRimText.Key("ClashOfRim.Admin.FixedTradeFeeThing"));
        Widgets.Label(new Rect(330f, y, 160f, 20f), ClashOfRimText.Key("ClashOfRim.Admin.FixedTradeFeeSilver"));
        Text.Font = GameFont.Small;
        y += 22f;

        for (int i = 0; i < adminFixedTradeFeeRows.Count; i++)
        {
            AdminFixedTradeFeeRow row = adminFixedTradeFeeRows[i];
            string thingLabel = AdminThingLabel(row.ThingDefName);
            if (Widgets.ButtonText(new Rect(20f, y, 300f, 26f), thingLabel, active: !mod.AdminInProgress))
            {
                AdminFixedTradeFeeRow capturedRow = row;
                Find.WindowStack.Add(new AdminThingDefSelectionDialogWindow(def =>
                {
                    capturedRow.ThingDefName = def.defName;
                }));
            }

            row.SilverPerUnit = Widgets.TextField(new Rect(330f, y, 120f, 26f), row.SilverPerUnit);
            if (Widgets.ButtonText(new Rect(470f, y, 80f, 26f), ClashOfRimText.Key("ClashOfRim.Admin.Remove"), active: !mod.AdminInProgress))
            {
                adminFixedTradeFeeRows.RemoveAt(i);
                i--;
                y += 30f;
                continue;
            }

            y += 30f;
        }

        y += 6f;
    }

    private void DrawAdminBankOverduePenaltyRows(Rect view, ref float y, ClashOfRimMod mod)
    {
        y += 6f;
        Widgets.Label(new Rect(0f, y, 300f, 24f), ClashOfRimText.Key("ClashOfRim.Admin.BankOverduePenaltyStages"));
        if (Widgets.ButtonText(new Rect(310f, y, 120f, 26f), ClashOfRimText.Key("ClashOfRim.Admin.AddBankPenaltyEvent"), active: !mod.AdminInProgress))
        {
            Find.WindowStack.Add(new AdminIncidentDefSelectionDialogWindow(def =>
            {
                adminBankOverduePenaltyStageRows.Add(new AdminBankOverduePenaltyStageRow("4", def.defName, "1"));
            }));
        }

        y += 32f;
        Text.Font = GameFont.Tiny;
        Widgets.Label(new Rect(20f, y, 100f, 20f), ClashOfRimText.Key("ClashOfRim.Admin.BankPenaltyStartCount"));
        Widgets.Label(new Rect(130f, y, 300f, 20f), ClashOfRimText.Key("ClashOfRim.Admin.BankPenaltyEvent"));
        Widgets.Label(new Rect(440f, y, 90f, 20f), ClashOfRimText.Key("ClashOfRim.Admin.BankPenaltySeverity"));
        Text.Font = GameFont.Small;
        y += 22f;

        for (int i = 0; i < adminBankOverduePenaltyStageRows.Count; i++)
        {
            AdminBankOverduePenaltyStageRow row = adminBankOverduePenaltyStageRows[i];
            row.TriggerPenaltyCount = Widgets.TextField(new Rect(20f, y, 100f, 26f), row.TriggerPenaltyCount);
            if (Widgets.ButtonText(new Rect(130f, y, 300f, 26f), AdminPenaltyEventLabel(row.Kind), active: !mod.AdminInProgress))
            {
                AdminBankOverduePenaltyStageRow capturedRow = row;
                Find.WindowStack.Add(new AdminIncidentDefSelectionDialogWindow(def =>
                {
                    capturedRow.Kind = def.defName;
                }));
            }

            row.Severity = Widgets.TextField(new Rect(440f, y, 80f, 26f), row.Severity);
            if (Widgets.ButtonText(new Rect(540f, y, 80f, 26f), ClashOfRimText.Key("ClashOfRim.Admin.Remove"), active: !mod.AdminInProgress))
            {
                adminBankOverduePenaltyStageRows.RemoveAt(i);
                i--;
                y += 30f;
                continue;
            }

            y += 30f;
        }

        y += 6f;
    }

    private void DrawAdminSaveFooter(Rect rect, ClashOfRimMod mod)
    {
        Rect footer = AdminSaveFooterRect(rect);
        bool parsed = TryBuildAdminConfiguration(out ModAdminConfigurationDto config);
        if (!parsed)
        {
            Widgets.Label(new Rect(footer.x, footer.y + 6f, footer.width - 170f, 30f), ClashOfRimText.Key("ClashOfRim.Admin.InvalidConfig"));
        }

        if (Widgets.ButtonText(new Rect(footer.xMax - 150f, footer.y + 2f, 140f, 30f), ClashOfRimText.Key("ClashOfRim.Admin.Save"), active: parsed && !mod.AdminInProgress))
        {
            mod.StartUpdateAdminConfiguration(config);
        }
    }

    private void OpenAdminPlayerActionMenu(ClashOfRimMod mod, ModAdminPlayerSummaryDto player)
    {
        var options = new List<FloatMenuOption>
        {
            BuildAdminPlayerActionOption(mod, "Kick", player, ClashOfRimText.Key("ClashOfRim.Admin.ActionKick")),
            BuildAdminPlayerActionOption(
                mod,
                player.IsBanned ? "Unban" : "Ban",
                player,
                player.IsBanned ? ClashOfRimText.Key("ClashOfRim.Admin.ActionUnban") : ClashOfRimText.Key("ClashOfRim.Admin.ActionBan")),
            BuildAdminPlayerActionOption(
                mod,
                player.IsAdministrator ? "RevokeAdmin" : "PromoteAdmin",
                player,
                player.IsAdministrator ? ClashOfRimText.Key("ClashOfRim.Admin.ActionRevokeAdmin") : ClashOfRimText.Key("ClashOfRim.Admin.ActionPromoteAdmin")),
            BuildAdminPlayerActionOption(
                mod,
                "DeletePlayerSave",
                player,
                ClashOfRimText.Key("ClashOfRim.Admin.ActionDeletePlayerSave"),
                ClashOfRimText.Key(
                    "ClashOfRim.Admin.ConfirmDeletePlayerSave",
                    player.UserId.Named("USER"))),
            new(ClashOfRimText.Key("ClashOfRim.Admin.ActionResetPassword"), () =>
            {
                Find.WindowStack.Add(new AdminResetPasswordDialogWindow(mod, player));
            })
        };
        Find.WindowStack.Add(new FloatMenu(options));
    }

    private static FloatMenuOption BuildAdminPlayerActionOption(
        ClashOfRimMod mod,
        string actionKind,
        ModAdminPlayerSummaryDto player,
        string label,
        string? confirmationText = null)
    {
        return new FloatMenuOption(label, () =>
        {
            Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                confirmationText ?? ClashOfRimText.Key("ClashOfRim.Admin.ConfirmAction", label.Named("ACTION"), player.UserId.Named("USER")),
                () => mod.StartAdminAction(actionKind, player.UserId, player.ColonyId)));
        });
    }

    private void EnsureAdminConfigFields(ModAdminConfigurationDto config)
    {
        string signature = string.Join("|",
            config.TradeMarketplaceEnabled,
            config.TradeOrderExpirationDays,
            config.MaxOpenTradeOrdersPerOwner,
            config.TradePostageBaseSilver,
            config.TradePostageSilverPerTile,
            config.TradePostageCrossLayerOverheadDistanceTiles,
            config.TradeBaseFeeRate,
            config.TradeFeeStrategy,
            config.DiplomacyRelationChangeCooldownHours,
            config.DiplomacySupportRequestCooldownMinutes,
            config.ForcedGiftDeliveryCooldownMinutes,
            config.GiftsEnabled,
            config.BankLoansEnabled,
            config.BankMinLoanSilver,
            config.BankMaxLoanSilver,
            config.BankMaxLoanWealthRatio,
            config.BankBaseAnnualInterestRate,
            config.BankMinDurationDays,
            config.BankMaxDurationDays,
            config.BankInterestDurationMultiplierCurve,
            config.BankPenaltyIntervalDays,
            config.BankPenaltyRaidPointsPerSilver,
            config.BankOverduePenaltyStages,
            config.MercenariesEnabled,
            config.MercenaryApprenticeDailySilver,
            config.MercenarySkilledDailySilver,
            config.MercenaryMasterDailySilver,
            config.MercenaryMinDurationDays,
            config.MercenaryMaxDurationDays,
            config.MercenaryDurationMultiplierCurve,
            config.MaxActiveMercenariesPerColony,
            config.MercenaryHarmfulSurgeryFineSilver,
            config.MercenaryApprenticeDeathFineSilver,
            config.MercenarySkilledDeathFineSilver,
            config.MercenaryMasterDeathFineSilver,
            config.MercenaryGuardsEnabled,
            config.MercenaryGuardApprenticeSilver,
            config.MercenaryGuardSkilledSilver,
            config.MercenaryGuardMasterSilver,
            config.MercenaryGuardApprenticePointsRatio,
            config.MercenaryGuardSkilledPointsRatio,
            config.MercenaryGuardMasterPointsRatio,
            config.PvpEnabled,
            config.RaidProtectionHours,
            config.RaidMaxDurationMinutes,
            config.RaidTimeoutGraceMinutes,
            config.RaidMinimumDefenderWealth,
            config.RaidSettlementLossRatio,
            config.RaidSettlementBuildingHitPointsLossRatio,
            config.RaidSettlementMinimumRemainingHitPointsRatio,
            config.PendingConfirmationTimeoutMinutes,
            string.Join(",", config.FixedTradeFees
                .OrderBy(entry => entry.ThingDefName, StringComparer.OrdinalIgnoreCase)
                .Select(entry => $"{entry.ThingDefName}:{entry.SilverPerUnit}")));
        if (string.Equals(adminConfigSignature, signature, StringComparison.Ordinal))
        {
            return;
        }

        adminConfigSignature = signature;
        adminTradeMarketplaceEnabled = config.TradeMarketplaceEnabled;
        adminTradeOrderExpirationDaysText = config.TradeOrderExpirationDays.ToString();
        adminMaxOpenTradeOrdersText = config.MaxOpenTradeOrdersPerOwner.ToString();
        adminPostageBaseText = config.TradePostageBaseSilver.ToString();
        adminPostagePerTileText = config.TradePostageSilverPerTile.ToString();
        adminPostageCrossLayerText = config.TradePostageCrossLayerOverheadDistanceTiles.ToString();
        adminTradeBaseFeeRateText = config.TradeBaseFeeRate.ToString("0.###");
        adminTradeFeeStrategy = NormalizeAdminTradeFeeStrategy(config.TradeFeeStrategy);
        adminRelationCooldownHoursText = config.DiplomacyRelationChangeCooldownHours.ToString("0.##");
        adminSupportCooldownMinutesText = config.DiplomacySupportRequestCooldownMinutes.ToString("0.##");
        adminForcedGiftCooldownMinutesText = config.ForcedGiftDeliveryCooldownMinutes.ToString("0.##");
        adminGiftsEnabled = config.GiftsEnabled;
        adminBankLoansEnabled = config.BankLoansEnabled;
        adminBankMinLoanSilverText = config.BankMinLoanSilver.ToString();
        adminBankMaxLoanSilverText = config.BankMaxLoanSilver.ToString();
        adminBankLoanRatioText = config.BankMaxLoanWealthRatio.ToString("0.###");
        adminBankInterestRateText = config.BankBaseAnnualInterestRate.ToString("0.###");
        adminBankMinDaysText = config.BankMinDurationDays.ToString();
        adminBankMaxDaysText = config.BankMaxDurationDays.ToString();
        adminBankInterestCurveText = config.BankInterestDurationMultiplierCurve;
        adminBankPenaltyIntervalText = config.BankPenaltyIntervalDays.ToString();
        adminBankPenaltyPointsText = config.BankPenaltyRaidPointsPerSilver.ToString("0.###");
        adminBankOverduePenaltyStageRows.Clear();
        adminBankOverduePenaltyStageRows.AddRange(ParseAdminBankOverduePenaltyStageRows(config.BankOverduePenaltyStages));
        adminMercenariesEnabled = config.MercenariesEnabled;
        adminMercApprenticeText = config.MercenaryApprenticeDailySilver.ToString();
        adminMercSkilledText = config.MercenarySkilledDailySilver.ToString();
        adminMercMasterText = config.MercenaryMasterDailySilver.ToString();
        adminMercMinDaysText = config.MercenaryMinDurationDays.ToString();
        adminMercMaxDaysText = config.MercenaryMaxDurationDays.ToString();
        adminMercDurationCurveText = config.MercenaryDurationMultiplierCurve;
        adminMercMaxActiveText = config.MaxActiveMercenariesPerColony.ToString();
        adminMercHarmfulSurgeryText = config.MercenaryHarmfulSurgeryFineSilver.ToString();
        adminMercApprenticeDeathText = config.MercenaryApprenticeDeathFineSilver.ToString();
        adminMercSkilledDeathText = config.MercenarySkilledDeathFineSilver.ToString();
        adminMercMasterDeathText = config.MercenaryMasterDeathFineSilver.ToString();
        adminMercGuardsEnabled = config.MercenaryGuardsEnabled;
        adminMercGuardApprenticeText = config.MercenaryGuardApprenticeSilver.ToString();
        adminMercGuardSkilledText = config.MercenaryGuardSkilledSilver.ToString();
        adminMercGuardMasterText = config.MercenaryGuardMasterSilver.ToString();
        adminMercGuardApprenticeRatioText = config.MercenaryGuardApprenticePointsRatio.ToString("0.###");
        adminMercGuardSkilledRatioText = config.MercenaryGuardSkilledPointsRatio.ToString("0.###");
        adminMercGuardMasterRatioText = config.MercenaryGuardMasterPointsRatio.ToString("0.###");
        adminPvpEnabled = config.PvpEnabled;
        adminRaidProtectionHoursText = config.RaidProtectionHours.ToString("0.##");
        adminRaidMaxDurationMinutesText = config.RaidMaxDurationMinutes.ToString("0.##");
        adminRaidTimeoutGraceMinutesText = config.RaidTimeoutGraceMinutes.ToString("0.##");
        adminRaidMinimumDefenderWealthText = config.RaidMinimumDefenderWealth.ToString();
        adminRaidLossRatioText = config.RaidSettlementLossRatio.ToString("0.###");
        adminRaidBuildingHpLossRatioText = config.RaidSettlementBuildingHitPointsLossRatio.ToString("0.###");
        adminRaidMinimumHpRatioText = config.RaidSettlementMinimumRemainingHitPointsRatio.ToString("0.###");
        adminPendingConfirmationMinutesText = config.PendingConfirmationTimeoutMinutes.ToString("0.##");
        adminFixedTradeFeeRows.Clear();
        adminFixedTradeFeeRows.AddRange(config.FixedTradeFees
            .OrderBy(entry => entry.ThingDefName, StringComparer.OrdinalIgnoreCase)
            .Select(entry => new AdminFixedTradeFeeRow(entry.ThingDefName, entry.SilverPerUnit.ToString())));
    }

    private bool TryBuildAdminConfiguration(out ModAdminConfigurationDto config)
    {
        config = new ModAdminConfigurationDto();
        if (!TryParseInt(adminTradeOrderExpirationDaysText, out int tradeOrderExpirationDays)
            || !TryParseInt(adminMaxOpenTradeOrdersText, out int maxOpenTradeOrders)
            || !TryParseInt(adminPostageBaseText, out int postageBase)
            || !TryParseInt(adminPostagePerTileText, out int postagePerTile)
            || !TryParseInt(adminPostageCrossLayerText, out int postageCrossLayer)
            || !TryParseFloat(adminTradeBaseFeeRateText, out float tradeBaseFeeRate)
            || !TryParseDouble(adminRelationCooldownHoursText, out double relationCooldownHours)
            || !TryParseDouble(adminSupportCooldownMinutesText, out double supportCooldownMinutes)
            || !TryParseDouble(adminForcedGiftCooldownMinutesText, out double forcedGiftCooldownMinutes)
            || !TryParseInt(adminBankMinLoanSilverText, out int bankMinLoanSilver)
            || !TryParseInt(adminBankMaxLoanSilverText, out int bankMaxLoanSilver)
            || !TryParseFloat(adminBankLoanRatioText, out float bankLoanRatio)
            || !TryParseFloat(adminBankInterestRateText, out float bankInterestRate)
            || !TryParseInt(adminBankMinDaysText, out int bankMinDays)
            || !TryParseInt(adminBankMaxDaysText, out int bankMaxDays)
            || !IsValidAdminCurveText(adminBankInterestCurveText)
            || !TryParseInt(adminBankPenaltyIntervalText, out int bankPenaltyInterval)
            || !TryParseFloat(adminBankPenaltyPointsText, out float bankPenaltyPoints)
            || !TryBuildAdminBankOverduePenaltyStages(out string bankOverduePenaltyStages)
            || !TryParseInt(adminMercApprenticeText, out int mercApprentice)
            || !TryParseInt(adminMercSkilledText, out int mercSkilled)
            || !TryParseInt(adminMercMasterText, out int mercMaster)
            || !TryParseInt(adminMercMinDaysText, out int mercMinDays)
            || !TryParseInt(adminMercMaxDaysText, out int mercMaxDays)
            || !IsValidAdminCurveText(adminMercDurationCurveText)
            || !TryParseInt(adminMercMaxActiveText, out int mercMaxActive)
            || !TryParseInt(adminMercHarmfulSurgeryText, out int mercHarmfulSurgery)
            || !TryParseInt(adminMercApprenticeDeathText, out int mercApprenticeDeath)
            || !TryParseInt(adminMercSkilledDeathText, out int mercSkilledDeath)
            || !TryParseInt(adminMercMasterDeathText, out int mercMasterDeath)
            || !TryParseInt(adminMercGuardApprenticeText, out int mercGuardApprentice)
            || !TryParseInt(adminMercGuardSkilledText, out int mercGuardSkilled)
            || !TryParseInt(adminMercGuardMasterText, out int mercGuardMaster)
            || !TryParseFloat(adminMercGuardApprenticeRatioText, out float mercGuardApprenticeRatio)
            || !TryParseFloat(adminMercGuardSkilledRatioText, out float mercGuardSkilledRatio)
            || !TryParseFloat(adminMercGuardMasterRatioText, out float mercGuardMasterRatio)
            || !TryParseDouble(adminRaidProtectionHoursText, out double raidProtectionHours)
            || !TryParseDouble(adminRaidMaxDurationMinutesText, out double raidMaxDurationMinutes)
            || !TryParseDouble(adminRaidTimeoutGraceMinutesText, out double raidTimeoutGraceMinutes)
            || !TryParseInt(adminRaidMinimumDefenderWealthText, out int raidMinimumDefenderWealth)
            || !TryParseDouble(adminRaidLossRatioText, out double raidLossRatio)
            || !TryParseDouble(adminRaidBuildingHpLossRatioText, out double raidBuildingHpLossRatio)
            || !TryParseDouble(adminRaidMinimumHpRatioText, out double raidMinimumHpRatio)
            || !TryParseDouble(adminPendingConfirmationMinutesText, out double pendingConfirmationMinutes)
            || !TryBuildFixedTradeFees(out List<ModAdminFixedTradeFeeDto> fixedTradeFees))
        {
            return false;
        }

        config.TradeMarketplaceEnabled = adminTradeMarketplaceEnabled;
        config.TradeOrderExpirationDays = tradeOrderExpirationDays;
        config.MaxOpenTradeOrdersPerOwner = maxOpenTradeOrders;
        config.TradePostageBaseSilver = postageBase;
        config.TradePostageSilverPerTile = postagePerTile;
        config.TradePostageCrossLayerOverheadDistanceTiles = postageCrossLayer;
        config.TradeBaseFeeRate = tradeBaseFeeRate;
        config.TradeFeeStrategy = NormalizeAdminTradeFeeStrategy(adminTradeFeeStrategy);
        config.DiplomacyRelationChangeCooldownHours = relationCooldownHours;
        config.DiplomacySupportRequestCooldownMinutes = supportCooldownMinutes;
        config.ForcedGiftDeliveryCooldownMinutes = forcedGiftCooldownMinutes;
        config.GiftsEnabled = adminGiftsEnabled;
        config.BankLoansEnabled = adminBankLoansEnabled;
        config.BankMinLoanSilver = bankMinLoanSilver;
        config.BankMaxLoanSilver = bankMaxLoanSilver;
        config.BankMaxLoanWealthRatio = bankLoanRatio;
        config.BankBaseAnnualInterestRate = bankInterestRate;
        config.BankMinDurationDays = bankMinDays;
        config.BankMaxDurationDays = bankMaxDays;
        config.BankInterestDurationMultiplierCurve = adminBankInterestCurveText.Trim();
        config.BankPenaltyIntervalDays = bankPenaltyInterval;
        config.BankPenaltyRaidPointsPerSilver = bankPenaltyPoints;
        config.BankOverduePenaltyStages = bankOverduePenaltyStages;
        config.MercenariesEnabled = adminMercenariesEnabled;
        config.MercenaryApprenticeDailySilver = mercApprentice;
        config.MercenarySkilledDailySilver = mercSkilled;
        config.MercenaryMasterDailySilver = mercMaster;
        config.MercenaryMinDurationDays = mercMinDays;
        config.MercenaryMaxDurationDays = mercMaxDays;
        config.MercenaryDurationMultiplierCurve = adminMercDurationCurveText.Trim();
        config.MaxActiveMercenariesPerColony = mercMaxActive;
        config.MercenaryHarmfulSurgeryFineSilver = mercHarmfulSurgery;
        config.MercenaryApprenticeDeathFineSilver = mercApprenticeDeath;
        config.MercenarySkilledDeathFineSilver = mercSkilledDeath;
        config.MercenaryMasterDeathFineSilver = mercMasterDeath;
        config.MercenaryGuardsEnabled = adminMercGuardsEnabled;
        config.MercenaryGuardApprenticeSilver = mercGuardApprentice;
        config.MercenaryGuardSkilledSilver = mercGuardSkilled;
        config.MercenaryGuardMasterSilver = mercGuardMaster;
        config.MercenaryGuardApprenticePointsRatio = mercGuardApprenticeRatio;
        config.MercenaryGuardSkilledPointsRatio = mercGuardSkilledRatio;
        config.MercenaryGuardMasterPointsRatio = mercGuardMasterRatio;
        config.PvpEnabled = adminPvpEnabled;
        config.RaidProtectionHours = raidProtectionHours;
        config.RaidMaxDurationMinutes = raidMaxDurationMinutes;
        config.RaidTimeoutGraceMinutes = raidTimeoutGraceMinutes;
        config.RaidMinimumDefenderWealth = raidMinimumDefenderWealth;
        config.RaidSettlementLossRatio = raidLossRatio;
        config.RaidSettlementBuildingHitPointsLossRatio = raidBuildingHpLossRatio;
        config.RaidSettlementMinimumRemainingHitPointsRatio = raidMinimumHpRatio;
        config.PendingConfirmationTimeoutMinutes = pendingConfirmationMinutes;
        config.FixedTradeFees = fixedTradeFees;
        config.CompatibilityMods = CloneAdminCompatibilityMods(
            LoadedModManager.GetMod<ClashOfRimMod>()?.AdminStatusSnapshot?.Configuration?.CompatibilityMods);
        return true;
    }

    private bool TryBuildFixedTradeFees(out List<ModAdminFixedTradeFeeDto> fixedTradeFees)
    {
        fixedTradeFees = new List<ModAdminFixedTradeFeeDto>();
        foreach (AdminFixedTradeFeeRow row in adminFixedTradeFeeRows)
        {
            string defName = row.ThingDefName.Trim();
            if (string.IsNullOrWhiteSpace(defName)
                && string.IsNullOrWhiteSpace(row.SilverPerUnit))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(defName)
                || !TryParseInt(row.SilverPerUnit, out int silverPerUnit))
            {
                return false;
            }

            fixedTradeFees.Add(new ModAdminFixedTradeFeeDto
            {
                ThingDefName = defName,
                SilverPerUnit = Math.Max(0, silverPerUnit)
            });
        }

        return true;
    }

    private bool TryBuildAdminBankOverduePenaltyStages(out string stagesText)
    {
        var parts = new List<string>();
        stagesText = string.Empty;
        foreach (AdminBankOverduePenaltyStageRow row in adminBankOverduePenaltyStageRows)
        {
            string kind = row.Kind.Trim();
            if (string.IsNullOrWhiteSpace(kind)
                && string.IsNullOrWhiteSpace(row.TriggerPenaltyCount)
                && string.IsNullOrWhiteSpace(row.Severity))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(kind)
                || !TryParseInt(row.TriggerPenaltyCount, out int triggerCount)
                || triggerCount <= 0
                || !TryParseFloat(row.Severity, out float severity)
                || severity < 0f
                || float.IsNaN(severity))
            {
                return false;
            }

            parts.Add(triggerCount.ToString(CultureInfo.InvariantCulture)
                + ":"
                + kind
                + ":"
                + severity.ToString("0.###", CultureInfo.InvariantCulture));
        }

        stagesText = string.Join(",", parts);
        return true;
    }

    private static IReadOnlyList<AdminBankOverduePenaltyStageRow> ParseAdminBankOverduePenaltyStageRows(string text)
    {
        var rows = new List<AdminBankOverduePenaltyStageRow>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return rows;
        }

        foreach (string part in text.Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string[] pieces = part.Trim().Split(':');
            if (pieces.Length < 2)
            {
                continue;
            }

            string trigger = pieces[0].Trim();
            string kind = pieces[1].Trim();
            string severity = pieces.Length >= 3 && !string.IsNullOrWhiteSpace(pieces[2])
                ? pieces[2].Trim()
                : "1";
            if (!string.IsNullOrWhiteSpace(trigger) && !string.IsNullOrWhiteSpace(kind))
            {
                rows.Add(new AdminBankOverduePenaltyStageRow(trigger, kind, severity));
            }
        }

        return rows;
    }

    private static List<ModAdminCompatibilityModDto> CloneAdminCompatibilityMods(IReadOnlyList<ModAdminCompatibilityModDto>? source)
    {
        return (source ?? new List<ModAdminCompatibilityModDto>())
            .Select(mod => new ModAdminCompatibilityModDto
            {
                PackageId = mod.PackageId,
                Name = mod.Name,
                LoadOrder = mod.LoadOrder,
                Role = mod.Role,
                Configs = mod.Configs
                    .Select(config => new ModAdminCompatibilityConfigDto
                    {
                        FileName = config.FileName,
                        Mode = config.Mode,
                        HasSavedFile = config.HasSavedFile
                    })
                    .ToList()
            })
            .ToList();
    }

    private static string DisplayAdminModName(ModAdminCompatibilityModDto mod)
    {
        return string.IsNullOrWhiteSpace(mod.Name) ? mod.PackageId : mod.Name;
    }

    private static string AdminModRoleLabel(string role)
    {
        if (string.Equals(role, "OptionalPureTranslation", StringComparison.OrdinalIgnoreCase))
        {
            return ClashOfRimText.Key("ClashOfRim.Admin.ModRoleTranslation");
        }

        return string.Equals(role, "Optional", StringComparison.OrdinalIgnoreCase)
            ? ClashOfRimText.Key("ClashOfRim.Admin.ModRoleOptional")
            : ClashOfRimText.Key("ClashOfRim.Admin.ModRoleRequired");
    }

    private static void OpenAdminModRoleMenu(ModAdminCompatibilityModDto mod)
    {
        var options = new List<FloatMenuOption>
        {
            new(ClashOfRimText.Key("ClashOfRim.Admin.ModRoleRequired"), () => mod.Role = "Required"),
            new(ClashOfRimText.Key("ClashOfRim.Admin.ModRoleOptional"), () => mod.Role = "Optional"),
            new(ClashOfRimText.Key("ClashOfRim.Admin.ModRoleTranslation"), () => mod.Role = "OptionalPureTranslation")
        };
        Find.WindowStack.Add(new FloatMenu(options));
    }

    private static string AdminConfigModeLabel(string mode)
    {
        return NormalizeAdminConfigMode(mode) switch
        {
            "Warn" => ClashOfRimText.Key("ClashOfRim.Admin.ConfigModeWarn"),
            "Ignore" => ClashOfRimText.Key("ClashOfRim.Admin.ConfigModeIgnore"),
            _ => ClashOfRimText.Key("ClashOfRim.Admin.ConfigModeEnforce")
        };
    }

    private static void OpenAdminConfigModeMenu(ModAdminCompatibilityConfigDto config)
    {
        var options = new List<FloatMenuOption>
        {
            new(ClashOfRimText.Key("ClashOfRim.Admin.ConfigModeEnforce"), () => config.Mode = "Enforce"),
            new(ClashOfRimText.Key("ClashOfRim.Admin.ConfigModeWarn"), () => config.Mode = "Warn"),
            new(ClashOfRimText.Key("ClashOfRim.Admin.ConfigModeIgnore"), () => config.Mode = "Ignore")
        };
        Find.WindowStack.Add(new FloatMenu(options));
    }

    private static string NormalizeAdminConfigMode(string mode)
    {
        if (string.Equals(mode, "Warn", StringComparison.OrdinalIgnoreCase))
        {
            return "Warn";
        }

        if (string.Equals(mode, "Ignore", StringComparison.OrdinalIgnoreCase))
        {
            return "Ignore";
        }

        return "Enforce";
    }

    private string AdminTradeFeeStrategyLabel(string strategy)
    {
        return NormalizeAdminTradeFeeStrategy(strategy) switch
        {
            "HighestSide" => ClashOfRimText.Key("ClashOfRim.Admin.TradeFeeStrategyHighestSide"),
            "SumBothSides" => ClashOfRimText.Key("ClashOfRim.Admin.TradeFeeStrategySumBothSides"),
            _ => ClashOfRimText.Key("ClashOfRim.Admin.TradeFeeStrategyPublisher")
        };
    }

    private void OpenAdminTradeFeeStrategyMenu()
    {
        var options = new List<FloatMenuOption>
        {
            new(ClashOfRimText.Key("ClashOfRim.Admin.TradeFeeStrategyPublisher"), () => adminTradeFeeStrategy = "Publisher"),
            new(ClashOfRimText.Key("ClashOfRim.Admin.TradeFeeStrategyHighestSide"), () => adminTradeFeeStrategy = "HighestSide"),
            new(ClashOfRimText.Key("ClashOfRim.Admin.TradeFeeStrategySumBothSides"), () => adminTradeFeeStrategy = "SumBothSides")
        };
        Find.WindowStack.Add(new FloatMenu(options));
    }

    private static string NormalizeAdminTradeFeeStrategy(string? strategy)
    {
        if (string.Equals(strategy, "HighestSide", StringComparison.OrdinalIgnoreCase))
        {
            return "HighestSide";
        }

        if (string.Equals(strategy, "SumBothSides", StringComparison.OrdinalIgnoreCase))
        {
            return "SumBothSides";
        }

        return "Publisher";
    }

    private static bool TryParseInt(string text, out int value)
    {
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryParseFloat(string text, out float value)
    {
        return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryParseDouble(string text, out double value)
    {
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static bool IsValidAdminCurveText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        foreach (string part in text.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string[] pieces = part.Split(new[] { ':', '=' }, StringSplitOptions.RemoveEmptyEntries);
            if (pieces.Length != 2
                || !int.TryParse(pieces[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int days)
                || days < 0
                || !float.TryParse(pieces[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float multiplier)
                || multiplier < 0f
                || float.IsNaN(multiplier))
            {
                return false;
            }
        }

        return true;
    }

    private static string AdminThingLabel(string defName)
    {
        ThingDef? def = DefDatabase<ThingDef>.GetNamedSilentFail(defName);
        if (def is null)
        {
            return string.IsNullOrWhiteSpace(defName)
                ? ClashOfRimText.Key("ClashOfRim.Admin.SelectThing")
                : defName;
        }

        return def.LabelCap.ToString();
    }

    private static string AdminPenaltyEventLabel(string kind)
    {
        if (string.Equals(kind, "PsychicWhisper", StringComparison.OrdinalIgnoreCase))
        {
            return ClashOfRimText.Key("ClashOfRim.Admin.BankPenaltyEventPsychicWhisper");
        }

        IncidentDef? def = DefDatabase<IncidentDef>.GetNamedSilentFail(kind);
        if (def is null)
        {
            return string.IsNullOrWhiteSpace(kind)
                ? ClashOfRimText.Key("ClashOfRim.Admin.SelectEvent")
                : kind;
        }

        return def.LabelCap.ToString();
    }

    private static void OpenCompatibilityBaselineOverride(ClashOfRimMod mod, ModAdminConfigurationDto config)
    {
        if (!AdminCompatibilityManifestHasModListOrOrderMismatch(config))
        {
            Find.WindowStack.Add(new CompatibilityBaselineOverrideWindow(mod));
            return;
        }

        Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
            ClashOfRimText.Key("ClashOfRim.Compatibility.Override.ModListWarning"),
            () => Find.WindowStack.Add(new CompatibilityBaselineOverrideWindow(mod))));
    }

    private static bool AdminCompatibilityManifestHasModListOrOrderMismatch(ModAdminConfigurationDto config)
    {
        try
        {
            List<string> serverIds = (config.CompatibilityMods ?? new List<ModAdminCompatibilityModDto>())
                .OrderBy(mod => mod.LoadOrder)
                .Select(mod => NormalizePackageId(mod.PackageId))
                .ToList();
            CompatibilityManifest localManifest = ClientCompatibilityManifestBuilder.Build();
            List<string> localIds = localManifest.Mods
                .OrderBy(mod => mod.LoadOrder)
                .Select(mod => NormalizePackageId(mod.PackageId))
                .ToList();
            return !serverIds.SequenceEqual(localIds, StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            Log.Warning("[ClashOfRim][Compatibility] Failed to compare admin override mod list: " + ex);
            return true;
        }
    }

    private static string NormalizePackageId(string? packageId)
    {
        if (string.IsNullOrWhiteSpace(packageId))
        {
            return string.Empty;
        }

        string value = packageId!.Trim();
        string postfix = ModMetaData.SteamModPostfix ?? string.Empty;
        return postfix.Length > 0 && value.EndsWith(postfix, StringComparison.OrdinalIgnoreCase)
            ? value.Substring(0, value.Length - postfix.Length).ToLowerInvariant()
            : value.ToLowerInvariant();
    }

    private sealed class AdminFixedTradeFeeRow
    {
        public AdminFixedTradeFeeRow(string thingDefName, string silverPerUnit)
        {
            ThingDefName = thingDefName;
            SilverPerUnit = silverPerUnit;
        }

        public string ThingDefName { get; set; }

        public string SilverPerUnit { get; set; }
    }

    private sealed class AdminBankOverduePenaltyStageRow
    {
        public AdminBankOverduePenaltyStageRow(string triggerPenaltyCount, string kind, string severity)
        {
            TriggerPenaltyCount = triggerPenaltyCount;
            Kind = kind;
            Severity = severity;
        }

        public string TriggerPenaltyCount { get; set; }

        public string Kind { get; set; }

        public string Severity { get; set; }
    }

    private sealed class AdminResetPasswordDialogWindow : Window
    {
        private readonly ClashOfRimMod mod;
        private readonly ModAdminPlayerSummaryDto player;
        private string newPassword = string.Empty;

        public AdminResetPasswordDialogWindow(ClashOfRimMod mod, ModAdminPlayerSummaryDto player)
        {
            this.mod = mod;
            this.player = player;
            doCloseX = true;
            closeOnClickedOutside = false;
            absorbInputAroundWindow = true;
            forcePause = false;
        }

        public override Vector2 InitialSize => new(460f, 190f);

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width, 30f), ClashOfRimText.Key("ClashOfRim.Admin.ActionResetPassword"));
            Text.Font = GameFont.Small;
            Widgets.Label(new Rect(inRect.x, inRect.y + 42f, inRect.width, 24f), ClashOfRimText.Key(
                "ClashOfRim.Admin.ResetPasswordTarget",
                player.UserId.Named("USER")));
            Widgets.Label(new Rect(inRect.x, inRect.y + 76f, 110f, 24f), ClashOfRimText.Key("ClashOfRim.Admin.NewPassword"));
            newPassword = GUI.PasswordField(new Rect(inRect.x + 120f, inRect.y + 74f, inRect.width - 120f, 28f), newPassword ?? string.Empty, '*');

            if (Widgets.ButtonText(new Rect(inRect.xMax - 204f, inRect.yMax - 36f, 96f, 32f), ClashOfRimText.Key("ClashOfRim.Cancel")))
            {
                Close();
            }

            if (Widgets.ButtonText(new Rect(inRect.xMax - 100f, inRect.yMax - 36f, 100f, 32f), ClashOfRimText.Key("ClashOfRim.Confirm")))
            {
                Confirm();
            }
        }

        public override void OnAcceptKeyPressed()
        {
            Confirm();
            Event.current?.Use();
        }

        private void Confirm()
        {
            mod.StartAdminAction("ResetOfflinePassword", player.UserId, player.ColonyId, message: newPassword ?? string.Empty);
            Close();
        }
    }
}
