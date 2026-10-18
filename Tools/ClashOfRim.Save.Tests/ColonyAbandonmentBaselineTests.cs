using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using Microsoft.Data.Sqlite;

internal static class ColonyAbandonmentBaselineTests
{
    public static void Run(Func<string, SaveSnapshotPackage> fixture)
    {
        SnapshotEndpointTestClient.LoadLocalization();
        foreach (bool reopen in new[] { false, true }) VerifyRecovery(fixture, reopen);
        foreach (bool revoke in new[] { false, true }) VerifyWaitingRequest(fixture, revoke);
        foreach (bool hasSnapshot in new[] { false, true }) VerifyOptionalBaseline(fixture, hasSnapshot);
        VerifySessionRetirement(fixture);
    }

    private static void VerifySessionRetirement(Func<string, SaveSnapshotPackage> fixture)
    {
        WithDirectory(root =>
        {
            var snapshots = new FileColonySnapshotIndexStore(root);
            var state = new ClashOfRimNetworkState(snapshotStore: snapshots);
            var package = fixture("session-retirement"); snapshots.StoreLatest(package, package.Index, DateTimeOffset.UtcNow);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            Require(state.LoginSessions.TryCreate("userA", "colonyA", now, out string oldSession)
                && state.LoginSessions.TryBeginStream("userA", "colonyA", oldSession, now), "Cannot start old stream.");
            Require(state.OnlinePresence.TryConnectExclusive("userA", oldSession, out var oldPresence), "Cannot acquire old presence.");
            using var oldLease = oldPresence!;
            string oldToken = state.AuthTokens.Issue("userA", "userA", "colonyA", oldSession, now);
            Require(Invoke(state, package.Envelope.Identity.SnapshotId, oldToken).Result.Accepted, "Abandonment rejected.");
            Require(!state.LoginSessions.IsValid("userA", "colonyA", oldSession, now)
                && !state.LoginSessions.Refresh("userA", "colonyA", oldSession, now)
                && !oldLease.Touch() && !state.AuthTokens.IsValid(oldToken, "userA", "colonyA", now),
                "Abandoned connection retained session/heartbeat/presence authorization.");
            Require(state.LoginSessions.TryCreate("userA", "rebuilt", now, out string newSession)
                && state.LoginSessions.TryBeginStream("userA", "rebuilt", newSession, now), "Old connection blocks immediate rebuild login.");
            Require(state.OnlinePresence.TryConnectExclusive("userA", newSession, out var newPresence), "Old presence blocks rebuild.");
            using var newLease = newPresence!;
            string newToken = state.AuthTokens.Issue("userA", "userA", "rebuilt", newSession, now);
            state.Players.Record("userA", "rebuilt", null, now);
            Require(!Invoke(state, package.Envelope.Identity.SnapshotId, oldToken).Result.Accepted, "Old token accepted repeated abandonment.");
            // An interrupted deletion may be replayed after another generation exists.
            // A transport's finally block can also arrive after the replacement login.
            state.ColonyDeletion.Delete("userA", "colonyA", now);
            state.SessionConnections.Close("userA", "colonyA", oldSession, oldLease);
            Require(state.LoginSessions.IsValid("userA", "rebuilt", newSession, now)
                && state.LoginSessions.Refresh("userA", "rebuilt", newSession, now) && newLease.Touch()
                && state.AuthTokens.IsValid(newToken, "userA", "rebuilt", now)
                && !state.Players.IsDeleted("userA", "rebuilt"), "Old deletion/transport cleanup damaged rebuilt session.");
        });
    }

