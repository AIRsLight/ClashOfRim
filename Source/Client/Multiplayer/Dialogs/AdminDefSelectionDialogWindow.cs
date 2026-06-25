using System;
using System.Collections.Generic;
using System.Linq;
using AIRsLight.ClashOfRim.Trades;
using RimWorld;
using UnityEngine;
using Verse;

namespace AIRsLight.ClashOfRim.Multiplayer;

internal sealed class AdminThingDefSelectionDialogWindow : Window
{
    private static List<ThingDef>? cachedSelectableThingDefs;
    private readonly Action<ThingDef> onSelected;
    private Vector2 scrollPosition;
    private string searchText = string.Empty;
    private string? cachedSearchText;
    private List<ThingDef> cachedSearchResults = new();

    public AdminThingDefSelectionDialogWindow(Action<ThingDef> onSelected)
    {
        this.onSelected = onSelected;
        doCloseX = true;
        closeOnAccept = false;
        closeOnClickedOutside = true;
        absorbInputAroundWindow = true;
        forcePause = false;
        draggable = true;
    }

    public override Vector2 InitialSize => new(720f, 620f);

    public override void DoWindowContents(Rect inRect)
    {
        DrawSearchList(
            inRect,
            ClashOfRimText.Key("ClashOfRim.Admin.SelectThingTitle"),
            SearchThingDefsCached,
            def => def.defName,
            def => def.LabelCap.ToString(),
            def => def.description ?? string.Empty,
            def =>
            {
                onSelected(def);
                Close();
            });
    }

