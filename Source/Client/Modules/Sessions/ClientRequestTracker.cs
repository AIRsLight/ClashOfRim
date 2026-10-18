using AIRsLight.ClashOfRim.Modules.Synchronization;
using System;
using AIRsLight.ClashOfRim.ClientNetwork;

namespace AIRsLight.ClashOfRim.Modules.Sessions;

// Requests share session ownership and exact completion/replacement semantics.
// Domains may bind an additional resource; ordinary snapshot advancement is allowed.
internal class ClientRequestTracker<TBoundary>
{
    private readonly ClientSessionLifecycle session;
    private readonly Func<TBoundary> bindBoundary;
    private readonly Func<TBoundary, bool> ownsBoundary;
    private readonly Action reset;
    private Scope? owner;
    private Request? work;
    internal ClientRequestTracker(ClientSessionLifecycle session, Func<TBoundary> bindBoundary, Func<TBoundary, bool> ownsBoundary, Action reset)
    { this.session = session; this.bindBoundary = bindBoundary; this.ownsBoundary = ownsBoundary; this.reset = reset; }
    internal bool Busy => Read(() => work is not null);
    internal Scope Capture() => Read(() => owner!);
    internal void Reset() { using var p = ClientSessionWork.EnterPublication(); owner = null; Normalize(); }
    internal T Read<T>(Func<T> read)
    { using var p = ClientSessionWork.EnterPublication(); Normalize(); return read(); }

    private void Normalize()
    {
        if (owner is not null && Owns(owner)) return;
        work?.Admission?.Dispose();
        owner = new(session.CaptureOwnership(), session.ReadContext(), bindBoundary());
        work = null; reset();
    }
    private bool Owns(Scope candidate) => ownsBoundary(candidate.Boundary) && session.Owns(candidate.Owner, candidate.Context, false);
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

    internal Request? Begin(ClientSynchronizationCoordinator.Lease? admission = null, bool replace = false) => Read(() =>
    {
        if (work is not null && !replace) return null;
        // Read-only refreshes may supersede an older request in this same scope.
        // Its completion cannot release the replacement's admission or publish data.
        work?.Admission?.Dispose();
        return work = new(owner!, session.ReadContext()) { Admission = admission };
    });
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

    internal sealed record Scope(ClientSessionLifecycle.Ownership Owner, ClashOfRimClientNetworkContext Context, TBoundary Boundary);
    internal sealed record Request(Scope Scope, ClashOfRimClientNetworkContext Context)
    { internal ClientSynchronizationCoordinator.Lease? Admission { get; set; } }
}

// Account/session work is independent of the loaded Game.
internal sealed class ClientRequestTracker : ClientRequestTracker<object?>
{
    internal ClientRequestTracker(ClientSessionLifecycle session, Action reset)
        : base(session, static () => null, static _ => true, reset) { }
}
