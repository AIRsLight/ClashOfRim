using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.CompatibilityClient;
using AIRsLight.ClashOfRim.Modules.Multiplayer;
using UnityEngine;
using Verse;
using static AIRsLight.ClashOfRim.Multiplayer.Pages.MultiplayerAdminLayout;

namespace AIRsLight.ClashOfRim.Multiplayer.Pages;

internal sealed class MultiplayerAdminManifestPage
{
    private readonly ClientAdminManifestPanel model;
    private ClientAdminConfigurationDraft? displayed;
    private Vector2 scroll;
    internal MultiplayerAdminManifestPage(ClientAdminManifestPanel model) => this.model = model;
    internal void Close() { displayed = null; scroll = default; }
    internal void Draw(Rect rect)
    {
        var draft = model.Configuration.Read();
        if (!ReferenceEquals(displayed, draft)) { displayed = draft; scroll = default; }
        if (draft is null)
        {
            Widgets.Label(rect, ClashOfRimText.Key("ClashOfRim.Admin.NoStatus"));
            return;
        }

        IReadOnlyList<ModAdminCompatibilityModDto> mods = draft.CompatibilityMods;
        float height = 136f + mods.Sum(modEntry => 42f + Math.Max(1, modEntry.Configs.Count) * 32f);
        Rect scrollRect = AdminScrollRectWithSaveFooter(rect);
        Rect view = new(0f, 0f, scrollRect.width - 16f, Math.Max(scrollRect.height, height));
        Widgets.BeginScrollView(scrollRect, ref scroll, view);
        float y = 0f;
        DrawAdminSectionTitle(view, ref y, ClashOfRimText.Key("ClashOfRim.Admin.SectionManifest"));
        Rect overrideBaselineRect = new(0f, y, 160f, 30f);
        if (ClashOfRimUiUtility.DangerButton(
                overrideBaselineRect,
                ClashOfRimText.Key("ClashOfRim.Compatibility.OverrideBaseline"),
                ClashOfRimText.Key("ClashOfRim.Compatibility.OverrideBaselineDesc"),
                active: !model.Configuration.Busy))
        {
            OpenOverride(draft);
        }

        Rect updateWorldBaselineRect = new(170f, y, 160f, 30f);
        if (ClashOfRimUiUtility.DangerButton(
                updateWorldBaselineRect,
                ClashOfRimText.Key("ClashOfRim.Admin.UpdateWorldBaseline"),
                ClashOfRimText.Key("ClashOfRim.Admin.UpdateWorldBaselineDesc"),
                active: !model.Configuration.Busy))
        {
            _ = model.UpdateWorldAsync(draft);
        }

        y += 38f;

        if (mods.Count == 0)
        {
            Widgets.Label(new Rect(0f, y, view.width, 30f), ClashOfRimText.Key("ClashOfRim.Admin.NoManifest"));
            Widgets.EndScrollView();
            DrawAdminSaveFooter(rect, model.Configuration, draft);
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
                    !model.Configuration.Busy))
            {
                OpenAdminModRoleMenu(draft, modEntry);
            }

            Text.Font = GameFont.Tiny;
            ClashOfRimUiUtility.DrawTruncatedLabel(new Rect(view.width - 124f, y + 5f, 120f, 20f), modEntry.PackageId);
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
                        !model.Configuration.Busy))
                {
                    OpenAdminConfigModeMenu(draft, modEntry, configEntry);
                }

                y += 32f;
            }
        }

        Widgets.EndScrollView();
        DrawAdminSaveFooter(rect, model.Configuration, draft);
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
        var select = model.Configuration.SelectModRole(draft, mod);
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
        var select = model.Configuration.SelectConfigMode(draft, mod, config);
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

    private void OpenOverride(ClientAdminConfigurationDraft draft)
    {
        var intent = model.PrepareOverride(draft);
        if (intent is null) return;
        void Open()
        {
            if (intent.CanStart) Find.WindowStack.Add(new CompatibilityBaselineOverrideWindow(
                (progress, completion) => intent.StartAsync(progress, completion) ?? Task.CompletedTask));
        }
        if (intent.RequiresWarning)
            Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(ClashOfRimText.Key("ClashOfRim.Compatibility.Override.ModListWarning"), Open));
        else Open();
    }
}
