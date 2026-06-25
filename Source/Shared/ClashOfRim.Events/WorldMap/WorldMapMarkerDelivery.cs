namespace AIRsLight.ClashOfRim.Events;

public sealed record WorldMapMarkerDelivery(
    string UserId,
    DateTimeOffset GeneratedAtUtc,
    IReadOnlyList<WorldMapMarker> Markers,
    bool GiftsEnabled = true,
    bool PvpEnabled = true);
