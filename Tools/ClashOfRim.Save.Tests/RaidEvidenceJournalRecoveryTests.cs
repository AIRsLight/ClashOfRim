using System.Text.Json;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;

internal static partial class RaidEvidenceAuthorizationTests
{
    private static void RunJournalRecovery(Func<string, string, SaveSnapshotPackage> fixture)
    {
        foreach (string phase in new[] { "initial", "upload", "artifact" })
        {
            foreach (string change in new[] { "none", "logout", "replace", "revoke", "revoke-failure", "cancel", "newer", "compete", "failure" })
                WithFixture(fixture, f => VerifyJournalRecovery(f, phase, change), persistent: true);
            if (phase != "initial")
                foreach (string change in new[] { "source", "attacker" })
                    WithFixture(fixture, f => VerifyJournalRecovery(f, phase, change), persistent: true);
            foreach (bool conflict in new[] { false, true })
                WithFixture(BindingFixture, f => VerifySettlementJournal(f, phase, conflict), persistent: true);
        }
        foreach (string identity in new[] { "defender", "delegated", "header", "metadata" })
            WithFixture(fixture, f => VerifyJournalIdentity(f, identity), persistent: true);
        WithFixture(fixture, VerifyConcurrentJournal, persistent: true);
    }

    private static void VerifyJournalRecovery(Fixture f, string phase, string change)
    {
        var intent = f.ForeignIntent();
        Action? recoveryHook = null;
        void Inject()
        {
            using (f.State.EnterSnapshotMutation()) f.SeedRecovery(intent);
            f.Store.BeforePrepare = () => recoveryHook?.Invoke();
        }
        if (phase == "initial") Inject();
        else if (phase == "upload") f.Store.BeforePrepare = Inject;
        else f.ArtifactStore.AfterPrepare = () => { f.ArtifactStore.AfterPrepare = null; Inject(); };
        ConfirmEventApplicationResponse? reply = null;
        Exception? failure = null;
        string? before = null;
        int captures = 0;
        SnapshotPreparationGateProbe.Run(f.State.RaidSettlementSnapshotMutationGate, hook => recoveryHook = hook,
            () => { try { reply = f.Invoke(); } catch (Exception ex) { failure = ex; } }, () =>
            {
                captures++;
                Require(f.Store.WritesByOwner.GetValueOrDefault("foreign") == 0 && f.State.SnapshotPostUploadJobs.ListAll().Count == 0,
                    "Evidence request published recovery or scheduled before preparation completed.");
                if (change is "logout" or "replace") f.State.LoginSessions.EndUser("userA");
                if (change == "replace") f.State.LoginSessions.TryCreate("userA", "colonyA", DateTimeOffset.UtcNow, out _);
                if (change is "revoke" or "revoke-failure") f.State.AuthTokens.RevokeForUser("userA");
                if (change is "failure" or "revoke-failure") f.Store.FailPreparation = true;
                if (change == "cancel") f.State.Ledger.ChangeStatus(f.Raid.EventId, ServerEventStatus.Cancelled);
                if (change == "compete") f.State.SnapshotMutations.Recover();
                if (change == "newer") f.Store.Inner.StoreLatest(intent.Package with { Envelope = intent.Package.Envelope with
                    { Identity = new("foreign", "foreign-colony", "newer"), NextLineageToken = "newer" } }, intent.Package.Index, DateTimeOffset.UtcNow);
                if (change == "source")
                {
                    var source = f.State.Ledger.Find(f.Raid.EventId)!;
                    var payload = (RaidEventPayload)source.Payload;
                    f.State.Ledger.ApplyBatch(new LedgerBatch([], [new(source.EventId, source.Status, source.Status, payload,
                        payload with { StartedAtUtc = payload.StartedAtUtc!.Value.AddMinutes(1) }, source)]));
                }
                if (change == "attacker") f.Store.Inner.StoreLatest(f.Package with { Envelope = f.Package.Envelope with
                    { Identity = new("userA", "colonyA", "newer-attacker"), NextLineageToken = "newer-attacker" } }, f.Package.Index, DateTimeOffset.UtcNow);
                before = f.RecoveryEvidence();
            });
        Require(captures == 1, "Evidence request missed its journal recovery boundary: " + phase);
        bool denied = change is "logout" or "replace" or "revoke" or "revoke-failure" or "cancel";
        bool failed = change is "newer" or "failure";
        bool stale = change is "source" or "attacker";
        Require((failure is not null) == failed, "Unexpected evidence recovery result: " + phase + "/" + change + " " + failure);
        if (denied || failed)
        {
            Require(before == f.RecoveryEvidence() && f.State.SnapshotCommits.HasPending && f.Store.WritesByOwner.Count == 0,
                "Rejected or failed evidence recovery changed business or discarded its intent.");
            if (denied) Require(reply is { Result.Accepted: false, AppliedSnapshotId: null, NextLineageToken: null },
                "Revoked evidence recovery returned confirmation credentials.");
            if (change == "failure")
            {
                f.Open(); f.State.SnapshotMutations.Recover();
                Require(!f.State.SnapshotCommits.HasPending && f.State.Ledger.FindByIdempotencyKey("foreign-gift") is not null,
                    "Evidence recovery preparation failure did not resume after reopening.");
            }
        }
        else
        {
            Require(!f.State.SnapshotCommits.HasPending && f.Store.WritesByOwner.GetValueOrDefault("foreign") == 1
                && f.Store.Inner.GetLatest("foreign", "foreign-colony")!.Envelope.NextLineageToken == "foreign-token"
                && JsonSerializer.Serialize(f.State.Ledger.FindByIdempotencyKey("foreign-gift")) == JsonSerializer.Serialize(intent.Event),
                "Evidence request did not recover the original intent exactly once.");
            Require(reply!.Result.Accepted != stale && f.Store.WritesByOwner.GetValueOrDefault("userA") == (stale ? 0 : 1)
                && f.State.SnapshotPostUploadJobs.ListAll().Count == (stale ? 0 : 1),
                "Evidence request used a stale source or snapshot after recovery: " + phase + "/" + change);
            if (stale) Require(reply.AppliedSnapshotId is null && reply.NextLineageToken is null, "Stale evidence disclosed a receipt.");
            else Require(reply.AppliedSnapshotId == "evidence" && !string.IsNullOrEmpty(reply.NextLineageToken), "Evidence confirmation was lost after recovery.");
        }
        f.AssertRecoveryReleased();
    }

