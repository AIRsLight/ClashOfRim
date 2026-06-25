using System;
using System.Collections.Generic;
using System.Linq;
using Verse;

namespace AIRsLight.ClashOfRim.Raids;

public static class RaidTrapVisibilityController
{
    private static readonly HashSet<string> HiddenThingKeys = new(StringComparer.Ordinal);
    private static readonly HashSet<string> RevealedThingKeys = new(StringComparer.Ordinal);

    public static bool Active { get; private set; }

    public static string? CurrentRaidEventId { get; private set; }

    public static string? CurrentTargetSnapshotId { get; private set; }

    public static string? CurrentTargetMapLoadId { get; private set; }

    public static int HiddenCount => HiddenThingKeys.Count;

    public static int RevealedCount => RevealedThingKeys.Count;

    public static RaidTrapVisibilityApplyResult ApplyHiddenTrapSession(
        RaidTrapVisibilityApplicationRequest? request,
        Map? currentMap)
    {
        if (request == null)
        {
            EndHiddenTrapSession();
            return RaidTrapVisibilityApplyResult.MissingRequest;
        }

        if (currentMap == null)
        {
            EndHiddenTrapSession();
            return RaidTrapVisibilityApplyResult.MissingMap;
        }

        string currentMapLoadId = currentMap.GetUniqueLoadID();
        if (!string.IsNullOrWhiteSpace(request.TargetMapLoadId) &&
            !string.Equals(request.TargetMapLoadId, currentMapLoadId, StringComparison.Ordinal))
        {
            EndHiddenTrapSession(currentMap);
            return RaidTrapVisibilityApplyResult.TargetMapMismatch;
        }

        CurrentRaidEventId = request.RaidEventId;
        CurrentTargetSnapshotId = request.TargetSnapshotId;
        CurrentTargetMapLoadId = currentMapLoadId;
        BeginHiddenTrapSession(request.HiddenThingKeys);
        return Active
            ? RaidTrapVisibilityApplyResult.Applied
            : RaidTrapVisibilityApplyResult.NoHiddenThings;
    }

    public static RaidTrapVisibilityApplyResult ApplyHiddenThingSession(
        string eventId,
        string snapshotId,
        Map? currentMap,
        IEnumerable<Thing> hiddenThings)
    {
        if (currentMap == null)
        {
            EndHiddenTrapSession();
            return RaidTrapVisibilityApplyResult.MissingMap;
        }

        CurrentRaidEventId = eventId;
        CurrentTargetSnapshotId = snapshotId;
        CurrentTargetMapLoadId = currentMap.GetUniqueLoadID();

        HiddenThingKeys.Clear();
        RevealedThingKeys.Clear();
        foreach (Thing thing in hiddenThings)
        {
            AddKnownKeys(thing, HiddenThingKeys);
        }

        Active = HiddenThingKeys.Count > 0;
        return Active
            ? RaidTrapVisibilityApplyResult.Applied
            : RaidTrapVisibilityApplyResult.NoHiddenThings;
    }

    public static void BeginHiddenTrapSession(IEnumerable<string> hiddenThingKeys)
    {
        HiddenThingKeys.Clear();
        RevealedThingKeys.Clear();

        foreach (string key in hiddenThingKeys)
        {
            if (!string.IsNullOrWhiteSpace(key))
            {
                HiddenThingKeys.Add(key);
            }
        }

        Active = HiddenThingKeys.Count > 0;
    }

    public static void EndHiddenTrapSession()
    {
        Active = false;
        CurrentRaidEventId = null;
        CurrentTargetSnapshotId = null;
        CurrentTargetMapLoadId = null;
        HiddenThingKeys.Clear();
        RevealedThingKeys.Clear();
    }

    public static void EndHiddenTrapSession(Map? map)
    {
        EndHiddenTrapSession();
    }

    public static void RegisterHiddenThing(Thing thing)
    {
        if (thing == null)
        {
            return;
        }

        AddKnownKeys(thing, HiddenThingKeys);
        Active = HiddenThingKeys.Count > 0;
    }

    public static bool ShouldHide(Thing thing, RaidTrapVisibilitySurface surface)
    {
        if (!Active || thing == null)
        {
            return false;
        }

        string? matchedKey = FindKnownKey(thing, HiddenThingKeys);
        return matchedKey != null
            && IsThingOnCurrentTargetMap(thing)
            && !RevealedThingKeys.Contains(matchedKey);
    }

    public static bool IsVirtuallyFogged(Thing thing, RaidTrapVisibilitySurface surface)
    {
        return ShouldHide(thing, surface);
    }

    public static bool ShouldUseTransparentGraphic(Thing thing)
    {
        return false;
    }

    public static bool Reveal(Thing thing, RaidTrapRevealReason reason)
    {
        if (!Active || thing == null)
        {
            return false;
        }

        string? matchedKey = FindKnownKey(thing, HiddenThingKeys);
        if (matchedKey == null || RevealedThingKeys.Contains(matchedKey))
        {
            return false;
        }

        RevealedThingKeys.Add(matchedKey);
        return true;
    }

    private static void AddKnownKeys(Thing thing, HashSet<string> keys)
    {
        AddEquivalentKeys(keys, thing.ThingID);
        AddEquivalentKeys(keys, thing.GetUniqueLoadID());
    }

    private static string? FindKnownKey(Thing thing, HashSet<string> keys)
    {
        string? matched = FindEquivalentKey(thing.ThingID, keys);
        if (matched is not null)
        {
            return matched;
        }

        return FindEquivalentKey(thing.GetUniqueLoadID(), keys);
    }

    private static void AddEquivalentKeys(HashSet<string> keys, string? key)
    {
        foreach (string equivalent in EquivalentThingKeys(key))
        {
            keys.Add(equivalent);
        }
    }

    private static string? FindEquivalentKey(string? key, HashSet<string> keys)
    {
        return EquivalentThingKeys(key).FirstOrDefault(keys.Contains);
    }

    private static IEnumerable<string> EquivalentThingKeys(string? key)
    {
        string trimmed = key?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            yield break;
        }

        yield return trimmed;

        if (trimmed.StartsWith("Thing_", StringComparison.Ordinal))
        {
            string local = trimmed.Substring("Thing_".Length);
            if (!string.IsNullOrWhiteSpace(local))
            {
                yield return local;
            }
        }
        else
        {
            yield return "Thing_" + trimmed;
        }
    }

    private static bool IsThingOnCurrentTargetMap(Thing thing)
    {
        return string.IsNullOrWhiteSpace(CurrentTargetMapLoadId) ||
            (thing.Spawned && string.Equals(thing.Map.GetUniqueLoadID(), CurrentTargetMapLoadId, StringComparison.Ordinal));
    }
}
