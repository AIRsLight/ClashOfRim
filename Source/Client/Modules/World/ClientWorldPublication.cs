using System;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Transport;

namespace AIRsLight.ClashOfRim.Modules.World;

internal enum ClientWorldProjection { Sites, Catalog, Storyteller, Full }

// HTTP sources capture once before sending. This module owns their common local
// publication order; an accepted server write may finish even if its view is old.
internal sealed class ClientWorldPublication
{
    private readonly IClientWorldIdentity identity;
    private readonly ClientWorldConfiguration configuration;
    private readonly ClientOccupiedColonySites sites;
    private readonly ClientColonyAppearance appearance;
    private readonly ClientWorldMarkers markers;
    private long accepted;

    internal ClientWorldPublication(IClientWorldIdentity identity, ClientWorldConfiguration configuration,
        ClientOccupiedColonySites sites, ClientColonyAppearance appearance, ClientWorldMarkers markers)
    { this.identity = identity; this.configuration = configuration; this.sites = sites; this.appearance = appearance; this.markers = markers; }

    internal sealed record Observation(ClientWorldMarkers.Observation Markers, ClientColonyAppearance.Observation Appearance);
    internal Observation Capture()
    {
        using var p = ClientSessionWork.EnterPublication();
        return new(markers.CaptureObservation(), appearance.CaptureObservation());
    }

    internal bool Publish(Observation observation, ModWorldConfigurationDto? source, ClientWorldProjection projection,
        Func<bool> current, (string Source, ModWorldMapMarkerDeliveryDto? Delivery)? markerUpdate = null)
    {
        using var p = ClientSessionWork.EnterPublication();
        if (!current() || !markers.Owns(observation.Markers)) return false;
        long number = observation.Markers.Number;
        if (number <= accepted) return true; // Superseded local view, not a failed server operation.
        accepted = number;
        var world = source is null ? null : ClientProtocolJson.Copy(source);
        var update = markerUpdate is { } value ? (value.Source, Delivery: value.Delivery is null ? null : ClientProtocolJson.Copy(value.Delivery)) : markerUpdate;
        var original = observation.Markers;
        string expected = string.IsNullOrWhiteSpace(world?.WorldConfigurationId) ? original.Scope.Boundary : world!.WorldConfigurationId.Trim();
        bool changedWorld = expected != original.Scope.Boundary;
        bool Current() => current() && accepted == number && markers.OwnsSession(original)
            && (identity.WorldId == original.Scope.Boundary || identity.WorldId == expected);
        // The only relaxed world check surrounds this deliberate Remember/Apply.
        // Other identity changes and newer reentrant publications remain invalid.
        if (!Current()) return false;
        if (world is not null)
        {
            if (projection == ClientWorldProjection.Sites) configuration.Remember(world);
            else if (!(projection == ClientWorldProjection.Full ? configuration.Apply(world, current: Current)
                : configuration.ApplyCatalog(world, applyStoryteller: projection == ClientWorldProjection.Storyteller, current: Current))) return false;
        }
        if (!Current() || identity.WorldId != expected) return false;
        var marker = changedWorld ? markers.AdoptWorld(original, expected) : original;
        var preference = changedWorld ? appearance.AdoptWorld(observation.Appearance, expected) : observation.Appearance;
        if (marker is null) return false;
        bool Bound() => Current() && markers.Owns(marker);
        // Marker deltas also update the occupied-site cache. Do not replace that
        // newer view with an older full catalog; configuration has its own stream.
        if (!markers.HasNewerObservation(marker))
            sites.Replace(world, selected => { if (preference is not null) appearance.ObserveServer(preference, selected, Bound); }, Bound, remember: false);
        if (!Bound()) return false;
        if (update is { } acceptedMarkers)
            markers.Observe(marker, acceptedMarkers.Delivery, reason: acceptedMarkers.Source, current: Bound);
        return Bound();
    }
}
