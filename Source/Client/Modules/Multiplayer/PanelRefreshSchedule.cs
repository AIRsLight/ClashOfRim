namespace AIRsLight.ClashOfRim.Modules.Multiplayer;

internal sealed class PanelRefreshSchedule
{
    internal const float IntervalSeconds = 30f;
    private float next;
    internal void Request() => next = 0f;
    internal void Started(float now) => next = now + IntervalSeconds;
    internal bool TryStart(float now, bool busy, bool force = false)
    {
        if (busy || (!force && now < next)) return false;
        Started(now);
        return true;
    }
}
