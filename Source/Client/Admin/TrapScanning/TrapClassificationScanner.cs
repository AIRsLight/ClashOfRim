using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim.Admin;

public static class TrapClassificationScanner
{
    private static readonly string[] CandidateMarkers =
    {
        "trap",
        "mine",
        "ied",
        "spring"
    };

    public static IReadOnlyList<TrapClassificationScanEntry> ScanLoadedThingDefs()
    {
        return DefDatabase<ThingDef>.AllDefsListForReading
            .Select(Scan)
            .Where(entry => entry != null)
            .Cast<TrapClassificationScanEntry>()
            .OrderBy(entry => entry.ModPackageId, StringComparer.Ordinal)
            .ThenBy(entry => entry.DefName, StringComparer.Ordinal)
            .ToList();
    }

    public static TrapClassificationScanEntry? Scan(ThingDef def)
    {
        if (def == null)
        {
            return null;
        }

        Type? thingClass = def.thingClass;
        string? thingClassName = thingClass?.FullName;
        if (thingClass != null && typeof(Building_Trap).IsAssignableFrom(thingClass))
        {
            return Entry(def, thingClassName, TrapClassificationScanStatus.ApprovedByInheritance, "inherits:RimWorld.Building_Trap");
        }

        if (LooksLikeTrapCandidate(def, thingClassName))
        {
            return Entry(def, thingClassName, TrapClassificationScanStatus.CandidateRequiresApproval, "candidate:name-or-class-marker");
        }

        return null;
    }

    private static TrapClassificationScanEntry Entry(
        ThingDef def,
        string? thingClassName,
        TrapClassificationScanStatus status,
        string reason)
    {
        ModContentPack? mod = def.modContentPack;
        return new TrapClassificationScanEntry(
            def.defName,
            thingClassName,
            mod?.PackageId,
            mod?.Name,
            status,
            reason);
    }

    private static bool LooksLikeTrapCandidate(ThingDef def, string? thingClassName)
    {
        return ContainsMarker(def.defName) ||
            ContainsMarker(def.label) ||
            ContainsMarker(thingClassName);
    }

    private static bool ContainsMarker(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        for (int i = 0; i < CandidateMarkers.Length; i++)
        {
            if (value!.IndexOf(CandidateMarkers[i], StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
        }

        return false;
    }
}
