using AIRsLight.ClashOfRim.Network.Modules.Snapshots;
using AIRsLight.ClashOfRim.Protocol;

internal static class SnapshotMutationCoordinatorTests
{
    internal static void Run()
    {
        foreach (int rejectOn in new[] { 1, 2, 3 }) AuthorizationBoundaries(rejectOn);
        NoPreparationRequired();
        NestedOwnership();
        foreach (string mode in new[] { "obsolete", "obsolete-failure", "install-conflict" }) PreparationRetry(mode);
        foreach (string phase in new[] { "authorize", "prerequisites", "capture", "prepare", "install", "replay" }) FailureReleasesGate(phase);
        Console.WriteLine("PASS: independent snapshot coordinator (authorization, lock-free preparation, nesting, retry and failures)");
    }

    private static void AuthorizationBoundaries(int rejectOn)
    {
        var f = new RecoveryProbe();
        int authorizations = 0;
        var denied = ProtocolResponse.Reject(ProtocolErrorCode.Unauthorized, "session replaced");
        using (var mutation = f.Coordinator.Enter(() =>
        {
            f.RequireHeld();
            return ++authorizations == rejectOn ? denied : null;
        }))
        {
            f.RequireHeld();
            Require(ReferenceEquals(mutation.Rejection, denied), "Authorization rejection was not returned.");
            Require(f.Preparations == (rejectOn == 1 ? 0 : 1), "Rejected request prepared recovery.");
            Require(f.Replays == (rejectOn == 3 ? 1 : 0), "Authorization boundary did not guard replay.");
        }
        Require(!Monitor.IsEntered(f.Gate), "Rejected scope leaked its gate.");
    }

    private static void NoPreparationRequired()
    {
        var f = new RecoveryProbe { NeedsPreparation = false };
        int authorizations = 0;
        using (var mutation = f.Coordinator.Enter(() => { authorizations++; return null; }))
        {
            f.RequireHeld();
            Require(f.Captures == 1 && f.Preparations == 0 && f.Replays == 1 && authorizations == 2,
                "Already-prepared recovery lost replay or authorization revalidation.");
        }
        Require(!Monitor.IsEntered(f.Gate), "Already-prepared recovery leaked its gate.");
    }

    private static void NestedOwnership()
    {
        var f = new RecoveryProbe();
        using (f.Coordinator.EnterUnrecovered())
        {
            using (var mutation = f.Coordinator.Enter())
            {
                f.RequireHeld();
                Require(mutation.Rejection is null, "Nested mutation was rejected.");
            }
            f.RequireHeld();
            Require(f.Captures == 0 && f.Preparations == 0 && f.Replays == 1, "Nested recovery tried to release its caller's boundary.");
        }
        Require(!Monitor.IsEntered(f.Gate), "Nested recovery leaked a lock level.");
    }

    private static void PreparationRetry(string mode)
    {
        var f = new RecoveryProbe { RetryMode = mode };
        using (var mutation = f.Coordinator.Enter())
        {
            f.RequireHeld();
            Require(mutation.Rejection is null && f.Replays == 1 && f.Captures == 2 && f.Preparations == 2,
                "Recovery failed to recapture a competing/replaced preparation: " + mode);
        }
        Require(!Monitor.IsEntered(f.Gate), "Retry leaked its gate.");
    }

    private static void FailureReleasesGate(string phase)
    {
        var f = new RecoveryProbe { FaultPhase = phase };
        bool threw = false;
        try
        {
            using var mutation = f.Coordinator.Enter(() =>
            {
                f.Fail("authorize");
                return null;
            });
        }
        catch (IOException ex) when (ReferenceEquals(ex, f.Failure)) { threw = true; }
        Require(threw && !Monitor.IsEntered(f.Gate), "Failure swallowed or mutation scope leaked: " + phase);
        // The same coordinator must remain usable after a fault at any phase.
        f.FaultPhase = null;
        using (var mutation = f.Coordinator.Enter()) f.RequireHeld();
        Require(!Monitor.IsEntered(f.Gate), "Retry after failure leaked its gate: " + phase);
    }

    private sealed class RecoveryProbe : ISnapshotRecovery
    {
        internal readonly object Gate = new();
        internal readonly SnapshotMutationCoordinator Coordinator;
        internal readonly IOException Failure = new("injected recovery failure");
        internal string? FaultPhase;
        internal string? RetryMode;
        internal bool NeedsPreparation = true;
        internal int Captures, Preparations, Replays;

        internal RecoveryProbe() => Coordinator = new(Gate, this);
        internal void RequireHeld() => Require(Monitor.IsEntered(Gate), "Recovery/publication ran outside the mutation gate.");
        internal void Fail(string phase) { if (FaultPhase == phase) throw Failure; }

        public void RecoverPrerequisites() { RequireHeld(); Fail("prerequisites"); }
        public ISnapshotRecoveryPreparation CapturePreparation()
        {
            RequireHeld();
            Fail("capture");
            return new PreparationProbe(this, ++Captures);
        }
        public void Replay() { RequireHeld(); Fail("replay"); Replays++; }

        private sealed class PreparationProbe(RecoveryProbe owner, int generation) : ISnapshotRecoveryPreparation
        {
            public bool NeedsPreparation { get { owner.RequireHeld(); return owner.NeedsPreparation; } }
            public bool IsCurrent
            {
                get
                {
                    owner.RequireHeld();
                    return generation != 1 || owner.RetryMode is not ("obsolete" or "obsolete-failure");
                }
            }
            public void Prepare()
            {
                Require(!Monitor.IsEntered(owner.Gate), "Preparation kept the mutation gate.");
                // Prove a competing thread can enter during preparation, without relying on timing sleeps.
                bool acquired = false;
                var competitor = new Thread(() =>
                {
                    if (!Monitor.TryEnter(owner.Gate, TimeSpan.FromSeconds(5))) return;
                    try { acquired = true; } finally { Monitor.Exit(owner.Gate); }
                });
                competitor.Start();
                Require(competitor.Join(TimeSpan.FromSeconds(10)) && acquired, "Preparation blocked a competing mutation.");
                owner.Preparations++;
                owner.Fail("prepare");
                if (generation == 1 && owner.RetryMode == "obsolete-failure") throw owner.Failure;
            }
            public bool TryInstall()
            {
                owner.RequireHeld();
                owner.Fail("install");
                return generation != 1 || owner.RetryMode != "install-conflict";
            }
        }
    }

    private static void Require(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
}
