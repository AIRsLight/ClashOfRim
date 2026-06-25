namespace AIRsLight.ClashOfRim.Save;

public sealed record RaidHiddenThingReference(
    string GlobalKey,
    string LocalThingId,
    string ClientUniqueLoadId,
    string? DefName,
    string? Position);
