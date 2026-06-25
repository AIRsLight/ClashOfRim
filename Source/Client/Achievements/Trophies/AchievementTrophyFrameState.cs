using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim.Achievements;

internal static class AchievementTrophyFrameState
{
    private static readonly ConditionalWeakTable<Frame, State> States = new();

    public static void Set(Frame frame, AchievementTrophyData? data)
    {
        if (data is null)
        {
            States.Remove(frame);
            return;
        }

        States.GetOrCreateValue(frame).Data = data.Clone();
    }

    public static AchievementTrophyData? Get(Frame frame)
    {
        return States.TryGetValue(frame, out State state)
            ? state.Data
            : null;
    }

    public static void Expose(Frame frame)
    {
        if (!AchievementTrophyUtility.IsTrophyFrame(frame))
        {
            return;
        }

        AchievementTrophyData? data = Get(frame);
        Scribe_Deep.Look(ref data, "clashOfRimAchievementTrophy");
        if (data is not null)
        {
            Set(frame, data);
        }
    }

    private sealed class State
    {
        public AchievementTrophyData? Data;
    }
}

[HarmonyPatch(typeof(Frame), nameof(Frame.ExposeData))]
internal static class AchievementTrophyFrameExposePatch
{
    private static void Postfix(Frame __instance)
    {
        AchievementTrophyFrameState.Expose(__instance);
    }
}

[HarmonyPatch(typeof(Frame), nameof(Frame.CompleteConstruction))]
internal static class AchievementTrophyFrameCompletePatch
{
    private sealed class CompleteState
    {
        public AchievementTrophyData? Data;
        public Map? Map;
        public IntVec3 Position;
    }

    private static void Prefix(Frame __instance, out CompleteState __state)
    {
        __state = new CompleteState
        {
            Data = AchievementTrophyFrameState.Get(__instance)?.Clone(),
            Map = __instance.Map,
            Position = __instance.Position
        };
    }

    private static void Postfix(CompleteState __state)
    {
        if (__state.Data is null || __state.Map is null)
        {
            return;
        }

        List<Thing> things = __state.Position.GetThingList(__state.Map);
        for (int index = 0; index < things.Count; index++)
        {
            Thing thing = things[index];
            if (AchievementTrophyUtility.IsTrophyDef(thing.def))
            {
                AchievementTrophyUtility.ApplyToThing(thing, __state.Data);
                return;
            }
        }
    }
}
