namespace AIRsLight.ClashOfRim.Save;

public sealed record ObjectIdentityMapping(
    GlobalObjectId GlobalId,
    LocalThingReference LocalReference,
    ObjectTracePurpose Purpose)
{
    public string GlobalKey => GlobalId.ToString();
}
