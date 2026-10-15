using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Network.Plugins;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;

internal static partial class RaidEvidenceAuthorizationTests
{
    private static void RunOnline(Func<string, string, SaveSnapshotPackage> fixture)
    {
        foreach (string phase in new[] { "defender", "artifact", "replay", "prepared" })
        foreach (string change in new[] { "none", "logout", "replace", "revoke", "cancel", "source", "attacker", "defender", "artifact", "duplicate" })
            WithFixture(fixture, f => VerifySchedulePreparation(f, phase, change));
        foreach (string change in new[] { "review", "remove", "artifact-delete", "artifact-change", "result", "identity", "hash" })
            WithFixture(fixture, f => VerifyReplayConflict(f, change));
        foreach (bool reopen in new[] { false, true })
        foreach (string failure in new[] { "before", "after", "before-plugin", "after-plugin", "ready", "enqueue", "player" })
            WithFixture(fixture, f => VerifyHttpRecovery(f, failure, reopen), persistent: true);
        WithFixture(fixture, VerifyLegacyReplay, legacyArtifacts: true);
        foreach (bool distinct in new[] { false, true })
            WithFixture(fixture, f => VerifyConcurrentHttp(f, distinct));
    }

    private static void VerifySchedulePreparation(Fixture f, string phase, string change)
    {
        bool replay = phase is "replay" or "prepared";
        bool pending = phase == "prepared";
        if (pending) f.Store.Fault = "before";
        ConfirmEventApplicationResponse? first = replay ? f.Invoke() : null, reply = null;
        Require(!replay || first!.Result.Accepted != pending, "Could not seed replay.");
        f.Store.Fault = null;
        if (pending)
        {
            var original = RaidSettlementDeferredPayload.Deserialize(f.State.SnapshotPostUploadJobs.ListAll().Single().PayloadJson).Publication!.Evidence;
            first = new ConfirmEventApplicationResponse(first!.Result, f.Raid.EventId, original.Identity.SnapshotId, "Prepared", original.NextLineageToken);
        }
        int callbacks = 0;
        f.BeforeSchedule = _ => callbacks++;
        string? expected = null;
        bool competing = false;
        SnapshotPreparationGateProbe.Run(f.State.RaidSettlementSnapshotMutationGate,
            hook => { if (phase == "defender") f.Store.BeforeRead = hook; else if (replay) f.ArtifactStore.AfterRead = hook; else f.ArtifactStore.AfterPrepare = hook; },
            () => reply = f.Invoke(), () =>
            {
                Require(callbacks == 0, "Postprocessors ran before schedule preparation was validated.");
                Require(f.State.SnapshotPostUploadJobs.ListAll().Count == (replay ? 1 : 0)
                    && f.Store.PreparedWrites == (replay && !pending ? 1 : 0), "Scheduling published before preparation was validated.");
                if (change is "logout" or "replace") f.State.LoginSessions.EndUser("userA");
                if (change == "replace") f.State.LoginSessions.TryCreate("userA", "colonyA", DateTimeOffset.UtcNow, out _);
                if (change == "revoke") f.State.AuthTokens.RevokeForUser("userA");
                if (change == "cancel") f.State.Ledger.ChangeStatus(f.Raid.EventId, ServerEventStatus.Cancelled);
                if (change == "source")
                {
                    var source = f.State.Ledger.Find(f.Raid.EventId)!;
                    var payload = (RaidEventPayload)source.Payload;
                    f.State.Ledger.ApplyBatch(new LedgerBatch([], [new LedgerStatusChange(source.EventId, source.Status, source.Status,
                        payload, payload with { StartedAtUtc = payload.StartedAtUtc!.Value.AddMinutes(1) })]));
                }
                if (change is "attacker" or "defender")
                {
                    string user = change == "attacker" ? "userA" : "userB", colony = change == "attacker" ? "colonyA" : "colonyB";
                    var package = f.Store.Inner.GetLatestPackage(user, colony) ?? f.Package;
                    package = package with { Envelope = package.Envelope with { NextLineageToken = "concurrent-token" } };
                    f.Store.Inner.StoreLatest(package, package.Index, DateTimeOffset.UtcNow);
                }
                if (change == "artifact")
                {
                    string id = replay ? RaidSettlementDeferredPayload.Deserialize(f.State.SnapshotPostUploadJobs.ListAll().Single().PayloadJson).EvidenceArtifactId
                        : RaidSettlementDeferredScheduler.BuildArtifactId(f.Raid.EventId, f.Package.Envelope.Identity.SnapshotId!);
                    var original = f.ArtifactStore.Inner.Read(id);
                    f.ArtifactStore.Inner.Delete(id);
                    if (original is not null) f.ArtifactStore.Inner.Store(id, original);
                }
                if (change == "duplicate")
                {
                    // Use the existing synchronous scheduler: a paused request may occupy the only preparation slot on CI.
                    var duplicate = ScheduleCompetitor(f);
                    Require(duplicate.Result.Accepted, "Competing request did not commit.");
                    first = duplicate; competing = true;
                }
                expected = f.StateEvidence();
            });
        bool accepted = change is "none" or "artifact" or "duplicate" || replay && change == "defender" || replay && !pending && change == "attacker";
        Require(reply!.Result.Accepted == accepted, "Online preparation result mismatch: " + phase + "/" + change);
        if (accepted)
        {
            bool legacyPublication = competing && (!replay || pending);
            Require(f.State.SnapshotPostUploadJobs.ListAll().Count == 1 && f.Store.PreparedWrites == (legacyPublication ? 0 : 1)
                && f.Store.RawWrites == (legacyPublication ? 1 : 0), "Online evidence did not publish exactly once with prepared bytes.");
            Require(callbacks == 1, "Stale artifact preparation ran postprocessors before recapture.");
            if (first is not null) Require(reply.NextLineageToken == first.NextLineageToken && reply.Result.ErrorCode == ProtocolErrorCode.DuplicateRequest,
                "Replay did not return the original acknowledgement.");
            if (replay && !pending) Require(f.StateEvidence() == expected, "Ready replay rewrote persistent state.");
        }
        else Require(reply.NextLineageToken is null && reply.AppliedSnapshotId is null && callbacks == 0 && f.StateEvidence() == expected,
            "Rejected online preparation mutated state or disclosed credentials: " + phase + "/" + change);
        Require(!Directory.EnumerateFiles(f.Artifacts, "*.tmp").Any(), "Online preparation leaked temporary files.");
    }

