using AIRsLight.ClashOfRim.DlcCompatibility.Biotech.Items;
using System;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.DlcCompatibility;
using AIRsLight.ClashOfRim.Modules.Compatibility.Biotech;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility;
using ClashOfRim.TestSupport;
using Verse;

internal static class BiotechTradeConstraintTests
{
    private static readonly BiotechThingReferenceCompatibility Items = new(
        () => ClashOfRimCompatibilityApi.HasCompatibilityCapability(BiotechCompatibilityKeys.TradeMetadata), new BiotechReproductiveSources(() => null));

    internal static void Run()
    {
        ClashOfRimCompatibilityApi.BeginRegistrationCycle("biotech-constraints");
        ClashOfRimCompatibilityApi.ApplyServerDlcBaseline(null);
        ClashOfRimCompatibilityApi.RegisterCompatibilityPackage(BiotechCompatibilityKeys.PackageId, () => true, new[] { BiotechCompatibilityKeys.TradeMetadata });
        try
        {
            BiotechTradeConstraintCases.Run(
                (required, candidate) => Items.ThingReferenceDtoMatches(new ModThingReferenceDto { Metadata = required! }, new ModThingReferenceDto { Metadata = candidate! }),
                required => Items.ThingReferenceStrictness(new ModThingReferenceDto { Metadata = required! }));
            RuntimeSourceProjectionAndDlcGate();
        }
        finally
        {
            ClashOfRimCompatibilityApi.ApplyServerDlcBaseline(null);
            ClashOfRimCompatibilityApi.BeginRegistrationCycle("client-tests");
        }
    }

    private static void RuntimeSourceProjectionAndDlcGate()
    {
        var requirement = new ModThingReferenceDto();
        requirement.Metadata[BiotechThingMetadataKeys.ReproductiveSourceCount] = "1";
        BiotechThingMetadata.WriteReproductiveSourceMetadata(requirement, 0, Source("Mother"));
        if (Items.ThingReferenceMatches(requirement, new Thing())
            || BiotechThingMetadata.ReproductiveSourceRequirementMatches(requirement, new[] { Source("Father") })
            || !BiotechThingMetadata.ReproductiveSourceRequirementMatches(requirement, new[] { Source("mother") }))
            throw new Exception("Runtime source matching ignored role-only trade constraints.");

        BiotechThingMetadata.WriteReproductiveSourceMetadata(requirement, 0,
            new ReproductiveSourceRecord(null, "pawn-id", null, null, null, null, null, false, null, null, null, null));
        if (!Items.ThingReferenceMatches(requirement, new Thing())
            || BiotechThingMetadata.ReproductiveSources(requirement).Count != 1)
            throw new Exception("Identity-only restoration data was lost or treated as a runtime trade constraint.");

        BiotechThingMetadata.WriteReproductiveSourceMetadata(requirement, 0, Source("Mother"));
        ClashOfRimCompatibilityApi.ApplyServerDlcBaseline(Array.Empty<string>());
        if (!Items.ThingReferenceMatches(requirement, new Thing())
            || !Items.ThingReferenceDtoMatches(requirement, new ModThingReferenceDto())
            || Items.ThingReferenceStrictness(requirement) != 0)
            throw new Exception("Shared constraint matching bypassed the Biotech capability gate.");
    }

    private static ReproductiveSourceRecord Source(string role) =>
        new(role, null, null, null, null, null, null, false, null, null, null, null);
}
