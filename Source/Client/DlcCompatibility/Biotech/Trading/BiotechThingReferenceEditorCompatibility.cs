using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility;
using AIRsLight.ClashOfRim.Trades;
using RimWorld;
using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace AIRsLight.ClashOfRim.DlcCompatibility;

internal static partial class BiotechCompatibility
{
    internal static bool DrawThingReferenceEditor(
        string surface,
        ThingDef? def,
        ModThingReferenceDto item,
        Rect rect,
        out float consumedHeight)
    {
        consumedHeight = 0f;
        if (!HasBiotechTradeMetadata
            || !IsGeneSetHolderDef(def))
        {
            return false;
        }

        if (string.Equals(surface, ThingReferenceSurfaces.TradeRequest, StringComparison.Ordinal))
        {
            IReadOnlyList<string> targetGeneDefNames = TargetGeneDefNames(item);
            string requestLabel = targetGeneDefNames.Count == 0
                ? ClashOfRimText.Key("ClashOfRim.Trade.SelectGene")
                : FormatGeneList(targetGeneDefNames);
            if (ClashOfRimUiUtility.SelectionButton(
                    new Rect(rect.x, rect.y, 150f, rect.height),
                    requestLabel))
            {
                Find.WindowStack.Add(new BiotechGeneSelectionDialogWindow(item));
            }

            consumedHeight = rect.height;
            return true;
        }

        if (!string.Equals(surface, ThingReferenceSurfaces.ServerShopListing, StringComparison.Ordinal))
        {
            return false;
        }

        IReadOnlyList<string> geneDefNames = GeneDefNames(item);
        string listingLabel = geneDefNames.Count == 0
            ? ClashOfRimText.Key("ClashOfRim.Shop.SelectGenes")
            : FormatGeneList(geneDefNames);
        if (ClashOfRimUiUtility.SelectionButton(
                new Rect(rect.x + 130f, rect.y, rect.width - 130f, 28f),
                listingLabel))
        {
            Find.WindowStack.Add(new BiotechThingReferenceDialogWindow(item));
        }

        Widgets.Label(new Rect(rect.x, rect.y + 3f, 120f, 24f), ClashOfRimText.Key("ClashOfRim.Shop.Genes"));
        consumedHeight = 38f;
        return true;
    }
}
