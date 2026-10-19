using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using System.Text.Json.Nodes;
using TestStore = SnapshotRecoveryTestStore;

internal static class ExpeditionEndpointRecoveryPreparationTests
{
    public static void Run(Func<string, SaveSnapshotPackage> factory)
    {
        SnapshotEndpointTestClient.LoadLocalization();
        foreach (string kind in new[] { "support", "raid", "guarded-raid" })
        {
            foreach (bool final in new[] { false, true })
            foreach (string change in new[] { "none", "logout", "replace", "revoke", "newer", "compete", "failure", "revoke-failure" })
                With(factory, kind, f => VerifyRecovery(f, final, change));
            With(factory, kind, VerifyConcurrent);
            With(factory, kind, VerifyPrincipal);
            foreach (string change in kind == "support" ? new[] { "source", "target-remove", "target-move" }
                : new[] { "pvp", "peace", "baseline", "guard" })
                With(factory, kind, f => VerifyFinalDecision(f, change));
        }
    }

    private static void VerifyRecovery(Fixture f, bool final, string change)
    {
        var original = f.Seed();
        if (final) File.Delete(Directory.GetFiles(f.Journal, "*.json").Single());
        f.Open(); f.Store.Reset();
        EventCreationResponse? reply = null; Exception? failure = null; string? before = null; int preparations = 0;
        void Change()
        {
            preparations++;
            if (change is "logout" or "replace") f.State.LoginSessions.EndUser("userA");
            if (change == "replace") f.State.LoginSessions.TryCreate("userA", "colonyA", DateTimeOffset.UtcNow, out _);
            if (change is "revoke" or "revoke-failure") f.State.AuthTokens.RevokeForUser("userA");
            if (change == "newer") f.ChangeDecision("source");
            if (change == "compete") f.State.SnapshotMutations.Recover();
            if (change is "failure" or "revoke-failure") f.Store.FailPreparation = true;
            before = f.Evidence();
        }
        SnapshotPreparationGateProbe.Run(f.Gate, hook => f.Store.BeforePrepare = hook,
            () => { try { reply = f.Call(); } catch (Exception ex) { failure = ex; } }, () =>
            {
                Check(f.Store.Writes == 0, "Expedition recovery published during preparation.");
                if (!final) Change();
                else
                {
                    f.SeedDirect(original); f.Store.Reset();
                    f.Store.BeforePrepare = () =>
                    {
                        f.Store.BeforePrepare = null;
                        Check(!Monitor.IsEntered(f.Gate), "Final expedition recovery prepared under the business gate.");
                        Task.Run(() => { using (f.State.EnterSnapshotMutation()) Change(); }).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                    };
                }
            });
        Check(preparations == 1, "Expedition recovery did not use the intended boundary: " + f.Kind);
        bool denied = change is "logout" or "replace" or "revoke" or "revoke-failure";
        bool failed = change is "newer" or "failure";
        Check((failure is not null) == failed, "Unexpected recovery result: " + f.Kind + "/" + final + "/" + change + " " + failure);
        if (denied) Check(reply is { Result.Accepted: false, EventId: null, AppliedSnapshotId: null, NextLineageToken: null }
            && before == f.Evidence() && f.Store.Writes == 0 && f.State.SnapshotCommits.HasPending, "Revoked expedition recovered business or exposed a receipt.");
        else if (failed) Check(before == f.Evidence() && f.Store.Writes == 0 && f.State.SnapshotCommits.HasPending, "Failed expedition recovery changed state.");
        else
        {
            f.AssertReplay(reply!, original);
            Check(f.Store.Writes == 1 && f.Store.PreparedWrites == (change == "compete" ? 0 : 1), "Expedition recovery did not reuse prepared bytes.");
        }
        f.AssertReleased();
    }

    private static void VerifyConcurrent(Fixture f)
    {
        var original = f.Seed(); f.Open();
        var replies = SnapshotReplayRace.Run(_ => Task.FromResult(f.Call()), () => { });
        foreach (var reply in replies) f.AssertReplay(reply, original);
        Check(f.Store.Writes == 1 && f.Store.PreparedWrites == 1, "Concurrent expedition recovery published twice.");
        f.AssertReleased();
    }

