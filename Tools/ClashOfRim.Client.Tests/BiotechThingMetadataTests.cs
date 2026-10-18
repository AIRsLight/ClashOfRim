using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.DlcCompatibility;
using AIRsLight.ClashOfRim.Modules.Compatibility.Biotech;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility;

internal static class BiotechThingMetadataTests
{
    internal static void Run()
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            foreach (string culture in new[] { "en-US", "zh-CN", "tr-TR", "ja-JP" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                WireAndGeneCodec();
                SourceRoundTripAndBounds();
                MatchingPolicies();
                SharedDisplayCache();
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
            ClashOfRimCompatibilityApi.ApplyServerDlcBaseline(null);
            ClashOfRimCompatibilityApi.BeginRegistrationCycle("client-tests");
        }
    }

    private static void WireAndGeneCodec()
    {
        Check(BiotechThingMetadataKeys.GeneDefNames == "rimworld.biotech.geneDefNames"
            && BiotechThingMetadataKeys.TargetGeneDefNames == "rimworld.biotech.targetGeneDefName"
            && BiotechThingMetadataKeys.XenotypeName == "rimworld.biotech.xenotypeName"
            && BiotechThingMetadataKeys.XenotypeIconDefName == "rimworld.biotech.xenotypeIconDefName"
            && BiotechThingMetadataKeys.ReproductiveSourceCount == "rimworld.biotech.reproductiveSource.count"
            && BiotechThingMetadataKeys.MaxReproductiveSources == 4, "Persisted metadata keys or source bound changed.");
        var dto = new ModThingReferenceDto { Metadata = null! };
        Check(BiotechThingMetadata.GeneDefNames(dto).Count == 0 && dto.Metadata is not null, "Reading legacy null metadata failed to normalize storage.");
        var genes = new List<string> { " I ", "i", "İ", "", " A ", null! };
        BiotechThingMetadata.SetGeneDefNames(dto, genes); genes.Clear();
        Check(dto.Metadata!["rimworld.biotech.geneDefNames"] == "I,İ,A"
            && BiotechThingMetadata.GeneDefNames(dto).SequenceEqual(new[] { "I", "İ", "A" }), "CSV normalization changed ordinal case, stable order or copy ownership.");
        BiotechThingMetadata.SetTargetGeneDefNames(dto, new[] { " B ", "b", "A" });
        Check(BiotechThingMetadata.TargetGeneDefName(dto) == "B" && BiotechThingMetadata.TargetGeneDefNames(dto).SequenceEqual(new[] { "B", "A" }), "Multi-gene requirements lost their first-gene compatibility accessor.");
        BiotechThingMetadata.SetXenotypeName(dto, " 文化 İ "); BiotechThingMetadata.SetXenotypeIconDefName(dto, " Icon ");
        Check(BiotechThingMetadata.XenotypeName(dto) == " 文化 İ " && BiotechThingMetadata.XenotypeIconDefName(dto) == " Icon ", "Text metadata was unexpectedly trimmed.");
        BiotechThingMetadata.SetGeneDefNames(dto, null); BiotechThingMetadata.SetTargetGeneDefName(dto, " ");
        BiotechThingMetadata.SetXenotypeName(dto, null); BiotechThingMetadata.SetXenotypeIconDefName(dto, " ");
        Check(dto.Metadata.Count == 0 && BiotechThingMetadata.TargetGeneDefName(dto) is null, "Empty metadata setters retained obsolete values.");
        BiotechThingMetadata.Normalize(null); BiotechThingMetadata.SetGeneDefNames(null, new[] { "A" });
        Check(BiotechThingMetadata.GeneDefNames(null).Count == 0 && BiotechThingMetadata.ReproductiveSources(null).Count == 0, "Null metadata references changed empty-read semantics.");
    }

    private static void SourceRoundTripAndBounds()
    {
        var dto = new ModThingReferenceDto();
        var record = new ReproductiveSourceRecord("Mother", "global", "owner", " 角色 İ ", "Human", "Colonist", "Female", true,
            "PlayerColony", "Xenotype", new[] { " A ", "a", "B" }, new[] { " X " });
        dto.Metadata[BiotechThingMetadataKeys.ReproductiveSourceCount] = "1";
        BiotechThingMetadata.WriteReproductiveSourceMetadata(dto, 0, record);
        var actual = BiotechThingMetadata.ReproductiveSources(dto).Single();
        Check(actual.Role == "Mother" && actual.GlobalId == "global" && actual.OwnerUserId == "owner" && actual.Name == " 角色 İ "
            && actual.RaceDefName == "Human" && actual.PawnKindDefName == "Colonist" && actual.Gender == "Female" && actual.Dead
            && actual.FactionDefName == "PlayerColony" && actual.XenotypeDefName == "Xenotype"
            && actual.EndogeneDefNames.SequenceEqual(new[] { "A", "B" }) && actual.XenogeneDefNames.Single() == "X",
            "Reproductive source roundtrip lost identity/state or diverged from the shared gene-list codec.");
        Check(dto.Metadata["rimworld.biotech.reproductiveSource.0.dead"] == "true"
            && dto.Metadata["rimworld.biotech.reproductiveSource.0.endogeneDefNames"] == "A,B", "Indexed wire fields changed.");
        for (int i = 0; i < 5; i++) BiotechThingMetadata.WriteReproductiveSourceMetadata(dto, i, Source("Mother", "Human", new[] { "A" }));
        dto.Metadata[BiotechThingMetadataKeys.ReproductiveSourceCount] = int.MaxValue.ToString(CultureInfo.InvariantCulture);
        Check(BiotechThingMetadata.ReproductiveSources(dto).Count == 4, "Oversized source counts bypassed the four-source bound.");
        foreach (string count in new[] { "-1", "0", "1,000", "2147483648", "invalid", "" })
        {
            dto.Metadata[BiotechThingMetadataKeys.ReproductiveSourceCount] = count;
            Check(BiotechThingMetadata.ReproductiveSources(dto).Count == 0, "Invalid source count was accepted: " + count);
        }
        dto.Metadata[BiotechThingMetadataKeys.ReproductiveSourceCount] = "2";
        BiotechThingMetadata.WriteReproductiveSourceMetadata(dto, 0, Source("Mother", null));
        BiotechThingMetadata.WriteReproductiveSourceMetadata(dto, 1, new ReproductiveSourceRecord(null, "identity-only", null, null, null, null, null, false, null, null, null, null));
        Check(BiotechThingMetadata.ReproductiveSources(dto).Single().GlobalId == "identity-only", "Client source eligibility changed for role-only/identity-only records.");
        dto.Metadata["foreign"] = "keep"; dto.Metadata["RIMWORLD.biotech.reproductiveSource.0.name"] = "keep-case";
        BiotechThingMetadata.SetGeneDefNames(dto, new[] { "A" }); BiotechThingMetadata.Clear(dto);
        Check(dto.Metadata.Count == 2 && dto.Metadata["foreign"] == "keep" && dto.Metadata.ContainsKey("RIMWORLD.biotech.reproductiveSource.0.name"),
            "Clearing Biotech metadata removed unrelated/case-distinct keys or retained stale indexed fields.");
    }

