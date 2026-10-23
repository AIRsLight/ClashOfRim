using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.Modules.Compatibility;
using static AIRsLight.ClashOfRim.Modules.Compatibility.ClientCompatibilityReport;
using RimWorld;
using UnityEngine;
using Verse;

namespace AIRsLight.ClashOfRim.CompatibilityClient;

internal sealed class CompatibilityMismatchWindow : Window
{
    private readonly ClientCompatibilityMismatch model;
    private ClientCompatibilityReport Report => model.Report!;
    private CompatibilityTab selectedTab = CompatibilityTab.Overview;
    private Vector2 manifestScroll, hashScroll, configScroll, serverModScroll, localModScroll, protocolScroll;
    internal bool IsCurrent => model.IsCurrent;
    internal bool CanContinue => model.CanContinue;
    internal bool TryContinue() => model.Continue(() => Close());
    internal CompatibilityMismatchWindow(ClientCompatibilityMismatch model)
    {
        this.model = model;
        doCloseX = true; closeOnClickedOutside = false; absorbInputAroundWindow = true; forcePause = false;
    }
    public override Vector2 InitialSize => new(720f, 500f);

    public override void DoWindowContents(Rect inRect)
    {
        if (!model.IsCurrent) { Close(); return; }
        Text.Font = GameFont.Small;

        Rect tabsRect = new(inRect.x, inRect.y + 36f, inRect.width, inRect.height - 108f);
        TabDrawer.DrawTabs(tabsRect, new List<TabRecord>
        {
            new(T("ClashOfRim.Compatibility.TabOverview"), () => selectedTab = CompatibilityTab.Overview, selectedTab == CompatibilityTab.Overview),
            new(T("ClashOfRim.Compatibility.TabManifest", Report.ManifestEntries.Count.Named("COUNT")), () => selectedTab = CompatibilityTab.Manifest, selectedTab == CompatibilityTab.Manifest),
            new(T("ClashOfRim.Compatibility.TabHash", Report.HashEntries.Count.Named("COUNT")), () => selectedTab = CompatibilityTab.Hash, selectedTab == CompatibilityTab.Hash),
            new(T("ClashOfRim.Compatibility.TabConfig", Report.ConfigEntries.Count.Named("COUNT")), () => selectedTab = CompatibilityTab.Config, selectedTab == CompatibilityTab.Config)
        });

        GUI.BeginGroup(tabsRect);
        {
            Rect tabInner = new(0f, 8f, tabsRect.width, tabsRect.height - 8f);
            switch (selectedTab)
            {
                case CompatibilityTab.Overview:
                    DrawOverviewTab(tabInner, Report.ManifestEntries.Count, Report.HashEntries.Count, Report.ConfigEntries.Count);
                    break;
                case CompatibilityTab.Manifest:
                    DrawManifestTab(tabInner);
                    break;
                case CompatibilityTab.Hash:
                    DrawEntryTab(tabInner, Report.HashEntries, ref hashScroll, T("ClashOfRim.Compatibility.FilesMatch"));
                    break;
                case CompatibilityTab.Config:
                    DrawEntryTab(tabInner, Report.ConfigEntries, ref configScroll, T("ClashOfRim.Compatibility.ConfigsMatch"));
                    break;
            }
        }
        GUI.EndGroup();

        Widgets.Label(new Rect(inRect.x, inRect.yMax - 76f, inRect.width, 24f), model.Status);

        Rect closeRect = new(inRect.xMax - 80f, inRect.yMax - 36f, 80f, 32f);
        if (Widgets.ButtonText(closeRect, T("ClashOfRim.Close")))
        {
            Close();
        }

        Rect previousButtonRect = closeRect;
        if (model.CanContinue)
        {
            Rect continueRect = new(closeRect.x - 124f, inRect.yMax - 36f, 112f, 32f);
            previousButtonRect = continueRect;
            if (Widgets.ButtonText(continueRect, T("ClashOfRim.Compatibility.JoinAnyway")))
            {
                TryContinue();
            }
        }

        if (model.CanApplyConfig)
        {
            Rect localConfigRect = new(closeRect.x - 184f, inRect.yMax - 36f, 172f, 32f);
            if (ClashOfRimUiUtility.DangerButton(
                    localConfigRect,
                    T("ClashOfRim.Compatibility.ConfigOverwriteLocalAndRestart"),
                    T("ClashOfRim.Compatibility.ConfigOverwriteLocalDesc")))
            {
                model.Apply(ClientCompatibilityRepair.LocalConfigs);
            }
            Rect overlayConfigRect = new(localConfigRect.x - 184f, inRect.yMax - 36f, 172f, 32f);
            previousButtonRect = overlayConfigRect;
            if (Widgets.ButtonText(overlayConfigRect, T("ClashOfRim.Compatibility.ConfigApplyOverlayAndRestart")))
            {
                model.Apply(ClientCompatibilityRepair.ConfigOverlay);
            }
            TooltipHandler.TipRegion(overlayConfigRect, T("ClashOfRim.Compatibility.ConfigApplyOverlayDesc"));
        }

        if (model.CanApplyManifest)
        {
            Rect applyRect = new(previousButtonRect.x - 124f, inRect.yMax - 36f, 112f, 32f);
            previousButtonRect = applyRect;
            if (Widgets.ButtonText(applyRect, T("ClashOfRim.Compatibility.ApplyAndRestart")))
            {
                model.Apply(ClientCompatibilityRepair.Manifest);
            }
            TooltipHandler.TipRegion(applyRect, T("ClashOfRim.Compatibility.ApplyAndRestartDesc"));
        }

        if (model.CanOverride)
        {
            Rect overrideRect = new(previousButtonRect.x - 124f, inRect.yMax - 36f, 112f, 32f);
            if (ClashOfRimUiUtility.DangerButton(
                    overrideRect,
                    T("ClashOfRim.Compatibility.OverrideBaseline"),
                    T("ClashOfRim.Compatibility.OverrideBaselineDesc")))
            {
                OpenOverrideBaselineWithWarning();
            }
        }
    }

