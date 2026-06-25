using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace AIRsLight.ClashOfRim.Achievements;

public sealed class Thing_ClashAchievementTrophyPlan : ThingWithComps
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

    public override IEnumerable<Gizmo> GetGizmos()
    {
        foreach (Gizmo gizmo in base.GetGizmos())
        {
            yield return gizmo;
        }

        if (trophyData is null || MapHeld is null)
        {
            yield break;
        }

        yield return new Command_Action
        {
            defaultLabel = ClashOfRimText.Key("ClashOfRim.Achievement.PlaceTrophy"),
            defaultDesc = ClashOfRimText.Key("ClashOfRim.Achievement.PlaceTrophyDesc"),
            icon = AchievementTrophyUtility.TrophyDef?.uiIcon ?? def.uiIcon,
            Order = 10f,
            action = OpenStuffMenu
        };
    }

    public override string GetInspectString()
    {
        string text = base.GetInspectString();
        if (trophyData is null)
        {
            return text;
        }

        string trophyLine = trophyData.BuildInspectLine();
        return string.IsNullOrWhiteSpace(text)
            ? trophyLine
            : text + "\n" + trophyLine;
    }

    private void OpenStuffMenu()
    {
        ThingDef? trophyDef = AchievementTrophyUtility.TrophyDef;
        Map? map = MapHeld;
        if (trophyDef is null || map is null)
        {
            Messages.Message(ClashOfRimText.Key("ClashOfRim.Achievement.TrophyMissingDef"), MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        List<FloatMenuOption> options = map.resourceCounter.AllCountedAmounts.Keys
            .Where(stuff => stuff.IsStuff
                && stuff.stuffProps.CanMake(trophyDef)
                && (DebugSettings.godMode || map.listerThings.ThingsOfDef(stuff).Count > 0))
            .OrderByDescending(stuff => stuff.stuffProps?.commonality ?? float.PositiveInfinity)
            .ThenBy(stuff => stuff.BaseMarketValue)
            .Select(stuff => BuildStuffOption(trophyDef, stuff))
            .ToList();

        if (options.Count == 0)
        {
            Messages.Message("NoStuffsToBuildWith".Translate(), MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        Find.WindowStack.Add(new FloatMenu(options));
    }

    private FloatMenuOption BuildStuffOption(ThingDef trophyDef, ThingDef stuff)
    {
        string label = GenLabel.ThingLabel(trophyDef, stuff, 1).CapitalizeFirst();
        return new FloatMenuOption(
            label,
            () =>
            {
                Designator_ClashAchievementTrophy designator = new(trophyDef, trophyData!, this);
                designator.SetStuffDef(stuff);
                Find.DesignatorManager.Select(designator);
                Messages.Message(ClashOfRimText.Key("ClashOfRim.Achievement.TrophyBlueprintSelected"), MessageTypeDefOf.TaskCompletion, historical: false);
            },
            stuff);
    }
}