    private void DrawSearchList<T>(
        Rect inRect,
        string title,
        Func<string, List<T>> source,
        Func<T, string> idSelector,
        Func<T, string> labelSelector,
        Func<T, string> descriptionSelector,
        Action<T> select)
    {
        Text.Font = GameFont.Medium;
        Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width, 32f), title);
        Text.Font = GameFont.Small;

        searchText = Widgets.TextField(new Rect(inRect.x, inRect.y + 38f, inRect.width, 28f), searchText ?? string.Empty);
        List<T> entries = source(searchText);
        Rect outRect = new(inRect.x, inRect.y + 76f, inRect.width, inRect.height - 76f);
        Rect viewRect = new(0f, 0f, outRect.width - 16f, Math.Max(outRect.height, entries.Count * 42f));
        Widgets.BeginScrollView(outRect, ref scrollPosition, viewRect);
        float y = 0f;
        foreach (T entry in entries)
        {
            Rect row = new(0f, y, viewRect.width, 38f);
            Widgets.DrawHighlightIfMouseover(row);
            string label = labelSelector(entry);
            string id = idSelector(entry);
            TradeUiUtility.DrawTruncatedLabel(new Rect(row.x + 4f, row.y + 3f, row.width - 8f, 20f), label);
            Text.Font = GameFont.Tiny;
            Widgets.Label(new Rect(row.x + 4f, row.y + 22f, row.width - 8f, 16f), id);
            Text.Font = GameFont.Small;
            TooltipHandler.TipRegion(row, descriptionSelector(entry));
            if (Widgets.ButtonInvisible(row))
            {
                select(entry);
            }

            y += 42f;
        }

        Widgets.EndScrollView();
    }

    private List<ThingDef> SearchThingDefsCached(string text)
    {
        string normalized = (text ?? string.Empty).Trim();
        if (cachedSearchText is not null && string.Equals(cachedSearchText, normalized, StringComparison.Ordinal))
        {
            return cachedSearchResults;
        }

        cachedSearchText = normalized;
        cachedSearchResults = SearchThingDefs(normalized).Take(300).ToList();
        return cachedSearchResults;
    }

    private static IEnumerable<ThingDef> SearchThingDefs(string text)
    {
        string normalized = (text ?? string.Empty).Trim();
        IEnumerable<ThingDef> defs = SelectableThingDefs();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return defs;
        }

        return defs.Where(def =>
            def.defName.IndexOf(normalized, StringComparison.OrdinalIgnoreCase) >= 0
            || def.label.IndexOf(normalized, StringComparison.OrdinalIgnoreCase) >= 0);
    }

    private static IReadOnlyList<ThingDef> SelectableThingDefs()
    {
        return cachedSelectableThingDefs ??= DefDatabase<ThingDef>.AllDefsListForReading
            .Where(def => IsPackableBuildingDef(def)
                || TradeThingReferenceUtility.IsBookDef(def)
                || TradeThingReferenceUtility.IsTradeableItemDef(def))
            .Where(def => !def.HasComp(typeof(CompUniqueWeapon)))
            .Where(def => !def.IsCorpse)
            .OrderBy(def => def.label, StringComparer.OrdinalIgnoreCase)
            .ThenBy(def => def.defName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsPackableBuildingDef(ThingDef def)
    {
        return def.category == ThingCategory.Building
            && def.Minifiable
            && def.minifiedDef is not null;
    }
}

internal sealed class AdminIncidentDefSelectionDialogWindow : Window
{
    private static List<IncidentDef>? cachedSelectableIncidentDefs;
    private readonly Action<IncidentDef> onSelected;
    private Vector2 scrollPosition;
    private string searchText = string.Empty;
    private string? cachedSearchText;
    private List<IncidentDef> cachedSearchResults = new();

    public AdminIncidentDefSelectionDialogWindow(Action<IncidentDef> onSelected)
    {
        this.onSelected = onSelected;
        doCloseX = true;
        closeOnAccept = false;
        closeOnClickedOutside = true;
        absorbInputAroundWindow = true;
        forcePause = false;
        draggable = true;
    }

    public override Vector2 InitialSize => new(720f, 620f);

    public override void DoWindowContents(Rect inRect)
    {
        Text.Font = GameFont.Medium;
        Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width, 32f), ClashOfRimText.Key("ClashOfRim.Admin.SelectEventTitle"));
        Text.Font = GameFont.Small;

        searchText = Widgets.TextField(new Rect(inRect.x, inRect.y + 38f, inRect.width, 28f), searchText ?? string.Empty);
        List<IncidentDef> entries = SearchIncidentDefsCached(searchText);
        Rect outRect = new(inRect.x, inRect.y + 76f, inRect.width, inRect.height - 76f);
        Rect viewRect = new(0f, 0f, outRect.width - 16f, Math.Max(outRect.height, entries.Count * 42f));
        Widgets.BeginScrollView(outRect, ref scrollPosition, viewRect);
        float y = 0f;
        foreach (IncidentDef def in entries)
        {
            Rect row = new(0f, y, viewRect.width, 38f);
            Widgets.DrawHighlightIfMouseover(row);
            TradeUiUtility.DrawTruncatedLabel(new Rect(row.x + 4f, row.y + 3f, row.width - 8f, 20f), def.LabelCap.ToString());
            Text.Font = GameFont.Tiny;
            Widgets.Label(new Rect(row.x + 4f, row.y + 22f, row.width - 8f, 16f), def.defName);
            Text.Font = GameFont.Small;
            TooltipHandler.TipRegion(row, def.description ?? string.Empty);
            if (Widgets.ButtonInvisible(row))
            {
                onSelected(def);
                Close();
            }

            y += 42f;
        }

        Widgets.EndScrollView();
    }

    private List<IncidentDef> SearchIncidentDefsCached(string text)
    {
        string normalized = (text ?? string.Empty).Trim();
        if (cachedSearchText is not null && string.Equals(cachedSearchText, normalized, StringComparison.Ordinal))
        {
            return cachedSearchResults;
        }

        cachedSearchText = normalized;
        cachedSearchResults = SearchIncidentDefs(normalized).Take(300).ToList();
        return cachedSearchResults;
    }

    private static IEnumerable<IncidentDef> SearchIncidentDefs(string text)
    {
        string normalized = (text ?? string.Empty).Trim();
        IEnumerable<IncidentDef> defs = SelectableIncidentDefs();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return defs;
        }

        return defs.Where(def =>
            def.defName.IndexOf(normalized, StringComparison.OrdinalIgnoreCase) >= 0
            || def.label.IndexOf(normalized, StringComparison.OrdinalIgnoreCase) >= 0);
    }

    private static IReadOnlyList<IncidentDef> SelectableIncidentDefs()
    {
        return cachedSelectableIncidentDefs ??= DefDatabase<IncidentDef>.AllDefsListForReading
            .Where(IsConfigurableDebtPenaltyIncident)
            .OrderBy(def => def.label, StringComparer.OrdinalIgnoreCase)
            .ThenBy(def => def.defName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsConfigurableDebtPenaltyIncident(IncidentDef def)
    {
        return def.category is not null
            && def.targetTags is not null
            && (def.TargetTagAllowed(IncidentTargetTagDefOf.Map_PlayerHome)
                || def.TargetTagAllowed(IncidentTargetTagDefOf.Map_Misc)
                || def.TargetTagAllowed(IncidentTargetTagDefOf.Map_RaidBeacon))
            && def.category != IncidentCategoryDefOf.GiveQuest
            && !def.defName.StartsWith("Quest", StringComparison.OrdinalIgnoreCase);
    }
}