    private static void VerifyRecovery(Func<string, SaveSnapshotPackage> fixture, bool reopen)
    {
        WithDirectory(root =>
        {
            string database = Path.Combine(root, "server.sqlite"), snapshots = Path.Combine(root, "snapshots"), journal = Path.Combine(root, "intents");
            ServerDatabaseMigrator.Migrate(database);
            BankLoanRegistry Bank() => new(new SqliteDomainKeyedJsonRecordStore(database, SqliteDomainRegistrySchema.BankLoans), null);
            var bank = Bank();
            var files = new FileColonySnapshotIndexStore(snapshots);
            var commits = new SnapshotCommitService(journal);
            var before = fixture("abandon-before");
            var next = fixture("abandon-recovered");
            next = next with { Envelope = next.Envelope with { NextLineageToken = "recovered-token" } };
            files.StoreLatest(before, before.Index, DateTimeOffset.UtcNow);
            var loan = bank.Create("borrow", "userA", "colonyA", before.Envelope.Identity.SnapshotId!, 100, 10, 1, 0, DateTimeOffset.UtcNow);
            var snapshot = new LatestSnapshotRecord(next.Envelope.Identity, next.Envelope, next.Index, DateTimeOffset.UtcNow);
            var failing = new ObservedStore(files) { FailWrite = true };
            bool interrupted = false;
            try
            {
                commits.CommitPendingOperations(failing, bank, new MercenaryContractRegistry(), next, snapshot,
                    new PendingSnapshotOperations(loan, [], []));
            }
            catch (IOException ex) when (ex.Message == "injected pre-publication failure") { interrupted = true; }
            Require(interrupted && Directory.EnumerateFiles(journal, "*.json").Count() == 1, "recovery fixture did not leave a pending intent");
            if (reopen)
            {
                bank = Bank();
                files = new FileColonySnapshotIndexStore(snapshots);
                commits = new SnapshotCommitService(journal);
            }
            var state = new ClashOfRimNetworkState(snapshotStore: files, bankLoans: bank, snapshotCommits: commits);
            var unauthorized = Invoke(state, before.Envelope.Identity.SnapshotId, "invalid-token");
            Require(!unauthorized.Result.Accepted && unauthorized.Result.ErrorCode == ProtocolErrorCode.ServerRejected
                && files.GetLatest("userA", "colonyA")?.Envelope == before.Envelope
                && Directory.EnumerateFiles(journal, "*.json").Count() == 1,
                "unauthenticated request performed snapshot recovery");
            string token = Authorize(state);
            var response = Invoke(state, before.Envelope.Identity.SnapshotId, token);
            Require(!response.Result.Accepted && response.Result.ErrorCode == ProtocolErrorCode.SnapshotMismatch,
                "stale abandonment deleted the snapshot published by recovery");
            Require(response.RemovedSnapshots == 0 && response.RemovedSites == 0 && response.RemovedEvents == 0 && response.RemovedRuntimeMarkers == 0,
                "rejected abandonment reported destructive work");
            Require(files.GetLatest("userA", "colonyA")?.Envelope == next.Envelope
                && new FileColonySnapshotIndexStore(snapshots).GetLatest("userA", "colonyA")?.Envelope == next.Envelope,
                "stale abandonment changed the recovered file");
            Require(!Directory.EnumerateFiles(journal, "*.json").Any()
                && bank.GetActive("userA", "colonyA")?.SnapshotId == next.Envelope.Identity.SnapshotId
                && Bank().GetActive("userA", "colonyA")?.SnapshotId == next.Envelope.Identity.SnapshotId,
                "recovery was skipped or its bank state was deleted");
            Require(!state.Players.IsDeleted("userA", "colonyA") && state.Ledger.ListAll().Count == 0
                && state.AuthTokens.IsValid(token, "userA", "colonyA", DateTimeOffset.UtcNow), "rejection left deletion side effects");

            response = Invoke(state, next.Envelope.Identity.SnapshotId, token);
            Require(response.Result.Accepted && response.RemovedSnapshots == 1
                && new FileColonySnapshotIndexStore(snapshots).GetLatest("userA", "colonyA") is null
                && state.Players.IsDeleted("userA", "colonyA") && Bank().GetActive("userA", "colonyA") is null,
                "abandonment with the recovered current version failed");
        });
    }

