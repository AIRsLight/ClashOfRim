using AIRsLight.ClashOfRim.DlcCompatibility.Biotech.Items;
using System;
using System.Linq;
using AIRsLight.ClashOfRim.ClientNetwork;
using RimWorld;
using Verse;

internal static class GeneHolderTransferTests
{
    public static void Run()
    {
        var items = new BiotechThingReferenceCompatibility(() => true, new BiotechReproductiveSources(() => null));
        var genes = new[] { new GeneDef { defName = "TransferGeneA" }, new GeneDef { defName = "TransferGeneB" } };
        foreach (var gene in genes) DefDatabase<GeneDef>.Add(gene);
        foreach (GeneSetHolderBase item in new GeneSetHolderBase[] { new HumanEmbryo(), new Genepack() })
        {
            item.def = new ThingDef { defName = item.GetType().Name, thingClass = item.GetType() };
            var reference = new ModThingReferenceDto(); reference.Metadata["rimworld.biotech.geneDefNames"] = "TransferGeneA,TransferGeneB";
            bool Apply() => items.TryApplyThingReferenceMetadata(reference, item, out _);
            if (!Apply() || !Apply() || item.GeneSet is null || !item.GeneSet.GenesListForReading.SequenceEqual(genes))
                throw new Exception("Gene holder lost selected genes or duplicated them on reapplication: " + item.GetType().Name);
            reference.Metadata["rimworld.biotech.geneDefNames"] = "MissingTransferGene";
            if (Apply() || !item.GeneSet.GenesListForReading.SequenceEqual(genes))
                throw new Exception("Unknown gene was silently dropped or partially replaced the existing set.");
        }
    }
}
