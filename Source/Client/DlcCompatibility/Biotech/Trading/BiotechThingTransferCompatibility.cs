using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility;
using RimWorld;
using System.Reflection;
using Verse;

namespace AIRsLight.ClashOfRim.DlcCompatibility;

internal static class BiotechThingTransferCompatibility
{
    private static readonly FieldInfo? OvumFertilizingManField = typeof(HumanOvum).GetField(
        "fertilizingMan",
        BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo? XenogermTargetPawnField = typeof(Xenogerm).GetField(
        "targetPawn",
        BindingFlags.Instance | BindingFlags.NonPublic);

    internal static void Apply()
    {
        ClashOfRimCompatibilityApi.RegisterThingTransferRule(
            BiotechCompatibilityKeys.PackageId + ".thing-transfer",
            null,
            null,
            FinalizeInbound);
    }

    private static bool FinalizeInbound(
        ModThingReferenceDto reference,
        Thing thing,
        ThingTransferContext context,
        out string? missingDefName)
    {
        missingDefName = null;
        switch (thing)
        {
            case HumanOvum ovum:
                OvumFertilizingManField?.SetValue(ovum, null);
                break;
            case HumanEmbryo embryo:
                embryo.implantTarget = null;
                break;
            case Genepack genepack:
                genepack.targetContainer = null;
                break;
            case Xenogerm xenogerm:
                XenogermTargetPawnField?.SetValue(xenogerm, null);
                break;
        }

        return true;
    }
}
