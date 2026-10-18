using System;
using System.Collections.Generic;
using System.Linq;
using AIRsLight.ClashOfRim.ClientNetwork;

namespace AIRsLight.ClashOfRim.Modules.Snapshots;

// Shared snapshot candidate identity and copying; no queue or workflow state.
internal static class ClientSnapshotCandidates
{
    internal static (string Achievement, string Event)? Key(ModSnapshotAchievementCandidateDto? value)
        => value is null || string.IsNullOrWhiteSpace(value.AchievementId) || string.IsNullOrWhiteSpace(value.EventKey)
            ? null : (value.AchievementId.Trim(), value.EventKey.Trim());
    internal static IReadOnlyList<ModSnapshotAchievementCandidateDto> DistinctCopy(IEnumerable<ModSnapshotAchievementCandidateDto>? candidates)
    {
        var result = new List<ModSnapshotAchievementCandidateDto>();
        var seen = new HashSet<(string Achievement, string Event)>();
        foreach (var candidate in candidates ?? Enumerable.Empty<ModSnapshotAchievementCandidateDto>())
            if (Key(candidate) is { } key && seen.Add(key)) result.Add(Copy(candidate));
        return result;
    }
    internal static ModSnapshotAchievementCandidateDto Copy(ModSnapshotAchievementCandidateDto value) => new()
    {
        AchievementId = value.AchievementId, EventKey = value.EventKey, Value = value.Value,
        Category = value.Category, LabelKey = value.LabelKey, IconId = value.IconId, Color = value.Color,
        AggregationKind = value.AggregationKind, MetadataJson = value.MetadataJson
    };
}
