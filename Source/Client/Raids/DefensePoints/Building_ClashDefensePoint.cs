using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace AIRsLight.ClashOfRim.Raids;

public class Building_ClashDefensePoint : Building
{
    public const float DefaultDefendRadius = 28f;
    public const float DefaultActionRadius = 24f;

    private const string SecurityDesignationCategoryDefName = "Security";
    private const float MinConfigurableRadius = 0f;
    private const float MaxConfigurableRadius = 64f;

    private DefensePointAiMode aiMode = DefensePointAiMode.Defend;
    private float actionRadius = DefaultActionRadius;

    public DefensePointAiMode AiMode => aiMode;

    public float ActionRadius => actionRadius;

    protected virtual IReadOnlyList<DefensePointAiMode> AvailableModes => DefensePointUtility.AllModes;

    public Pawn? AssignedPawn => this.TryGetComp<CompAssignableToPawn>()?.AssignedPawnsForReading.FirstOrDefault();

    public static bool ShouldShowOverlay(Thing? thing)
    {
        if (thing is null || !thing.Spawned)
        {
            return false;
        }

        Selector? selector = Find.Selector;
        if (selector?.IsSelected(thing) == true)
        {
            return true;
        }

        if (selector?.SelectedObjectsListForReading.Any(selected => selected is Building_ClashDefensePoint) == true)
        {
            return true;
        }

        if (Find.DesignatorManager?.SelectedDesignator is Designator_Build buildDesignator)
        {
            return buildDesignator.PlacingDef is ThingDef thingDef && DefensePointUtility.IsDefensePointDef(thingDef.defName);
        }

        if (IsSecurityArchitectTabOpen())
        {
            return true;
        }

        return Prefs.DevMode && DebugSettings.godMode;
    }

    private static bool IsSecurityArchitectTabOpen()
    {
        if (Find.MainTabsRoot?.OpenTab != MainButtonDefOf.Architect)
        {
            return false;
        }

        return MainButtonDefOf.Architect.TabWindow is MainTabWindow_Architect architectWindow
            && string.Equals(
                architectWindow.selectedDesPanel?.def?.defName,
                SecurityDesignationCategoryDefName,
                System.StringComparison.Ordinal);
    }

    public override void ExposeData()
    {
        base.ExposeData();
        Scribe_Values.Look(ref aiMode, "clashOfRimDefensePointAiMode", DefensePointAiMode.Defend);
        Scribe_Values.Look(ref actionRadius, "clashOfRimDefensePointActionRadius", DefaultActionRadius);
        if (!AvailableModes.Contains(aiMode))
        {
            aiMode = DefensePointAiMode.Defend;
        }

        actionRadius = ClampRadius(actionRadius);
    }

    protected override void DrawAt(Vector3 drawLoc, bool flip = false)
    {
        if (RaidTrapVisibilityController.ShouldHide(this, RaidTrapVisibilitySurface.MapDrawing))
        {
            return;
        }

        if (!ShouldShowOverlay(this))
        {
            return;
        }

        base.DrawAt(drawLoc, flip);
    }

    public override void Print(SectionLayer layer)
    {
        // Defense points are map markers. They are drawn only while selected or being placed.
    }

    public override string GetInspectString()
    {
        string inspect = base.GetInspectString();
        string modeLine = "ClashOfRim.DefensePoint.ModeInspect".Translate(DefensePointUtility.ModeLabel(aiMode));
        string rangeLine = "ClashOfRim.DefensePoint.RangeInspect".Translate(FormatRadius(actionRadius));
        Pawn? assignedPawn = AssignedPawn;
        string? assignedLine = assignedPawn is null
            ? null
            : "ClashOfRim.DefensePoint.AssignedInspect".Translate(assignedPawn.LabelShort);

        var lines = new List<string> { modeLine, rangeLine };
        if (assignedLine is not null)
        {
            lines.Add(assignedLine);
        }

        if (inspect.NullOrEmpty())
        {
            return string.Join("\n", lines);
        }

        return inspect + "\n" + string.Join("\n", lines);
    }

    public override void DrawExtraSelectionOverlays()
    {
        base.DrawExtraSelectionOverlays();
        if (Spawned
            && actionRadius > 0f
            && !RaidTrapVisibilityController.ShouldHide(this, RaidTrapVisibilitySurface.MapDrawing))
        {
            GenDraw.DrawRadiusRing(Position, actionRadius);
        }
    }

    public override IEnumerable<Gizmo> GetGizmos()
    {
        foreach (Gizmo gizmo in base.GetGizmos())
        {
            yield return gizmo;
        }

        if (Faction != Faction.OfPlayer)
        {
            yield break;
        }

        yield return new Command_Action
        {
            defaultLabel = "ClashOfRim.DefensePoint.ModeGizmo".Translate(DefensePointUtility.ModeLabel(aiMode)),
            defaultDesc = "ClashOfRim.DefensePoint.ModeGizmoDesc".Translate(),
            icon = ContentFinder<Texture2D>.Get("UI/Commands/Attack", reportFailure: false) ?? BaseContent.BadTex,
            action = OpenModeMenu
        };

        yield return new Command_Action
        {
            defaultLabel = "ClashOfRim.DefensePoint.RangeGizmo".Translate(FormatRadius(actionRadius)),
            defaultDesc = "ClashOfRim.DefensePoint.RangeGizmoDesc".Translate(),
            icon = ContentFinder<Texture2D>.Get("UI/Commands/SetTargetFuelLevel", reportFailure: false) ?? BaseContent.BadTex,
            action = OpenRangeMenu
        };
    }

    private void OpenModeMenu()
    {
        var options = new List<FloatMenuOption>();
        foreach (DefensePointAiMode mode in AvailableModes)
        {
            DefensePointAiMode captured = mode;
            options.Add(new FloatMenuOption(
                DefensePointUtility.ModeLabel(captured),
                () => aiMode = captured));
        }

        Find.WindowStack.Add(new FloatMenu(options));
    }

    private void OpenRangeMenu()
    {
        Find.WindowStack.Add(new Dialog_Slider(
            value => "ClashOfRim.DefensePoint.RangeOption".Translate(value).ToString(),
            Mathf.RoundToInt(MinConfigurableRadius),
            Mathf.RoundToInt(MaxConfigurableRadius),
            value => actionRadius = ClampRadius(value),
            Mathf.RoundToInt(actionRadius),
            1f));
    }

    private static float ClampRadius(float value)
    {
        return Mathf.Clamp(value, MinConfigurableRadius, MaxConfigurableRadius);
    }

    private static string FormatRadius(float value)
    {
        return Mathf.RoundToInt(value).ToString();
    }
}
