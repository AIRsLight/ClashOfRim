namespace AIRsLight.ClashOfRim.Raids;

public sealed class RaidLostThingReference
{
    public RaidLostThingReference(string globalKey, string? defName = null, int stackCount = 1)
    {
        GlobalKey = globalKey;
        DefName = defName;
        StackCount = stackCount;
    }

    public string GlobalKey { get; }

    public string? DefName { get; }

    public int StackCount { get; }
}
