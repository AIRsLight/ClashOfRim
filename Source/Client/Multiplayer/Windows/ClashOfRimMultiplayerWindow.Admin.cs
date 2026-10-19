using AIRsLight.ClashOfRim.Modules.Multiplayer;
using static AIRsLight.ClashOfRim.Multiplayer.Pages.MultiplayerAdminLayout;
using System;
using System.Collections.Generic;
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
                DrawAdminMaintenancePanel(body, mod, status);
                break;
            case AdminPanelTab.Manifest:
                DrawAdminManifestPanel(body, mod, status?.Configuration, draft);
                break;
            case AdminPanelTab.Audit:
                DrawAdminAuditPanel(body, status);
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

    private void DrawAdminManifestPanel(Rect rect, ClashOfRimMod mod, ModAdminConfigurationDto? config, ClientAdminConfigurationDraft? draft)
    {
        if (config is null || draft is null)
        {
            Widgets.Label(rect, ClashOfRimText.Key("ClashOfRim.Admin.NoStatus"));
            return;
        }

        IReadOnlyList<ModAdminCompatibilityModDto> mods = draft.CompatibilityMods;
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
            adminConfigurationPage!.DrawAdminSaveFooter(rect, draft);
            return;
        }

        foreach (ModAdminCompatibilityModDto modEntry in mods.OrderBy(entry => entry.LoadOrder))
        {
            Rect modRow = new(0f, y, view.width, 34f);
            Widgets.DrawHighlightIfMouseover(modRow);
            Widgets.Label(new Rect(0f, y + 4f, view.width - 260f, 24f), $"{modEntry.LoadOrder + 1}. {DisplayAdminModName(modEntry)}");
            if (DrawAdminSelectionButton(
                    new Rect(view.width - 240f, y, 110f, 28f),
                    AdminModRoleLabel(modEntry.Role),
                    !mod.AdminInProgress))
            {
                OpenAdminModRoleMenu(draft, modEntry);
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
                if (DrawAdminSelectionButton(
                        new Rect(view.width - 160f, y, 140f, 28f),
                        AdminConfigModeLabel(configEntry.Mode),
                        !mod.AdminInProgress))
                {
                    OpenAdminConfigModeMenu(draft, modEntry, configEntry);
                }

                y += 32f;
            }
        }

        Widgets.EndScrollView();
        adminConfigurationPage!.DrawAdminSaveFooter(rect, draft);
    }

    private void DrawAdminMaintenancePanel(Rect rect, ClashOfRimMod mod, ModAdminStatusResponseDto? status)
    {
        Rect view = BeginAdminScroll(rect, ref adminScrollPosition, 300f);
        float y = 0f;
        DrawAdminSectionTitle(view, ref y, ClashOfRimText.Key("ClashOfRim.Admin.SectionBroadcast"));
        Widgets.Label(new Rect(0f, y, 160f, 24f), ClashOfRimText.Key("ClashOfRim.Admin.BroadcastMessage"));
        adminBroadcastMessage = Widgets.TextField(new Rect(170f, y, view.width - 430f, 28f), adminBroadcastMessage);
        if (DrawAdminSelectionButton(
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
        if (DrawAdminSelectionButton(
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
        Rect view = BeginAdminScroll(rect, ref adminScrollPosition, Math.Max(rect.height, audits.Count * 42f + 20f));
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

    private void OpenAdminModRoleMenu(ClientAdminConfigurationDraft draft, ModAdminCompatibilityModDto mod)
    {
        var select = adminConfiguration!.SelectModRole(draft, mod);
        var options = new List<FloatMenuOption>
        {
            new(ClashOfRimText.Key("ClashOfRim.Admin.ModRoleRequired"), () => select("Required")),
            new(ClashOfRimText.Key("ClashOfRim.Admin.ModRoleOptional"), () => select("Optional")),
            new(ClashOfRimText.Key("ClashOfRim.Admin.ModRoleTranslation"), () => select("OptionalPureTranslation"))
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

    private void OpenAdminConfigModeMenu(ClientAdminConfigurationDraft draft, ModAdminCompatibilityModDto mod, ModAdminCompatibilityConfigDto config)
    {
        var select = adminConfiguration!.SelectConfigMode(draft, mod, config);
        var options = new List<FloatMenuOption>
        {
            new(ClashOfRimText.Key("ClashOfRim.Admin.ConfigModeEnforce"), () => select("Enforce")),
            new(ClashOfRimText.Key("ClashOfRim.Admin.ConfigModeWarn"), () => select("Warn")),
            new(ClashOfRimText.Key("ClashOfRim.Admin.ConfigModeIgnore"), () => select("Ignore"))
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

}
