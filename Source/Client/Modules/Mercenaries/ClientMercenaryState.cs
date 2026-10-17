using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Snapshots;

namespace AIRsLight.ClashOfRim.Modules.Mercenaries;

internal sealed class ClientMercenaryState
{
    internal readonly ClientGameRequestTracker Requests;
    private string message = "";
    internal ClientMercenaryState(ClientSessionLifecycle session, IClientSnapshotGame game, IClientMercenaryHiringPresentation presentation)
    { Requests = new(session, game, () => message = presentation.Idle); }
    internal bool Busy => Requests.Busy;
    internal string Message { get => Requests.Read(() => message); set => Requests.Read(() => message = value); }
    internal void Reset() => Requests.Reset();
}
