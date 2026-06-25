using System;
using System.Collections.Generic;
using System.Linq;

namespace AIRsLight.ClashOfRim.Raids;

public sealed class RaidAttackerLossApplicationRequest
{
    public RaidAttackerLossApplicationRequest(
        string? sourceRaidEventId,
        string? attackerSnapshotId,
        string? currentSnapshotId,
        IEnumerable<string>? lostPawnGlobalKeys,
        IEnumerable<RaidLostThingReference>? lostThings,
        string? reason,
        RaidAttackerLossClientEffect clientEffect = RaidAttackerLossClientEffect.TriggerVanillaCaravanLostEvent)
    {
        SourceRaidEventId = sourceRaidEventId;
        AttackerSnapshotId = attackerSnapshotId;
        CurrentSnapshotId = currentSnapshotId;
        LostPawnGlobalKeys = (lostPawnGlobalKeys ?? Array.Empty<string>())
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        LostThings = (lostThings ?? Array.Empty<RaidLostThingReference>())
            .Where(thing => thing != null && !string.IsNullOrWhiteSpace(thing.GlobalKey))
            .ToList();
        Reason = reason;
        ClientEffect = clientEffect;
    }

    public string? SourceRaidEventId { get; }

    public string? AttackerSnapshotId { get; }

    public string? CurrentSnapshotId { get; }

    public IReadOnlyList<string> LostPawnGlobalKeys { get; }

    public IReadOnlyList<RaidLostThingReference> LostThings { get; }

    public string? Reason { get; }

    public RaidAttackerLossClientEffect ClientEffect { get; }
}