    private static void VerifyJournalIdentity(Fixture f, string identity)
    {
        f.SeedRecovery(f.ForeignIntent());
        string before = f.RecoveryEvidence();
        f.Store.BeforePrepare = () => throw new Exception("Wrong-party evidence request prepared journal recovery.");
        var reply = f.Invoke(identity);
        Require(!reply.Result.Accepted && reply.AppliedSnapshotId is null && reply.NextLineageToken is null
            && before == f.RecoveryEvidence() && f.Store.WritesByOwner.Count == 0,
            "Wrong-party evidence request recovered unrelated business.");
        f.AssertRecoveryReleased();
    }

    private static void VerifySettlementJournal(Fixture f, string phase, bool conflict)
    {
        Require(f.Invoke().Result.Accepted, "Could not queue settlement recovery fixture.");
        f.Store.Fault = "before";
        try { f.Complete(); throw new Exception("Settlement did not interrupt publication."); } catch (IOException) { }
        string path = Directory.GetFiles(f.RecoveryJournal, "*.json").Single();
        var intent = JsonSerializer.Deserialize<SnapshotCommitIntent>(File.ReadAllBytes(path))!;
        File.Delete(path);
        f.Open();
        var mutation = intent.RaidSettlement!;
        if (conflict) mutation = mutation with { Settlement = mutation.Settlement with
            { Payload = ((RaidEventPayload)mutation.Settlement.Payload) with { EvidenceRequestHash = "v1:different-request" } } };
        Action? recoveryHook = null;
        void Inject()
        {
            using (f.State.EnterSnapshotMutation())
            {
                f.Store.FailRecoveryPublication = true;
                try { f.State.SnapshotCommits.CommitRaidSettlement(f.Store, f.State.BankLoans, f.State.Ledger, f.State.Players,
                    intent.Package, mutation, _ => { }); }
                catch (IOException) { }
                finally { f.Store.FailRecoveryPublication = false; }
            }
            Require(f.State.SnapshotCommits.HasPending, "Could not inject settlement journal.");
            f.Store.BeforePrepare = () => recoveryHook?.Invoke();
        }
        if (phase == "initial") Inject();
        else if (phase == "upload") f.Store.BeforePrepare = Inject;
        else f.ArtifactStore.AfterRead = () => { f.ArtifactStore.AfterRead = null; Inject(); };
        ConfirmEventApplicationResponse? reply = null;
        SnapshotPreparationGateProbe.Run(f.State.RaidSettlementSnapshotMutationGate, hook => recoveryHook = hook,
            () => reply = f.Invoke(), () => Require(f.Store.WritesByOwner.Count == 0
                && f.State.Ledger.Find(f.Raid.EventId)!.Status != ServerEventStatus.AppliedToSnapshot,
                "Settlement journal published before recovery preparation completed."));
        Require(!f.State.SnapshotCommits.HasPending && f.Store.WritesByOwner.GetValueOrDefault("userB") == 1
            && f.Store.WritesByOwner.GetValueOrDefault("userA") == 0
            && f.State.Ledger.Find(f.Raid.EventId)!.Status == ServerEventStatus.AppliedToSnapshot,
            "Evidence request did not recover the pending settlement exactly once.");
        Require(reply!.Result.Accepted != conflict && (conflict ? reply.AppliedSnapshotId is null && reply.NextLineageToken is null
            : reply.AppliedSnapshotId == "evidence" && reply.ServerValidationResult == "AlreadyApplied"),
            "Evidence request used a stale source or mismatched receipt after journal recovery: " + phase + "/" + conflict);
        string before = f.RecoveryEvidence();
        Require(f.Invoke().Result.Accepted != conflict && before == f.RecoveryEvidence(), "Recovered settlement replay changed durable state.");
        f.AssertRecoveryReleased();
    }

