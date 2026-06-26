using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Diplomacy;
using AIRsLight.ClashOfRim.Pawns;
using RimWorld;
using RimWorld.Planet;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Verse;

namespace AIRsLight.ClashOfRim.DlcCompatibility;

internal static partial class BiotechCompatibility
{
    private const string PawnGlobalIdTagPrefix = "ClashOfRim.PawnGlobalId:";
    private const string ReproductiveSourcePlaceholderTagPrefix = "ClashOfRim.ReproductiveSourcePlaceholder:";
    private const string ReproductiveSourceRoleMother = "Mother";
    private const string ReproductiveSourceRoleFather = "Father";
    private const string ReproductiveSourceRoleSource = "Source";

    private static void AppendReproductiveSourceMetadata(Thing metadataThing, ModThingReferenceDto reference)
    {
        if (metadataThing is null || reference is null || metadataThing.TryGetComp<CompHasPawnSources>() is not { } comp)
        {
            return;
        }

        ClearReproductiveSourceMetadata(reference);
        List<Pawn> sources = (comp.pawnSources ?? new List<Pawn>())
            .Where(pawn => pawn is not null)
            .Distinct()
            .Take(MaxReproductiveSources)
            .ToList();
        if (sources.Count == 0)
        {
            return;
        }

        ClashOfRimMod? mod = ClashOfRimMod.Instance ?? LoadedModManager.GetMod<ClashOfRimMod>();
        reference.Metadata[MetadataReproductiveSourceCount] = sources.Count.ToString(CultureInfo.InvariantCulture);
        for (int i = 0; i < sources.Count; i++)
        {
            WriteReproductiveSourceMetadata(reference, i, BuildReproductiveSourceRecord(sources[i], mod));
        }
    }

    private static ReproductiveSourceRecord BuildReproductiveSourceRecord(Pawn pawn, ClashOfRimMod? mod)
    {
        string? globalId = ExistingReproductiveSourceGlobalId(pawn) ?? PawnGlobalIdUtility.Build(mod?.UserId, pawn);
        return new ReproductiveSourceRecord(
            Role: ReproductiveSourceRoleFor(pawn),
            GlobalId: globalId,
            OwnerUserId: ReproductiveSourceOwnerUserIdFor(pawn, globalId, mod?.UserId),
            Name: pawn.LabelShort,
            RaceDefName: pawn.def?.defName,
            PawnKindDefName: pawn.kindDef?.defName,
            Gender: pawn.gender.ToString(),
            Dead: pawn.Dead,
            FactionDefName: pawn.Faction?.def?.defName,
            XenotypeDefName: pawn.genes?.Xenotype?.defName,
            EndogeneDefNames: GeneDefNames(pawn.genes?.Endogenes),
            XenogeneDefNames: GeneDefNames(pawn.genes?.Xenogenes));
    }

    private static IReadOnlyList<string> GeneDefNames(List<Gene>? genes)
    {
        return genes is null
            ? Array.Empty<string>()
            : genes
                .Where(gene => gene?.def is not null)
                .Select(gene => gene.def.defName)
                .Where(defName => !string.IsNullOrWhiteSpace(defName))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
    }

    private static string ReproductiveSourceRoleFor(Pawn pawn)
    {
        return pawn.gender switch
        {
            Gender.Female => ReproductiveSourceRoleMother,
            Gender.Male => ReproductiveSourceRoleFather,
            _ => ReproductiveSourceRoleSource
        };
    }

    private static bool TryApplyReproductiveSourceMetadata(ModThingReferenceDto reference, Thing thing, out string? missingDefName)
    {
        missingDefName = null;
        IReadOnlyList<ReproductiveSourceRecord> records = ReproductiveSources(reference);
        if (records.Count == 0)
        {
            return true;
        }

        CompHasPawnSources? comp = thing.TryGetComp<CompHasPawnSources>();
        if (comp is null)
        {
            return true;
        }

        ClearExistingPawnSources(comp);
        foreach (ReproductiveSourceRecord record in records)
        {
            if (!TryFindOrCreateReproductiveSourcePawn(record, out Pawn? sourcePawn, out missingDefName)
                || sourcePawn is null)
            {
                return false;
            }

            comp.AddSource(sourcePawn);
        }

        return true;
    }

