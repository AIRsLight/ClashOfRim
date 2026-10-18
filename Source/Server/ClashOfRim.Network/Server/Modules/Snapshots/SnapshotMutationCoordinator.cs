using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Network.Modules.Snapshots;

internal sealed class SnapshotMutationCoordinator(object gate, Lazy<ISnapshotRecovery> recovery)
{
    internal SnapshotMutationCoordinator(object gate, ISnapshotRecovery recovery)
        : this(gate, new Lazy<ISnapshotRecovery>(() => recovery)) { }

    internal bool IsEntered => Monitor.IsEntered(gate);
    internal SnapshotMutationScope EnterUnrecovered([CallerMemberName] string operation = "") => new(gate, operation);

    internal void Recover([CallerMemberName] string operation = "")
    {
        using var mutation = Enter(operation: operation);
    }

    // Transfers ownership of a synchronous scope on this thread; dispose it before awaiting.
    internal RecoveredSnapshotMutation Enter(Func<ProtocolResponse?>? authorize = null, [CallerMemberName] string operation = "")
    {
        bool nested = IsEntered;
        while (true)
        {
            ISnapshotRecoveryPreparation? preparation;
            var scope = EnterUnrecovered(operation);
            try
            {
                var rejection = authorize?.Invoke();
                if (rejection is not null) return new(scope, rejection);
                recovery.Value.RecoverPrerequisites();
                preparation = nested ? null : recovery.Value.CapturePreparation();
                // A nested caller may own other domain gates; its boundary must not be released here.
                if (preparation is null || !preparation.NeedsPreparation)
                {
                    recovery.Value.Replay();
                    // Recovery can revoke a session or change event-based authorization.
                    return new(scope, authorize?.Invoke());
                }
            }
            catch { scope.Dispose(); throw; }
            scope.Dispose();

            Exception? failure = null;
            try { SnapshotPreparationService.Run(() => { preparation.Prepare(); return true; }); }
            catch (Exception ex) { failure = ex; }
            scope = EnterUnrecovered(operation);
            try
            {
                var rejection = authorize?.Invoke();
                if (rejection is not null) return new(scope, rejection);
                recovery.Value.RecoverPrerequisites();
                // Competing recovery may have completed or replaced the captured intent.
                if (preparation.IsCurrent)
                {
                    if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
                    if (preparation.TryInstall())
                    {
                        recovery.Value.Replay();
                        return new(scope, authorize?.Invoke());
                    }
                }
            }
            catch { scope.Dispose(); throw; }
            scope.Dispose();
        }
    }
}

internal sealed class RecoveredSnapshotMutation(SnapshotMutationScope scope, ProtocolResponse? rejection) : IDisposable
{
    public ProtocolResponse? Rejection { get; } = rejection;
    public void Dispose() => scope.Dispose();
}
