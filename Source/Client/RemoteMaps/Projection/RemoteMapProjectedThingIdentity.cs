namespace AIRsLight.ClashOfRim.RemoteMaps;

public sealed class RemoteMapProjectedThingIdentity
{
    public RemoteMapProjectedThingIdentity(string projectedThingId, string originalThingId)
    {
        ProjectedThingId = projectedThingId;
        OriginalThingId = originalThingId;
    }

    public string ProjectedThingId { get; }

    public string OriginalThingId { get; }
}
