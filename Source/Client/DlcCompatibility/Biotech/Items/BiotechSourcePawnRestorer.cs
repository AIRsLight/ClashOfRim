using AIRsLight.ClashOfRim.DlcCompatibility.Biotech.Genes;
using AIRsLight.ClashOfRim.Diplomacy;
using AIRsLight.ClashOfRim.Modules.Compatibility.Biotech;
using RimWorld;
using RimWorld.Planet;
using System;
using System.Collections.Generic;
using System.Linq;
using Verse;

namespace AIRsLight.ClashOfRim.DlcCompatibility.Biotech.Items;

internal static class BiotechSourcePawnRestorer
{
    internal static bool TryFindOrCreate(
        ReproductiveSourceRecord record,
        string? currentUserId,
        out Pawn? pawn,
        out string? missingDefName)
    {
        missingDefName = null;
        pawn = FindExisting(record.GlobalId);
        if (pawn is not null)
        {
            return TryApplyState(pawn, record, currentUserId, out missingDefName);
        }

        if (!TryResolvePawnKind(record, out PawnKindDef? pawnKind, out missingDefName)
            || pawnKind is null)
        {
            return false;
        }

        Faction? faction = ResolveFaction(record, currentUserId);
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
            QuestUtility.AddQuestTag(ref pawn.questTags, BiotechSourcePawnIdentity.ReproductiveSourcePlaceholderTagPrefix + record.GlobalId!.Trim());
        }

        BiotechSourcePawnIdentity.MarkGlobalId(pawn, record.GlobalId);
        if (!TryApplyState(pawn, record, currentUserId, out missingDefName))
        {
            return false;
        }

        if (Find.WorldPawns is not null && !Find.WorldPawns.Contains(pawn))
        {
            Find.WorldPawns.PassToWorld(pawn, PawnDiscardDecideMode.KeepForever);
        }

        return true;
    }

    private static bool TryApplyState(
        Pawn pawn,
        ReproductiveSourceRecord record,
        string? currentUserId,
        out string? missingDefName)
    {
        missingDefName = null;
        Faction? faction = ResolveFaction(record, currentUserId);
        if (faction is not null && pawn.Faction != faction)
        {
            pawn.SetFaction(faction);
        }

        if (Enum.TryParse(record.Gender, ignoreCase: true, out Gender gender) && gender != Gender.None)
        {
            pawn.gender = gender;
        }

        BiotechSourcePawnIdentity.MarkGlobalId(pawn, record.GlobalId);
        return TryApplyGenes(pawn, record, out missingDefName);
    }

    private static bool TryApplyGenes(
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
            BiotechGeneRuntime.ResetTrackerCaches(pawn);
            return true;
        }

        pawn.genes.SetXenotypeDirect(xenotype ?? XenotypeDefOf.Baseliner);
        pawn.genes.Endogenes.Clear();
        pawn.genes.ClearXenogenes();
        foreach (string geneDefName in record.EndogeneDefNames)
        {
            GeneDef? gene = BiotechGeneRuntime.ResolveDef(geneDefName);
            if (gene is null)
            {
                missingDefName = geneDefName;
                return false;
            }

            pawn.genes.AddGene(gene, xenogene: false);
        }

        foreach (string geneDefName in record.XenogeneDefNames)
        {
            GeneDef? gene = BiotechGeneRuntime.ResolveDef(geneDefName);
            if (gene is null)
            {
                missingDefName = geneDefName;
                return false;
            }

            pawn.genes.AddGene(gene, xenogene: true);
        }

        BiotechGeneRuntime.ResetTrackerCaches(pawn);
        pawn.skills?.DirtyAptitudes();
        pawn.Notify_DisabledWorkTypesChanged();
        return true;
    }

    private static bool TryResolvePawnKind(
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

    private static Faction? ResolveFaction(ReproductiveSourceRecord record, string? currentUserId)
    {
        string? ownerUserId = BiotechSourcePawnIdentity.OwnerFromRecord(record);
        if (!string.IsNullOrWhiteSpace(ownerUserId))
        {
            if (!string.IsNullOrWhiteSpace(currentUserId)
                && string.Equals(ownerUserId, currentUserId, StringComparison.Ordinal))
            {
                return Faction.OfPlayer ?? ResolveFactionByDef(record.FactionDefName, allowPlayerFaction: true);
            }

            Faction? proxy = PlayerFactionProxyUtility.EnsureProxyForUser(
                ownerUserId,
                record.FactionDefName);
            if (proxy is not null)
            {
                return proxy;
            }

            return ResolveFactionByDef(record.FactionDefName, allowPlayerFaction: false);
        }

        return ResolveFactionByDef(record.FactionDefName, allowPlayerFaction: true)
            ?? Faction.OfPlayer;
    }

    private static Faction? ResolveFactionByDef(string? factionDefName, bool allowPlayerFaction)
    {
        if (string.IsNullOrWhiteSpace(factionDefName))
        {
            return allowPlayerFaction ? Faction.OfPlayer : null;
        }

        if (!allowPlayerFaction && BiotechSourcePawnIdentity.IsPlayerLikeFactionDef(factionDefName))
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

    private static Pawn? FindExisting(string? globalId)
    {
        if (string.IsNullOrWhiteSpace(globalId))
        {
            return null;
        }

        string globalTag = BiotechSourcePawnIdentity.PawnGlobalIdTagPrefix + globalId!.Trim();
        string placeholderTag = BiotechSourcePawnIdentity.ReproductiveSourcePlaceholderTagPrefix + globalId.Trim();
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
}
