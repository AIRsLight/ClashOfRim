using System.Linq;
using AIRsLight.ClashOfRim.CoreCompatibility;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim.Achievements;

internal static class AchievementTrophyUtility
{
    public const string TrophyDefName = "ClashOfRim_AchievementTrophy";
    public const string TrophyPlanDefName = "ClashOfRim_AchievementTrophyPlan";

    private static ThingDef? cachedTrophyDef;
    private static ThingDef? cachedTrophyPlanDef;

    public static ThingDef? TrophyDef
    {
        get
        {
            cachedTrophyDef ??= DefDatabase<ThingDef>.GetNamed(TrophyDefName, errorOnFail: false);
            return cachedTrophyDef;
        }
    }

    public static ThingDef? TrophyPlanDef
    {
        get
        {
            cachedTrophyPlanDef ??= DefDatabase<ThingDef>.GetNamed(TrophyPlanDefName, errorOnFail: false);
            return cachedTrophyPlanDef;
        }
    }

    public static bool IsTrophyDef(ThingDef? def)
    {
        return def is not null && string.Equals(def.defName, TrophyDefName, System.StringComparison.Ordinal);
    }

    public static bool IsTrophyFrame(Frame frame)
    {
        return IsTrophyDef(frame.def?.entityDefToBuild as ThingDef);
    }

    public static void ApplyToThing(Thing? thing, AchievementTrophyData? data)
    {
        if (thing is Building_ClashAchievementTrophy trophy)
        {
            trophy.SetTrophyData(data);
            return;
        }

        if (thing is not null && data is not null)
        {
            CompArtFixedText.Set(thing.TryGetComp<CompArt>(), data.BuildArtDescription());
        }
    }

    public static bool TryCreatePlanOnCurrentMap(AchievementTrophyData data, out string message)
    {
        Map? map = Find.CurrentMap;
        if (map is null)
        {
            message = ClashOfRimText.Key("ClashOfRim.Achievement.TrophyNoMap");
            return false;
        }

        ThingDef? planDef = TrophyPlanDef;
        if (planDef is null)
        {
            message = ClashOfRimText.Key("ClashOfRim.Achievement.TrophyPlanMissingDef");
            return false;
        }

        Thing plan = ThingMaker.MakeThing(planDef);
        if (plan is not Thing_ClashAchievementTrophyPlan trophyPlan)
        {
            message = ClashOfRimText.Key("ClashOfRim.Achievement.TrophyPlanMissingDef");
            return false;
        }

        trophyPlan.SetTrophyData(data);
        IntVec3 dropCell = BestPlanDropCell(map);
        if (!GenPlace.TryPlaceThing(trophyPlan, dropCell, map, ThingPlaceMode.Near))
        {
            message = ClashOfRimText.Key("ClashOfRim.Achievement.TrophyPlanCreateFailed");
            return false;
        }

        Find.Selector.ClearSelection();
        Find.Selector.Select(trophyPlan);
        message = ClashOfRimText.Key("ClashOfRim.Achievement.TrophyPlanCreated");
        return true;
    }

    private static IntVec3 BestPlanDropCell(Map map)
    {
        Pawn? selectedPawn = Find.Selector.SelectedObjects.OfType<Pawn>()
            .FirstOrDefault(pawn => pawn.Spawned && pawn.Map == map && pawn.Faction == Faction.OfPlayer);
        if (selectedPawn is not null)
        {
            return selectedPawn.Position;
        }

        Pawn? colonist = map.mapPawns.FreeColonistsSpawned.FirstOrDefault();
        if (colonist is not null)
        {
            return colonist.Position;
        }

        return map.Center;
    }
}
