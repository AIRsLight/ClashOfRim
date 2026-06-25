using System.Linq;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim.Raids;

internal static class RaidDefenderLootProtectionUtility
{
    private const string ProtectionHediffDefName = "ClashOfRim_RaidLootDissolver";

    private static HediffDef? protectionHediff;

    public static HediffDef? ProtectionHediff =>
        protectionHediff ??= DefDatabase<HediffDef>.GetNamedSilentFail(ProtectionHediffDefName);

    public static void Apply(Map map, Faction defenderFaction)
    {
        if (map?.mapPawns?.AllPawnsSpawned is null || defenderFaction is null)
        {
            return;
        }

        int protectedPawnCount = 0;
        int markedPawnCount = 0;

        foreach (Pawn pawn in map.mapPawns.AllPawnsSpawned.ToList())
        {
            if (!ShouldProtect(pawn, defenderFaction))
            {
                continue;
            }

            protectedPawnCount++;
            pawn.guest.Recruitable = false;
            if (TryAddProtectionHediff(pawn))
            {
                markedPawnCount++;
            }
        }

        ClashLog.Message(
            $"[ClashOfRim][Raid] Protected defender pawns={protectedPawnCount} lootDissolverHediffs={markedPawnCount}.");
    }

    private static bool ShouldProtect(Pawn pawn, Faction defenderFaction)
    {
        return pawn is not null
            && !pawn.Dead
            && pawn.Faction == defenderFaction
            && pawn.RaceProps?.Humanlike == true;
    }

    public static bool HasProtectionHediff(Pawn? pawn)
    {
        HediffDef? def = ProtectionHediff;
        return pawn?.health?.hediffSet is not null
            && def is not null
            && pawn.health.hediffSet.GetFirstHediffOfDef(def) is not null;
    }

    public static bool IsProtectedDefenderPawn(Pawn? pawn)
    {
        return pawn is not null
            && !pawn.Destroyed
            && pawn.RaceProps?.Humanlike == true
            && HasProtectionHediff(pawn);
    }

    public static bool IsProtectedDefenderCorpse(Thing? thing)
    {
        return thing is Corpse corpse
            && corpse.InnerPawn is not null
            && IsProtectedDefenderPawn(corpse.InnerPawn);
    }

    public static bool IsProtectedDefenderPawnOrCorpse(Thing? thing)
    {
        return IsProtectedDefenderPawn(thing as Pawn)
            || IsProtectedDefenderCorpse(thing);
    }

    public static void DestroyProtectedGear(Pawn? pawn)
    {
        if (!HasProtectionHediff(pawn))
        {
            return;
        }

        pawn!.equipment?.DestroyAllEquipment(DestroyMode.Vanish);
        pawn.apparel?.DestroyAll(DestroyMode.Vanish);
    }

    public static bool TryDissolveEquipment(Pawn? pawn, ThingWithComps? equipment)
    {
        if (!HasProtectionHediff(pawn) || equipment is null || equipment.Destroyed)
        {
            return false;
        }

        equipment.Destroy(DestroyMode.Vanish);
        return true;
    }

    public static bool TryDissolveApparel(Pawn? pawn, Apparel? apparel)
    {
        if (!HasProtectionHediff(pawn) || apparel is null || apparel.Destroyed)
        {
            return false;
        }

        apparel.Destroy(DestroyMode.Vanish);
        return true;
    }

    private static bool TryAddProtectionHediff(Pawn pawn)
    {
        HediffDef? def = ProtectionHediff;
        if (def is null
            || pawn.health?.hediffSet is null
            || pawn.health.hediffSet.GetFirstHediffOfDef(def) is not null
            || !pawn.health.hediffSet.TryGetBodyPartRecord(BodyPartDefOf.Torso, out BodyPartRecord torso))
        {
            return false;
        }

        pawn.health.AddHediff(HediffMaker.MakeHediff(def, pawn, torso), torso);
        return true;
    }
}
