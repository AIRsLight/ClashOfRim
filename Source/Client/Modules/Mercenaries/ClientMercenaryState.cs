using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Snapshots;

namespace AIRsLight.ClashOfRim.Modules.Mercenaries;

internal sealed class ClientMercenaryState
{
    internal readonly ClientGameRequestTracker Requests;
    private readonly IClientMercenaryHiringPresentation presentation;
    private string? message;
    internal ClientMercenaryState(ClientSessionLifecycle session, IClientSnapshotGame game, IClientMercenaryHiringPresentation presentation)
    { this.presentation = presentation; Requests = new(session, game, () => message = null); }
    internal bool Busy => Requests.Busy;
    internal string Message { get => Requests.Read(() => message ?? presentation.Idle); set => Requests.Read(() => message = value); }
    internal void Reset() => Requests.Reset();
}
