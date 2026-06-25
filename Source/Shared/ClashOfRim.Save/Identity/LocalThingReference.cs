namespace AIRsLight.ClashOfRim.Save;

public sealed record LocalThingReference(
    string LocalThingId,
    string? MapUniqueId,
    string? Def,
    string? Position,
    string? Faction,
    bool IsPawn);
