using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Protocol;
using System;
using System.Collections.Generic;
using System.Linq;

namespace AIRsLight.ClashOfRim.Modules.Compatibility.Biotech;

internal static class BiotechThingMetadata
{
    internal static IReadOnlyList<string> GeneDefNames(ModThingReferenceDto? reference)
    {
        return ReadMetadataList(reference, BiotechThingMetadataKeys.GeneDefNames);
    }

    internal static void SetGeneDefNames(ModThingReferenceDto? reference, IEnumerable<string>? geneDefNames)
    {
        WriteMetadataList(reference, BiotechThingMetadataKeys.GeneDefNames, geneDefNames);
    }

    internal static string? TargetGeneDefName(ModThingReferenceDto? reference)
    {
        return TargetGeneDefNames(reference).FirstOrDefault();
    }

    internal static IReadOnlyList<string> TargetGeneDefNames(ModThingReferenceDto? reference)
    {
        return ReadMetadataList(reference, BiotechThingMetadataKeys.TargetGeneDefNames);
    }

    internal static void SetTargetGeneDefName(ModThingReferenceDto? reference, string? geneDefName)
    {
        SetTargetGeneDefNames(reference, string.IsNullOrWhiteSpace(geneDefName)
            ? Array.Empty<string>()
            : new[] { geneDefName! });
    }

    internal static void SetTargetGeneDefNames(ModThingReferenceDto? reference, IEnumerable<string>? geneDefNames)
    {
        WriteMetadataList(reference, BiotechThingMetadataKeys.TargetGeneDefNames, geneDefNames);
    }

    internal static string? XenotypeName(ModThingReferenceDto? reference)
    {
        return ReadMetadataText(reference, BiotechThingMetadataKeys.XenotypeName);
    }

    internal static void SetXenotypeName(ModThingReferenceDto? reference, string? xenotypeName)
    {
        WriteMetadataText(reference, BiotechThingMetadataKeys.XenotypeName, xenotypeName);
    }

    internal static string? XenotypeIconDefName(ModThingReferenceDto? reference)
    {
        return ReadMetadataText(reference, BiotechThingMetadataKeys.XenotypeIconDefName);
    }

    internal static void SetXenotypeIconDefName(ModThingReferenceDto? reference, string? iconDefName)
    {
        WriteMetadataText(reference, BiotechThingMetadataKeys.XenotypeIconDefName, iconDefName);
    }

    internal static bool GeneRequirementMatches(ModThingReferenceDto requirement, IReadOnlyCollection<string>? candidateGeneDefNames)
    {
        Normalize(requirement);
        return BiotechTradeConstraints.MatchesGenes(requirement.Metadata, candidateGeneDefNames);
    }

    internal static void Normalize(ModThingReferenceDto? reference)
    {
        if (reference is null)
        {
            return;
        }

        reference.Metadata ??= new Dictionary<string, string?>();
    }

    private static IReadOnlyList<string> ReadMetadataList(ModThingReferenceDto? reference, string key)
    {
        Normalize(reference);
        return BiotechThingMetadataCodec.ReadList(reference?.Metadata, key);
    }

    private static void WriteMetadataList(ModThingReferenceDto? reference, string key, IEnumerable<string>? values)
    {
        if (reference is null)
        {
            return;
        }

        Normalize(reference);
        BiotechThingMetadataCodec.WriteList(reference.Metadata, key, values);
    }

    private static string? ReadMetadataText(ModThingReferenceDto? reference, string key)
    {
        Normalize(reference);
        return BiotechThingMetadataCodec.ReadText(reference?.Metadata, key);
    }

    private static void WriteMetadataText(ModThingReferenceDto? reference, string key, string? value)
    {
        if (reference is null)
        {
            return;
        }

        Normalize(reference);
        BiotechThingMetadataCodec.WriteText(reference.Metadata, key, value);
    }

    internal static bool ReproductiveSourceRequirementMatches(
        ModThingReferenceDto requirement,
        IReadOnlyList<ReproductiveSourceRecord> candidateSources)
    {
        Normalize(requirement);
        return BiotechTradeConstraints.MatchesSources(requirement.Metadata, candidateSources
            .Select(source => new BiotechSourceConstraint(source.Role, source.RaceDefName, source.EndogeneDefNames, source.XenogeneDefNames))
            .ToArray());
    }

    internal static IReadOnlyList<ReproductiveSourceRecord> ReproductiveSources(ModThingReferenceDto? reference)
    {
        Normalize(reference);
        if (reference is null)
        {
            return Array.Empty<ReproductiveSourceRecord>();
        }

        int count = BiotechThingMetadataCodec.SourceCount(reference.Metadata);
        var records = new List<ReproductiveSourceRecord>(count);
        for (int i = 0; i < count; i++)
        {
            ReproductiveSourceRecord record = ReadReproductiveSourceMetadata(reference, i);
            if (!string.IsNullOrWhiteSpace(record.GlobalId)
                || !string.IsNullOrWhiteSpace(record.RaceDefName)
                || record.EndogeneDefNames.Count > 0
                || record.XenogeneDefNames.Count > 0)
            {
                records.Add(record);
            }
        }

        return records;
    }

