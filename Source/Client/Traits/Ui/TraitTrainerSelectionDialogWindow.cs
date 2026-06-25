using System;
using System.Collections.Generic;
using System.Linq;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Trades;
using UnityEngine;
using Verse;

namespace AIRsLight.ClashOfRim.Traits;

internal sealed class TraitTrainerSelectionDialogWindow : Window
{
    private readonly ModThingReferenceDto target;
    private Vector2 scrollPosition;
    private string searchText = string.Empty;
    private string? cachedSearchText;
    private List<TraitSelection> cachedTraits = new();

    public TraitTrainerSelectionDialogWindow(ModThingReferenceDto target)
    {
        this.target = target;
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
        Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width, 32f), ClashOfRimText.Key("ClashOfRim.TraitTrainer.SelectTitle"));
        Text.Font = GameFont.Small;

        Rect searchRect = new(inRect.x, inRect.y + 38f, inRect.width, 28f);
        searchText = Widgets.TextField(searchRect, searchText ?? string.Empty);

        List<TraitSelection> traits = FilterTraitsCached(searchText);
        Rect outRect = new(inRect.x, searchRect.yMax + 10f, inRect.width, inRect.height - searchRect.yMax - 10f);
        Rect viewRect = new(0f, 0f, outRect.width - 16f, Math.Max(outRect.height, traits.Count * 42f));
        Widgets.BeginScrollView(outRect, ref scrollPosition, viewRect);
        for (int index = 0; index < traits.Count; index++)
        {
            TraitSelection selection = traits[index];
            Rect row = new(0f, index * 42f, viewRect.width, 40f);
            DrawTraitRow(row, selection);
        }

        Widgets.EndScrollView();
    }

    private void DrawTraitRow(Rect row, TraitSelection selection)
    {
        bool selected = string.Equals(
                TraitTrainerUtility.TraitDefName(target),
                selection.Def.defName,
                StringComparison.OrdinalIgnoreCase)
            && TraitTrainerUtility.TraitDegree(target) == selection.Degree;
        if (selected)
        {
            Widgets.DrawHighlightSelected(row);
        }
        else
        {
            Widgets.DrawHighlightIfMouseover(row);
        }

        string label = TraitTrainerUtility.TraitLabel(selection.Def.defName, selection.Degree);
        Text.Font = GameFont.Small;
        TradeUiUtility.DrawTruncatedLabel(new Rect(row.x + 6f, row.y + 2f, row.width - 12f, 20f), label);
        Text.Font = GameFont.Tiny;
        string description = TraitTrainerUtility.DegreeData(selection.Def, selection.Degree)?.description ?? selection.Def.description;
        TradeUiUtility.DrawTruncatedLabel(new Rect(row.x + 6f, row.y + 21f, row.width - 12f, 18f), description);
        Text.Font = GameFont.Small;
        TooltipHandler.TipRegion(row, description);

        if (Widgets.ButtonInvisible(row))
        {
            TraitTrainerUtility.SetTrait(target, selection.Def, selection.Degree);
            Close();
        }
    }

    private List<TraitSelection> FilterTraitsCached(string query)
    {
        string normalized = (query ?? string.Empty).Trim();
        if (cachedSearchText is not null && string.Equals(cachedSearchText, normalized, StringComparison.Ordinal))
        {
            return cachedTraits;
        }

        cachedSearchText = normalized;
        cachedTraits = FilterTraits(normalized).ToList();
        return cachedTraits;
    }

    private static IEnumerable<TraitSelection> FilterTraits(string query)
    {
        IEnumerable<TraitSelection> traits = TraitTrainerUtility.AllTraitSelections();
        if (string.IsNullOrWhiteSpace(query))
        {
            return traits;
        }

        return traits.Where(selection =>
            selection.Def.defName.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
            || TraitTrainerUtility.TraitLabel(selection.Def.defName, selection.Degree).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0);
    }
}
