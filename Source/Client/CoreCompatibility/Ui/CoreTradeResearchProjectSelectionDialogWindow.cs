using System;
using System.Collections.Generic;
using System.Linq;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Trades;
using RimWorld;
using UnityEngine;
using Verse;

namespace AIRsLight.ClashOfRim.CoreCompatibility;

internal sealed class TradeResearchProjectSelectionDialogWindow : Window
{
    private readonly ModThingReferenceDto target;
    private readonly bool requirementMode;
    private Vector2 scrollPosition;
    private string searchText = string.Empty;
    private string? cachedSearchText;
    private List<ResearchProjectDef> cachedProjects = new();

    public TradeResearchProjectSelectionDialogWindow(ModThingReferenceDto target, bool requirementMode)
    {
        this.target = target;
        this.requirementMode = requirementMode;
        doCloseX = true;
        closeOnAccept = false;
        closeOnClickedOutside = true;
        absorbInputAroundWindow = true;
        forcePause = false;
        draggable = true;
    }

    public override Vector2 InitialSize => new(760f, 620f);

    public override void DoWindowContents(Rect inRect)
    {
        Text.Font = GameFont.Medium;
        Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width, 32f), ClashOfRimText.Key("ClashOfRim.Trade.SelectResearchProjectTitle"));
        Text.Font = GameFont.Small;

        Rect searchRect = new(inRect.x, inRect.y + 38f, inRect.width, 28f);
        searchText = Widgets.TextField(searchRect, searchText ?? string.Empty);

        List<ResearchProjectDef> projects = FilterProjectsCached(searchText);
        Rect outRect = new(inRect.x, searchRect.yMax + 10f, inRect.width, inRect.height - searchRect.yMax - 10f);
        Rect viewRect = new(0f, 0f, outRect.width - 16f, Math.Max(outRect.height, projects.Count * 38f));
        Widgets.BeginScrollView(outRect, ref scrollPosition, viewRect);
        for (int index = 0; index < projects.Count; index++)
        {
            ResearchProjectDef project = projects[index];
            Rect row = new(0f, index * 38f, viewRect.width, 36f);
            DrawProjectRow(row, project);
        }

        Widgets.EndScrollView();
    }

    private void DrawProjectRow(Rect row, ResearchProjectDef project)
    {
        ThingDef? projectThing = CoreThingReferenceMetadata.ProjectThingDefForReference(target, project);
        if (projectThing is null)
        {
            return;
        }

        string? selectedProject = requirementMode
            ? CoreThingReferenceMetadata.TargetResearchProjectDefName(target)
            : CoreThingReferenceMetadata.ResearchProjectDefName(target);
        bool selected = string.Equals(selectedProject, project.defName, StringComparison.OrdinalIgnoreCase);
        if (selected)
        {
            Widgets.DrawHighlightSelected(row);
        }
        else
        {
            Widgets.DrawHighlightIfMouseover(row);
        }

        Rect iconRect = new(row.x + 3f, row.y + 3f, TradeUiUtility.IconSize, TradeUiUtility.IconSize);
        bool openedInfo = TradeUiUtility.DrawThingIconWithInfo(iconRect, projectThing.defName);
        Rect labelRect = new(iconRect.xMax + 8f, row.y, row.width - iconRect.width - 16f, row.height);
        TradeUiUtility.DrawTruncatedLabel(labelRect, project.LabelCap);
        TooltipHandler.TipRegion(row, project.description);
        if (!openedInfo && Widgets.ButtonInvisible(row))
        {
            ApplySelection(project);
            Close();
        }
    }

    private void ApplySelection(ResearchProjectDef project)
    {
        ThingDef? projectThing = CoreThingReferenceMetadata.ProjectThingDefForReference(target, project);
        if (projectThing is null)
        {
            return;
        }

        target.DefName = projectThing.defName;
        target.DisplayLabel = projectThing.LabelCap;
        CoreThingReferenceMetadata.SetResearchProjectDefName(target, requirementMode ? null : project.defName);
        CoreThingReferenceMetadata.SetTargetResearchProjectDefName(target, requirementMode ? project.defName : null);
    }

    private List<ResearchProjectDef> FilterProjectsCached(string query)
    {
        string normalized = (query ?? string.Empty).Trim();
        if (cachedSearchText is not null && string.Equals(cachedSearchText, normalized, StringComparison.Ordinal))
        {
            return cachedProjects;
        }

        cachedSearchText = normalized;
        cachedProjects = FilterProjects(normalized).ToList();
        return cachedProjects;
    }

    private IEnumerable<ResearchProjectDef> FilterProjects(string query)
    {
        IEnumerable<ResearchProjectDef> projects = CoreThingReferenceMetadata.ResearchProjectsForReference(target);
        string normalized = (query ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return projects;
        }

        return projects.Where(project =>
            project.defName.IndexOf(normalized, StringComparison.OrdinalIgnoreCase) >= 0
            || project.label.IndexOf(normalized, StringComparison.OrdinalIgnoreCase) >= 0);
    }
}
