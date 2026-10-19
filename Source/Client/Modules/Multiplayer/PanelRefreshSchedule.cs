namespace AIRsLight.ClashOfRim.Modules.Multiplayer;

internal sealed class PanelRefreshSchedule
{
    internal const float IntervalSeconds = 30f;
    private readonly float interval;
    private float next;
    internal PanelRefreshSchedule(float intervalSeconds = IntervalSeconds) => interval = intervalSeconds;
    internal void Request() => next = 0f;
    internal void Started(float now) => next = now + interval;
    internal bool TryStart(float now, bool busy, bool force = false)
    {
        if (busy || (!force && now < next)) return false;
        Started(now);
        return true;
    }
}
