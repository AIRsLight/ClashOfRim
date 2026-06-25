using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.RemoteMaps;
using AIRsLight.ClashOfRim.Raids;
using RimWorld;
using RimWorld.Planet;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using Verse;

namespace AIRsLight.ClashOfRim.ThirdPartyCompatibility;

public static partial class ClashOfRimCompatibilityApi
{
    public static RaidCaravanMapEntryResult TryEnterRaidCaravanMap(Caravan caravan, Map map, IReadOnlyList<Pawn> attackPawns)
    {
        if (RaidCaravanMapEntryHandlers.Count == 0)
        {
            return RaidCaravanMapEntryResult.NotHandled;
        }

        IReadOnlyList<Pawn> safeAttackPawns = attackPawns ?? new List<Pawn>();
        foreach (RaidCaravanMapEntryHandler handler in RaidCaravanMapEntryHandlers.ToList())
        {
            if (!TryInvokeCompatibilityCallback(
                    nameof(TryEnterRaidCaravanMap),
                    handler,
                    () => handler(caravan, map, safeAttackPawns),
                    out RaidCaravanMapEntryResult result))
            {
                continue;
            }

            if (result.Kind != RaidCaravanMapEntryResultKind.NotHandled)
            {
                return result;
            }
        }

        return RaidCaravanMapEntryResult.NotHandled;
    }

    public static void NotifyRemoteDefenderMapPrepared(Map map, Faction defenderFaction)
    {
        if (RemoteDefenderMapPreparedHandlers.Count == 0)
        {
            return;
        }

        foreach (RemoteDefenderMapPreparedHandler handler in RemoteDefenderMapPreparedHandlers.ToList())
        {
            TryInvokeCompatibilityCallback(
                nameof(NotifyRemoteDefenderMapPrepared),
                handler,
                () => handler(map, defenderFaction));
        }
    }

    public static void NotifyRemoteMapLoaded(Map map, RemoteSessionMapParent carrier, string scope, ModSnapshotPackageMetadataDto package)
    {
        if (RemoteMapLoadedHandlers.Count == 0)
        {
            return;
        }

        foreach (RemoteMapLoadedHandler handler in RemoteMapLoadedHandlers.ToList())
        {
            TryInvokeCompatibilityCallback(
                nameof(NotifyRemoteMapLoaded),
                handler,
                () => handler(map, carrier, scope, package));
        }
    }

    public static IReadOnlyList<ILoadReferenceable> RewriteRemoteMapProjectionReferences(
        ModSnapshotPackageMetadataDto package,
        XDocument sourceDocument,
        XElement projectionElement)
    {
        if (package is null || sourceDocument is null || projectionElement is null)
        {
            return Array.Empty<ILoadReferenceable>();
        }

        var references = new List<ILoadReferenceable>();
        foreach (RemoteMapProjectionReferenceRewriter rewriter in RemoteMapProjectionReferenceRewriters.ToList())
        {
            if (!TryInvokeCompatibilityCallback(
                    nameof(RewriteRemoteMapProjectionReferences),
                    rewriter,
                    () => rewriter(package, sourceDocument, projectionElement),
                    out IReadOnlyList<ILoadReferenceable>? result))
            {
                continue;
            }

            if (result is null || result.Count == 0)
            {
                continue;
            }

            references.AddRange(result.Where(reference => reference is not null));
        }

        return references
            .GroupBy(reference => reference.GetUniqueLoadID(), StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();
    }

    public static void SanitizeRemoteMapProjection(
        ModSnapshotPackageMetadataDto package,
        XElement mapElement,
        XElement referencePawnsElement)
    {
        foreach (RemoteMapProjectionSanitizer sanitizer in RemoteMapProjectionSanitizers.ToList())
        {
            TryInvokeCompatibilityCallback(
                nameof(SanitizeRemoteMapProjection),
                sanitizer,
                () => sanitizer(package, mapElement, referencePawnsElement));
        }
    }

    public static bool TryCleanupRemoteSessionPawn(Pawn pawn, string reason)
    {
        if (pawn is null || RemoteSessionPawnCleanupHandlers.Count == 0)
        {
            return false;
        }

        foreach (RemoteSessionPawnCleanupHandler handler in RemoteSessionPawnCleanupHandlers.ToList())
        {
            if (!TryInvokeCompatibilityCallback(
                    nameof(TryCleanupRemoteSessionPawn),
                    handler,
                    () => handler(pawn, reason),
                    out bool handled))
            {
                continue;
            }

            if (handled)
            {
                return true;
            }
        }

        return false;
    }

    public static int SanitizeSnapshotSave(XDocument document)
    {
        int changes = 0;
        foreach (CompatibilitySnapshotSaveSanitizer sanitizer in SnapshotSaveSanitizers.ToList())
        {
            if (TryInvokeCompatibilityCallback(
                    nameof(SanitizeSnapshotSave),
                    sanitizer,
                    () => sanitizer(document),
                    out int sanitizerChanges))
            {
                changes += Math.Max(0, sanitizerChanges);
            }
        }

        return changes;
    }

    public static void ApplyRaidDefenderProxyPawnProtection(Pawn pawn, Faction defenderFaction)
    {
        if (pawn is null || defenderFaction is null || pawn.Destroyed || pawn.Dead)
        {
            return;
        }

        if (pawn.Faction != defenderFaction)
        {
            pawn.SetFaction(defenderFaction);
        }

        if (pawn.guest is not null)
        {
            pawn.guest.Recruitable = false;
        }

        HediffDef? protection = RaidDefenderLootProtectionUtility.ProtectionHediff;
        if (protection is null
            || pawn.health?.hediffSet is null
            || pawn.health.hediffSet.GetFirstHediffOfDef(protection) is not null
            || !pawn.health.hediffSet.TryGetBodyPartRecord(BodyPartDefOf.Torso, out BodyPartRecord torso))
        {
            return;
        }

        pawn.health.AddHediff(HediffMaker.MakeHediff(protection, pawn, torso), torso);
    }

}
