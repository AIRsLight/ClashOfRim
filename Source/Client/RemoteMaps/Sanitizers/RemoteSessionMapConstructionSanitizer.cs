using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim.RemoteMaps;

public static class RemoteSessionMapConstructionSanitizer
{
    public static RemoteSessionMapConstructionSanitizerResult Apply(Map? map)
    {
        if (map is null)
        {
            return RemoteSessionMapConstructionSanitizerResult.Empty;
        }

        List<Thing> constructionPlaceholders = (map.listerThings?.AllThings ?? Enumerable.Empty<Thing>())
            .Where(IsConstructionPlaceholder)
            .ToList();
        int removedDesignations = RemoveDesignations(map);
        int removedPlaceholders = RemoveConstructionPlaceholders(constructionPlaceholders);
        int removedPlans = RemovePlans(map);

        if (removedPlaceholders > 0 || removedDesignations > 0 || removedPlans > 0)
        {
            ClashLog.Message("[ClashOfRim][RemoteSession] Removed remote map orders and construction placeholders: map="
                + map.GetUniqueLoadID()
                + ", placeholders="
                + removedPlaceholders
                + ", designations="
                + removedDesignations
                + ", plans="
                + removedPlans
                + ".");
        }

        return new RemoteSessionMapConstructionSanitizerResult(removedPlaceholders, removedDesignations, removedPlans);
    }

    private static int RemovePlans(Map map)
    {
        if (map.planManager is null) return 0;

        int removed = 0;
        foreach (Plan plan in map.planManager.AllPlans.ToList())
        {
            try
            {
                // Plans are separate from designations in 1.7. Native deletion also
                // clears the cell grid, render meshes and current selection.
                plan.Delete(playSound: false);
                removed++;
            }
            catch (Exception ex)
            {
                Log.Warning("[ClashOfRim][RemoteSession] Failed to remove remote map plan: " + ex);
            }
        }

        return removed;
    }

    private static int RemoveDesignations(Map map)
    {
        if (map.designationManager?.AllDesignations is null)
        {
            return 0;
        }

        int removed = 0;
        foreach (Designation designation in map.designationManager.AllDesignations.ToList())
        {
            try
            {
                // Source-colony orders never apply to the remote copy, including
                // modded orders and designations the UI does not allow cancelling.
                map.designationManager.RemoveDesignation(designation);
                removed++;
            }
            catch (Exception ex)
            {
                Log.Warning("[ClashOfRim][RemoteSession] Failed to remove remote map designation "
                    + DescribeDesignation(designation)
                    + ": "
                    + ex);
            }
        }

        return removed;
    }

    private static int RemoveConstructionPlaceholders(IEnumerable<Thing> constructionPlaceholders)
    {
        int removed = 0;
        foreach (Thing thing in constructionPlaceholders.ToList())
        {
            try
            {
                if (thing.Destroyed)
                {
                    continue;
                }

                thing.Destroy(DestroyMode.Vanish);
                removed++;
            }
            catch (Exception ex)
            {
                Log.Warning("[ClashOfRim][RemoteSession] Failed to remove remote map construction placeholder "
                    + DescribeThing(thing)
                    + ": "
                    + ex);
            }
        }

        return removed;
    }

    private static bool IsConstructionPlaceholder(Thing thing)
    {
        return thing is Blueprint or Frame
            || thing.def?.IsBlueprint == true
            || thing.def?.IsFrame == true;
    }

    private static string DescribeThing(Thing thing)
    {
        return (thing.def?.defName ?? thing.GetType().Name)
            + "/"
            + thing.ThingID;
    }

    private static string DescribeDesignation(Designation designation)
    {
        return (designation.def?.defName ?? designation.GetType().Name)
            + "/"
            + designation.target;
    }
}

public readonly struct RemoteSessionMapConstructionSanitizerResult
{
    public static readonly RemoteSessionMapConstructionSanitizerResult Empty = new(0, 0);

    public RemoteSessionMapConstructionSanitizerResult(int removedPlaceholders, int removedDesignations)
        : this(removedPlaceholders, removedDesignations, 0) { }

    public RemoteSessionMapConstructionSanitizerResult(int removedPlaceholders, int removedDesignations, int removedPlans)
    {
        RemovedPlaceholders = removedPlaceholders;
        RemovedDesignations = removedDesignations;
        RemovedPlans = removedPlans;
    }

    public int RemovedPlaceholders { get; }

    public int RemovedDesignations { get; }

    public int RemovedPlans { get; }
}
