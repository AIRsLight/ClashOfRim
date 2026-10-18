using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Protocol;
using System;
using System.Collections.Generic;
using System.Globalization;
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
        IReadOnlyList<string> targetGeneDefNames = TargetGeneDefNames(requirement);
        if (targetGeneDefNames.Count == 0)
        {
            return true;
        }

        if (candidateGeneDefNames is null || candidateGeneDefNames.Count == 0)
        {
            return false;
        }

        return targetGeneDefNames.All(targetGeneDefName =>
            candidateGeneDefNames.Any(geneDefName => string.Equals(
                    geneDefName,
                    targetGeneDefName,
                    StringComparison.OrdinalIgnoreCase)));
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
        if (reference is null
            || !reference.Metadata.TryGetValue(key, out string? value)
            || string.IsNullOrWhiteSpace(value))
        {
            return Array.Empty<string>();
        }

        return value!
            .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(gene => gene.Trim())
            .Where(gene => !string.IsNullOrWhiteSpace(gene))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void WriteMetadataList(ModThingReferenceDto? reference, string key, IEnumerable<string>? values)
    {
        if (reference is null)
        {
            return;
        }

        reference.Metadata ??= new Dictionary<string, string?>();
        string[] normalized = (values ?? Array.Empty<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalized.Length == 0)
        {
            reference.Metadata.Remove(key);
            return;
        }

        reference.Metadata[key] = string.Join(",", normalized);
    }

    private static string? ReadMetadataText(ModThingReferenceDto? reference, string key)
    {
        Normalize(reference);
        return reference is not null
            && reference.Metadata.TryGetValue(key, out string? value)
            && !string.IsNullOrWhiteSpace(value)
                ? value
                : null;
    }

    private static void WriteMetadataText(ModThingReferenceDto? reference, string key, string? value)
    {
        if (reference is null)
        {
            return;
        }

        reference.Metadata ??= new Dictionary<string, string?>();
        if (string.IsNullOrWhiteSpace(value))
        {
            reference.Metadata.Remove(key);
            return;
        }

        reference.Metadata[key] = value;
    }

    internal static bool ReproductiveSourceRequirementMatches(
        ModThingReferenceDto requirement,
        IReadOnlyList<ReproductiveSourceRecord> candidateSources)
    {
        IReadOnlyList<ReproductiveSourceRecord> requiredSources = ReproductiveSources(requirement);
        if (requiredSources.Count == 0)
        {
            return true;
        }

        if (candidateSources.Count == 0)
        {
            return false;
        }

        foreach (ReproductiveSourceRecord required in requiredSources)
        {
            if (!candidateSources.Any(candidate => ReproductiveSourceRecordMatches(required, candidate)))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ReproductiveSourceRecordMatches(
        ReproductiveSourceRecord required,
        ReproductiveSourceRecord candidate)
    {
        return MetadataTextMatches(required.Role, candidate.Role)
            && MetadataTextMatches(required.RaceDefName, candidate.RaceDefName)
            && MetadataListMatches(required.EndogeneDefNames, candidate.EndogeneDefNames)
            && MetadataListMatches(required.XenogeneDefNames, candidate.XenogeneDefNames);
    }

    private static bool MetadataTextMatches(string? required, string? candidate)
    {
        return string.IsNullOrWhiteSpace(required)
            || string.Equals(required, candidate, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MetadataListMatches(IReadOnlyList<string> required, IReadOnlyList<string> candidate)
    {
        if (required.Count == 0)
        {
            return true;
        }

        return required.Count == candidate.Count
            && required.All(requiredValue => candidate.Any(candidateValue => string.Equals(
                requiredValue,
                candidateValue,
                StringComparison.OrdinalIgnoreCase)));
    }

    internal static IReadOnlyList<ReproductiveSourceRecord> ReproductiveSources(ModThingReferenceDto? reference)
    {
        Normalize(reference);
        if (reference is null
            || !reference.Metadata.TryGetValue(BiotechThingMetadataKeys.ReproductiveSourceCount, out string? countText)
            || !int.TryParse(countText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int count)
            || count <= 0)
        {
            return Array.Empty<ReproductiveSourceRecord>();
        }

        count = Math.Min(count, BiotechThingMetadataKeys.MaxReproductiveSources);
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
        ReadMetadataText(reference, ReproductiveSourceKey(index, field));

    private static void WriteIndexedMetadataText(ModThingReferenceDto reference, int index, string field, string? value) =>
        WriteMetadataText(reference, ReproductiveSourceKey(index, field), value);

    private static IReadOnlyList<string> ReadIndexedMetadataList(ModThingReferenceDto reference, int index, string field) =>
        ReadMetadataList(reference, ReproductiveSourceKey(index, field));

    private static void WriteIndexedMetadataList(ModThingReferenceDto reference, int index, string field, IEnumerable<string>? values) =>
        WriteMetadataList(reference, ReproductiveSourceKey(index, field), values);

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

    private static string ReproductiveSourceKey(int index, string field)
    {
        return BiotechThingMetadataKeys.ReproductiveSourcePrefix
            + index.ToString(CultureInfo.InvariantCulture)
            + "."
            + field;
    }
}
