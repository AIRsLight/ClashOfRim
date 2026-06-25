using System;
using System.Collections.Generic;
using System.Linq;

namespace AIRsLight.ClashOfRim.Raids;

public sealed class RaidTrapVisibilityDelivery
{
    public RaidTrapVisibilityDelivery(
        string raidEventId,
        string? targetSnapshotId,
        string targetClientMapLoadId,
        IEnumerable<string> hiddenThingKeys)
    {
        if (string.IsNullOrWhiteSpace(raidEventId))
        {
            throw new ArgumentException("Raid event id is required.", nameof(raidEventId));
        }

        if (string.IsNullOrWhiteSpace(targetClientMapLoadId))
        {
            throw new ArgumentException("Target map load id is required.", nameof(targetClientMapLoadId));
        }

        RaidEventId = raidEventId;
        TargetSnapshotId = targetSnapshotId;
        TargetClientMapLoadId = targetClientMapLoadId;
        HiddenThingKeys = (hiddenThingKeys ?? Array.Empty<string>())
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    public string RaidEventId { get; }

    public string? TargetSnapshotId { get; }

    public string TargetClientMapLoadId { get; }

    public IReadOnlyList<string> HiddenThingKeys { get; }

    public RaidTrapVisibilityApplicationRequest ToApplicationRequest()
    {
        return new RaidTrapVisibilityApplicationRequest(
            RaidEventId,
            TargetSnapshotId,
            TargetClientMapLoadId,
            HiddenThingKeys);
    }
}
