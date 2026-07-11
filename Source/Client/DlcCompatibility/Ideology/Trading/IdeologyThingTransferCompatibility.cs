using AIRsLight.ClashOfRim.ThirdPartyCompatibility;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim.DlcCompatibility;

internal static class IdeologyThingTransferCompatibility
{
    internal const string RelicRejectionCode = "ClashOfRim.ThingTransfer.RejectRelic";

    internal static void Apply()
    {
        ClashOfRimCompatibilityApi.RegisterThingTransferRule(
            IdeologyCompatibilityKeys.PackageId + ".thing-transfer",
            ValidateOutbound,
            null,
            null);
    }

    private static ThingTransferDecision ValidateOutbound(Thing thing, ThingTransferContext context)
    {
        return context.Direction == ThingTransferDirection.Outbound
            && thing.StyleSourcePrecept is Precept_Relic
                ? ThingTransferDecision.Reject(RelicRejectionCode)
                : ThingTransferDecision.Allow();
    }
}