    public override void PostClose()
    {
        model.Close();
        base.PostClose();
    }
    private void OpenOverrideBaselineWithWarning()
    {
        var intent = model.PrepareOverride();
        if (intent is null) return;
        void Start()
        {
            if (!intent.CanStart) return;
            Find.WindowStack.Add(new CompatibilityBaselineOverrideWindow((progress, completion) => intent.StartAsync(progress, completion) ?? Task.CompletedTask));
            Close();
        }
        if (intent.RequiresWarning)
            Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(T("ClashOfRim.Compatibility.Override.ModListWarning"), Start));
        else Start();
    }

    private string BuildIntro(int manifestCount, int hashCount, int configCount)
    {
        string summary = FormatSummary(manifestCount, hashCount, configCount);
        if (model.CanContinue)
        {
            return summary;
        }

        if (Report.RequiresManualUpdate)
            summary += T("ClashOfRim.Compatibility.SummaryFilesManual");

        if (model.CanOverride)
        {
            return summary + T("ClashOfRim.Compatibility.SummaryAdminCanOverride");
        }

        if (Report.RequiresManualUpdate) return summary;

        return model.CanApplyManifest
            ? summary + T("ClashOfRim.Compatibility.SummaryApplyServer")
            : model.CanApplyConfig
            ? summary + T("ClashOfRim.Compatibility.ConfigApplyAvailable")
            : summary;
    }

    private void DrawOverviewTab(Rect inRect, int manifestCount, int hashCount, int configCount)
    {
        if (Report.ProtocolMismatch)
        {
            string details = T("ClashOfRim.Compatibility.ProtocolUpdateRequired") + "\n\n"
                + T("ClashOfRim.Compatibility.ProtocolVersionLine", Display(Report.ServerProtocol).Named("SERVER"), Report.LocalProtocol.Named("LOCAL"))
                + "\n\n" + Report.ProtocolMessage
                + (Report.HasServerManifest ? "" : "\n\n" + T("ClashOfRim.Compatibility.ManifestNotAvailable"));
            Rect area = inRect.ContractedBy(8f);
            Rect content = new(0f, 0f, area.width - 16f, Mathf.Max(area.height, Text.CalcHeight(details, area.width - 16f)));
            Widgets.BeginScrollView(area, ref protocolScroll, content);
            Widgets.Label(content, details);
            Widgets.EndScrollView();
            return;
        }
        Rect contentRect = inRect.ContractedBy(8f);
        float y = contentRect.y + 8f;
        Widgets.Label(
            new Rect(contentRect.x, y, contentRect.width, 28f),
            FormatSummary(manifestCount, hashCount, configCount));
        y += 34f;

        Widgets.Label(new Rect(contentRect.x, y, contentRect.width, 54f), BuildIntro(manifestCount, hashCount, configCount));
        y += 66f;

        Widgets.DrawBox(new Rect(contentRect.x, y, contentRect.width, 134f));
        Rect detailRect = new(contentRect.x + 12f, y + 10f, contentRect.width - 24f, 116f);
        string serverId = ShortHash(Display(Report.ServerId));
        string localId = ShortHash(Display(Report.LocalId));
        string serverVersion = Display(Report.ServerVersion);
        string localVersion = Display(Report.LocalVersion);
        string serverLanguage = Report.ServerLanguage;
        if (string.IsNullOrWhiteSpace(serverLanguage))
        {
            serverLanguage = None();
        }
        string localLanguage = Display(Report.LocalLanguage);
        string serverProtocol = Display(Report.ServerProtocol);
        string localProtocol = Report.LocalProtocol;
        string protocolLine = T(
            "ClashOfRim.Compatibility.ProtocolVersionLine",
            serverProtocol.Named("SERVER"),
            localProtocol.Named("LOCAL"));
        string languageLine = T(
            "ClashOfRim.Compatibility.GameLanguageLine",
            serverLanguage.Named("SERVER"),
            localLanguage.Named("LOCAL"));
        if (Report.LanguageMismatch)
        {
            languageLine = "<color=#ffd166>" + languageLine + "</color>";
        }
        Widgets.Label(
            detailRect,
            T("ClashOfRim.Compatibility.ServerManifestLine", serverId.Named("ID"))
            + "\n" + T("ClashOfRim.Compatibility.LocalManifestLine", localId.Named("ID"))
            + "\nRimWorld: " + serverVersion + " / " + localVersion
            + "\n" + protocolLine
            + "\n" + languageLine);
        y += 150f;

        Widgets.Label(
            new Rect(contentRect.x, y, contentRect.width, 80f),
            Report.RequiresManualUpdate
                ? T("ClashOfRim.Compatibility.FileMismatchCannotApply")
                : model.CanApplyManifest
                ? T("ClashOfRim.Compatibility.ManifestApplyAllowed")
                : model.CanApplyConfig
                ? T("ClashOfRim.Compatibility.ConfigApplyAvailable")
                : string.Empty);
    }

    private void DrawManifestTab(Rect inRect)
    {
        if (!Report.HasServerManifest)
        {
            DrawEntryTab(inRect, Report.ManifestEntries, ref manifestScroll, T("ClashOfRim.Compatibility.ManifestNotAvailable"));
            return;
        }
        const float rowHeight = 22f;
        const float listHeight = 165f;
        const float gap = 16f;
        float columnWidth = (inRect.width - 24f - gap) / 2f;
        Rect serverRect = new(8f, 24f, columnWidth, listHeight);
        Rect localRect = new(serverRect.xMax + gap, 24f, columnWidth, listHeight);

        DrawModColumn(serverRect, T("ClashOfRim.Compatibility.ServerColumn"), Report.ServerMods, ref serverModScroll);
        DrawModColumn(localRect, T("ClashOfRim.Compatibility.LocalColumn"), Report.LocalMods, ref localModScroll);

        Rect summaryRect = new(8f, serverRect.yMax + 8f, inRect.width - 16f, 24f);
        Widgets.Label(summaryRect, BuildManifestSummaryText());

        Rect detailRect = new(0f, summaryRect.yMax + 4f, inRect.width, inRect.height - summaryRect.yMax - 4f);
        DrawEntryTab(detailRect, Report.ManifestEntries, ref manifestScroll, T("ClashOfRim.Compatibility.ModsMatch"), showWorkshopButtons: true);

        static void DrawModColumn(Rect rect, string title, IReadOnlyList<ModRow> mods, ref Vector2 scroll)
        {
            Widgets.Label(new Rect(rect.x, rect.y - 20f, rect.width, 20f), title);
            Widgets.DrawBox(rect);
            Rect view = new(0f, 0f, rect.width - 16f, Math.Max(rect.height, mods.Count * rowHeight));
            Widgets.BeginScrollView(rect, ref scroll, view);
            for (int i = 0; i < mods.Count; i++)
            {
                ModRow mod = mods[i];
                Rect row = new(0f, i * rowHeight, view.width, rowHeight);
                if (Mouse.IsOver(row))
                {
                    Widgets.DrawHighlight(row);
                }

                Color color = StatusColor(mod.Status);
                Color previous = GUI.color;
                GUI.color = color;
                Widgets.Label(row, $"{i + 1}. {mod.Name}");
                GUI.color = previous;
                TooltipHandler.TipRegion(row, mod.PackageId);
            }
            Widgets.EndScrollView();
        }
    }

    private string BuildManifestSummaryText()
    {
        string serverId = Display(Report.ServerId);
        string localId = Display(Report.LocalId);
        string serverVersion = Display(Report.ServerVersion);
        string localVersion = Display(Report.LocalVersion);
        return T("ClashOfRim.Compatibility.ServerColumn")
            + " "
            + ShortHash(serverId)
            + "  "
            + T("ClashOfRim.Compatibility.LocalColumn")
            + " "
            + ShortHash(localId)
            + "  RimWorld "
            + serverVersion
            + "/"
            + localVersion;
    }

    private void DrawEntryTab(Rect inRect, IReadOnlyList<UiDiffEntry> visibleEntries, ref Vector2 scroll, string emptyText, bool showWorkshopButtons = false)
    {
        Rect outRect = inRect.ContractedBy(8f);
        if (visibleEntries.Count == 0)
        {
            Widgets.Label(outRect, Report.HasServerManifest ? emptyText : T("ClashOfRim.Compatibility.ManifestNotAvailable"));
            return;
        }

        float contentHeight = Math.Max(outRect.height, visibleEntries.Sum(EntryHeight));
        Rect view = new(0f, 0f, outRect.width - 16f, contentHeight);
        Widgets.BeginScrollView(outRect, ref scroll, view);
        float y = 0f;
        bool alternate = false;
        foreach (UiDiffEntry entry in visibleEntries)
        {
            float height = EntryHeight(entry);
            Rect row = new(0f, y, view.width, height);
            if (alternate)
            {
                Widgets.DrawAltRect(row);
            }
            if (Mouse.IsOver(row))
            {
                Widgets.DrawHighlight(row);
            }

            Color previous = GUI.color;
            GUI.color = StatusColor(entry.Status);
            Rect titleRect = new(row.x + 4f, row.y + 2f, row.width, 22f);
            if (showWorkshopButtons && Report.WorkshopId(entry) is string workshopId)
            {
                const float buttonWidth = 92f;
                Rect buttonRect = new(row.xMax - buttonWidth - 4f, row.y + 2f, buttonWidth, 22f);
                titleRect.width -= buttonWidth + 8f;
                GUI.color = Color.white;
                if (Widgets.ButtonText(buttonRect, T("ClashOfRim.Compatibility.OpenWorkshop")))
                {
                    OpenWorkshopPage(workshopId);
                }
                GUI.color = StatusColor(entry.Status);
            }

            Widgets.Label(titleRect, $"{StatusLabel(entry.Status)} - {entry.Title}");
            GUI.color = previous;
            Widgets.Label(new Rect(row.x + 16f, row.y + 24f, row.width - 18f, height - 24f), entry.Detail);
            y += height;
            alternate = !alternate;
        }
        Widgets.EndScrollView();
    }

    private static void OpenWorkshopPage(string workshopId)
    {
        if (string.IsNullOrWhiteSpace(workshopId))
        {
            return;
        }

        Application.OpenURL("https://steamcommunity.com/sharedfiles/filedetails/?id=" + Uri.EscapeDataString(workshopId.Trim()));
    }

    private static float EntryHeight(UiDiffEntry entry)
    {
        return Math.Max(50f, Text.CalcHeight(entry.Detail, 640f) + 30f);
    }

    private static string FormatSummary(int manifestCount, int hashCount, int configCount)
    {
        return T(
            "ClashOfRim.Compatibility.Summary",
            manifestCount.Named("MODS"),
            hashCount.Named("FILES"),
            configCount.Named("CONFIGS"));
    }

    private static string None() => T("ClashOfRim.None");
    private static string Display(string value) => string.IsNullOrWhiteSpace(value) ? None() : value;

    private static Color StatusColor(UiDiffStatus status)
    {
        return status switch
        {
            UiDiffStatus.Missing => new Color(1f, 0.25f, 0.25f),
            UiDiffStatus.Added => new Color(1f, 0.55f, 0.25f),
            UiDiffStatus.Modified => new Color(1f, 0.9f, 0.25f),
            _ => Color.white
        };
    }

    private static string StatusLabel(UiDiffStatus status)
    {
        return status switch
        {
            UiDiffStatus.Missing => T("ClashOfRim.Compatibility.StatusMissing"),
            UiDiffStatus.Added => T("ClashOfRim.Compatibility.StatusAdded"),
            UiDiffStatus.Modified => T("ClashOfRim.Compatibility.StatusModified"),
            _ => T("ClashOfRim.Compatibility.StatusMatched")
        };
    }

    private static string ShortHash(string? value)
    {
        string text = value ?? string.Empty;
        return text.Length <= 16 ? text : text.Substring(0, 16) + "...";
    }

    private static string T(string key)
    {
        return ClashOfRimText.Key(key);
    }

    private static string T(string key, params NamedArgument[] args)
    {
        return ClashOfRimText.Key(key, args);
    }

}
