namespace AIRsLight.ClashOfRim.Save;

public static class RaidHiddenTrapListBuilder
{
    public static RaidHiddenTrapList Build(
        string raidEventId,
        SnapshotIdentity targetSnapshot,
        SaveSnapshotIndex snapshot,
        string targetMapUniqueId,
        ThingDefTrapClassificationManifest manifest)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(raidEventId);
        ArgumentNullException.ThrowIfNull(targetSnapshot);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetMapUniqueId);
        ArgumentNullException.ThrowIfNull(manifest);

        RaidTrapVisibilityState state = RaidTrapVisibilityState.FromSnapshot(
            snapshot,
            manifest,
            targetMapUniqueId);

        IReadOnlyList<RaidHiddenThingReference> hiddenThings = state.HiddenTraps
            .Select(trap => new RaidHiddenThingReference(
                trap.Thing.GlobalKey,
                trap.Thing.LocalId,
                ClientThingLoadId(trap.Thing.LocalId),
                trap.Thing.Def,
                trap.Thing.Position))
            .ToList();

        return new RaidHiddenTrapList(
            raidEventId,
            targetSnapshot,
            targetMapUniqueId,
            ClientMapLoadId(targetMapUniqueId),
            hiddenThings);
    }

    public static string ClientMapLoadId(string mapUniqueId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mapUniqueId);
        return mapUniqueId.StartsWith("Map_", StringComparison.Ordinal)
            ? mapUniqueId
            : "Map_" + mapUniqueId;
    }

    public static string ClientThingLoadId(string localThingId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localThingId);
        return localThingId.StartsWith("Thing_", StringComparison.Ordinal)
            ? localThingId
            : "Thing_" + localThingId;
    }
}