    private static ConfirmEventApplicationResponse ScheduleCompetitor(Fixture f)
    {
        var existing = f.State.SnapshotPostUploadJobs.ListAll().SingleOrDefault();
        LatestSnapshotRecord snapshot;
        if (existing is null)
        {
            var receiver = new SnapshotUploadReceiver(f.Store);
            var package = ProtocolDtoMapper.ToSaveSnapshotPackage(ProtocolDtoMapper.ToMetadataDto(f.Package), f.Package.Payload);
            var upload = receiver.Receive(new("userA", "colonyA", "evidence", SnapshotUploadKind: SnapshotUploadKinds.RaidSettlementEvidence,
                RequiredRaidEventId: f.Raid.EventId), package, DateTimeOffset.UtcNow, storeAccepted: false);
            Require(upload.Accepted, "Could not prepare synchronous competitor.");
            snapshot = upload.AcceptedSnapshot!;
        }
        else
        {
            var original = f.ArtifactStore.Inner.Read(RaidSettlementDeferredPayload.Deserialize(existing.PayloadJson).EvidenceArtifactId)!;
            snapshot = new(original.Envelope.Identity, original.Envelope, original.Index, existing.OccurredAtUtc);
        }
        var context = new SnapshotPostUploadContext(f.State, SnapshotPostUploadKind.RaidSettlementEvidence, snapshot,
            "userA", "colonyA", null, snapshot.AcceptedAtUtc, SnapshotPostUploadExtraData.Empty, false);
        RaidSettlementDeferredScheduler.Schedule(f.State, new(f.Raid.EventId, context, f.Package.Payload, RaidSettlementOrigin.OnlineEvidence, "Applied"));
        return new(ProtocolResponse.Ok("queued"), f.Raid.EventId, snapshot.Identity.SnapshotId, "SettlementQueued", snapshot.Envelope.NextLineageToken);
    }

