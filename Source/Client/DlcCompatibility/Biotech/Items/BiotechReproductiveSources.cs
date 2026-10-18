using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Compatibility.Biotech;
using AIRsLight.ClashOfRim.Pawns;
using AIRsLight.ClashOfRim.Protocol;
using RimWorld;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Verse;

namespace AIRsLight.ClashOfRim.DlcCompatibility.Biotech.Items;

internal sealed class BiotechReproductiveSources
{
    private readonly Func<string?> currentUserId;

    internal BiotechReproductiveSources(Func<string?> currentUserId) => this.currentUserId = currentUserId;

    internal void Append(Thing thing, ModThingReferenceDto reference)
    {
        if (thing.TryGetComp<CompHasPawnSources>() is null)
        {
            return;
        }

        BiotechThingMetadata.ClearReproductiveSourceMetadata(reference);
        IReadOnlyList<ReproductiveSourceRecord> sources = Capture(thing);
        if (sources.Count == 0)
        {
            return;
        }

        reference.Metadata[BiotechThingMetadataKeys.ReproductiveSourceCount] = sources.Count.ToString(CultureInfo.InvariantCulture);
        for (int i = 0; i < sources.Count; i++)
        {
            BiotechThingMetadata.WriteReproductiveSourceMetadata(reference, i, sources[i]);
        }
    }

    internal IReadOnlyList<ReproductiveSourceRecord> Capture(Thing metadataThing)
    {
        if (metadataThing?.TryGetComp<CompHasPawnSources>() is not { } comp)
        {
            return Array.Empty<ReproductiveSourceRecord>();
        }

        string? userId = currentUserId();
        return (comp.pawnSources ?? new List<Pawn>())
            .Where(pawn => pawn is not null)
            .Distinct()
            .Take(BiotechThingMetadataKeys.MaxReproductiveSources)
            .Select(pawn => CapturePawn(pawn, userId))
            .ToList();
    }

    internal static ReproductiveSourceRecord CapturePawn(Pawn pawn, string? currentUserId)
    {
        string? globalId = BiotechSourcePawnIdentity.ExistingGlobalId(pawn) ?? PawnGlobalIdUtility.Build(currentUserId, pawn);
        return new ReproductiveSourceRecord(
            Role: ReproductiveSourceRoleFor(pawn),
            GlobalId: globalId,
            OwnerUserId: BiotechSourcePawnIdentity.OwnerFor(pawn, globalId, currentUserId),
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
            Gender.Female => "Mother",
            Gender.Male => "Father",
            _ => "Source"
        };
    }

    internal bool TryApply(ModThingReferenceDto reference, Thing thing, out string? missingDefName)
    {
        missingDefName = null;
        IReadOnlyList<ReproductiveSourceRecord> records = BiotechThingMetadata.ReproductiveSources(reference);
        if (records.Count == 0)
        {
            return true;
        }

        CompHasPawnSources? comp = thing.TryGetComp<CompHasPawnSources>();
        if (comp is null)
        {
            return true;
        }

        string? userId = currentUserId();
        ClearExistingPawnSources(comp);
        foreach (ReproductiveSourceRecord record in records)
        {
            if (!BiotechSourcePawnRestorer.TryFindOrCreate(record, userId, out Pawn? sourcePawn, out missingDefName)
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
}
