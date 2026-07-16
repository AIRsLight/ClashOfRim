using System.Collections.Generic;
using System.Linq;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility;
using RimWorld;
using UnityEngine;
using Verse;

namespace AIRsLight.ClashOfRim.Raids;

public static class DefensePointUtility
{
    private const string DefensePointDefName = "ClashOfRim_DefensePoint";

    public static readonly DefensePointAiMode[] AllModes =
    {
        DefensePointAiMode.Hold,
        DefensePointAiMode.Defend,
        DefensePointAiMode.Assault,
        DefensePointAiMode.OperateEquipment
    };

    public static readonly DefensePointAiMode[] NonEquipmentModes =
    {
        DefensePointAiMode.Hold,
        DefensePointAiMode.Defend,
        DefensePointAiMode.Assault
    };

    public static IEnumerable<Building_ClashDefensePoint> AllDefensePoints(Map map)
    {
        foreach (ThingDef def in AllDefensePointDefs())
        {
            foreach (Building_ClashDefensePoint point in map.listerThings.ThingsOfDef(def).OfType<Building_ClashDefensePoint>())
            {
                yield return point;
            }
        }
    }

    public static IEnumerable<CompAssignableToPawn> AllDefensePointAssignmentComps(Map map)
    {
        return AllDefensePoints(map)
            .Select(point => point.GetComp<CompAssignableToPawn>())
            .Where(comp => comp is not null)!;
    }

    public static string ModeLabel(DefensePointAiMode mode)
    {
        return ("ClashOfRim.DefensePoint.Mode." + mode).Translate().ToString();
    }

    public static Gizmo? CreateSelectAssignedPawnGizmo(CompAssignableToPawn assignmentComp)
    {
        Pawn? pawn = assignmentComp.AssignedPawnsForReading.FirstOrDefault();
        if (pawn is null || pawn.Destroyed || !pawn.def.selectable)
        {
            return null;
        }

        RenderTexture portrait = PortraitsCache.Get(
            pawn,
            new Vector2(75f, 75f),
            Rot4.South,
            default,
            1f,
            supersample: true,
            compensateForUIScale: true,
            renderHeadgear: true,
            renderClothes: true,
            healthStateOverride: PawnHealthState.Mobile);

        return new Command_Action
        {
            defaultLabel = "ClashOfRim.DefensePoint.SelectAssignedGizmo".Translate(pawn.LabelShort),
            defaultDesc = "ClashOfRim.DefensePoint.SelectAssignedGizmoDesc".Translate(pawn.LabelShort),
            icon = portrait,
            iconDrawScale = 0.85f,
            action = () =>
            {
                Find.Selector.ClearSelection();
                Find.Selector.Select(pawn, playSound: true, forceDesignatorDeselect: true);
            }
        };
    }

    public static bool IsDefensePointDef(string? defName)
    {
        return AllDefensePointDefNames()
            .Any(candidate => string.Equals(candidate, defName, System.StringComparison.Ordinal));
    }

    private static IEnumerable<ThingDef> AllDefensePointDefs()
    {
        foreach (string defName in AllDefensePointDefNames())
        {
            ThingDef? def = DefDatabase<ThingDef>.GetNamed(defName, errorOnFail: false);
            if (def is not null)
            {
                yield return def;
            }
        }
    }

    private static IEnumerable<string> AllDefensePointDefNames()
    {
        yield return DefensePointDefName;

        var seen = new HashSet<string> { DefensePointDefName };
        foreach (string defName in ClashOfRimCompatibilityApi.GetCompatibilityDefensePointDefNames())
        {
            if (seen.Add(defName))
            {
                yield return defName;
            }
        }
    }
}
