namespace AIRsLight.ClashOfRim.Events;

public sealed record CrossMapPawnReference(
    string GlobalId,
    string? SourceSnapshotId,
    string? Name,
    bool? Dead,
    string? Faction,
    Dictionary<string, string?>? Metadata = null);
