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
        if (map?.listerThings?.AllThings is null)
        {
            return RemoteSessionMapConstructionSanitizerResult.Empty;
        }

        List<Thing> constructionPlaceholders = map.listerThings.AllThings
            .Where(IsConstructionPlaceholder)
            .ToList();
        int removedDesignations = RemoveRelatedDesignations(map, constructionPlaceholders);
        int removedPlaceholders = RemoveConstructionPlaceholders(constructionPlaceholders);

        if (removedPlaceholders > 0 || removedDesignations > 0)
        {
            ClashLog.Message("[ClashOfRim][RemoteSession] Removed remote map construction placeholders: map="
                + map.GetUniqueLoadID()
                + ", placeholders="
                + removedPlaceholders
                + ", designations="
                + removedDesignations
                + ".");
        }

        return new RemoteSessionMapConstructionSanitizerResult(removedPlaceholders, removedDesignations);
    }

    private static int RemoveRelatedDesignations(Map map, IReadOnlyCollection<Thing> constructionPlaceholders)
    {
        if (map.designationManager?.AllDesignations is null)
        {
            return 0;
        }

        var targets = new HashSet<Thing>(constructionPlaceholders);
        int removed = 0;
        foreach (Designation designation in map.designationManager.AllDesignations.ToList())
        {
            if (!ShouldRemoveDesignation(designation, targets))
            {
                continue;
            }

            try
            {
                map.designationManager.RemoveDesignation(designation);
                removed++;
            }
            catch (Exception ex)
            {
                Log.Warning("[ClashOfRim][RemoteSession] Failed to remove remote map construction designation "
                    + DescribeDesignation(designation)
                    + ": "
                    + ex);
            }
        }

        return removed;
    }

    private static bool ShouldRemoveDesignation(Designation designation, HashSet<Thing> constructionPlaceholders)
    {
        if (designation is null)
        {
            return false;
        }

        if (designation.target.HasThing && designation.target.Thing is Thing thing)
        {
            return constructionPlaceholders.Contains(thing)
                || IsConstructionPlaceholder(thing)
                || IsConstructionDesignation(designation.def);
        }

        return IsConstructionDesignation(designation.def);
    }

    private static bool IsConstructionDesignation(DesignationDef? def)
    {
        string? defName = def?.defName;
        return string.Equals(defName, "Build", StringComparison.Ordinal)
            || string.Equals(defName, "Repair", StringComparison.Ordinal)
            || string.Equals(defName, "Deconstruct", StringComparison.Ordinal)
            || string.Equals(defName, "Uninstall", StringComparison.Ordinal);
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
    {
        RemovedPlaceholders = removedPlaceholders;
        RemovedDesignations = removedDesignations;
    }

    public int RemovedPlaceholders { get; }

    public int RemovedDesignations { get; }
}