    private static void VerifyPrincipal(Fixture f)
    {
        f.Seed(); f.Open(); string before = f.Evidence();
        f.Store.BeforePrepare = () => throw new Exception("Wrong principal prepared expedition recovery.");
        Check(!f.Call(wrongPrincipal: true).Result.Accepted && before == f.Evidence() && f.Store.Writes == 0,
            "Wrong principal recovered expedition state.");
        f.AssertReleased();
    }

    private static void VerifyFinalDecision(Fixture f, string change)
    {
        EventCreationResponse? reply = null; string? before = null;
        SnapshotPreparationGateProbe.Run(f.Gate, hook => f.Store.BeforePrepare = hook, () => reply = f.Call(),
            () => { f.ChangeDecision(change); before = f.Evidence(); });
        if (change == "target-move") Check(reply!.Result.Accepted && f.State.Ledger.Find(reply.EventId!)!.TargetContext!.MapUniqueId == "Map_new"
            && f.Store.Writes == 1, "Support did not resolve its current target after preparation.");
        else Check(reply is { Result.Accepted: false, EventId: null, AppliedSnapshotId: null, NextLineageToken: null }
            && before == f.Evidence() && f.Store.Writes == 0 && !f.State.SnapshotCommits.HasPending, "Expedition accepted stale decision: " + f.Kind + "/" + change);
        f.AssertReleased();
    }

