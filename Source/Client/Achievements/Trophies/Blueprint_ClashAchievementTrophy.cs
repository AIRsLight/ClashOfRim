using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim.Achievements;

public sealed class Blueprint_ClashAchievementTrophy : Blueprint_Build
{
    private AchievementTrophyData? trophyData;

    internal void SetTrophyData(AchievementTrophyData? data)
    {
        trophyData = data?.Clone();
    }

    public override void ExposeData()
    {
        base.ExposeData();
        Scribe_Deep.Look(ref trophyData, "clashOfRimAchievementTrophy");
    }

    protected override Thing MakeSolidThing(out bool shouldSelect)
    {
        Thing thing = base.MakeSolidThing(out shouldSelect);
        if (thing is Frame frame)
        {
            AchievementTrophyFrameState.Set(frame, trophyData);
        }

        return thing;
    }
}
