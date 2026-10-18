using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Transport;

namespace AIRsLight.ClashOfRim.Modules.World;

// Login can replace authentication before the caller finishes a snapshot upload.
// Preserve the original observation order through that explicit transition and
// retain the received data independently of callbacks and later transport reuse.
internal sealed class ClientWorldLoginPublication
{
    private readonly ClientSessionLifecycle session;
    private readonly ClientWorldMarkers markers;
    internal ClientWorldLoginPublication(ClientSessionLifecycle session, ClientWorldMarkers markers)
    { this.session = session; this.markers = markers; }
    internal ClientWorldMarkers.Observation Capture() => markers.CaptureObservation();
    internal bool IsCurrent(ClientWorldMarkers.Observation source) => markers.OwnsLoginSource(source);
    internal bool IsCurrent(Accepted accepted) => session.Owns(accepted.Login) && markers.Owns(accepted.Observation);
    internal Accepted? Accept(ClientWorldMarkers.Observation source, ModLoginResponseDto response)
    {
        using var p = ClientSessionWork.EnterPublication();
        if (!IsCurrent(source) || response.Result?.Accepted == false) return null;
        var frozen = ClientProtocolJson.Copy(response);
        var login = session.PublishLogin(source.Scope.Owner, source.Scope.Context, frozen);
        return login is null ? null : Bind(source, login, frozen.WorldMapMarkers);
    }
    internal Accepted? Adopt(ClientWorldMarkers.Observation source, ClientSessionLifecycle.Login login, ModLoginResponseDto response)
    {
        using var p = ClientSessionWork.EnterPublication();
        return Bind(source, login, response.WorldMapMarkers is null ? null : ClientProtocolJson.Copy(response.WorldMapMarkers));
    }
    private Accepted? Bind(ClientWorldMarkers.Observation source, ClientSessionLifecycle.Login login, ModWorldMapMarkerDeliveryDto? delivery)
    {
        var observation = markers.AdoptLogin(source, login);
        return observation is null ? null : new(login, observation, delivery);
    }
    internal void Publish(Accepted accepted, string reason)
    {
        using var p = ClientSessionWork.EnterPublication();
        if (IsCurrent(accepted)) accepted.PublishTo(markers, reason);
    }
    internal sealed class Accepted
    {
        internal readonly ClientSessionLifecycle.Login Login;
        internal readonly ClientWorldMarkers.Observation Observation;
        private readonly ModWorldMapMarkerDeliveryDto? delivery;
        internal void PublishTo(ClientWorldMarkers target, string reason) => target.Observe(Observation, delivery, reason: reason);
        internal Accepted(ClientSessionLifecycle.Login login, ClientWorldMarkers.Observation observation, ModWorldMapMarkerDeliveryDto? delivery)
        { Login = login; Observation = observation; this.delivery = delivery; }
    }
}