    private static void ClearExistingPawnSources(CompHasPawnSources comp)
    {
        if (comp.pawnSources is null || comp.pawnSources.Count == 0)
        {
            comp.pawnSources = new List<Pawn>();
            return;
        }

        try
        {
            Find.WorldPawns?.RemovePawnSources(comp.pawnSources, comp);
        }
        catch (Exception ex)
        {
            Log.Warning("[ClashOfRim][Biotech] Failed to unlink old reproductive source references: " + ex.Message);
        }

        comp.pawnSources.Clear();
    }

    private static bool TryFindOrCreateReproductiveSourcePawn(
        ReproductiveSourceRecord record,
        out Pawn? pawn,
        out string? missingDefName)
    {
        missingDefName = null;
        pawn = FindExistingReproductiveSourcePawn(record.GlobalId);
        if (pawn is not null)
        {
            return TryApplyReproductiveSourcePawnState(pawn, record, out missingDefName);
        }

        if (!TryResolveReproductiveSourcePawnKind(record, out PawnKindDef? pawnKind, out missingDefName)
            || pawnKind is null)
        {
            return false;
        }

        Faction? faction = ResolveReproductiveSourceFaction(record);
        try
        {
            pawn = PawnGenerator.GeneratePawn(pawnKind, faction);
        }
        catch (Exception ex)
        {
            missingDefName = pawnKind.defName;
            Log.Warning("[ClashOfRim][Biotech] Failed to generate reproductive source pawn placeholder: "
                + pawnKind.defName
                + " "
                + ex.Message);
            return false;
        }

        if (!string.IsNullOrWhiteSpace(record.Name))
        {
            pawn.Name = new NameSingle(record.Name!.Trim());
        }

        if (!string.IsNullOrWhiteSpace(record.GlobalId))
        {
            QuestUtility.AddQuestTag(ref pawn.questTags, ReproductiveSourcePlaceholderTagPrefix + record.GlobalId!.Trim());
        }

        MarkReproductiveSourcePawnGlobalId(pawn, record.GlobalId);
        if (!TryApplyReproductiveSourcePawnState(pawn, record, out missingDefName))
        {
            return false;
        }

        if (Find.WorldPawns is not null && !Find.WorldPawns.Contains(pawn))
        {
            Find.WorldPawns.PassToWorld(pawn, PawnDiscardDecideMode.KeepForever);
        }

        return true;
    }

    private static bool TryApplyReproductiveSourcePawnState(
        Pawn pawn,
        ReproductiveSourceRecord record,
        out string? missingDefName)
    {
        missingDefName = null;
        Faction? faction = ResolveReproductiveSourceFaction(record);
        if (faction is not null && pawn.Faction != faction)
        {
            pawn.SetFaction(faction);
        }

        if (Enum.TryParse(record.Gender, ignoreCase: true, out Gender gender) && gender != Gender.None)
        {
            pawn.gender = gender;
        }

        MarkReproductiveSourcePawnGlobalId(pawn, record.GlobalId);
        return TryApplyReproductiveSourceGenes(pawn, record, out missingDefName);
    }

    private static bool TryApplyReproductiveSourceGenes(
        Pawn pawn,
        ReproductiveSourceRecord record,
        out string? missingDefName)
    {
        missingDefName = null;
        if (pawn.genes is null)
        {
            return true;
        }

        XenotypeDef? xenotype = XenotypeDefOf.Baseliner;
        if (!string.IsNullOrWhiteSpace(record.XenotypeDefName))
        {
            xenotype = DefDatabase<XenotypeDef>.GetNamedSilentFail(record.XenotypeDefName!);
            if (xenotype is null)
            {
                missingDefName = record.XenotypeDefName;
                return false;
            }
        }

        bool hasExplicitGenes = record.EndogeneDefNames.Count > 0 || record.XenogeneDefNames.Count > 0;
        if (!hasExplicitGenes)
        {
            pawn.genes.SetXenotype(xenotype ?? XenotypeDefOf.Baseliner);
            ResetGeneTrackerCaches(pawn);
            return true;
        }

        pawn.genes.SetXenotypeDirect(xenotype ?? XenotypeDefOf.Baseliner);
        pawn.genes.Endogenes.Clear();
        pawn.genes.ClearXenogenes();
        foreach (string geneDefName in record.EndogeneDefNames)
        {
            GeneDef? gene = ResolveGeneDef(geneDefName);
            if (gene is null)
            {
                missingDefName = geneDefName;
                return false;
            }

            pawn.genes.AddGene(gene, xenogene: false);
        }

        foreach (string geneDefName in record.XenogeneDefNames)
        {
            GeneDef? gene = ResolveGeneDef(geneDefName);
            if (gene is null)
            {
                missingDefName = geneDefName;
                return false;
            }

            pawn.genes.AddGene(gene, xenogene: true);
        }

        ResetGeneTrackerCaches(pawn);
        pawn.skills?.DirtyAptitudes();
        pawn.Notify_DisabledWorkTypesChanged();
        return true;
    }

