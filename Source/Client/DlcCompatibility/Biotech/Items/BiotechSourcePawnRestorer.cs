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
    internal static bool TryPrepare(ReproductiveSourceRecord record, PreparedPawn? sharedTarget,
        out PreparedPawn? preparation, out string? missingDefName)
    {
        preparation = null;
        missingDefName = null;
        Pawn? pawn = sharedTarget?.Pawn ?? FindExisting(record.GlobalId);
        PawnKindDef? kind = null;
        if (sharedTarget is null && pawn is null && !TryResolvePawnKind(record, out kind, out missingDefName))
            return false;
        BiotechPawnGeneRestoration? genes = null;
        if (pawn is not null && !BiotechPawnGeneRestoration.TryPrepare(pawn, record, out genes, out missingDefName))
            return false;
        preparation = new PreparedPawn(record, pawn, kind, sharedTarget, genes);
        return true;
    }

    internal sealed class PreparedPawn
    {
        private readonly ReproductiveSourceRecord record;
        private readonly PawnKindDef? kind;
        private readonly PreparedPawn? sharedTarget;
        private readonly bool genesPrepared;
        private Pawn? pawn;
        private Faction? generatedFaction;
        private BiotechPawnGeneRestoration? genes;
        private bool created;

        internal PreparedPawn(ReproductiveSourceRecord record, Pawn? pawn, PawnKindDef? kind,
            PreparedPawn? sharedTarget, BiotechPawnGeneRestoration? genes)
        {
            this.record = record;
            this.pawn = pawn;
            this.kind = kind;
            this.sharedTarget = sharedTarget;
            this.genes = genes;
            genesPrepared = pawn is not null;
        }

        internal Pawn? Pawn => sharedTarget?.Pawn ?? pawn;

        internal bool TryCreate(string? currentUserId, out string? missingDefName)
        {
            missingDefName = null;
            if (sharedTarget is null && pawn is null)
            {
                generatedFaction = ResolveFaction(record, currentUserId);
                try
                {
                    pawn = PawnGenerator.GeneratePawn(new PawnGenerationRequest(kind!, generatedFaction,
                        forceGenerateNewPawn: true, canGeneratePawnRelations: false));
                    created = pawn is not null;
                }
                catch (Exception ex)
                {
                    missingDefName = kind!.defName;
                    Log.Warning("[ClashOfRim][Biotech] Failed to generate reproductive source pawn placeholder: "
                        + kind.defName + " " + ex.Message);
                    return false;
                }
                if (pawn is null)
                {
                    missingDefName = kind!.defName;
                    return false;
                }
            }
            return genesPrepared || BiotechPawnGeneRestoration.TryPrepare(Pawn!, record, out genes, out missingDefName);
        }

        internal void Apply(string? currentUserId)
        {
            Pawn target = Pawn!;
            if (created)
            {
                if (!string.IsNullOrWhiteSpace(record.Name)) target.Name = new NameSingle(record.Name!.Trim());
                if (!string.IsNullOrWhiteSpace(record.GlobalId))
                    QuestUtility.AddQuestTag(ref target.questTags, BiotechSourcePawnIdentity.ReproductiveSourcePlaceholderTagPrefix + record.GlobalId!.Trim());
            }
            Faction? faction = created ? generatedFaction : ResolveFaction(record, currentUserId);
            if (faction is not null && target.Faction != faction) target.SetFaction(faction);
            if (Enum.TryParse(record.Gender, ignoreCase: true, out Gender gender) && gender != Gender.None) target.gender = gender;
            BiotechSourcePawnIdentity.MarkGlobalId(target, record.GlobalId);
            genes?.Apply(target);
            if (created && Find.WorldPawns is not null && !Find.WorldPawns.Contains(target))
                Find.WorldPawns.PassToWorld(target, PawnDiscardDecideMode.KeepForever);
        }

        internal void DiscardCreatedPawn()
        {
            if (!created || pawn is null) return;
            if (Find.WorldPawns?.Contains(pawn) == true) Find.WorldPawns.RemoveAndDiscardPawnViaGC(pawn);
            else pawn.Discard();
            pawn = null;
            created = false;
        }
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
