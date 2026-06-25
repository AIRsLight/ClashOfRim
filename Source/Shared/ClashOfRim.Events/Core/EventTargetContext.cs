namespace AIRsLight.ClashOfRim.Events;

public sealed record EventTargetContext(
    string? WorldObjectId,
    string? MapUniqueId,
    int? Tile,
    EventLandingMode LandingMode);
