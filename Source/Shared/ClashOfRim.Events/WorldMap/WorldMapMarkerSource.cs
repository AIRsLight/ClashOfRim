using AIRsLight.ClashOfRim.Save;

namespace AIRsLight.ClashOfRim.Events;

public sealed record WorldMapMarkerSource(
    SnapshotIdentity SnapshotIdentity,
    IReadOnlyList<WorldObjectSummary> WorldObjects,
    IReadOnlyList<MapSummary> Maps);
