using System;
using System.Linq;
using System.Reflection;
using AIRsLight.ClashOfRim;
using AIRsLight.ClashOfRim.ClientNetwork;
using HarmonyLib;
using RimWorld;
using Verse;

internal static class GeneHolderTransferTests
{
    private static bool Enabled(ref bool __result) { __result = true; return false; }
    public static void Run()
    {
        var type = typeof(ClashOfRimMod).Assembly.GetType("AIRsLight.ClashOfRim.DlcCompatibility.BiotechCompatibility", true)!;
        var method = type.GetMethod("TryApplyThingReferenceMetadata", BindingFlags.Static | BindingFlags.NonPublic)!;
        var harmony = new Harmony("ClashOfRim.Tests.GeneHolderTransfer");
        var genes = new[] { new GeneDef { defName = "TransferGeneA" }, new GeneDef { defName = "TransferGeneB" } };
        foreach (var gene in genes) DefDatabase<GeneDef>.Add(gene);
        try
        {
            harmony.Patch(AccessTools.PropertyGetter(type, "HasBiotechTradeMetadata"), prefix: new HarmonyMethod(typeof(GeneHolderTransferTests), nameof(Enabled)));
            foreach (GeneSetHolderBase item in new GeneSetHolderBase[] { new HumanEmbryo(), new Genepack() })
            {
                item.def = new ThingDef { defName = item.GetType().Name, thingClass = item.GetType() };
                var reference = new ModThingReferenceDto(); reference.Metadata["rimworld.biotech.geneDefNames"] = "TransferGeneA,TransferGeneB";
                bool Apply() => (bool)method.Invoke(null, new object?[] { reference, item, null })!;
                if (!Apply() || !Apply() || item.GeneSet is null || !item.GeneSet.GenesListForReading.SequenceEqual(genes))
                    throw new Exception("Gene holder lost selected genes or duplicated them on reapplication: " + item.GetType().Name);
                reference.Metadata["rimworld.biotech.geneDefNames"] = "MissingTransferGene";
                if (Apply() || !item.GeneSet.GenesListForReading.SequenceEqual(genes))
                    throw new Exception("Unknown gene was silently dropped or partially replaced the existing set.");
            }
        }
        finally { harmony.UnpatchAll(harmony.Id); }
    }
}
