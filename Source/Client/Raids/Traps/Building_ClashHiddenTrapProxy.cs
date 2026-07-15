using System;
using System.Collections.Generic;
using System.Reflection;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace AIRsLight.ClashOfRim.Raids;

public sealed class Building_ClashHiddenTrapProxy : Building
{
    private static readonly MethodInfo? SpringChanceMethod = typeof(Building_Trap).GetMethod(
        "SpringChance",
        BindingFlags.Instance | BindingFlags.NonPublic);

    private Thing? originalTrap;
    private List<Pawn> touchingPawns = new();
    private string originalTrapId = string.Empty;
    private string originalTrapDefName = string.Empty;
    private bool sprung;
    private bool areaSpringRadiusResolved;
    private float areaSpringRadius;
    private bool releaseEntityTrapResolved;
    private bool releaseEntityTrap;

    public bool HasOriginalTrap => originalTrap is not null;

    public bool Sprung => sprung;

    public string OriginalTrapId => originalTrapId;

    public override string LabelMouseover => string.Empty;

    public void BindOriginalTrap(Thing trap, string? sourceThingId = null)
    {
        originalTrap = trap;
        originalTrapId = string.IsNullOrWhiteSpace(sourceThingId)
            ? trap.ThingID ?? trap.GetUniqueLoadID()
            : sourceThingId!;
        originalTrapDefName = trap.def?.defName ?? string.Empty;
        areaSpringRadiusResolved = false;
        areaSpringRadius = 0f;
        releaseEntityTrapResolved = false;
        releaseEntityTrap = false;
        SetFactionDirect(trap.Faction);
    }

    public override void ExposeData()
    {
        base.ExposeData();
        Scribe_Deep.Look(ref originalTrap, "clashOfRimOriginalTrap");
        Scribe_Collections.Look(ref touchingPawns, "clashOfRimTouchingPawns", LookMode.Reference);
        Scribe_Values.Look(ref originalTrapId, "clashOfRimOriginalTrapId", string.Empty);
        Scribe_Values.Look(ref originalTrapDefName, "clashOfRimOriginalTrapDefName", string.Empty);
        Scribe_Values.Look(ref sprung, "clashOfRimProxySprung", false);
        if (Scribe.mode == LoadSaveMode.PostLoadInit)
        {
            touchingPawns ??= new List<Pawn>();
            touchingPawns.RemoveAll(pawn => pawn is null);
            areaSpringRadiusResolved = false;
            areaSpringRadius = 0f;
            releaseEntityTrapResolved = false;
            releaseEntityTrap = false;
        }
    }

    protected override void Tick()
    {
        base.Tick();
        if (sprung || !Spawned || originalTrap is null)
        {
            return;
        }

        if (TryGetAreaSpringRadius(out float springRadius))
        {
            TickAreaTrap(springRadius);
            return;
        }

        if (IsReleaseEntityTrapCached())
        {
            TickReleaseEntityTrap();
            return;
        }

        TickTouchingPawnTrap();
    }

    public void RestoreOriginalIfUntriggered()
    {
        if (sprung || !Spawned || originalTrap is null)
        {
            return;
        }

        Map map = Map;
        IntVec3 position = Position;
        Rot4 rotation = Rotation;
        DeSpawn(DestroyMode.Vanish);
        GenSpawn.Spawn(originalTrap, position, map, rotation);
    }

    protected override void DrawAt(Vector3 drawLoc, bool flip = false)
    {
    }

    public override void Print(SectionLayer layer)
    {
    }

    private void TickTouchingPawnTrap()
    {
        List<Thing> thingList = Position.GetThingList(Map);
        for (int i = 0; i < thingList.Count; i++)
        {
            if (thingList[i] is Pawn pawn && !pawn.Flying && !touchingPawns.Contains(pawn))
            {
                touchingPawns.Add(pawn);
                CheckSpringProxy(pawn);
                if (sprung || !Spawned)
                {
                    return;
                }
            }
        }

        for (int i = touchingPawns.Count - 1; i >= 0; i--)
        {
            Pawn pawn = touchingPawns[i];
            if (pawn is null || !pawn.Spawned || pawn.Flying || pawn.Position != Position)
            {
                touchingPawns.RemoveAt(i);
            }
        }
    }

    private void TickAreaTrap(float springRadius)
    {
        Faction? trapFaction = originalTrap?.Faction;
        if (trapFaction is null)
        {
            return;
        }

        HashSet<IAttackTarget> targets = Map.attackTargetsCache.TargetsHostileToFaction(trapFaction);
        float springRadiusSquared = springRadius * springRadius;
        foreach (IAttackTarget attackTarget in targets)
        {
            if (attackTarget is not Pawn pawn)
            {
                continue;
            }

            if (pawn.PositionHeld.DistanceToSquared(Position) > springRadiusSquared)
            {
                continue;
            }

            SpringProxy(pawn, checkChance: true);
            if (sprung || !Spawned)
            {
                return;
            }
        }
    }