    private static bool TryResolveReproductiveSourcePawnKind(
        ReproductiveSourceRecord record,
        out PawnKindDef? pawnKind,
        out string? missingDefName)
    {
        missingDefName = null;
        ThingDef? raceDef = string.IsNullOrWhiteSpace(record.RaceDefName)
            ? null
            : DefDatabase<ThingDef>.GetNamedSilentFail(record.RaceDefName!);
        if (!string.IsNullOrWhiteSpace(record.RaceDefName) && raceDef is null)
        {
            pawnKind = null;
            missingDefName = record.RaceDefName;
            return false;
        }

        pawnKind = string.IsNullOrWhiteSpace(record.PawnKindDefName)
            ? null
            : DefDatabase<PawnKindDef>.GetNamedSilentFail(record.PawnKindDefName!);
        if (pawnKind is not null && (raceDef is null || pawnKind.race == raceDef))
        {
            return true;
        }

        pawnKind = raceDef is null
            ? PawnKindDefOf.Colonist
            : DefDatabase<PawnKindDef>.AllDefsListForReading.FirstOrDefault(kind => kind.race == raceDef);
        if (pawnKind is not null)
        {
            return true;
        }

        missingDefName = record.PawnKindDefName ?? record.RaceDefName;
        return false;
    }

    private static Faction? ResolveReproductiveSourceFaction(ReproductiveSourceRecord record)
    {
        string? ownerUserId = ReproductiveSourceOwnerUserIdFromRecord(record);
        if (!string.IsNullOrWhiteSpace(ownerUserId))
        {
            ClashOfRimMod? mod = ClashOfRimMod.Instance ?? LoadedModManager.GetMod<ClashOfRimMod>();
            string? currentUserId = mod?.UserId;
            if (!string.IsNullOrWhiteSpace(currentUserId)
                && string.Equals(ownerUserId, currentUserId, StringComparison.Ordinal))
            {
                return Faction.OfPlayer ?? ResolveReproductiveSourceFactionByDef(record.FactionDefName, allowPlayerFaction: true);
            }

            Faction? proxy = PlayerFactionProxyUtility.EnsureProxyForUser(
                ownerUserId,
                record.FactionDefName);
            if (proxy is not null)
            {
                return proxy;
            }

            return ResolveReproductiveSourceFactionByDef(record.FactionDefName, allowPlayerFaction: false);
        }

        return ResolveReproductiveSourceFactionByDef(record.FactionDefName, allowPlayerFaction: true)
            ?? Faction.OfPlayer;
    }

    private static Faction? ResolveReproductiveSourceFactionByDef(string? factionDefName, bool allowPlayerFaction)
    {
        if (string.IsNullOrWhiteSpace(factionDefName))
        {
            return allowPlayerFaction ? Faction.OfPlayer : null;
        }

        if (!allowPlayerFaction && IsPlayerLikeReproductiveSourceFactionDefName(factionDefName))
        {
            return null;
        }

        Faction? faction = Find.FactionManager?.AllFactionsListForReading?
            .FirstOrDefault(candidate => string.Equals(
                candidate?.def?.defName,
                factionDefName,
                StringComparison.Ordinal));
        return faction ?? (allowPlayerFaction ? Faction.OfPlayer : null);
    }

    private static string? ReproductiveSourceOwnerUserIdFromRecord(ReproductiveSourceRecord record)
    {
        if (!string.IsNullOrWhiteSpace(record.OwnerUserId))
        {
            return record.OwnerUserId!.Trim();
        }

        if (IsPlayerLikeReproductiveSourceFactionDefName(record.FactionDefName)
            && PawnGlobalIdUtility.TryExtractOwnerUserId(record.GlobalId, out string? ownerUserId))
        {
            return ownerUserId;
        }

        return null;
    }

