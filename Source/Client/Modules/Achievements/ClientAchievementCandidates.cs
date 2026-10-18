using System;
using System.Collections.Generic;
using static AIRsLight.ClashOfRim.Modules.Snapshots.ClientSnapshotCandidates;
using AIRsLight.ClashOfRim.ClientNetwork;

namespace AIRsLight.ClashOfRim.Modules.Achievements;

// One game's pending awards. The save adapter owns serialization, not queue rules.
internal sealed class ClientAchievementCandidates
{
    private readonly Dictionary<(string Achievement, string Event), ModSnapshotAchievementCandidateDto> pending = new();
    internal int Count => pending.Count;
    internal IReadOnlyList<ModSnapshotAchievementCandidateDto> Enqueue(IEnumerable<ModSnapshotAchievementCandidateDto>? candidates)
    {
        var added = new List<ModSnapshotAchievementCandidateDto>();
        foreach (var candidate in DistinctCopy(candidates))
            if (pending.TryAdd(Key(candidate)!.Value, candidate)) added.Add(Copy(candidate));
        return added;
    }
    internal IReadOnlyList<ModSnapshotAchievementCandidateDto> Snapshot() => DistinctCopy(pending.Values);
    internal void Restore(IEnumerable<ModSnapshotAchievementCandidateDto>? candidates)
    { pending.Clear(); Enqueue(candidates); }
    internal void Acknowledge(IEnumerable<ModSnapshotAchievementCandidateDto>? candidates)
    {
        foreach (var candidate in candidates ?? Array.Empty<ModSnapshotAchievementCandidateDto>())
            if (Key(candidate) is { } key) pending.Remove(key);
    }
}