    private void TickReleaseEntityTrap()
    {
        if (!this.IsHashIntervalTick(60))
        {
            return;
        }

        Map map = Map;
        IntVec3 position = Position;
        IReadOnlyList<Pawn> pawns = map.mapPawns.AllPawnsSpawned;
        for (int i = 0; i < pawns.Count; i++)
        {
            Pawn pawn = pawns[i];
            if (pawn.IsPsychologicallyInvisible()
                || !pawn.HostileTo(this)
                || pawn.Position.DistanceTo(position) > 20f
                || !GenSight.LineOfSight(position, pawn.Position, map))
            {
                continue;
            }

            SpringProxy(pawn, checkChance: false);
            return;
        }
    }

    private bool ShouldTriggerFor(Pawn pawn)
    {
        if (pawn.Destroyed || pawn.Dead || pawn.Downed)
        {
            return false;
        }

        Faction? originalFaction = originalTrap?.Faction;
        return originalFaction is null || pawn.Faction != originalFaction;
    }

    private void CheckSpringProxy(Pawn pawn)
    {
        if (!ShouldTriggerFor(pawn)
            || originalTrap is not Building_Trap trap
            || !Rand.Chance(ResolveSpringChance(trap, pawn)))
        {
            return;
        }

        SpringProxy(pawn, checkChance: false);
    }

    private void SpringProxy(Pawn pawn, bool checkChance)
    {
        if (sprung || originalTrap is null || !Spawned)
        {
            return;
        }

        if (checkChance
            && originalTrap is Building_Trap originalBuildingTrap
            && !Rand.Chance(ResolveSpringChance(originalBuildingTrap, pawn)))
        {
            return;
        }

        sprung = true;
        Map map = Map;
        IntVec3 position = Position;
        Rot4 rotation = Rotation;
        Thing trapThing = originalTrap;

        DeSpawn(DestroyMode.Vanish);
        GenSpawn.Spawn(trapThing, position, map, rotation);
        WakeRestoredTrap(trapThing);

        if (trapThing is Building_Trap restoredTrap)
        {
            // The pawn is already standing in the trigger area when the real trap is restored.
            // Its normal Tick therefore cannot observe a fresh entry; spring it through the
            // vanilla entry point so overridden SpringSub implementations and trap comps run.
            restoredTrap.Spring(pawn);
        }
        else
        {
            Log.Warning("[ClashOfRim][TrapProxy] Restored hidden tactical object has no supported trigger entry point: def="
                + trapThing.def?.defName
                + ", type="
                + trapThing.GetType().FullName);
        }

        // The proxy def is intentionally non-destroyable so players cannot interact with it as a real building.
        // Once despawned it is no longer owned by the map; do not call Destroy on it.
        originalTrap = null;
    }

    private bool IsReleaseEntityTrapCached()
    {
        if (releaseEntityTrapResolved)
        {
            return releaseEntityTrap;
        }

        releaseEntityTrapResolved = true;
        Type? type = originalTrap?.GetType();
        while (type is not null)
        {
            if (string.Equals(type.FullName, "RimWorld.Building_TrapReleaseEntity", StringComparison.Ordinal))
            {
                releaseEntityTrap = true;
                return true;
            }

            type = type.BaseType;
        }

        releaseEntityTrap = false;
        return false;
    }

    private bool TryGetAreaSpringRadius(out float springRadius)
    {
        if (areaSpringRadiusResolved)
        {
            springRadius = areaSpringRadius;
            return springRadius > 0f;
        }

        areaSpringRadiusResolved = true;
        areaSpringRadius = 0f;
        List<DefModExtension>? modExtensions = originalTrap?.def?.modExtensions;
        if (modExtensions is null)
        {
            springRadius = 0f;
            return false;
        }

        foreach (DefModExtension extension in modExtensions)
        {
            FieldInfo? radiusField = extension.GetType().GetField(
                "springRadius",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (radiusField?.GetValue(extension) is float radius && radius > 0f)
            {
                areaSpringRadius = radius;
                springRadius = areaSpringRadius;
                return true;
            }
        }

        springRadius = 0f;
        return false;
    }

    private static float ResolveSpringChance(Building_Trap trap, Pawn pawn)
    {
        if (SpringChanceMethod is null)
        {
            return 1f;
        }

        return SpringChanceMethod.Invoke(trap, new object[] { pawn }) is float chance ? chance : 1f;
    }

    private static void WakeRestoredTrap(Thing trap)
    {
        List<ThingComp>? comps = (trap as ThingWithComps)?.AllComps;
        if (comps is null)
        {
            return;
        }

        foreach (ThingComp comp in comps)
        {
            try
            {
                comp.ReceiveCompSignal("CompCanBeDormant.WakeUp");
            }
            catch (Exception exception)
            {
                Log.Warning("[ClashOfRim][TrapProxy] Restored hidden trap comp rejected wake signal: def="
                    + trap.def?.defName
                    + ", comp="
                    + comp.GetType().FullName
                    + ", error="
                    + exception.Message);
            }
        }
    }
}
