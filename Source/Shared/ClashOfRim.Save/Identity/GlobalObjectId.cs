namespace AIRsLight.ClashOfRim.Save;

public sealed record GlobalObjectId(
    string? OwnerId,
    string? ColonyId,
    string? SnapshotId,
    string? MapUniqueId,
    string LocalThingId)
{
    public static GlobalObjectId ForThing(SnapshotIdentity identity, string? mapUniqueId, string localThingId)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentException.ThrowIfNullOrWhiteSpace(localThingId);

        return new GlobalObjectId(
            identity.OwnerId,
            identity.ColonyId,
            identity.SnapshotId,
            mapUniqueId,
            localThingId);
    }

    public override string ToString()
    {
        string owner = Segment("owner", OwnerId);
        string colony = Segment("colony", ColonyId);
        string snapshot = Segment("snapshot", SnapshotId);
        string map = Segment("map", MapUniqueId);

        return $"{owner}/{colony}/{snapshot}/{map}/thing:{LocalThingId}";
    }

    private static string Segment(string name, string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? $"{name}:unknown" : $"{name}:{value}";
    }
}
