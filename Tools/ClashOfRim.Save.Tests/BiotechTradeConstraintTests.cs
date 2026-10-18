using AIRsLight.ClashOfRim.Network.Plugins.DlcCompatibility;
using AIRsLight.ClashOfRim.Protocol;
using ClashOfRim.TestSupport;

internal static class BiotechTradeConstraintTests
{
    internal static void Run()
    {
        ServerTestLocalization.LoadEnglish();
        var matcher = new BiotechTradeThingMetadataMatcher();
        BiotechTradeConstraintCases.Run(
            (required, candidate) => matcher.Matches(new ThingReferenceDto("required", null, 1, metadata: required), new ThingReferenceDto("candidate", null, 1, metadata: candidate)),
            required => matcher.RequirementStrictness(new ThingReferenceDto("required", null, 1, metadata: required)),
            required => matcher.DescribeConstraints(new ThingReferenceDto("required", null, 1, metadata: required)).Count);
    }
}
