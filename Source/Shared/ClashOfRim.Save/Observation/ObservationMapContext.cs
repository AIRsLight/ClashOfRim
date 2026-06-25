namespace AIRsLight.ClashOfRim.Save;

public sealed record ObservationMapContext(
    string MapUniqueId,
    string? WorldObjectId = null,
    string? Tile = null);
