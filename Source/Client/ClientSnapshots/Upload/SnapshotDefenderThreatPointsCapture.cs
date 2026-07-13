using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim.ClientSnapshots;

internal static class SnapshotDefenderThreatPointsCapture
{
    public static float? TryCapture()
    {
        Map? map = Find.AnyPlayerHomeMap;
        if (Current.ProgramState != ProgramState.Playing || map is null)
        {
            return null;
        }

        float points = StorytellerUtility.DefaultThreatPointsNow(map);
        return float.IsNaN(points) || float.IsInfinity(points)
            ? null
            : System.Math.Max(0f, points);
    }
}
