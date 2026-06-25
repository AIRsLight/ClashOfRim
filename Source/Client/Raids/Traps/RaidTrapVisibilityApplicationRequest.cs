using System;
using System.Collections.Generic;

namespace AIRsLight.ClashOfRim.Raids;

public sealed class RaidTrapVisibilityApplicationRequest
{
    public RaidTrapVisibilityApplicationRequest(
        string? raidEventId,
        string? targetSnapshotId,
        string? targetMapLoadId,
        IEnumerable<string>? hiddenThingKeys)
    {
        RaidEventId = raidEventId;
        TargetSnapshotId = targetSnapshotId;
        TargetMapLoadId = targetMapLoadId;
        HiddenThingKeys = hiddenThingKeys == null
            ? Array.Empty<string>()
            : new List<string>(hiddenThingKeys);
    }

    public string? RaidEventId { get; }

    public string? TargetSnapshotId { get; }

    public string? TargetMapLoadId { get; }

    public IReadOnlyList<string> HiddenThingKeys { get; }
}
