using System;
using System.Collections.Generic;
using System.Linq;
using AIRsLight.ClashOfRim.ClientNetwork;
using RimWorld;
using UnityEngine;
using Verse;

namespace AIRsLight.ClashOfRim.DlcCompatibility;

internal sealed class BiotechGeneSelectionDialogWindow : Window
{
    private static readonly Vector2 GeneSize = new(140f, 90f);
    private const float GeneGap = 8f;

    private readonly ModThingReferenceDto target;
    private Vector2 scrollPosition;
    private string searchText = string.Empty;

    public BiotechGeneSelectionDialogWindow(ModThingReferenceDto target)
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
        Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width, 32f), ClashOfRimText.Key("ClashOfRim.Trade.SelectGeneTitle"));
        Text.Font = GameFont.Small;

        Rect searchRect = new(inRect.x, inRect.y + 38f, inRect.width, 28f);
        searchText = Widgets.TextField(searchRect, searchText ?? string.Empty);

        Rect actionsRect = new(inRect.x, searchRect.yMax + 8f, inRect.width, 30f);
        if (Widgets.ButtonText(new Rect(actionsRect.x, actionsRect.y, 120f, 28f), ClashOfRimText.Key("ClashOfRim.Clear")))
        {
            BiotechCompatibility.SetTargetGeneDefNames(target, Array.Empty<string>());
            target.DisplayLabel = null;
        }

        if (Widgets.ButtonText(new Rect(actionsRect.xMax - 120f, actionsRect.y, 120f, 28f), ClashOfRimText.Key("ClashOfRim.Done")))
        {
            Close();
        }

        List<GeneDef> genes = FilterGenes(searchText).ToList();
        IReadOnlyList<string> selectedGeneDefNames = BiotechCompatibility.TargetGeneDefNames(target);
        Rect outRect = new(inRect.x, actionsRect.yMax + 10f, inRect.width, inRect.height - actionsRect.yMax - 10f);
        int columns = Math.Max(1, Mathf.FloorToInt((outRect.width - 16f + GeneGap) / (GeneSize.x + GeneGap)));
        int rows = Mathf.CeilToInt(genes.Count / (float)columns);
        Rect viewRect = new(0f, 0f, outRect.width - 16f, Math.Max(outRect.height, rows * (GeneSize.y + GeneGap)));

        Widgets.BeginScrollView(outRect, ref scrollPosition, viewRect);
        for (int index = 0; index < genes.Count; index++)
        {
            int column = index % columns;
            int row = index / columns;
            GeneDef gene = genes[index];
            Rect geneRect = new(
                column * (GeneSize.x + GeneGap),
                row * (GeneSize.y + GeneGap),
                GeneSize.x,
                GeneSize.y);
            bool selected = selectedGeneDefNames.Any(selectedDefName => string.Equals(
                selectedDefName,
                gene.defName,
                StringComparison.OrdinalIgnoreCase));
            if (selected)
            {
                Widgets.DrawHighlightSelected(geneRect);
            }

            GeneUIUtility.DrawGeneDef(
                gene,
                geneRect,
                GeneType.Xenogene,
                () => gene.description,
                doBackground: true,
                clickable: false);
            if (Widgets.ButtonInvisible(geneRect))
            {
                ToggleGene(gene.defName);
                selectedGeneDefNames = BiotechCompatibility.TargetGeneDefNames(target);
                target.DisplayLabel = null;
            }
        }

        Widgets.EndScrollView();
    }

    private void ToggleGene(string geneDefName)
    {
        List<string> selected = BiotechCompatibility.TargetGeneDefNames(target).ToList();
        int existingIndex = selected.FindIndex(candidate => string.Equals(
            candidate,
            geneDefName,
            StringComparison.OrdinalIgnoreCase));
        if (existingIndex >= 0)
        {
            selected.RemoveAt(existingIndex);
        }
        else
        {
            selected.Add(geneDefName);
        }

        BiotechCompatibility.SetTargetGeneDefNames(target, selected);
    }

    private static IEnumerable<GeneDef> FilterGenes(string query)
    {
        IEnumerable<GeneDef> genes = GeneUtility.GenesInOrder;
        string normalized = (query ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return genes;
        }

        return genes.Where(gene =>
            gene.defName.IndexOf(normalized, StringComparison.OrdinalIgnoreCase) >= 0
            || gene.label.IndexOf(normalized, StringComparison.OrdinalIgnoreCase) >= 0);
    }
}