    private static string? ReproductiveSourceOwnerUserIdFor(Pawn pawn, string? globalId, string? currentUserId)
    {
        string? ownerFromGlobalId = PawnGlobalIdUtility.TryExtractOwnerUserId(globalId, out string? parsedOwnerUserId)
            ? parsedOwnerUserId
            : null;
        if (PlayerFactionProxyUtility.IsServerPlayerProxy(pawn.Faction))
        {
            return ownerFromGlobalId ?? PlayerFactionProxyUtility.ProxyOwnerUserId(pawn.Faction);
        }

        if (pawn.Faction == Faction.OfPlayer || pawn.Faction?.IsPlayer == true)
        {
            return ownerFromGlobalId ?? (string.IsNullOrWhiteSpace(currentUserId) ? null : currentUserId);
        }

        if (!string.IsNullOrWhiteSpace(ownerFromGlobalId) && HasExistingReproductiveSourceGlobalId(pawn))
        {
            return ownerFromGlobalId;
        }

        return null;
    }

    private static bool IsPlayerLikeReproductiveSourceFactionDefName(string? factionDefName)
    {
        if (string.IsNullOrWhiteSpace(factionDefName))
        {
            return false;
        }

        string trimmed = factionDefName!.Trim();
        if (string.Equals(trimmed, PlayerFactionProxyUtility.ProxyFactionDefName, StringComparison.Ordinal)
            || string.Equals(trimmed, Faction.OfPlayer?.def?.defName, StringComparison.Ordinal))
        {
            return true;
        }

        return DefDatabase<FactionDef>.GetNamedSilentFail(trimmed)?.isPlayer == true;
    }

    private static Pawn? FindExistingReproductiveSourcePawn(string? globalId)
    {
        if (string.IsNullOrWhiteSpace(globalId))
        {
            return null;
        }

        string globalTag = PawnGlobalIdTagPrefix + globalId!.Trim();
        string placeholderTag = ReproductiveSourcePlaceholderTagPrefix + globalId.Trim();
        return EnumerateKnownPawns()
            .FirstOrDefault(pawn => pawn.questTags is not null
                && (pawn.questTags.Contains(globalTag, StringComparer.Ordinal)
                    || pawn.questTags.Contains(placeholderTag, StringComparer.Ordinal)));
    }

    private static IEnumerable<Pawn> EnumerateKnownPawns()
    {
        var seen = new HashSet<Pawn>();
        foreach (Map map in Find.Maps ?? new List<Map>())
        {
            foreach (Pawn pawn in map.mapPawns?.AllPawns ?? new List<Pawn>())
            {
                if (pawn is not null && seen.Add(pawn))
                {
                    yield return pawn;
                }
            }
        }

        if (Find.WorldObjects is not null)
        {
            foreach (Caravan caravan in Find.WorldObjects.Caravans)
            {
                foreach (Pawn pawn in caravan.PawnsListForReading ?? new List<Pawn>())
                {
                    if (pawn is not null && seen.Add(pawn))
                    {
                        yield return pawn;
                    }
                }
            }
        }

        if (Find.WorldPawns is not null)
        {
            foreach (Pawn pawn in Find.WorldPawns.AllPawnsAliveOrDead)
            {
                if (pawn is not null && seen.Add(pawn))
                {
                    yield return pawn;
                }
            }
        }
    }

    private static bool HasExistingReproductiveSourceGlobalId(Pawn pawn)
    {
        return ExistingReproductiveSourceGlobalId(pawn) is not null;
    }

    private static string? ExistingReproductiveSourceGlobalId(Pawn pawn)
    {
        if (pawn?.questTags is null)
        {
            return null;
        }

        foreach (string tag in pawn.questTags)
        {
            if (string.IsNullOrWhiteSpace(tag))
            {
                continue;
            }

            if (tag.StartsWith(PawnGlobalIdTagPrefix, StringComparison.Ordinal))
            {
                string globalId = tag.Substring(PawnGlobalIdTagPrefix.Length).Trim();
                if (!string.IsNullOrWhiteSpace(globalId))
                {
                    return globalId;
                }
            }

            if (tag.StartsWith(ReproductiveSourcePlaceholderTagPrefix, StringComparison.Ordinal))
            {
                string globalId = tag.Substring(ReproductiveSourcePlaceholderTagPrefix.Length).Trim();
                if (!string.IsNullOrWhiteSpace(globalId))
                {
                    return globalId;
                }
            }
        }

        return null;
    }