    private static void With(Func<string, SaveSnapshotPackage> factory, string kind, Action<Fixture> run)
    {
        string root = Path.Combine(Path.GetTempPath(), "cor-expedition-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { run(new(root, factory, kind)); }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

    private sealed class Fixture
    {
        private readonly string root, database;
        private string token = "";
        public string Kind { get; }
        public string Journal => Path.Combine(root, "intents");
        public SaveSnapshotPackage Package { get; }
        public ClashOfRimNetworkState State = null!;
        public TestStore Store = null!;
        public object Gate => State.RaidSettlementSnapshotMutationGate;
        public Fixture(string root, Func<string, SaveSnapshotPackage> factory, string kind)
        {
            this.root = root; Kind = kind; database = Path.Combine(root, "server.sqlite");
            ServerDatabaseMigrator.Migrate(database); Open(); Store.Gate = null;
            Package = kind == "support" ? SupportCommitTests.Prepare(factory, State, root)
                : RaidTestPreparation.Prepare(State.SnapshotStore, State.DiplomacyRelations, State.MercenaryGuards, factory, kind == "guarded-raid");
            Store.Gate = Gate; Store.Reset();
        }
        public void Open()
        {
            Store = new(Path.Combine(root, "snapshots"));
            State = new(snapshotStore: Store, ledger: new SqliteAuthoritativeEventLedger(database),
                mercenaryGuards: RaidTestPreparation.OpenGuards(database),
                snapshotCommits: new SnapshotCommitService(Journal), serverConfiguration: new ClashOfRimServerConfiguration(pvpEnabled: true));
            Store.Gate = Gate;
            token = SessionTestAuthentication.Token(State, "userA", "colonyA");
            RaidTestPreparation.SetHostile(State.DiplomacyRelations);
        }
        public EventCreationResponse Call(bool wrongPrincipal = false)
        {
            var principal = new ProtocolIdentity("userA", "colonyA", "before");
            if (Kind != "support") return RaidCreationTestClient.Invoke(State, Package, Kind == "guarded-raid", principal: principal, token: token,
                actorOverride: wrongPrincipal ? new("userB", "colonyB", "before") : null).GetAwaiter().GetResult();
            var request = JsonSerializer.SerializeToNode(SupportCommitTests.Request(Package))!;
            request["SupportPawn"]!["TargetContext"] = null;
            if (wrongPrincipal) { request["SupportPawn"]!["Actor"]!["UserId"] = "userB"; request["SupportPawn"]!["Actor"]!["ColonyId"] = "colonyB"; }
            return SnapshotEndpointTestClient.Invoke<EventCreationResponse>("CreateSupportPawnWithSnapshot", State, _ => request,
                Package.Payload, principal, token).GetAwaiter().GetResult();
        }
        public SnapshotCommitIntent Seed()
        {
            Store.FailPublication = true;
            try { Call(); throw new Exception("Missing expedition publication failure."); } catch (IOException) { }
            finally { Store.FailPublication = false; }
            return JsonSerializer.Deserialize<SnapshotCommitIntent>(File.ReadAllBytes(Directory.GetFiles(Journal, "*.json").Single()))!;
        }
        public void SeedDirect(SnapshotCommitIntent intent)
        {
            Store.FailPublication = true;
            var package = intent.Package;
            var snapshot = new LatestSnapshotRecord(package.Envelope.Identity, package.Envelope, package.Index, intent.AcceptedAtUtc);
            try
            {
                if (intent.Support is { } support) State.SnapshotCommits.CommitSupport(Store, State.BankLoans, State.Ledger, package, snapshot,
                    support, (_, _) => { }, _ => { });
                else State.SnapshotCommits.CommitRaid(Store, State.BankLoans, State.Ledger, State.MercenaryContracts, State.MercenaryGuards,
                    package, snapshot, intent.Raid!, (_, _) => { }, _ => { });
            }
            catch (IOException) { }
            finally { Store.FailPublication = false; }
            Check(State.SnapshotCommits.HasPending, "Could not inject expedition intent.");
        }
        public void AssertReplay(EventCreationResponse reply, SnapshotCommitIntent original)
        {
            var main = original.Support?.Support ?? original.Raid!.Raid;
            Check(reply.Result.Accepted && reply.Result.ErrorCode == ProtocolErrorCode.DuplicateRequest && reply.EventId == main.EventId
                && reply.AppliedSnapshotId == original.Package.Envelope.Identity.SnapshotId && reply.NextLineageToken == original.Package.Envelope.NextLineageToken
                && !State.SnapshotCommits.HasPending && JsonSerializer.Serialize(State.Ledger.Find(main.EventId)) == JsonSerializer.Serialize(main),
                "Recovery lost original expedition reply or event.");
            Check(State.Ledger.ListAll().Count == (Kind == "guarded-raid" ? 2 : 1), "Recovery duplicated expedition events.");
            if (original.Raid?.Guard is { } guard) Check(State.MercenaryGuards.FindByIdempotencyKey(guard.IdempotencyKey)!.ConsumedRaidEventId == main.EventId,
                "Recovery lost original guard consumption.");
        }
        public void ChangeDecision(string change)
        {
            if (change == "source") Store.Inner.StoreLatest(Package with { Envelope = Package.Envelope with
                { Identity = new("userA", "colonyA", "newer"), NextLineageToken = "newer" } }, Package.Index, DateTimeOffset.UtcNow);
            else if (change == "target-remove") Store.Inner.RemoveLatest("userB", "colonyB");
            else if (change is "target-move" or "baseline")
            {
                var old = Store.Inner.GetLatest("userB", "colonyB")!;
                Store.Inner.StoreLatest(old with { Identity = old.Identity with { SnapshotId = "changed" },
                    Envelope = old.Envelope with { Identity = old.Identity with { SnapshotId = "changed" } },
                    Index = old.Index with { Maps = old.Index.Maps.Select(m => m with { UniqueId = "Map_new" }).ToArray() } });
            }
            else if (change == "pvp") State.UpdateServerConfiguration(new(pvpEnabled: false));
            else if (change == "peace") State.DiplomacyRelations.SetRelationKind("userA", "colonyA", "userB", "colonyB", DiplomacyRelationRegistry.RelationNeutral, null, DateTimeOffset.UtcNow);
            else if (change == "guard")
            {
                if (Kind == "guarded-raid") State.MercenaryGuards.ConsumeForRaid("userB", "colonyB", "other-raid", "guard", DateTimeOffset.UtcNow);
                State.MercenaryGuards.ActivateWithSnapshot("replacement", "userB", "colonyB", "replacement", "defender", "Apprentice", 600, 0.35f, 1000, DateTimeOffset.UtcNow);
            }
        }
        public string Evidence() => JsonSerializer.Serialize(new { snapshots = Store.ListLatest(), events = State.Ledger.ListAll(),
            guard = State.MercenaryGuards.FindByIdempotencyKey("guard"), replacement = State.MercenaryGuards.FindByIdempotencyKey("replacement"),
            a = State.EventNotifications.GetVersion("userA"), b = State.EventNotifications.GetVersion("userB"),
            journal = Directory.GetFiles(Journal, "*.json").Order().Select(File.ReadAllText).ToArray() });
        public void AssertReleased() => Task.Run(() =>
        {
            Check(Monitor.TryEnter(Gate, TimeSpan.FromSeconds(3)), "Expedition request leaked mutation gate."); Monitor.Exit(Gate);
        }).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
    }
}
