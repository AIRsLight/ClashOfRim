namespace AIRsLight.ClashOfRim.Save;

public sealed record RaidHiddenTrapDelivery(
    string RaidEventId,
    string? TargetSnapshotId,
    string TargetClientMapLoadId,
    IReadOnlyList<string> HiddenThingKeys)
{
    public static RaidHiddenTrapDelivery FromHiddenTrapList(RaidHiddenTrapList hiddenTrapList)
    {
        ArgumentNullException.ThrowIfNull(hiddenTrapList);

        return new RaidHiddenTrapDelivery(
            hiddenTrapList.RaidEventId,
            hiddenTrapList.TargetSnapshot.SnapshotId,
            hiddenTrapList.TargetClientMapLoadId,
            hiddenTrapList.ClientHiddenThingKeys);
    }
}