    private static void VerifyReplayConflict(Fixture f, string change)
    {
        Require(f.Invoke().Result.Accepted, "Could not seed conflict test.");
        var job = f.State.SnapshotPostUploadJobs.ListAll().Single();
        var payload = RaidSettlementDeferredPayload.Deserialize(job.PayloadJson);
        var package = f.Package;
        string result = "Applied";
        if (change == "result") result = "Different";
        if (change == "identity") package = package with { Envelope = package.Envelope with { Identity = package.Envelope.Identity with { SnapshotId = "different" } } };
        if (change == "hash") package = package with { Payload = [1, 2, 3] };
        if (change is "artifact-delete" or "artifact-change")
        {
            var original = f.ArtifactStore.Inner.Read(payload.EvidenceArtifactId)!;
            f.ArtifactStore.Inner.Delete(payload.EvidenceArtifactId);
            if (change == "artifact-change") f.ArtifactStore.Inner.Store(payload.EvidenceArtifactId,
                original with { Envelope = original.Envelope with { NextLineageToken = "tampered" } });
        }
        int calls = 0;
        f.BeforeSchedule = _ => calls++;
        ConfirmEventApplicationResponse? reply = null;
        string before;
        if (change is "review" or "remove")
        {
            before = "";
            SnapshotPreparationGateProbe.Run(f.State.RaidSettlementSnapshotMutationGate, hook => f.ArtifactStore.AfterRead = hook,
                () => reply = f.Invoke(), () =>
                {
                    if (change == "review") f.State.SnapshotPostUploadJobs.MarkManualReview(job.JobId, "review");
                    else f.State.SnapshotPostUploadJobs.MarkCompleted(job.JobId);
                    before = f.StateEvidence();
                });
        }
        else { before = f.StateEvidence(); reply = f.Invoke(package: package, result: result); }
        Require(!reply!.Result.Accepted && reply.NextLineageToken is null && calls == 0 && before == f.StateEvidence(),
            "Conflicting evidence retry changed durable state: " + change);
    }

    private static void VerifyHttpRecovery(Fixture f, string failure, bool reopen)
    {
        var observations = new List<(string Stage, string? Token, DateTimeOffset At)>();
        bool fail = true;
        f.BeforeSchedule = context =>
        {
            observations.Add(("before", context.Snapshot.Envelope.NextLineageToken, context.OccurredAtUtc));
            if (fail && failure == "before-plugin") throw new IOException("before plugin");
        };
        f.AfterSchedule = context =>
        {
            observations.Add(("after", context.Snapshot.Envelope.NextLineageToken, context.OccurredAtUtc));
            if (fail && failure == "after-plugin") throw new IOException("after plugin");
        };
        f.Store.Fault = failure;
        if (failure is "ready" or "enqueue") f.Sql("create trigger fail_job before insert on server_snapshot_post_upload_jobs "
            + (failure == "ready" ? "when NEW.job_state = 1 " : "") + "begin select raise(abort, 'job failure'); end;");
        if (failure == "player") f.Sql("create trigger fail_player before insert on server_players begin select raise(abort, 'player failure'); end;");
        Require(!f.Invoke().Result.Accepted, "Fault did not interrupt HTTP confirmation: " + failure);
        var job = f.State.SnapshotPostUploadJobs.ListAll().SingleOrDefault();
        Require((job is null) == (failure is "before-plugin" or "enqueue"), "Failure lost durable publication intent.");
        string? originalToken = job is null ? null : RaidSettlementDeferredPayload.Deserialize(job.PayloadJson).Publication!.Evidence.NextLineageToken;
        f.Store.Fault = null; fail = false;
        if (failure is "ready" or "enqueue") f.Sql("drop trigger fail_job;");
        if (failure == "player") f.Sql("drop trigger fail_player;");
        int priorWrites = reopen ? f.Store.PreparedWrites : 0;
        if (reopen) f.Open();
        var reply = f.Invoke();
        Require(reply.Result.Accepted && f.State.SnapshotPostUploadJobs.ListAll().Single().State == SnapshotPostUploadJobState.Ready
            && f.Store.PreparedWrites + priorWrites == 1 && f.Store.RawWrites == 0, "HTTP retry did not recover the prepared evidence: " + failure);
        if (job is not null) Require(reply.NextLineageToken == originalToken && observations.TakeLast(2).All(o => o.Token == originalToken && o.At == job.OccurredAtUtc),
            "Recovered pipeline used a new acknowledgement or timestamp.");
        Require(observations.TakeLast(2).Select(o => o.Stage).SequenceEqual(new[] { "before", "after" }), "Retry skipped or reordered postprocessors.");
        string before = f.StateEvidence();
        Require(f.Invoke().NextLineageToken == reply.NextLineageToken && before == f.StateEvidence(), "Repeated recovery rewrote durable state.");
    }

