using System.Collections.Generic;
using AIRsLight.ClashOfRim.Modules.Multiplayer;
using UnityEngine;
using Verse;

namespace AIRsLight.ClashOfRim.Multiplayer.Pages;

internal sealed class MultiplayerAdminPage
{
    private readonly ClientAdminPanel model;
    private readonly ClientAdminConfigurationEditor configuration;
    private readonly MultiplayerAdminConfigurationPage configurationPage;
    private readonly MultiplayerAdminShopPage shop;
    private readonly MultiplayerAdminPlayersPage players;
    private readonly MultiplayerAdminMaintenancePage maintenance;
    private readonly MultiplayerAdminManifestPage manifest;
    private readonly MultiplayerAdminAuditPage audit;
    private readonly MultiplayerTabCache<ClientAdminTab> tabs = new();
    internal MultiplayerAdminPage(ClientAdminPanel model, ClientAdminConfigurationEditor configuration,
        MultiplayerAdminConfigurationPage configurationPage, MultiplayerAdminShopPage shop,
        MultiplayerAdminPlayersPage players, MultiplayerAdminMaintenancePage maintenance,
        MultiplayerAdminManifestPage manifest, MultiplayerAdminAuditPage audit)
    {
        this.model = model; this.configuration = configuration; this.configurationPage = configurationPage;
        this.shop = shop; this.players = players; this.maintenance = maintenance; this.manifest = manifest; this.audit = audit;
    }
    internal bool IsAvailable => model.IsAvailable;
    internal void Tick(float now) => _ = model.RefreshAsync(now);
    internal void RequestRefresh() => model.RequestRefresh();
    internal void Close()
    {
        model.Close(); tabs.Clear();
        configurationPage.Close(); shop.Close(); players.Close(); maintenance.Close(); manifest.Close(); audit.Close();
    }
    internal void Draw(Rect rect)
    {
        if (!model.IsAvailable) { Widgets.Label(rect, ClashOfRimText.Key("ClashOfRim.Admin.NoStatus")); return; }
        Widgets.DrawMenuSection(rect);
        Rect inner = rect.ContractedBy(10f);
        Text.Font = GameFont.Medium;
        Widgets.Label(new Rect(inner.x, inner.y, inner.width - 120f, 30f), ClashOfRimText.Key("ClashOfRim.Multiplayer.TabAdmin"));
        Text.Font = GameFont.Small;
        if (Widgets.ButtonText(new Rect(inner.xMax - 110f, inner.y, 110f, 30f), ClashOfRimText.Key("ClashOfRim.Refresh"), active: model.CanRefresh))
            _ = model.RefreshAsync(Time.realtimeSinceStartup, manual: true);

        const float headerHeight = 40f, tabHeight = 34f;
        Rect tabContent = new(inner.x, inner.y + headerHeight + tabHeight, inner.width, inner.height - headerHeight - tabHeight);
        TabDrawer.DrawTabs(tabContent, tabs.Read(model.Ownership, model.Selected, LanguageDatabase.activeLanguage, BuildTabs));
        Rect content = tabContent.ContractedBy(8f);
        Widgets.Label(new Rect(content.x, content.y, content.width, 24f), model.Status);
        var draft = configuration.Read();
        Rect body = new(content.x, content.y + 32f, content.width, content.height - 32f);
        switch (model.Selected)
        {
            case ClientAdminTab.Shop: shop.Draw(body); break;
            case ClientAdminTab.Bank: configurationPage.DrawAdminBankPanel(body, draft); break;
            case ClientAdminTab.Mercenary: configurationPage.DrawAdminMercenaryPanel(body, draft); break;
            case ClientAdminTab.Raid: configurationPage.DrawAdminRaidPanel(body, draft); break;
            case ClientAdminTab.Players: players.Draw(body); break;
            case ClientAdminTab.Maintenance: maintenance.Draw(body); break;
            case ClientAdminTab.Manifest: manifest.Draw(body); break;
            case ClientAdminTab.Audit: audit.Draw(body); break;
            default: configurationPage.DrawAdminTradePanel(body, draft); break;
        }
    }
    private List<TabRecord> BuildTabs() => new()
    {
        Tab(ClientAdminTab.Trade, "ClashOfRim.Admin.TabTrade"),
        Tab(ClientAdminTab.Shop, "ClashOfRim.Admin.TabShop"),
        Tab(ClientAdminTab.Bank, "ClashOfRim.Admin.TabBank"),
        Tab(ClientAdminTab.Mercenary, "ClashOfRim.Admin.TabMercenary"),
        Tab(ClientAdminTab.Raid, "ClashOfRim.Admin.TabRaid"),
        Tab(ClientAdminTab.Players, "ClashOfRim.Admin.TabPlayers"),
        Tab(ClientAdminTab.Maintenance, "ClashOfRim.Admin.TabMaintenance"),
        Tab(ClientAdminTab.Manifest, "ClashOfRim.Admin.TabManifest"),
        Tab(ClientAdminTab.Audit, "ClashOfRim.Admin.TabAudit")
    };
    private TabRecord Tab(ClientAdminTab tab, string key) => new(ClashOfRimText.Key(key), model.Select(tab), model.Selected == tab);
}