    internal static void WriteReproductiveSourceMetadata(
        ModThingReferenceDto reference,
        int index,
        ReproductiveSourceRecord record)
    {
        WriteIndexedMetadataText(reference, index, BiotechThingMetadataKeys.SourceRole, record.Role);
        WriteIndexedMetadataText(reference, index, BiotechThingMetadataKeys.SourceGlobalId, record.GlobalId);
        WriteIndexedMetadataText(reference, index, BiotechThingMetadataKeys.SourceOwnerUserId, record.OwnerUserId);
        WriteIndexedMetadataText(reference, index, BiotechThingMetadataKeys.SourceName, record.Name);
        WriteIndexedMetadataText(reference, index, BiotechThingMetadataKeys.SourceRaceDef, record.RaceDefName);
        WriteIndexedMetadataText(reference, index, BiotechThingMetadataKeys.SourcePawnKindDef, record.PawnKindDefName);
        WriteIndexedMetadataText(reference, index, BiotechThingMetadataKeys.SourceGender, record.Gender);
        WriteIndexedMetadataText(reference, index, BiotechThingMetadataKeys.SourceDead, record.Dead ? "true" : null);
        WriteIndexedMetadataText(reference, index, BiotechThingMetadataKeys.SourceFactionDef, record.FactionDefName);
        WriteIndexedMetadataText(reference, index, BiotechThingMetadataKeys.SourceXenotypeDef, record.XenotypeDefName);
        WriteIndexedMetadataList(reference, index, BiotechThingMetadataKeys.SourceEndogeneDefNames, record.EndogeneDefNames);
        WriteIndexedMetadataList(reference, index, BiotechThingMetadataKeys.SourceXenogeneDefNames, record.XenogeneDefNames);
    }

    private static ReproductiveSourceRecord ReadReproductiveSourceMetadata(ModThingReferenceDto reference, int index)
    {
        return new ReproductiveSourceRecord(
            Role: ReadIndexedMetadataText(reference, index, BiotechThingMetadataKeys.SourceRole),
            GlobalId: ReadIndexedMetadataText(reference, index, BiotechThingMetadataKeys.SourceGlobalId),
            OwnerUserId: ReadIndexedMetadataText(reference, index, BiotechThingMetadataKeys.SourceOwnerUserId),
            Name: ReadIndexedMetadataText(reference, index, BiotechThingMetadataKeys.SourceName),
            RaceDefName: ReadIndexedMetadataText(reference, index, BiotechThingMetadataKeys.SourceRaceDef),
            PawnKindDefName: ReadIndexedMetadataText(reference, index, BiotechThingMetadataKeys.SourcePawnKindDef),
            Gender: ReadIndexedMetadataText(reference, index, BiotechThingMetadataKeys.SourceGender),
            Dead: string.Equals(ReadIndexedMetadataText(reference, index, BiotechThingMetadataKeys.SourceDead), "true", StringComparison.OrdinalIgnoreCase),
            FactionDefName: ReadIndexedMetadataText(reference, index, BiotechThingMetadataKeys.SourceFactionDef),
            XenotypeDefName: ReadIndexedMetadataText(reference, index, BiotechThingMetadataKeys.SourceXenotypeDef),
            EndogeneDefNames: ReadIndexedMetadataList(reference, index, BiotechThingMetadataKeys.SourceEndogeneDefNames),
            XenogeneDefNames: ReadIndexedMetadataList(reference, index, BiotechThingMetadataKeys.SourceXenogeneDefNames));
    }

    private static string? ReadIndexedMetadataText(ModThingReferenceDto reference, int index, string field) =>
        ReadMetadataText(reference, BiotechThingMetadataCodec.SourceKey(index, field));

    private static void WriteIndexedMetadataText(ModThingReferenceDto reference, int index, string field, string? value) =>
        WriteMetadataText(reference, BiotechThingMetadataCodec.SourceKey(index, field), value);

    private static IReadOnlyList<string> ReadIndexedMetadataList(ModThingReferenceDto reference, int index, string field) =>
        ReadMetadataList(reference, BiotechThingMetadataCodec.SourceKey(index, field));

    private static void WriteIndexedMetadataList(ModThingReferenceDto reference, int index, string field, IEnumerable<string>? values) =>
        WriteMetadataList(reference, BiotechThingMetadataCodec.SourceKey(index, field), values);

    internal static void Clear(ModThingReferenceDto reference)
    {
        reference.Metadata.Remove(BiotechThingMetadataKeys.GeneDefNames);
        reference.Metadata.Remove(BiotechThingMetadataKeys.TargetGeneDefNames);
        reference.Metadata.Remove(BiotechThingMetadataKeys.XenotypeName);
        reference.Metadata.Remove(BiotechThingMetadataKeys.XenotypeIconDefName);
        ClearReproductiveSourceMetadata(reference);
    }

    internal static void ClearReproductiveSourceMetadata(ModThingReferenceDto? reference)
    {
        if (reference?.Metadata is null || reference.Metadata.Count == 0)
        {
            return;
        }

        foreach (string key in reference.Metadata.Keys
                     .Where(key => key.StartsWith(BiotechThingMetadataKeys.ReproductiveSourcePrefix, StringComparison.Ordinal))
                     .ToList())
        {
            reference.Metadata.Remove(key);
        }
    }
}
