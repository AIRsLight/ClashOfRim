using AIRsLight.ClashOfRim.Compatibility;
using AIRsLight.ClashOfRim.ClientNetwork;
using RimWorld;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Reflection;
using UnityEngine;
using Verse;

namespace AIRsLight.ClashOfRim.CompatibilityClient;

internal static class CompatibilityBaselineApplicator
{
    public static bool TryApplyServerManifest(string manifestJson, out string message)
    {
        message = string.Empty;
        if (string.IsNullOrWhiteSpace(manifestJson))
        {
            message = T("ClashOfRim.Compatibility.ManifestMissing");
            return false;
        }

        CompatibilityManifestDto? manifest = ReadManifest(manifestJson);
        if (manifest is null)
        {
            message = T("ClashOfRim.Compatibility.ManifestParseFailed");
            return false;
        }

        CompatibilityConfigOverlayPath.Activate(manifestJson);
        ApplyConfigOverlays(manifest);
        ApplyModList(manifest);
        ModsConfig.Save();
        message = T("ClashOfRim.Compatibility.ApplyRestarting");
        GenCommandLine.Restart();
        return true;
    }

    private static string T(string key)
    {
        return ClashOfRimText.Key(key);
    }

    private static CompatibilityManifestDto? ReadManifest(string json)
    {
        try
        {
            var serializer = new DataContractJsonSerializer(typeof(CompatibilityManifestDto));
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
            return serializer.ReadObject(stream) as CompatibilityManifestDto;
        }
        catch (Exception ex)
        {
            Log.Warning("[ClashOfRim][Compatibility] Failed to parse server manifest: " + ex);
            return null;
        }
    }

