namespace AIRsLight.ClashOfRim.Save;

public sealed record SnapshotIdentity(
    string? OwnerId = null,
    string? ColonyId = null,
    string? SnapshotId = null)
{
    public string ThingKey(string? mapUniqueId, string localThingId)
    {
        return GlobalObjectId.ForThing(this, mapUniqueId, localThingId).ToString();
    }
}