    private static void MatchingPolicies()
    {
        var requirement = new ModThingReferenceDto();
        Check(BiotechThingMetadata.GeneRequirementMatches(requirement, null), "Empty gene requirement stopped being a wildcard.");
        BiotechThingMetadata.SetTargetGeneDefNames(requirement, new[] { "A", "B" });
        Check(BiotechThingMetadata.GeneRequirementMatches(requirement, new[] { "b", "a", "C" })
            && !BiotechThingMetadata.GeneRequirementMatches(requirement, new[] { "A" }), "Gene requirements stopped matching candidate supersets.");
        requirement.Metadata[BiotechThingMetadataKeys.ReproductiveSourceCount] = "1";
        BiotechThingMetadata.WriteReproductiveSourceMetadata(requirement, 0, Source("Mother", "Human", new[] { "A", "B" }));
        Check(BiotechThingMetadata.ReproductiveSourceRequirementMatches(requirement, new[] { Source("mother", "human", new[] { "b", "a" }) })
            && !BiotechThingMetadata.ReproductiveSourceRequirementMatches(requirement, new[] { Source("Father", "Human", new[] { "A", "B" }) })
            && !BiotechThingMetadata.ReproductiveSourceRequirementMatches(requirement, new[] { Source("Mother", "Human", new[] { "A", "B", "C" }) })
            && !BiotechThingMetadata.ReproductiveSourceRequirementMatches(requirement, Array.Empty<ReproductiveSourceRecord>()),
            "Source role/race matching or exact nonempty gene-set semantics changed.");
        BiotechThingMetadata.WriteReproductiveSourceMetadata(requirement, 0, Source(null, "Human"));
        Check(BiotechThingMetadata.ReproductiveSourceRequirementMatches(requirement, new[] { Source("Father", "human", new[] { "anything" }) }),
            "Unspecified source role/gene fields stopped being wildcards.");
    }

    private static void SharedDisplayCache()
    {
        ClashOfRimCompatibilityApi.BeginRegistrationCycle("biotech-codec"); ClashOfRimCompatibilityApi.ApplyServerDlcBaseline(null);
        ClashOfRimCompatibilityApi.RegisterCompatibilityPackage(BiotechCompatibilityKeys.PackageId, () => true, new[] { BiotechCompatibilityKeys.TradeMetadata });
        var dto = new ModThingReferenceDto();
        BiotechThingMetadata.SetGeneDefNames(dto, new[] { "B", "a" }); BiotechThingMetadata.SetTargetGeneDefNames(dto, new[] { "a" });
        BiotechThingMetadata.SetXenotypeName(dto, "测试"); BiotechThingMetadata.SetXenotypeIconDefName(dto, "Icon");
        dto.Metadata[BiotechThingMetadataKeys.ReproductiveSourceCount] = "1";
        BiotechThingMetadata.WriteReproductiveSourceMetadata(dto, 0, Source("Mother", "Human", new[] { "A" }));
        string[] parts = BiotechCompatibility.ThingReferenceCacheKeyParts(dto).ToArray();
        Check(parts.Take(4).SequenceEqual(new[] { "B,a", "a", "测试", "Icon" }) && parts.Length == 13 && parts[4] == "Mother" && parts[7] == "Human"
            && parts[11] == "A", "Display cache did not consume the same metadata codec as editing/matching.");
        ClashOfRimCompatibilityApi.ApplyServerDlcBaseline(Array.Empty<string>());
        Check(!BiotechCompatibility.ThingReferenceCacheKeyParts(dto).Any(), "Codec extraction bypassed the presentation DLC gate.");
    }

    private static ReproductiveSourceRecord Source(string? role, string? race, IReadOnlyList<string>? genes = null) =>
        new(role, null, null, null, race, null, null, false, null, null, genes, null);
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
}