    private static void ApplyModList(CompatibilityManifestDto manifest)
    {
        List<string> activeMods = manifest.Mods
            .OrderBy(mod => mod.LoadOrder)
            .Select(ToLocalActiveModId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToList();
        object? data = typeof(ModsConfig)
            .GetField("data", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?.GetValue(null);
        FieldInfo? activeModsField = data?.GetType()
            .GetField("activeMods", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        activeModsField?.SetValue(data, activeMods);
    }

    private static void ApplyConfigOverlays(CompatibilityManifestDto manifest)
    {
        foreach (CompatibilityModDto mod in manifest.Mods)
        {
            if (mod.Configs.Count == 0)
            {
                continue;
            }

            foreach (CompatibilityConfigDto config in mod.Configs)
            {
                if (string.IsNullOrWhiteSpace(config.FileName))
                {
                    continue;
                }

                string path = CompatibilityConfigOverlayPath.Resolve(mod.PackageId, config.FileName);
                if (!config.HasSavedFile || string.IsNullOrWhiteSpace(config.CanonicalXml))
                {
                    CompatibilityConfigOverlayPath.Delete(path);
                    continue;
                }

                CompatibilityConfigOverlayPath.EnsureDirectoryFor(path);

                File.WriteAllText(path, config.CanonicalXml);
            }
        }
    }

    private static string ToLocalActiveModId(CompatibilityModDto serverMod)
    {
        ModMetaData? installed = FindInstalledMod(serverMod.PackageId);
        if (installed is not null)
        {
            return installed.PackageId;
        }

        return serverMod.PackageId.StartsWith("ludeon.", StringComparison.OrdinalIgnoreCase)
            ? serverMod.PackageId
            : serverMod.PackageId + ModMetaData.SteamModPostfix;
    }

    private static ModMetaData? FindInstalledMod(string packageId)
    {
        if (string.IsNullOrWhiteSpace(packageId))
        {
            return null;
        }

        return ModLister.GetModWithIdentifier(packageId)
            ?? ModLister.GetModWithIdentifier(packageId + ModMetaData.SteamModPostfix);
    }

    [DataContract]
    private sealed class CompatibilityManifestDto
    {
        [DataMember(Name = "mods")]
        public List<CompatibilityModDto> Mods { get; set; } = new();
    }

    [DataContract]
    private sealed class CompatibilityModDto
    {
        [DataMember(Name = "loadOrder")]
        public int LoadOrder { get; set; }

        [DataMember(Name = "packageId")]
        public string PackageId { get; set; } = string.Empty;

        [DataMember(Name = "configs")]
        public List<CompatibilityConfigDto> Configs { get; set; } = new();
    }

    [DataContract]
    private sealed class CompatibilityConfigDto
    {
        [DataMember(Name = "fileName")]
        public string FileName { get; set; } = string.Empty;

        [DataMember(Name = "hasSavedFile")]
        public bool HasSavedFile { get; set; } = true;

        [DataMember(Name = "canonicalXml")]
        public string CanonicalXml { get; set; } = string.Empty;
    }
}

internal sealed class CompatibilityMismatchWindow : Window
{
    private readonly ClashOfRimMod mod;
    private readonly ModLoginResponseDto response;
    private readonly UiManifest? serverManifest;
    private readonly UiManifest? localManifest;
    private readonly List<UiDiffEntry> manifestEntries;
    private readonly List<UiDiffEntry> hashEntries;
    private readonly List<UiDiffEntry> configEntries;
    private readonly List<UiDiffEntry> visibleManifestEntries;
    private readonly List<UiDiffEntry> visibleHashEntries;
    private readonly List<UiDiffEntry> visibleConfigEntries;
    private readonly int manifestMismatchCount;
    private readonly int hashMismatchCount;
    private readonly int configMismatchCount;
    private readonly bool onlyFileMismatch;
    private readonly bool canApplyAndRestart;
    private CompatibilityTab selectedTab = CompatibilityTab.Overview;
    private Vector2 manifestScroll;
    private Vector2 hashScroll;
    private Vector2 configScroll;
    private Vector2 serverModScroll;
    private Vector2 localModScroll;
    private string status = string.Empty;

    public CompatibilityMismatchWindow(ClashOfRimMod mod, ModLoginResponseDto response)
    {
        this.mod = mod;
        this.response = response;
        serverManifest = UiManifest.Read(response.ServerCompatibilityManifestJson);
        localManifest = UiManifest.FromCurrentClient();
        manifestEntries = BuildManifestEntries(serverManifest, localManifest, response.CompatibilityIssues);
        hashEntries = BuildHashEntries(serverManifest, localManifest, response.CompatibilityIssues);
        configEntries = BuildConfigEntries(serverManifest, localManifest, response.CompatibilityIssues);
        visibleManifestEntries = VisibleEntries(manifestEntries);
        visibleHashEntries = VisibleEntries(hashEntries);
        visibleConfigEntries = VisibleEntries(configEntries);
        manifestMismatchCount = visibleManifestEntries.Count;
        hashMismatchCount = visibleHashEntries.Count;
        configMismatchCount = visibleConfigEntries.Count;
        onlyFileMismatch = manifestMismatchCount == 0 && configMismatchCount == 0 && hashMismatchCount > 0;
        canApplyAndRestart = (manifestMismatchCount > 0 || configMismatchCount > 0) && !onlyFileMismatch;
        doCloseX = true;
        closeOnClickedOutside = false;
        absorbInputAroundWindow = true;
        forcePause = false;
    }

    public override Vector2 InitialSize => new(720f, 500f);

    private static List<UiDiffEntry> VisibleEntries(IEnumerable<UiDiffEntry> entries)
    {
        return entries
            .Where(entry => entry.Status != UiDiffStatus.Match)
            .ToList();
    }

    public override void DoWindowContents(Rect inRect)
    {
        Text.Font = GameFont.Small;

        Rect tabsRect = new(inRect.x, inRect.y + 36f, inRect.width, inRect.height - 108f);
        TabDrawer.DrawTabs(tabsRect, new List<TabRecord>
        {
            new(T("ClashOfRim.Compatibility.TabOverview"), () => selectedTab = CompatibilityTab.Overview, selectedTab == CompatibilityTab.Overview),
            new(T("ClashOfRim.Compatibility.TabManifest", manifestMismatchCount.Named("COUNT")), () => selectedTab = CompatibilityTab.Manifest, selectedTab == CompatibilityTab.Manifest),
            new(T("ClashOfRim.Compatibility.TabHash", hashMismatchCount.Named("COUNT")), () => selectedTab = CompatibilityTab.Hash, selectedTab == CompatibilityTab.Hash),
            new(T("ClashOfRim.Compatibility.TabConfig", configMismatchCount.Named("COUNT")), () => selectedTab = CompatibilityTab.Config, selectedTab == CompatibilityTab.Config)
        });

        GUI.BeginGroup(tabsRect);
        {
            Rect tabInner = new(0f, 8f, tabsRect.width, tabsRect.height - 8f);
            switch (selectedTab)
            {
                case CompatibilityTab.Overview:
                    DrawOverviewTab(tabInner, manifestMismatchCount, hashMismatchCount, configMismatchCount, onlyFileMismatch);
                    break;
                case CompatibilityTab.Manifest:
                    DrawManifestTab(tabInner);
                    break;
                case CompatibilityTab.Hash:
                    DrawEntryTab(tabInner, visibleHashEntries, ref hashScroll, T("ClashOfRim.Compatibility.FilesMatch"));
                    break;
                case CompatibilityTab.Config:
                    DrawEntryTab(tabInner, visibleConfigEntries, ref configScroll, T("ClashOfRim.Compatibility.ConfigsMatch"));
                    break;
            }
        }
        GUI.EndGroup();

        Widgets.Label(new Rect(inRect.x, inRect.yMax - 76f, inRect.width, 24f), status);

        Rect closeRect = new(inRect.xMax - 80f, inRect.yMax - 36f, 80f, 32f);
        if (Widgets.ButtonText(closeRect, T("ClashOfRim.Close")))
        {
            Close();
        }

        Rect previousButtonRect = closeRect;
        if (canApplyAndRestart)
        {
            Rect applyRect = new(closeRect.x - 124f, inRect.yMax - 36f, 112f, 32f);
            previousButtonRect = applyRect;
            if (Widgets.ButtonText(applyRect, T("ClashOfRim.Compatibility.ApplyAndRestart")))
            {
                if (CompatibilityBaselineApplicator.TryApplyServerManifest(response.ServerCompatibilityManifestJson ?? string.Empty, out string message))
                {
                    status = message;
                }
                else
                {
                    status = message;
                }
            }
        }

        if (response.CanOverrideCompatibilityBaseline)
        {
            Rect overrideRect = new(previousButtonRect.x - 124f, inRect.yMax - 36f, 112f, 32f);
            if (Widgets.ButtonText(overrideRect, T("ClashOfRim.Compatibility.OverrideBaseline")))
            {
                OpenOverrideBaselineWithWarning();
            }
        }
    }

    private void OpenOverrideBaselineWithWarning()
    {
        if (!HasModListOrOrderMismatch())
        {
            OpenOverrideBaselineWindow();
            return;
        }

        Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
            T("ClashOfRim.Compatibility.Override.ModListWarning"),
            OpenOverrideBaselineWindow));
    }

    private void OpenOverrideBaselineWindow()
    {
        Find.WindowStack.Add(new CompatibilityBaselineOverrideWindow(mod));
        Close();
    }

    private bool HasModListOrOrderMismatch()
    {
        if (serverManifest is null || localManifest is null)
        {
            return manifestMismatchCount > 0;
        }

        IReadOnlyList<string> serverIds = serverManifest.Mods
            .OrderBy(mod => mod.LoadOrder)
            .Select(mod => NormalizeId(mod.PackageId))
            .ToList();
        IReadOnlyList<string> localIds = localManifest.Mods
            .OrderBy(mod => mod.LoadOrder)
            .Select(mod => NormalizeId(mod.PackageId))
            .ToList();
        return !serverIds.SequenceEqual(localIds, StringComparer.Ordinal);
    }

    private string BuildIntro(int manifestCount, int hashCount, int configCount, bool onlyFileMismatch)
    {
        string summary = FormatSummary(manifestCount, hashCount, configCount);
        if (onlyFileMismatch)
        {
            return summary + T("ClashOfRim.Compatibility.SummaryFilesManual");
        }

        if (response.CanOverrideCompatibilityBaseline)
        {
            return summary + T("ClashOfRim.Compatibility.SummaryAdminCanOverride");
        }

        return canApplyAndRestart
            ? summary + T("ClashOfRim.Compatibility.SummaryApplyServer")
            : summary;
    }

    private void DrawOverviewTab(Rect inRect, int manifestCount, int hashCount, int configCount, bool onlyFileMismatch)
    {
        Rect contentRect = inRect.ContractedBy(8f);
        float y = contentRect.y + 8f;
        Widgets.Label(
            new Rect(contentRect.x, y, contentRect.width, 28f),
            FormatSummary(manifestCount, hashCount, configCount));
        y += 34f;

        Widgets.Label(new Rect(contentRect.x, y, contentRect.width, 54f), BuildIntro(manifestCount, hashCount, configCount, onlyFileMismatch));
        y += 66f;

        Widgets.DrawBox(new Rect(contentRect.x, y, contentRect.width, 112f));
        Rect detailRect = new(contentRect.x + 12f, y + 10f, contentRect.width - 24f, 94f);
        string serverId = ShortHash(serverManifest?.ManifestId ?? None());
        string localId = ShortHash(localManifest?.ManifestId ?? None());
        string serverVersion = serverManifest?.RimWorldVersion ?? None();
        string localVersion = localManifest?.RimWorldVersion ?? None();
        Widgets.Label(
            detailRect,
            T("ClashOfRim.Compatibility.ServerManifestLine", serverId.Named("ID"))
            + "\n" + T("ClashOfRim.Compatibility.LocalManifestLine", localId.Named("ID"))
            + "\nRimWorld：" + serverVersion + " / " + localVersion);
        y += 128f;

        Widgets.Label(
            new Rect(contentRect.x, y, contentRect.width, 80f),
            canApplyAndRestart
                ? T("ClashOfRim.Compatibility.ManifestApplyAllowed")
                : onlyFileMismatch
                ? T("ClashOfRim.Compatibility.FileMismatchCannotApply")
                : string.Empty);
    }

    private void DrawManifestTab(Rect inRect)
    {
        const float rowHeight = 22f;
        const float listHeight = 165f;
        const float gap = 16f;
        float columnWidth = (inRect.width - 24f - gap) / 2f;
        Rect serverRect = new(8f, 24f, columnWidth, listHeight);
        Rect localRect = new(serverRect.xMax + gap, 24f, columnWidth, listHeight);

        DrawModColumn(serverRect, T("ClashOfRim.Compatibility.ServerColumn"), serverManifest?.Mods ?? new List<UiMod>(), localManifest, ref serverModScroll, serverSide: true);
        DrawModColumn(localRect, T("ClashOfRim.Compatibility.LocalColumn"), localManifest?.Mods ?? new List<UiMod>(), serverManifest, ref localModScroll, serverSide: false);

        Rect summaryRect = new(8f, serverRect.yMax + 8f, inRect.width - 16f, 24f);
        Widgets.Label(summaryRect, BuildManifestSummaryText());

        Rect detailRect = new(0f, summaryRect.yMax + 4f, inRect.width, inRect.height - summaryRect.yMax - 4f);
        DrawEntryTab(detailRect, visibleManifestEntries, ref manifestScroll, T("ClashOfRim.Compatibility.ModsMatch"), showWorkshopButtons: true);

        static void DrawModColumn(Rect rect, string title, IReadOnlyList<UiMod> mods, UiManifest? other, ref Vector2 scroll, bool serverSide)
        {
            Widgets.Label(new Rect(rect.x, rect.y - 20f, rect.width, 20f), title);
            Widgets.DrawBox(rect);
            Rect view = new(0f, 0f, rect.width - 16f, Math.Max(rect.height, mods.Count * rowHeight));
            Widgets.BeginScrollView(rect, ref scroll, view);
            for (int i = 0; i < mods.Count; i++)
            {
                UiMod mod = mods[i];
                Rect row = new(0f, i * rowHeight, view.width, rowHeight);
                if (Mouse.IsOver(row))
                {
                    Widgets.DrawHighlight(row);
                }

                Color color = ResolveModColor(mod, other, i, serverSide);
                Color previous = GUI.color;
                GUI.color = color;
                Widgets.Label(row, $"{i + 1}. {DisplayModName(mod)}");
                GUI.color = previous;
                TooltipHandler.TipRegion(row, mod.PackageId);
            }
            Widgets.EndScrollView();
        }
    }

    private string BuildManifestSummaryText()
    {
        string serverId = serverManifest?.ManifestId ?? None();
        string localId = localManifest?.ManifestId ?? None();
        string serverVersion = serverManifest?.RimWorldVersion ?? None();
        string localVersion = localManifest?.RimWorldVersion ?? None();
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
            Widgets.Label(outRect, emptyText);
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
            if (showWorkshopButtons && TryResolveWorkshopId(entry, out string workshopId))
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

    private bool TryResolveWorkshopId(UiDiffEntry entry, out string workshopId)
    {
        workshopId = string.Empty;
        if (entry.Status != UiDiffStatus.Missing || serverManifest is null || string.IsNullOrWhiteSpace(entry.Subject))
        {
            return false;
        }

        UiMod? mod = serverManifest.Mods.FirstOrDefault(item =>
            string.Equals(NormalizeId(item.PackageId), NormalizeId(entry.Subject), StringComparison.Ordinal));
        if (mod is null || string.IsNullOrWhiteSpace(mod.WorkshopId))
        {
            return false;
        }

        workshopId = mod.WorkshopId.Trim();
        return true;
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

    private static List<UiDiffEntry> BuildManifestEntries(
        UiManifest? server,
        UiManifest? local,
        IReadOnlyList<ModCompatibilityIssueDto>? fallbackIssues)
    {
        var entries = new List<UiDiffEntry>();
        if (server is null || local is null)
        {
            entries.AddRange(FallbackEntries(fallbackIssues, CompatibilityTab.Manifest));
            return entries;
        }

        AddScalar(entries, T("ClashOfRim.Compatibility.SchemaVersion"), server.SchemaVersion.ToString(), local.SchemaVersion.ToString());
        AddScalar(entries, T("ClashOfRim.Compatibility.ProtocolVersion"), server.ProtocolVersion, local.ProtocolVersion);
        AddScalar(entries, T("ClashOfRim.Compatibility.RimWorldVersion"), server.RimWorldVersion, local.RimWorldVersion);
        AddSequence(entries, T("ClashOfRim.Compatibility.DlcList"), server.DlcIds, local.DlcIds);

        Dictionary<string, UiMod> localById = local.Mods.ToDictionary(mod => NormalizeId(mod.PackageId), mod => mod);
        Dictionary<string, UiMod> serverById = server.Mods.ToDictionary(mod => NormalizeId(mod.PackageId), mod => mod);
        foreach (UiMod serverMod in server.Mods)
        {
            if (!localById.ContainsKey(NormalizeId(serverMod.PackageId)))
            {
                entries.Add(new UiDiffEntry(UiDiffStatus.Missing, T("ClashOfRim.Compatibility.MissingRequiredMod"), DisplayModName(serverMod), serverMod.PackageId));
            }
        }

        foreach (UiMod localMod in local.Mods)
        {
            if (!serverById.ContainsKey(NormalizeId(localMod.PackageId)))
            {
                entries.Add(new UiDiffEntry(UiDiffStatus.Added, T("ClashOfRim.Compatibility.UnexpectedLocalMod"), DisplayModName(localMod), localMod.PackageId));
            }
        }

        int max = Math.Max(server.Mods.Count, local.Mods.Count);
        for (int i = 0; i < max; i++)
        {
            string serverId = i < server.Mods.Count ? server.Mods[i].PackageId : None();
            string localId = i < local.Mods.Count ? local.Mods[i].PackageId : None();
            if (!string.Equals(NormalizeId(serverId), NormalizeId(localId), StringComparison.Ordinal))
            {
                entries.Add(new UiDiffEntry(
                    UiDiffStatus.Modified,
                    T("ClashOfRim.Compatibility.LoadOrderMismatch", (i + 1).Named("INDEX")),
                    FormatServerLocal(serverId, localId),
                    "mods"));
            }
        }

        AppendUnrepresentedFallbackEntries(entries, fallbackIssues, CompatibilityTab.Manifest);
        return entries;
    }

    private static List<UiDiffEntry> BuildHashEntries(
        UiManifest? server,
        UiManifest? local,
        IReadOnlyList<ModCompatibilityIssueDto>? fallbackIssues)
    {
        var entries = new List<UiDiffEntry>();
        if (server is null || local is null)
        {
            entries.AddRange(FallbackEntries(fallbackIssues, CompatibilityTab.Hash));
            return entries;
        }

        Dictionary<string, UiMod> localById = local.Mods.ToDictionary(mod => NormalizeId(mod.PackageId), mod => mod);
        foreach (UiMod serverMod in server.Mods)
        {
            if (!localById.TryGetValue(NormalizeId(serverMod.PackageId), out UiMod? localMod))
            {
                continue;
            }

            Dictionary<string, UiFile> localFiles = localMod.Files.ToDictionary(file => NormalizePath(file.RelativePath), file => file);
            Dictionary<string, UiFile> serverFiles = serverMod.Files.ToDictionary(file => NormalizePath(file.RelativePath), file => file);
            foreach (UiFile serverFile in serverMod.Files)
            {
                string key = NormalizePath(serverFile.RelativePath);
                if (!localFiles.TryGetValue(key, out UiFile? localFile))
                {
                    entries.Add(new UiDiffEntry(UiDiffStatus.Missing, T("ClashOfRim.Compatibility.MissingLocalFile"), $"{serverMod.PackageId}/{serverFile.RelativePath}", serverMod.PackageId));
                    continue;
                }

                if (serverFile.Size != localFile.Size)
                {
                    string hint = BuildVersionHint(serverMod, localMod, serverFile, localFile);
                    entries.Add(new UiDiffEntry(
                        UiDiffStatus.Modified,
                        T("ClashOfRim.Compatibility.FileSizeMismatch"),
                        $"{serverMod.PackageId}/{serverFile.RelativePath}\n" + FormatServerLocal(
                            T("ClashOfRim.Compatibility.Bytes", serverFile.Size.Named("COUNT")),
                            T("ClashOfRim.Compatibility.Bytes", localFile.Size.Named("COUNT"))) + hint,
                        serverMod.PackageId));
                }

                if (!string.Equals(serverFile.Sha256, localFile.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    string hint = BuildVersionHint(serverMod, localMod, serverFile, localFile);
                    entries.Add(new UiDiffEntry(
                        UiDiffStatus.Modified,
                        T("ClashOfRim.Compatibility.FileHashMismatch"),
                        $"{serverMod.PackageId}/{serverFile.RelativePath}\n" + FormatServerLocal(ShortHash(serverFile.Sha256), ShortHash(localFile.Sha256)) + hint,
                        serverMod.PackageId));
                }
            }

            foreach (UiFile localFile in localMod.Files)
            {
                if (!serverFiles.ContainsKey(NormalizePath(localFile.RelativePath)))
                {
                    entries.Add(new UiDiffEntry(UiDiffStatus.Added, T("ClashOfRim.Compatibility.UnexpectedLocalFile"), $"{localMod.PackageId}/{localFile.RelativePath}", localMod.PackageId));
                }
            }
        }

        Dictionary<string, UiDefSummary> localDefs = local.DefSummaries.ToDictionary(def => def.Name, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, UiDefSummary> serverDefs = server.DefSummaries.ToDictionary(def => def.Name, StringComparer.OrdinalIgnoreCase);
        foreach (UiDefSummary serverDef in server.DefSummaries)
        {
            if (!localDefs.TryGetValue(serverDef.Name, out UiDefSummary? localDef))
            {
                entries.Add(new UiDiffEntry(UiDiffStatus.Missing, T("ClashOfRim.Compatibility.MissingDefSummary"), serverDef.Name, serverDef.Name));
                continue;
            }

            if (serverDef.Count != localDef.Count || serverDef.Hash != localDef.Hash)
            {
                entries.Add(new UiDiffEntry(
                    UiDiffStatus.Modified,
                    T("ClashOfRim.Compatibility.DefSummaryMismatch"),
                    $"{serverDef.Name}\n" + FormatServerLocal(
                        $"count={serverDef.Count}, hash={serverDef.Hash}",
                        $"count={localDef.Count}, hash={localDef.Hash}"),
                    serverDef.Name));
            }
        }

        foreach (UiDefSummary localDef in local.DefSummaries)
        {
            if (!serverDefs.ContainsKey(localDef.Name))
            {
                entries.Add(new UiDiffEntry(UiDiffStatus.Added, T("ClashOfRim.Compatibility.UnexpectedDefSummary"), localDef.Name, localDef.Name));
            }
        }

        AppendUnrepresentedFallbackEntries(entries, fallbackIssues, CompatibilityTab.Hash);
        return entries;
    }

    private static string BuildVersionHint(UiMod serverMod, UiMod localMod, UiFile serverFile, UiFile localFile)
    {
        if (serverFile.LastWriteUtcUnix > 0 && localFile.LastWriteUtcUnix > 0)
        {
            if (localFile.LastWriteUtcUnix > serverFile.LastWriteUtcUnix)
            {
                return "\n" + T("ClashOfRim.Compatibility.FileTimeHintServerMayNeedUpdate");
            }

            if (serverFile.LastWriteUtcUnix > localFile.LastWriteUtcUnix)
            {
                return "\n" + T("ClashOfRim.Compatibility.FileTimeHintClientMayNeedUpdate");
            }
        }

        return BuildWorkshopVersionHint(serverMod, localMod);
    }

    private static string BuildWorkshopVersionHint(UiMod serverMod, UiMod localMod)
    {
        if (string.IsNullOrWhiteSpace(serverMod.WorkshopId)
            || string.IsNullOrWhiteSpace(localMod.WorkshopId)
            || !string.Equals(serverMod.WorkshopId.Trim(), localMod.WorkshopId.Trim(), StringComparison.Ordinal))
        {
            return string.Empty;
        }

        if ((localMod.WorkshopItemState ?? string.Empty).IndexOf("NeedsUpdate", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return "\n" + T("ClashOfRim.Compatibility.WorkshopHintClientNeedsUpdate");
        }

        if (serverMod.WorkshopLocalInstalledAtUnix > 0 && localMod.WorkshopLocalInstalledAtUnix > 0)
        {
            if (localMod.WorkshopLocalInstalledAtUnix > serverMod.WorkshopLocalInstalledAtUnix)
            {
                return "\n" + T("ClashOfRim.Compatibility.WorkshopHintServerMayNeedUpdate");
            }

            if (serverMod.WorkshopLocalInstalledAtUnix > localMod.WorkshopLocalInstalledAtUnix)
            {
                return "\n" + T("ClashOfRim.Compatibility.WorkshopHintClientMayNeedUpdate");
            }
        }

        return "\n" + T("ClashOfRim.Compatibility.WorkshopHintUncertain");
    }

    private static List<UiDiffEntry> BuildConfigEntries(
        UiManifest? server,
        UiManifest? local,
        IReadOnlyList<ModCompatibilityIssueDto>? fallbackIssues)
    {
        var entries = new List<UiDiffEntry>();
        if (server is null || local is null)
        {
            entries.AddRange(FallbackEntries(fallbackIssues, CompatibilityTab.Config));
            return entries;
        }

        AddScalar(entries, T("ClashOfRim.Compatibility.ConfigTotalHash"), server.ConfigSha256, local.ConfigSha256);
        Dictionary<string, UiMod> localById = local.Mods.ToDictionary(mod => NormalizeId(mod.PackageId), mod => mod);
        foreach (UiMod serverMod in server.Mods)
        {
            if (!localById.TryGetValue(NormalizeId(serverMod.PackageId), out UiMod? localMod))
            {
                continue;
            }

            Dictionary<string, UiConfig> localConfigs = localMod.Configs.ToDictionary(config => config.FileName, StringComparer.OrdinalIgnoreCase);
            Dictionary<string, UiConfig> serverConfigs = serverMod.Configs.ToDictionary(config => config.FileName, StringComparer.OrdinalIgnoreCase);
            foreach (UiConfig serverConfig in serverMod.Configs)
            {
                if (!localConfigs.TryGetValue(serverConfig.FileName, out UiConfig? localConfig))
                {
                    entries.Add(new UiDiffEntry(UiDiffStatus.Missing, T("ClashOfRim.Compatibility.MissingLocalConfig"), $"{serverMod.PackageId}/{serverConfig.FileName}", serverMod.PackageId));
                    continue;
                }

                if (serverConfig.HasSavedFile != localConfig.HasSavedFile
                    || (serverConfig.HasSavedFile
                        && !string.Equals(serverConfig.Sha256, localConfig.Sha256, StringComparison.OrdinalIgnoreCase)))
                {
                    entries.Add(new UiDiffEntry(
                        UiDiffStatus.Modified,
                        T("ClashOfRim.Compatibility.ConfigHashMismatch"),
                        $"{serverMod.PackageId}/{serverConfig.FileName}\n" + FormatServerLocal(ShortHash(serverConfig.Sha256), ShortHash(localConfig.Sha256)),
                        serverMod.PackageId));
                }
            }

            // Extra local configs are passthrough files and are intentionally omitted from the
            // mismatch list. The server baseline only owns config files it explicitly declares.
        }

        AppendUnrepresentedFallbackEntries(entries, fallbackIssues, CompatibilityTab.Config);
        return entries;
    }

    private static IEnumerable<UiDiffEntry> FallbackEntries(
        IReadOnlyList<ModCompatibilityIssueDto>? issues,
        CompatibilityTab tab)
    {
        return (issues ?? new List<ModCompatibilityIssueDto>())
            .Where(issue => IssueBelongsToTab(issue.Code, tab))
            .Select(issue => new UiDiffEntry(
                IsSeverity(issue, "Error") ? UiDiffStatus.Modified : UiDiffStatus.Added,
                LocalizeCode(issue.Code),
                (string.IsNullOrWhiteSpace(issue.Subject) ? string.Empty : "[" + issue.Subject + "] ")
                + (string.IsNullOrWhiteSpace(issue.Message) ? T("ClashOfRim.Compatibility.ServerIssueFallback") : issue.Message.Trim()),
                issue.Subject));
    }

    private static void AppendUnrepresentedFallbackEntries(
        List<UiDiffEntry> entries,
        IReadOnlyList<ModCompatibilityIssueDto>? issues,
        CompatibilityTab tab)
    {
        foreach (UiDiffEntry fallback in FallbackEntries(issues, tab))
        {
            bool hasMatchingSubject = !string.IsNullOrWhiteSpace(fallback.Subject)
                && entries.Any(entry => string.Equals(
                    NormalizeId(entry.Subject ?? string.Empty),
                    NormalizeId(fallback.Subject),
                    StringComparison.Ordinal));
            bool hasMatchingContent = entries.Any(entry => string.Equals(entry.Title, fallback.Title, StringComparison.Ordinal)
                && string.Equals(entry.Detail, fallback.Detail, StringComparison.Ordinal));
            if (!hasMatchingSubject && !hasMatchingContent)
            {
                entries.Add(fallback);
            }
        }
    }

    private static bool IssueBelongsToTab(string? code, CompatibilityTab tab)
    {
        string value = code ?? string.Empty;
        return tab switch
        {
            CompatibilityTab.Manifest => ContainsOrdinal(value, "Mod")
                || ContainsOrdinal(value, "Version")
                || ContainsOrdinal(value, "Dlc")
                || ContainsOrdinal(value, "Protocol")
                || ContainsOrdinal(value, "Schema")
                || ContainsOrdinal(value, "RimWorld")
                || ContainsOrdinal(value, "Language"),
            CompatibilityTab.Hash => ContainsOrdinal(value, "File")
                || ContainsOrdinal(value, "DefSummary"),
            CompatibilityTab.Config => ContainsOrdinal(value, "Config"),
            _ => false
        };
    }

    private static bool ContainsOrdinal(string value, string fragment)
    {
        return value.IndexOf(fragment, StringComparison.Ordinal) >= 0;
    }

    private static void AddScalar(List<UiDiffEntry> entries, string title, string server, string local)
    {
        if (!string.Equals(server ?? string.Empty, local ?? string.Empty, StringComparison.Ordinal))
        {
            entries.Add(new UiDiffEntry(
                UiDiffStatus.Modified,
                title,
                FormatServerLocal(server ?? string.Empty, local ?? string.Empty),
                title));
        }
    }

    private static void AddSequence(List<UiDiffEntry> entries, string title, IReadOnlyList<string> server, IReadOnlyList<string> local)
    {
        string serverText = string.Join(", ", server ?? new List<string>());
        string localText = string.Join(", ", local ?? new List<string>());
        if (!string.Equals(serverText, localText, StringComparison.Ordinal))
        {
            entries.Add(new UiDiffEntry(
                UiDiffStatus.Modified,
                title,
                FormatServerLocal(serverText, localText),
                title));
        }
    }

    private static string FormatSummary(int manifestCount, int hashCount, int configCount)
    {
        return T(
            "ClashOfRim.Compatibility.Summary",
            manifestCount.Named("MODS"),
            hashCount.Named("FILES"),
            configCount.Named("CONFIGS"));
    }

    private static string FormatServerLocal(string server, string local)
    {
        return T(
            "ClashOfRim.Compatibility.ServerLocalLines",
            server.Named("SERVER"),
            local.Named("LOCAL"));
    }

    private static string None()
    {
        return T("ClashOfRim.None");
    }

    private static Color ResolveModColor(UiMod mod, UiManifest? other, int index, bool serverSide)
    {
        if (other is null)
        {
            return Color.white;
        }

        int otherIndex = other.Mods.FindIndex(item => string.Equals(NormalizeId(item.PackageId), NormalizeId(mod.PackageId), StringComparison.Ordinal));
        if (otherIndex < 0)
        {
            return serverSide ? new Color(1f, 0.25f, 0.25f) : new Color(1f, 0.55f, 0.25f);
        }

        return otherIndex == index ? Color.white : new Color(1f, 0.9f, 0.25f);
    }

    private static string DisplayModName(UiMod mod)
    {
        return string.IsNullOrWhiteSpace(mod.Name) ? mod.PackageId : mod.Name;
    }

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

    private static string NormalizeId(string? value)
    {
        return (value ?? string.Empty).Trim().ToLowerInvariant();
    }

    private static string NormalizePath(string? value)
    {
        return (value ?? string.Empty).Replace('\\', '/').TrimStart('/').ToLowerInvariant();
    }

    private static string ShortHash(string? value)
    {
        string text = value ?? string.Empty;
        return text.Length <= 16 ? text : text.Substring(0, 16) + "...";
    }

    private static bool IsSeverity(ModCompatibilityIssueDto issue, string severity)
    {
        return string.Equals(issue.Severity, severity, StringComparison.OrdinalIgnoreCase);
    }

    private static string LocalizeCode(string? code)
    {
        return (code ?? string.Empty).Trim() switch
        {
            "SchemaVersionMismatch" => T("ClashOfRim.Compatibility.Issue.SchemaVersionMismatch"),
            "ProtocolVersionMismatch" => T("ClashOfRim.Compatibility.Issue.ProtocolVersionMismatch"),
            "RimWorldVersionMismatch" => T("ClashOfRim.Compatibility.Issue.RimWorldVersionMismatch"),
            "GameLanguageMismatch" => T("ClashOfRim.Compatibility.Issue.GameLanguageMismatch"),
            "DlcListMismatch" => T("ClashOfRim.Compatibility.Issue.DlcListMismatch"),
            "ModListMismatch" => T("ClashOfRim.Compatibility.Issue.ModListMismatch"),
            "ModOrderMismatch" => T("ClashOfRim.Compatibility.Issue.ModOrderMismatch"),
            "MissingMod" => T("ClashOfRim.Compatibility.Issue.MissingMod"),
            "UnexpectedMod" => T("ClashOfRim.Compatibility.Issue.UnexpectedMod"),
            "AllowedPureTranslationMod" => T("ClashOfRim.Compatibility.Issue.AllowedPureTranslationMod"),
            "FileMissing" => T("ClashOfRim.Compatibility.Issue.FileMissing"),
            "FileUnexpected" => T("ClashOfRim.Compatibility.Issue.FileUnexpected"),
            "FileHashMismatch" => T("ClashOfRim.Compatibility.Issue.FileHashMismatch"),
            "FileSizeMismatch" => T("ClashOfRim.Compatibility.Issue.FileSizeMismatch"),
            "ConfigVersionMismatch" => T("ClashOfRim.Compatibility.Issue.ConfigVersionMismatch"),
            "ConfigHashMismatch" => T("ClashOfRim.Compatibility.Issue.ConfigHashMismatch"),
            "ConfigFileMismatch" => T("ClashOfRim.Compatibility.Issue.ConfigFileMismatch"),
            "ConfigFileMissing" => T("ClashOfRim.Compatibility.Issue.ConfigFileMissing"),
            "ConfigFileUnexpected" => T("ClashOfRim.Compatibility.Issue.ConfigFileUnexpected"),
            "ConfigFileWarning" => T("ClashOfRim.Compatibility.Issue.ConfigFileWarning"),
            "DefSummaryMissing" => T("ClashOfRim.Compatibility.Issue.DefSummaryMissing"),
            "DefSummaryUnexpected" => T("ClashOfRim.Compatibility.Issue.DefSummaryUnexpected"),
            "DefSummaryCountMismatch" => T("ClashOfRim.Compatibility.Issue.DefSummaryCountMismatch"),
            "DefSummaryHashMismatch" => T("ClashOfRim.Compatibility.Issue.DefSummaryHashMismatch"),
            "" => T("ClashOfRim.Compatibility.Issue.Unknown"),
            var value => value
        };
    }

    private static string T(string key)
    {
        return ClashOfRimText.Key(key);
    }

    private static string T(string key, params NamedArgument[] args)
    {
        return ClashOfRimText.Key(key, args);
    }

    private enum CompatibilityTab
    {
        Overview,
        Manifest,
        Hash,
        Config
    }

    private enum UiDiffStatus
    {
        Match,
        Missing,
        Added,
        Modified
    }

    private sealed class UiDiffEntry
    {
        public UiDiffEntry(UiDiffStatus status, string title, string detail, string? subject)
        {
            Status = status;
            Title = title;
            Detail = detail;
            Subject = subject;
        }

        public UiDiffStatus Status { get; }

        public string Title { get; }

        public string Detail { get; }

        public string? Subject { get; }
    }

    [DataContract]
    private sealed class UiManifest
    {
        [DataMember(Name = "schemaVersion")]
        public int SchemaVersion { get; set; }

        [DataMember(Name = "manifestId")]
        public string ManifestId { get; set; } = string.Empty;

        [DataMember(Name = "protocolVersion")]
        public string ProtocolVersion { get; set; } = string.Empty;

        [DataMember(Name = "rimWorldVersion")]
        public string RimWorldVersion { get; set; } = string.Empty;

        [DataMember(Name = "dlcIds")]
        public List<string> DlcIds { get; set; } = new();

        [DataMember(Name = "configVersion")]
        public string ConfigVersion { get; set; } = string.Empty;

        [DataMember(Name = "configSha256")]
        public string ConfigSha256 { get; set; } = string.Empty;

        [DataMember(Name = "mods")]
        public List<UiMod> Mods { get; set; } = new();

        [DataMember(Name = "defSummaries")]
        public List<UiDefSummary> DefSummaries { get; set; } = new();

        public static UiManifest? Read(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            try
            {
                var serializer = new DataContractJsonSerializer(typeof(UiManifest));
                using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
                return serializer.ReadObject(stream) as UiManifest;
            }
            catch (Exception ex)
            {
                Log.Warning("[ClashOfRim][Compatibility] Failed to parse manifest for mismatch UI: " + ex);
                return null;
            }
        }

        public static UiManifest? FromCurrentClient()
        {
            try
            {
                CompatibilityManifest manifest = ClientCompatibilityManifestBuilder.Build();
                return new UiManifest
                {
                    SchemaVersion = manifest.SchemaVersion,
                    ManifestId = manifest.ManifestId,
                    ProtocolVersion = manifest.ProtocolVersion,
                    RimWorldVersion = manifest.RimWorldVersion,
                    DlcIds = manifest.DlcIds.ToList(),
                    ConfigVersion = manifest.ConfigVersion,
                    ConfigSha256 = manifest.ConfigSha256,
                    Mods = manifest.Mods.Select(UiMod.From).ToList(),
                    DefSummaries = manifest.DefSummaries
                        .Select(def => new UiDefSummary { Name = def.Name, Count = def.Count, Hash = def.Hash })
                        .ToList()
                };
            }
            catch (Exception ex)
            {
                Log.Warning("[ClashOfRim][Compatibility] Failed to build local manifest for mismatch UI: " + ex);
                return null;
            }
        }
    }

    [DataContract]
    private sealed class UiMod
    {
        [DataMember(Name = "loadOrder")]
        public int LoadOrder { get; set; }

        [DataMember(Name = "packageId")]
        public string PackageId { get; set; } = string.Empty;

        [DataMember(Name = "name")]
        public string Name { get; set; } = string.Empty;

        [DataMember(Name = "source")]
        public string Source { get; set; } = string.Empty;

        [DataMember(Name = "workshopId")]
        public string WorkshopId { get; set; } = string.Empty;

        [DataMember(Name = "workshopItemState")]
        public string WorkshopItemState { get; set; } = string.Empty;

        [DataMember(Name = "workshopLocalInstalledAtUnix")]
        public long WorkshopLocalInstalledAtUnix { get; set; }

        [DataMember(Name = "role")]
        public string Role { get; set; } = string.Empty;

        [DataMember(Name = "files")]
        public List<UiFile> Files { get; set; } = new();

        [DataMember(Name = "configs")]
        public List<UiConfig> Configs { get; set; } = new();

        public static UiMod From(ModManifestEntry mod)
        {
            return new UiMod
            {
                LoadOrder = mod.LoadOrder,
                PackageId = mod.PackageId,
                Name = mod.Name,
                Source = mod.Source,
                WorkshopId = mod.WorkshopId,
                WorkshopItemState = mod.WorkshopItemState,
                WorkshopLocalInstalledAtUnix = mod.WorkshopLocalInstalledAtUnix,
                Role = mod.Role.ToString(),
                Files = mod.Files.Select(UiFile.From).ToList(),
                Configs = mod.Configs.Select(UiConfig.From).ToList()
            };
        }
    }

    [DataContract]
    private sealed class UiFile
    {
        [DataMember(Name = "relativePath")]
        public string RelativePath { get; set; } = string.Empty;

        [DataMember(Name = "size")]
        public long Size { get; set; }

        [DataMember(Name = "sha256")]
        public string Sha256 { get; set; } = string.Empty;

        [DataMember(Name = "lastWriteUtcUnix")]
        public long LastWriteUtcUnix { get; set; }

        [DataMember(Name = "kind")]
        public string Kind { get; set; } = string.Empty;

        public static UiFile From(ControlledFileEntry file)
        {
            return new UiFile
            {
                RelativePath = file.RelativePath,
                Size = file.Size,
                Sha256 = file.Sha256,
                LastWriteUtcUnix = file.LastWriteUtcUnix,
                Kind = file.Kind.ToString()
            };
        }
    }

    [DataContract]
    private sealed class UiConfig
    {
        [DataMember(Name = "fileName")]
        public string FileName { get; set; } = string.Empty;

        [DataMember(Name = "sha256")]
        public string Sha256 { get; set; } = string.Empty;

        [DataMember(Name = "hasSavedFile")]
        public bool HasSavedFile { get; set; } = true;

        [DataMember(Name = "canonicalXml")]
        public string CanonicalXml { get; set; } = string.Empty;

        public static UiConfig From(ModConfigDigest config)
        {
            return new UiConfig
            {
                FileName = config.FileName,
                Sha256 = config.Sha256,
                HasSavedFile = config.HasSavedFile,
                CanonicalXml = config.CanonicalXml
            };
        }
    }

    [DataContract]
    private sealed class UiDefSummary
    {
        [DataMember(Name = "name")]
        public string Name { get; set; } = string.Empty;

        [DataMember(Name = "count")]
        public int Count { get; set; }

        [DataMember(Name = "hash")]
        public int Hash { get; set; }
    }
}

internal sealed class CompatibilityBaselineOverrideWindow : Window
{
    private readonly ClashOfRimMod mod;
    private string stage = T("ClashOfRim.Compatibility.Override.StagePreparing");
    private string result = string.Empty;
    private float progress;
    private bool finished;
    private bool success;

    public CompatibilityBaselineOverrideWindow(ClashOfRimMod mod)
    {
        this.mod = mod;
        absorbInputAroundWindow = true;
        closeOnClickedOutside = false;
        forcePause = false;
        doCloseX = false;
        closeOnCancel = false;
    }

    public override Vector2 InitialSize => new(560f, 260f);

    public override void PreOpen()
    {
        base.PreOpen();
        mod.StartOverrideCompatibilityBaselineFromCurrentClient(UpdateProgress, Finish);
    }

    public override void DoWindowContents(Rect inRect)
    {
        Text.Font = GameFont.Medium;
        Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width, 34f), T("ClashOfRim.Compatibility.Override.Title"));
        Text.Font = GameFont.Small;

        Rect messageRect = new(inRect.x, inRect.y + 48f, inRect.width, 72f);
        Widgets.Label(messageRect, finished ? result : stage);

        Rect barRect = new(inRect.x, inRect.y + 132f, inRect.width, 24f);
        Widgets.DrawBox(barRect);
        Rect fillRect = new(barRect.x + 2f, barRect.y + 2f, (barRect.width - 4f) * Mathf.Clamp01(progress), barRect.height - 4f);
        Color barColor = finished
            ? success ? new Color(0.34f, 0.72f, 0.38f) : new Color(0.82f, 0.28f, 0.24f)
            : new Color(0.35f, 0.62f, 0.95f);
        Widgets.DrawBoxSolid(fillRect, barColor);

        Text.Anchor = TextAnchor.MiddleCenter;
        Widgets.Label(barRect, Mathf.RoundToInt(Mathf.Clamp01(progress) * 100f) + "%");
        Text.Anchor = TextAnchor.UpperLeft;

        if (!finished)
        {
            return;
        }

        Rect buttonRect = new(inRect.xMax - 130f, inRect.yMax - 38f, 130f, 32f);
        if (Widgets.ButtonText(buttonRect, T("ClashOfRim.Compatibility.Override.ReturnMainMenu")))
        {
            Close();
            GenScene.GoToMainMenu();
        }
    }

    private void UpdateProgress(string message, float value)
    {
        stage = message ?? string.Empty;
        progress = Mathf.Clamp01(value);
    }

    private void Finish(string message, bool accepted)
    {
        result = string.IsNullOrWhiteSpace(message)
            ? accepted ? T("ClashOfRim.Compatibility.Override.Success") : T("ClashOfRim.Compatibility.Override.Failed")
            : message;
        success = accepted;
        finished = true;
        progress = accepted ? 1f : Mathf.Max(progress, 0.95f);
    }

    private static string T(string key)
    {
        return ClashOfRimText.Key(key);
    }
}
