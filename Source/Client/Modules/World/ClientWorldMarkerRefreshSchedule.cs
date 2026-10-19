namespace AIRsLight.ClashOfRim.Modules.World;

internal enum ClientWorldMarkerRefresh { Entered, Stayed }

// Both setup and the in-game world view refresh immediately on entry, then
// every 30 seconds. Each view owns its own schedule.
internal sealed class ClientWorldMarkerRefreshSchedule
{
    private bool visible;
    private float next;
    internal void Reset() { visible = false; next = 0; }
    internal ClientWorldMarkerRefresh? Advance(bool selected, float now)
    {
        if (!selected) { visible = false; return null; }
        bool entered = !visible;
        if (!entered && now < next) return null;
        visible = true; next = now + 30f;
        return entered ? ClientWorldMarkerRefresh.Entered : ClientWorldMarkerRefresh.Stayed;
    }
}
