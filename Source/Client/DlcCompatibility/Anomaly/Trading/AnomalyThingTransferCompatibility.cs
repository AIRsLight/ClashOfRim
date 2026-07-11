using AIRsLight.ClashOfRim.ThirdPartyCompatibility;
using Verse;

namespace AIRsLight.ClashOfRim.DlcCompatibility;

internal static class AnomalyThingTransferCompatibility
{
    internal const string UnnaturalCorpseRejectionCode = "ClashOfRim.ThingTransfer.RejectUnnaturalCorpse";

    internal static void Apply()
    {
        ClashOfRimCompatibilityApi.RegisterThingTransferRule(
            "clashofrim.anomaly.thing-transfer",
            ValidateOutbound,
            null,
            null);
    }

    private static ThingTransferDecision ValidateOutbound(Thing thing, ThingTransferContext context)
    {
        return context.Direction == ThingTransferDirection.Outbound && thing is UnnaturalCorpse
            ? ThingTransferDecision.Reject(UnnaturalCorpseRejectionCode)
            : ThingTransferDecision.Allow();
    }
}