    private static void MarkReproductiveSourcePawnGlobalId(Pawn pawn, string? globalId)
    {
        if (pawn is null || string.IsNullOrWhiteSpace(globalId))
        {
            return;
        }

        QuestUtility.AddQuestTag(ref pawn.questTags, PawnGlobalIdTagPrefix + globalId!.Trim());
    }

    private static bool IsReproductiveSourceCarrierDef(ThingDef? def)
    {
        return HasBiotechTradeMetadata
            && def?.comps is not null
            && def.comps.Any(props => props?.compClass is not null
                && typeof(CompHasPawnSources).IsAssignableFrom(props.compClass));
    }

    private static IReadOnlyList<ReproductiveSourceRecord> CandidateReproductiveSources(Thing metadataThing)
    {
        if (metadataThing?.TryGetComp<CompHasPawnSources>() is not { } comp)
        {
            return Array.Empty<ReproductiveSourceRecord>();
        }

        ClashOfRimMod? mod = ClashOfRimMod.Instance ?? LoadedModManager.GetMod<ClashOfRimMod>();
        return (comp.pawnSources ?? new List<Pawn>())
            .Where(pawn => pawn is not null)
            .Distinct()
            .Take(MaxReproductiveSources)
            .Select(pawn => BuildReproductiveSourceRecord(pawn, mod))
            .ToList();
    }

