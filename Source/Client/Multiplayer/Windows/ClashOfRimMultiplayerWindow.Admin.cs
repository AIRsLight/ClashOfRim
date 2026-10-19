using System.Collections.Generic;
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

        var draft = adminConfiguration!.Read();

        Rect body = new(content.x, content.y + 32f, content.width, content.height - 32f);
        switch (selectedAdminPanelTab)
        {
            case AdminPanelTab.Shop:
                adminShopPage!.Draw(body);
                break;
            case AdminPanelTab.Bank:
                adminConfigurationPage!.DrawAdminBankPanel(body, draft);
                break;
            case AdminPanelTab.Mercenary:
                adminConfigurationPage!.DrawAdminMercenaryPanel(body, draft);
                break;
            case AdminPanelTab.Raid:
                adminConfigurationPage!.DrawAdminRaidPanel(body, draft);
                break;
            case AdminPanelTab.Players:
                adminPlayersPage!.Draw(body);
                break;
            case AdminPanelTab.Maintenance:
                adminMaintenancePage!.Draw(body);
                break;
            case AdminPanelTab.Manifest:
                adminManifestPage!.Draw(body);
                break;
            case AdminPanelTab.Audit:
                adminAuditPage!.Draw(body);
                break;
            default:
                adminConfigurationPage!.DrawAdminTradePanel(body, draft);
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

}
