namespace AIRsLight.ClashOfRim.Save;

public sealed record RaidHiddenTrapList(
    string RaidEventId,
    SnapshotIdentity TargetSnapshot,
    string TargetMapUniqueId,
    string TargetClientMapLoadId,
    IReadOnlyList<RaidHiddenThingReference> HiddenThings)
{
    public IReadOnlyList<string> ClientHiddenThingKeys =>
        HiddenThings
            .SelectMany(thing => new[] { thing.LocalThingId, thing.ClientUniqueLoadId })
            .Distinct(StringComparer.Ordinal)
            .ToList();
}