    private static void VerifyWaitingRequest(Func<string, SaveSnapshotPackage> fixture, bool revoke)
    {
        WithDirectory(root =>
        {
            var files = new FileColonySnapshotIndexStore(root);
            var store = new ObservedStore(files);
            var state = new ClashOfRimNetworkState(snapshotStore: store);
            var before = fixture("waiting-before");
            var next = fixture("waiting-next");
            files.StoreLatest(before, before.Index, DateTimeOffset.UtcNow);
            string token = Authorize(state);
            bool readOutsideGate = false;
            store.OnRead = () => readOutsideGate |= !Monitor.IsEntered(state.RaidSettlementSnapshotMutationGate);
            AbandonPlayerColonyResponse? response = null;
            Exception? failure = null;
            using var started = new ManualResetEventSlim();
            var worker = new Thread(() =>
            {
                started.Set();
                try { response = Invoke(state, before.Envelope.Identity.SnapshotId, token); }
                catch (Exception ex) { failure = ex; }
            }) { IsBackground = true };
            bool waiting;
            Monitor.Enter(state.RaidSettlementSnapshotMutationGate);
            try
            {
                worker.Start();
                Require(started.Wait(TimeSpan.FromSeconds(10)), "request thread did not start");
                // The only contested lock is the mutation gate held by this thread.
                waiting = SpinWait.SpinUntil(() => (worker.ThreadState & ThreadState.WaitSleepJoin) != 0 || !worker.IsAlive,
                    TimeSpan.FromSeconds(10)) && worker.IsAlive;
                if (revoke) state.AuthTokens.RevokeForColony("userA", "colonyA");
                else files.StoreLatest(next, next.Index, DateTimeOffset.UtcNow);
            }
            finally
            {
                Monitor.Exit(state.RaidSettlementSnapshotMutationGate);
                Require(worker.Join(TimeSpan.FromSeconds(30)), "request did not finish after releasing the mutation gate");
            }
            if (failure is not null) throw new Exception("Abandonment endpoint failed.", failure);
            Require(waiting && !readOutsideGate, "request inspected the deletion baseline before acquiring its mutation gate");
            Require(response is not null && !response.Result.Accepted
                && response.Result.ErrorCode == (revoke ? ProtocolErrorCode.ServerRejected : ProtocolErrorCode.SnapshotMismatch),
                "waiting request ignored new authorization or snapshot state");
            Require(files.GetLatest("userA", "colonyA")?.Envelope == (revoke ? before : next).Envelope
                && !state.Players.IsDeleted("userA", "colonyA") && state.Ledger.ListAll().Count == 0,
                "waiting stale request deleted colony state");
        });
    }

    private static void VerifyOptionalBaseline(Func<string, SaveSnapshotPackage> fixture, bool hasSnapshot)
    {
        WithDirectory(root =>
        {
            var files = new FileColonySnapshotIndexStore(root);
            var state = new ClashOfRimNetworkState(snapshotStore: files);
            var package = fixture("optional-baseline");
            if (hasSnapshot) files.StoreLatest(package, package.Index, DateTimeOffset.UtcNow);
            var response = Invoke(state, hasSnapshot ? null : "already-missing", Authorize(state));
            Require(response.Result.Accepted && response.RemovedSnapshots == (hasSnapshot ? 1 : 0)
                && files.GetLatest("userA", "colonyA") is null && state.Players.IsDeleted("userA", "colonyA"),
                "optional baseline or already-missing snapshot behavior changed");
        });
    }

    private static string Authorize(ClashOfRimNetworkState state)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Require(state.LoginSessions.TryCreate("userA", "colonyA", now, out string session), "could not create login session");
        return state.AuthTokens.Issue("userA", "userA", "colonyA", session, now);
    }

    private static AbandonPlayerColonyResponse Invoke(ClashOfRimNetworkState state, string? snapshot, string token)
    {
        var request = new AbandonPlayerColonyRequest(ProtocolApiVersion.Current, "userA", "colonyA", snapshot, "abandon-test", token);
        return state.ColonyAbandonment.Abandon(request);
    }

    private static void WithDirectory(Action<string> action)
    {
        string root = Path.Combine(Path.GetTempPath(), "cor-abandon-baseline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { action(root); }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception("Colony abandonment: " + message);
    }

    private sealed class ObservedStore(IColonySnapshotPackageStore inner) : IColonySnapshotPackageStore
    {
        public bool FailWrite { get; init; }
        public Action? OnRead { get; set; }
        public void StoreLatest(SaveSnapshotPackage package, SaveSnapshotIndex index, DateTimeOffset at)
        {
            if (FailWrite) throw new IOException("injected pre-publication failure");
            inner.StoreLatest(package, index, at);
        }
        public LatestSnapshotRecord? GetLatest(string user, string colony) { OnRead?.Invoke(); return inner.GetLatest(user, colony); }
        public void StoreLatest(LatestSnapshotRecord snapshot) => throw new NotSupportedException();
        public SaveSnapshotPackage? GetLatestPackage(string user, string colony) => inner.GetLatestPackage(user, colony);
        public IReadOnlyList<LatestSnapshotRecord> ListLatest() => inner.ListLatest();
        public bool RemoveLatest(string user, string colony) => inner.RemoveLatest(user, colony);
    }
}