    private static void VerifyConcurrentJournal(Fixture f)
    {
        f.SeedRecovery(f.ForeignIntent());
        var replies = ServiceSnapshotReplayRaceTests.Race(_ => Task.FromResult(f.Invoke()), () => { });
        Require(replies.All(r => r.Result.Accepted && r.AppliedSnapshotId == "evidence")
            && replies.Select(r => r.NextLineageToken).Distinct().Count() == 1
            && !f.State.SnapshotCommits.HasPending && f.Store.WritesByOwner.GetValueOrDefault("foreign") == 1
            && f.Store.WritesByOwner.GetValueOrDefault("userA") == 1 && f.State.SnapshotPostUploadJobs.ListAll().Count == 1,
            "Concurrent evidence recovery did not converge on one publication and acknowledgement.");
        f.AssertRecoveryReleased();
    }

    private sealed partial class Fixture
    {
        public string RecoveryJournal => Path.Combine(root, "intents");
        public (SaveSnapshotPackage Package, AuthoritativeEvent Event) ForeignIntent()
        {
            var template = Store.Inner.GetLatestPackage("userB", "colonyB")!;
            var package = template with { Envelope = template.Envelope with { Identity = new("foreign", "foreign-colony", "foreign-snapshot"),
                PreviousSnapshotId = null, LineageToken = null, NextLineageToken = "foreign-token" } };
            var gift = AuthoritativeEventFactory.Create(ServerEventType.ItemDelivery, new("foreign", "foreign-colony"), new("recipient", "recipient-colony"),
                "foreign-gift", false, new ItemDeliveryEventPayload([new("cargo", "Silver", 1, "foreign-snapshot")], "foreign"), DateTimeOffset.UtcNow);
            return (package, gift);
        }
        public void SeedRecovery((SaveSnapshotPackage Package, AuthoritativeEvent Event) intent)
        {
            Store.FailRecoveryPublication = true;
            var snapshot = new LatestSnapshotRecord(intent.Package.Envelope.Identity, intent.Package.Envelope, intent.Package.Index, DateTimeOffset.UtcNow);
            try { State.SnapshotCommits.CommitGift(Store, State.BankLoans, State.Ledger, intent.Package, snapshot, intent.Event, (_, _) => { }, _ => { }); }
            catch (IOException) { }
            finally { Store.FailRecoveryPublication = false; }
            Require(State.SnapshotCommits.HasPending, "Could not seed journal recovery for evidence confirmation.");
        }
        public string RecoveryEvidence() => JsonSerializer.Serialize(new { state = StateEvidence(),
            journal = Directory.GetFiles(RecoveryJournal, "*.json").Order().Select(File.ReadAllText).ToArray() });
        public void AssertRecoveryReleased()
        {
            Require(!Directory.EnumerateFiles(Artifacts, "*.tmp").Any(), "Journal recovery leaked prepared evidence files.");
            Task.Run(() =>
            {
                Require(Monitor.TryEnter(State.RaidSettlementSnapshotMutationGate, TimeSpan.FromSeconds(3)), "Evidence recovery leaked the mutation gate.");
                Monitor.Exit(State.RaidSettlementSnapshotMutationGate);
            }).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        }
    }
}