    private static bool ReproductiveSourceRequirementMatches(
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

    private static IReadOnlyList<ReproductiveSourceRecord> ReproductiveSources(ModThingReferenceDto? reference)
    {
        NormalizeBiotechMetadata(reference);
        if (reference is null
            || !reference.Metadata.TryGetValue(MetadataReproductiveSourceCount, out string? countText)
            || !int.TryParse(countText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int count)
            || count <= 0)
        {
            return Array.Empty<ReproductiveSourceRecord>();
        }

        count = Math.Min(count, MaxReproductiveSources);
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

    private static void WriteReproductiveSourceMetadata(
        ModThingReferenceDto reference,
        int index,
        ReproductiveSourceRecord record)
    {
        WriteIndexedMetadataText(reference, index, MetadataReproductiveSourceRole, record.Role);
        WriteIndexedMetadataText(reference, index, MetadataReproductiveSourceGlobalId, record.GlobalId);
        WriteIndexedMetadataText(reference, index, MetadataReproductiveSourceOwnerUserId, record.OwnerUserId);
        WriteIndexedMetadataText(reference, index, MetadataReproductiveSourceName, record.Name);
        WriteIndexedMetadataText(reference, index, MetadataReproductiveSourceRaceDef, record.RaceDefName);
        WriteIndexedMetadataText(reference, index, MetadataReproductiveSourcePawnKindDef, record.PawnKindDefName);
        WriteIndexedMetadataText(reference, index, MetadataReproductiveSourceGender, record.Gender);
        WriteIndexedMetadataText(reference, index, MetadataReproductiveSourceDead, record.Dead ? "true" : null);
        WriteIndexedMetadataText(reference, index, MetadataReproductiveSourceFactionDef, record.FactionDefName);
        WriteIndexedMetadataText(reference, index, MetadataReproductiveSourceXenotypeDef, record.XenotypeDefName);
        WriteIndexedMetadataList(reference, index, MetadataReproductiveSourceEndogeneDefNames, record.EndogeneDefNames);
        WriteIndexedMetadataList(reference, index, MetadataReproductiveSourceXenogeneDefNames, record.XenogeneDefNames);
    }

    private static ReproductiveSourceRecord ReadReproductiveSourceMetadata(ModThingReferenceDto reference, int index)
    {
        return new ReproductiveSourceRecord(
            Role: ReadIndexedMetadataText(reference, index, MetadataReproductiveSourceRole),
            GlobalId: ReadIndexedMetadataText(reference, index, MetadataReproductiveSourceGlobalId),
            OwnerUserId: ReadIndexedMetadataText(reference, index, MetadataReproductiveSourceOwnerUserId),
            Name: ReadIndexedMetadataText(reference, index, MetadataReproductiveSourceName),
            RaceDefName: ReadIndexedMetadataText(reference, index, MetadataReproductiveSourceRaceDef),
            PawnKindDefName: ReadIndexedMetadataText(reference, index, MetadataReproductiveSourcePawnKindDef),
            Gender: ReadIndexedMetadataText(reference, index, MetadataReproductiveSourceGender),
            Dead: string.Equals(ReadIndexedMetadataText(reference, index, MetadataReproductiveSourceDead), "true", StringComparison.OrdinalIgnoreCase),
            FactionDefName: ReadIndexedMetadataText(reference, index, MetadataReproductiveSourceFactionDef),
            XenotypeDefName: ReadIndexedMetadataText(reference, index, MetadataReproductiveSourceXenotypeDef),
            EndogeneDefNames: ReadIndexedMetadataList(reference, index, MetadataReproductiveSourceEndogeneDefNames),
            XenogeneDefNames: ReadIndexedMetadataList(reference, index, MetadataReproductiveSourceXenogeneDefNames));
    }

    private static string? ReadIndexedMetadataText(ModThingReferenceDto reference, int index, string field)
    {
        string key = ReproductiveSourceKey(index, field);
        return reference.Metadata.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;
    }

    private static void WriteIndexedMetadataText(ModThingReferenceDto reference, int index, string field, string? value)
    {
        string key = ReproductiveSourceKey(index, field);
        if (string.IsNullOrWhiteSpace(value))
        {
            reference.Metadata.Remove(key);
            return;
        }

        reference.Metadata[key] = value;
    }

    private static IReadOnlyList<string> ReadIndexedMetadataList(ModThingReferenceDto reference, int index, string field)
    {
        string? value = ReadIndexedMetadataText(reference, index, field);
        if (string.IsNullOrWhiteSpace(value))
        {
            return Array.Empty<string>();
        }

        return value!
            .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(item => item.Trim())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void WriteIndexedMetadataList(
        ModThingReferenceDto reference,
        int index,
        string field,
        IEnumerable<string>? values)
    {
        string[] normalized = (values ?? Array.Empty<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        WriteIndexedMetadataText(reference, index, field, normalized.Length == 0 ? null : string.Join(",", normalized));
    }

    private static void ClearReproductiveSourceMetadata(ModThingReferenceDto? reference)
    {
        if (reference?.Metadata is null || reference.Metadata.Count == 0)
        {
            return;
        }

        foreach (string key in reference.Metadata.Keys
                     .Where(key => key.StartsWith(MetadataReproductiveSourcePrefix, StringComparison.Ordinal))
                     .ToList())
        {
            reference.Metadata.Remove(key);
        }
    }

    private static string ReproductiveSourceKey(int index, string field)
    {
        return MetadataReproductiveSourcePrefix
            + index.ToString(CultureInfo.InvariantCulture)
            + "."
            + field;
    }

    private readonly struct ReproductiveSourceRecord
    {
        public ReproductiveSourceRecord(
            string? Role,
            string? GlobalId,
            string? OwnerUserId,
            string? Name,
            string? RaceDefName,
            string? PawnKindDefName,
            string? Gender,
            bool Dead,
            string? FactionDefName,
            string? XenotypeDefName,
            IReadOnlyList<string>? EndogeneDefNames,
            IReadOnlyList<string>? XenogeneDefNames)
        {
            this.Role = Role;
            this.GlobalId = GlobalId;
            this.OwnerUserId = OwnerUserId;
            this.Name = Name;
            this.RaceDefName = RaceDefName;
            this.PawnKindDefName = PawnKindDefName;
            this.Gender = Gender;
            this.Dead = Dead;
            this.FactionDefName = FactionDefName;
            this.XenotypeDefName = XenotypeDefName;
            this.EndogeneDefNames = EndogeneDefNames ?? Array.Empty<string>();
            this.XenogeneDefNames = XenogeneDefNames ?? Array.Empty<string>();
        }

        public string? Role { get; }

        public string? GlobalId { get; }

        public string? OwnerUserId { get; }

        public string? Name { get; }

        public string? RaceDefName { get; }

        public string? PawnKindDefName { get; }

        public string? Gender { get; }

        public bool Dead { get; }

        public string? FactionDefName { get; }

        public string? XenotypeDefName { get; }

        public IReadOnlyList<string> EndogeneDefNames { get; }

        public IReadOnlyList<string> XenogeneDefNames { get; }
    }
}
