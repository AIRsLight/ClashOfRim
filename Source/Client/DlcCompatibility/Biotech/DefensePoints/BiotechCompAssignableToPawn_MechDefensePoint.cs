using System.Collections.Generic;
using System.Linq;
using AIRsLight.ClashOfRim.Raids;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim.DlcCompatibility;

public sealed class CompAssignableToPawn_MechDefensePoint : CompAssignableToPawn
{
    public override void DrawGUIOverlay()
    {
        if (AssignedPawnsForReading.Count > 0 || Building_ClashDefensePoint.ShouldShowOverlay(parent))
        {
            base.DrawGUIOverlay();
        }
    }

    public override IEnumerable<Pawn> AssigningCandidates
    {
        get
        {
            if (!parent.Spawned)
            {
                return Enumerable.Empty<Pawn>();
            }

            return parent.Map.mapPawns.AllPawnsSpawned
                .Where(pawn => pawn.Faction == Faction.OfPlayer && pawn.IsColonyMech && !pawn.Dead)
                .OrderBy(pawn => pawn.LabelShort);
        }
    }

    public override AcceptanceReport CanAssignTo(Pawn pawn)
    {
        if (pawn.Dead)
        {
            return "Dead".Translate();
        }

        if (pawn.Faction != Faction.OfPlayer)
        {
            return "ClashOfRim.DefensePoint.AssignPlayerOnly".Translate();
        }

        if (!pawn.IsColonyMech)
        {
            return "ClashOfRim.DefensePoint.AssignMechOnly".Translate();
        }

        return AcceptanceReport.WasAccepted;
    }

    public override bool AssignedAnything(Pawn pawn)
    {
        if (!parent.Spawned)
        {
            return base.AssignedAnything(pawn);
        }

        return DefensePointUtility.AllDefensePointAssignmentComps(parent.Map)
            .Any(comp => comp.AssignedPawnsForReading.Contains(pawn));
    }

    public override void TryAssignPawn(Pawn pawn)
    {
        if (parent.Spawned)
        {
            foreach (CompAssignableToPawn comp in DefensePointUtility.AllDefensePointAssignmentComps(parent.Map))
            {
                if (comp != this)
                {
                    comp.TryUnassignPawn(pawn);
                }
            }
        }

        base.TryAssignPawn(pawn);
    }

    protected override string GetAssignmentGizmoLabel()
    {
        Pawn? assignedPawn = AssignedPawnsForReading.FirstOrDefault();
        if (assignedPawn is not null)
        {
            return "ClashOfRim.DefensePoint.AssignMechGizmoAssigned".Translate(assignedPawn.LabelShort);
        }

        return "ClashOfRim.DefensePoint.AssignMechGizmo".Translate();
    }

    protected override string GetAssignmentGizmoDesc()
    {
        return "ClashOfRim.DefensePoint.AssignMechGizmoDesc".Translate();
    }
}
