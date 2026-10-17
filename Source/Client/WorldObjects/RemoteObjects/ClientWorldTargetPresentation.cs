using AIRsLight.ClashOfRim.ClientNetwork;
using Verse;

namespace AIRsLight.ClashOfRim.WorldObjects;

internal static class ClientWorldTargetPresentation
{
    internal static string Label(ModWorldMapMarkerDto marker) => ClashOfRimText.Key("ClashOfRim.Support.TargetLabel",
        (string.IsNullOrWhiteSpace(marker.Label) ? $"{marker.OwnerUserId}/{marker.OwnerColonyId}" : marker.Label!).Named("LABEL"),
        marker.OwnerUserId.Named("USER"), (marker.OwnerColonyId ?? "").Named("COLONY"), marker.Tile.Named("TILE"));
}
