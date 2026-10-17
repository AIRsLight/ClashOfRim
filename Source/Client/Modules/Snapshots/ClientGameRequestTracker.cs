using System;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Synchronization;

namespace AIRsLight.ClashOfRim.Modules.Snapshots;

// A domain request owns its busy marker and optional mutation lease. Session identity
// and Game replacement invalidate work; advancing an ordinary snapshot does not.
internal sealed class ClientGameRequestTracker
{
    private readonly ClientSessionLifecycle session;
    private readonly IClientSnapshotGame game;
    private readonly Action reset;
    private Scope? owner;
    private Request? work;
    internal ClientGameRequestTracker(ClientSessionLifecycle session, IClientSnapshotGame game, Action reset)
    { this.session = session; this.game = game; this.reset = reset; }
    internal bool Busy => Read(() => work is not null);
    internal Scope Capture() => Read(() => owner!);
    internal void Reset() { using var p = ClientSessionWork.EnterPublication(); owner = null; Normalize(); }
    internal T Read<T>(Func<T> read)
    { using var p = ClientSessionWork.EnterPublication(); Normalize(); return read(); }

    private void Normalize()
    {
        if (owner is not null && Owns(owner)) return;
        work?.Admission?.Dispose();
        owner = new(session.CaptureOwnership(), session.ReadContext(), game.BindCurrentGame());
        work = null; reset();
    }
    private bool Owns(Scope candidate) => candidate.Game.IsCurrent && session.Owns(candidate.Owner, candidate.Context, false);
    internal bool TryApply(Scope candidate, Action action)
    {
        bool applied = false;
        ClientSessionWork.TryApply(() =>
        {
            Normalize();
            if (!ReferenceEquals(owner, candidate) || !Owns(candidate)) return;
            action(); applied = true;
        });
        return applied;
    }

    internal Request? Begin(ClientSynchronizationCoordinator.Lease? admission = null) =>
        Read(() => work is null ? work = new(owner!, session.ReadContext()) { Admission = admission } : null);
    internal void Track(Request request, ClientSynchronizationCoordinator.Lease admission) => TryApply(request, () => request.Admission = admission);
    internal bool TryApply(Request request, Action action)
    {
        bool applied = false;
        TryApply(request.Scope, () =>
        {
            if (!ReferenceEquals(work, request)) return;
            action(); applied = true;
        });
        return applied;
    }
    internal void End(Request request) => TryApply(request, () => { request.Admission?.Dispose(); work = null; });

    internal sealed record Scope(ClientSessionLifecycle.Ownership Owner, ClashOfRimClientNetworkContext Context, IClientSnapshotGameSession Game);
    internal sealed record Request(Scope Scope, ClashOfRimClientNetworkContext Context)
    { internal ClientSynchronizationCoordinator.Lease? Admission { get; set; } }
}
