using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace AIRsLight.ClashOfRim.Achievements;

internal sealed class Designator_ClashAchievementTrophy : Designator_Build
{
    private readonly AchievementTrophyData trophyData;
    private readonly Thing_ClashAchievementTrophyPlan? sourcePlan;

    public Designator_ClashAchievementTrophy(
        ThingDef trophyDef,
        AchievementTrophyData trophyData,
        Thing_ClashAchievementTrophyPlan? sourcePlan = null)
        : base(trophyDef)
    {
        this.trophyData = trophyData.Clone();
        this.sourcePlan = sourcePlan;
        defaultLabel = trophyDef.LabelCap;
        defaultDesc = ClashOfRimText.Key("ClashOfRim.Achievement.PlaceTrophyDesc");
        Order = 2000f;
    }

    public override bool CanRemainSelected()
    {
        return sourcePlan is null || !sourcePlan.Destroyed;
    }

    public override void DesignateSingleCell(IntVec3 c)
    {
        if (TutorSystem.TutorialMode && !TutorSystem.AllowAction(new EventPack(TutorTagDesignate, c)))
        {
            return;
        }

        List<Thing> thingList = c.GetThingList(Map);
        for (int index = thingList.Count - 1; index >= 0; index--)
        {
            if (thingList[index] is Frame frame
                && entDef.blueprintDef is not null
                && entDef.blueprintDef.replaceTags.NotNullAndContainsAnyElement(frame.def.replaceTags))
            {
                frame.Destroy(DestroyMode.Cancel);
            }
        }

        if (DebugSettings.godMode || entDef.GetStatValueAbstract(StatDefOf.WorkToBuild, StuffDef) == 0f)
        {
            Thing thing = ThingMaker.MakeThing((ThingDef)entDef, StuffDef);
            thing.SetFactionDirect(Faction.OfPlayer);
            Thing spawned = GenSpawn.Spawn(thing, c, Map, placingRot, WipeMode.Vanish);

            if (glowerColorOverride is not null && spawned.TryGetComp<CompGlower>() is { } compGlower)
            {
                compGlower.GlowColor = glowerColorOverride.Value;
            }

            AchievementTrophyUtility.ApplyToThing(spawned, trophyData);
            ConsumeSourcePlan();
        }
        else
        {
            ThingDef? blueprintDef = entDef.blueprintDef;
            if (blueprintDef is null || entDef.frameDef is null)
            {
                Log.ErrorOnce(
                    $"[ClashOfRim] Achievement trophy build def is missing blueprint/frame defs: {entDef.defName}.",
                    1872160413);
                Messages.Message("ClashOfRim: achievement trophy blueprint is not configured.", MessageTypeDefOf.RejectInput, false);
                return;
            }

            GenSpawn.WipeExistingThings(c, placingRot, blueprintDef, Map, DestroyMode.Deconstruct);
            Blueprint_Build blueprint = GenConstruct.PlaceBlueprintForBuild(
                entDef,
                c,
                Map,
                placingRot,
                Faction.OfPlayer,
                StuffDef,
                null,
                null,
                true);
            blueprint.glowerColorOverride = glowerColorOverride;
            if (blueprint is Blueprint_ClashAchievementTrophy trophyBlueprint)
            {
                trophyBlueprint.SetTrophyData(trophyData);
            }

            ConsumeSourcePlan();
        }

        FleckMaker.ThrowMetaPuffs(GenAdj.OccupiedRect(c, placingRot, entDef.Size), Map);
        if (TutorSystem.TutorialMode)
        {
            TutorSystem.Notify_Event(new EventPack(TutorTagDesignate, c));
        }

        if (entDef.PlaceWorkers is not null)
        {
            for (int index = 0; index < entDef.PlaceWorkers.Count; index++)
            {
                entDef.PlaceWorkers[index].PostPlace(Map, entDef, c, placingRot);
            }
        }
    }

    private void ConsumeSourcePlan()
    {
        if (sourcePlan is null || sourcePlan.Destroyed)
        {
            return;
        }

        sourcePlan.Destroy(DestroyMode.Vanish);
        Find.DesignatorManager.Deselect();
    }
}
