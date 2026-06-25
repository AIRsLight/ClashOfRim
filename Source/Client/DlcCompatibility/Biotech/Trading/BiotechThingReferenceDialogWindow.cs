using System;
using System.Collections.Generic;
using System.Linq;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Trades;
using RimWorld;
using UnityEngine;
using Verse;

namespace AIRsLight.ClashOfRim.DlcCompatibility;

internal sealed class BiotechThingReferenceDialogWindow : Window
{
    private static readonly Vector2 EntrySize = new(140f, 90f);
    private const float EntryGap = 8f;

    private readonly ModThingReferenceDto target;
    private Vector2 scrollPosition;
    private string searchText = string.Empty;

    public BiotechThingReferenceDialogWindow(ModThingReferenceDto target)
    {
        this.target = target;
        doCloseX = true;
        closeOnAccept = false;
        closeOnClickedOutside = false;
        absorbInputAroundWindow = true;
        forcePause = false;
        draggable = true;
    }

    public override Vector2 InitialSize => new(760f, 620f);

    public override void DoWindowContents(Rect inRect)
    {
        Text.Font = GameFont.Medium;
        Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width, 32f), ClashOfRimText.Key("ClashOfRim.Shop.SelectGenesTitle"));
        Text.Font = GameFont.Small;

        Rect searchRect = new(inRect.x, inRect.y + 38f, inRect.width, 28f);
        searchText = Widgets.TextField(searchRect, searchText ?? string.Empty);

        Rect footerRect = new(inRect.x, inRect.yMax - 36f, inRect.width, 32f);
        if (Widgets.ButtonText(new Rect(footerRect.x, footerRect.y, 110f, 32f), ClashOfRimText.Key("ClashOfRim.Clear")))
        {
            BiotechCompatibility.SetGeneDefNames(target, Array.Empty<string>());
            BiotechCompatibility.SetTargetGeneDefName(target, null);
        }

        Widgets.Label(
            new Rect(footerRect.x + 120f, footerRect.y + 6f, footerRect.width - 280f, 24f),
            BiotechCompatibility.FormatGeneList(BiotechCompatibility.GeneDefNames(target)));
        if (Widgets.ButtonText(new Rect(footerRect.xMax - 120f, footerRect.y, 120f, 32f), ClashOfRimText.Key("ClashOfRim.Done")))
        {
            Close();
        }

        List<GeneDef> genes = FilterGenes(searchText).ToList();
        Rect outRect = new(inRect.x, searchRect.yMax + 10f, inRect.width, inRect.height - searchRect.yMax - 56f);
        int columns = Math.Max(1, Mathf.FloorToInt((outRect.width - 16f + EntryGap) / (EntrySize.x + EntryGap)));
        int rows = Mathf.CeilToInt(genes.Count / (float)columns);
        Rect viewRect = new(0f, 0f, outRect.width - 16f, Math.Max(outRect.height, rows * (EntrySize.y + EntryGap)));

        Widgets.BeginScrollView(outRect, ref scrollPosition, viewRect);
        for (int index = 0; index < genes.Count; index++)
        {
            int column = index % columns;
            int row = index / columns;
            GeneDef gene = genes[index];
            Rect geneRect = new(
                column * (EntrySize.x + EntryGap),
                row * (EntrySize.y + EntryGap),
                EntrySize.x,
                EntrySize.y);
            bool selected = BiotechCompatibility.GeneDefNames(target).Any(defName => string.Equals(defName, gene.defName, StringComparison.OrdinalIgnoreCase));
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
            }
        }

        Widgets.EndScrollView();
    }

    private void ToggleGene(string defName)
    {
        List<string> geneDefNames = BiotechCompatibility.GeneDefNames(target).ToList();
        int existing = geneDefNames.FindIndex(candidate => string.Equals(candidate, defName, StringComparison.OrdinalIgnoreCase));
        if (existing >= 0)
        {
            geneDefNames.RemoveAt(existing);
        }
        else
        {
            geneDefNames.Add(defName);
        }

        BiotechCompatibility.SetGeneDefNames(target, geneDefNames);
        BiotechCompatibility.SetTargetGeneDefName(target, null);
        target.DisplayLabel = null;
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