    private static void VerifyConcurrentHttp(Fixture f, bool distinct)
    {
        using var start = new ManualResetEventSlim();
        var tasks = Enumerable.Range(0, 12).Select(i => Task.Run(() =>
        {
            start.Wait();
            var package = distinct ? f.Package with { Envelope = f.Package.Envelope with { Identity = f.Package.Envelope.Identity with { SnapshotId = "candidate-" + i } } } : f.Package;
            return f.Invoke(package: package);
        })).ToArray();
        start.Set();
        var replies = Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(40)).GetAwaiter().GetResult();
        var accepted = replies.Where(r => r.Result.Accepted).ToArray();
        Require(accepted.Length == (distinct ? 1 : 12) && accepted.Select(r => r.NextLineageToken).Distinct().Count() == 1
            && f.State.SnapshotPostUploadJobs.ListAll().Count == 1 && f.Store.PreparedWrites == 1 && f.Store.RawWrites == 0,
            "Concurrent HTTP requests did not converge on one publication: " + distinct);
    }

    private static void VerifyLegacyReplay(Fixture f)
    {
        bool fail = true;
        var order = new List<string>();
        f.BeforeSchedule = _ => order.Add("before");
        f.AfterSchedule = _ => { order.Add("after"); if (fail) throw new IOException("after legacy scheduler"); };
        Require(!f.Invoke().Result.Accepted, "Legacy plugin failure was ignored.");
        var job = f.State.SnapshotPostUploadJobs.ListAll().Single();
        var original = RaidSettlementDeferredPayload.Deserialize(job.PayloadJson).Publication!.Evidence;
        fail = false;
        string before = f.StateEvidence();
        var reply = f.Invoke();
        Require(reply.Result.Accepted && reply.Result.ErrorCode == ProtocolErrorCode.DuplicateRequest && reply.NextLineageToken == original.NextLineageToken
            && before == f.StateEvidence() && order.SequenceEqual(new[] { "before", "after", "before", "after" }), "Legacy replay changed its result or pipeline order.");
        order.Clear();
        Require(!f.Invoke(result: "changed").Result.Accepted && order.Count == 0 && before == f.StateEvidence(), "Legacy conflicting replay ran postprocessors.");
    }

    private sealed class LegacyArtifacts(ISnapshotPostUploadArtifactStore inner) : ISnapshotPostUploadArtifactStore
    {
        public void Store(string id, SaveSnapshotPackage package) => inner.Store(id, package);
        public SaveSnapshotPackage? Read(string id) => inner.Read(id);
        public bool Exists(string id) => inner.Exists(id);
        public void Delete(string id) => inner.Delete(id);
        public void DeleteUnreferenced(IReadOnlySet<string> ids) => inner.DeleteUnreferenced(ids);
    }

    private sealed class EvidenceProcessor(int order, Action<SnapshotPostUploadContext> process) : IInlineSnapshotPostUploadProcessor
    {
        public string Id => "test.evidence." + order;
        public int Order => order;
        public SnapshotPostUploadStage Stage => SnapshotPostUploadStage.EventReconciliation;
        public SnapshotPostUploadFailureMode FailureMode => SnapshotPostUploadFailureMode.AbortPipeline;
        public SnapshotPostUploadExecutionMode ExecutionMode => SnapshotPostUploadExecutionMode.Inline;
        public bool Supports(SnapshotPostUploadKind kind) => kind == SnapshotPostUploadKind.RaidSettlementEvidence;
        public void Process(SnapshotPostUploadContext context) => process(context);
    }

    private sealed class ObservedArtifacts(string root) : IPreparedSnapshotPostUploadArtifactStore, IVersionedSnapshotPostUploadArtifactStore
    {
        public FileSnapshotPostUploadArtifactStore Inner { get; } = new(root);
        public int ActiveHandles => ((System.Collections.IDictionary)typeof(FileSnapshotPostUploadArtifactStore)
            .GetField("activePreparations", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(Inner)!).Count;
        public Action? AfterPrepare, AfterRead;
        public IPreparedSnapshotPostUploadArtifact Prepare(string id, SaveSnapshotPackage package)
        {
            var handle = Inner.Prepare(id, package);
            try { AfterPrepare?.Invoke(); return handle; } catch { handle.Dispose(); throw; }
        }
        public ISnapshotPostUploadArtifactRead ReadCaptured(string id)
        {
            var handle = Inner.ReadCaptured(id);
            try { AfterRead?.Invoke(); return handle; } catch { handle.Dispose(); throw; }
        }
        public void Store(string id, SaveSnapshotPackage package) => Inner.Store(id, package);
        public SaveSnapshotPackage? Read(string id) => Inner.Read(id);
        public bool Exists(string id) => Inner.Exists(id);
        public void Delete(string id) => Inner.Delete(id);
        public void DeleteUnreferenced(IReadOnlySet<string> ids) => Inner.DeleteUnreferenced(ids);
    }
}
