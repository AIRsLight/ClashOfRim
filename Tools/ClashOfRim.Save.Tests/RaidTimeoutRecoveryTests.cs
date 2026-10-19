using AIRsLight.ClashOfRim.Network.Modules.Raids;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Network.Plugins;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using Microsoft.Data.Sqlite;
using static SnapshotTestAssertions;

internal static class RaidTimeoutRecoveryTests
{
    public static void Run(SaveSnapshotPackage original)
    {
        SnapshotEndpointTestClient.LoadLocalization();
        foreach (bool offline in new[] { false, true })
        {
            foreach (bool recovery in new[] { false, true })
            foreach (string change in new[] { "none", "newer", "token", "removed", "source", "owner", "new-raid", "job-review", "job-removed" }.Where(change => offline || !change.StartsWith("job-")))
                RaidTimeoutFixture.With(original, f => VerifyPreparation(f, original, offline, recovery, change));
            RaidTimeoutFixture.With(original, f => VerifyConcurrent(f, offline));
        }
        foreach (bool recovery in new[] { false, true })
        foreach (string phase in new[] { "read", "artifact" })
        foreach (string change in new[] { "none", "newer", "token", "defender", "defender-token", "removed", "source", "owner", "new-raid", "job-review", "job-prepared", "competing", "artifact-deleted" })
            RaidTimeoutFixture.With(original, f => VerifyOfflineEvidencePreparation(f, original, recovery, phase, change));
        foreach (bool recovery in new[] { false, true })
        foreach (string change in new[] { "missing", "restored" })
            RaidTimeoutFixture.With(original, f => VerifyOfflineEvidencePreparation(f, original, recovery, "read", change));
        foreach (bool revoke in new[] { false, true }) RaidTimeoutFixture.With(original, f => VerifyRaidEntry(f, original, revoke));
        foreach (bool changedSnapshot in new[] { false, true }) RaidTimeoutFixture.With(original, f => VerifyStaleCompletion(f, original, changedSnapshot));
        foreach (bool offline in new[] { false, true })
        foreach (bool reopen in new[] { false, true })
        foreach (string fault in new[] { "notification", "mark", "before", "after", "loss", "ack", "schedule", "settled" })
        {
            if (!offline && fault is "schedule" or "settled") continue;
            RaidTimeoutFixture.With(original, f => Recovery(f, offline, reopen, fault));
        }
        foreach (string kind in new[] { "memory", "file", "sqlite" }) RaidTimeoutFixture.With(original, f => VerifyBatch(f, kind));
        RaidTimeoutFixture.With(original, VerifyBackgroundRecovery);
    }

    private static void VerifyOfflineEvidencePreparation(RaidTimeoutFixture f, SaveSnapshotPackage original, bool recovery, string phase, string change)
    {
        var defense = f.Files.GetLatestPackage("defender", "defender-colony")!;
        if (change is "missing" or "restored") f.Files.RemoveLatest("defender", "defender-colony");
        if (recovery) RaidTimeoutProcessor.ProcessExpiredRaids(f.State.Ledger, f.State.Ledger.ListAll(), f.Now, isAttackerOnline: _ => false);
        SnapshotPreparationGateProbe.Run(f.State.RaidSettlementSnapshotMutationGate,
            hook => { if (phase == "read") f.Store.BeforeRead = owner => { if (owner == "attacker") hook?.Invoke(); }; else f.Artifacts.AfterPrepare = hook; },
            () => { if (recovery) f.State.RaidTimeouts.RecoverPending(f.Now); else f.Reconcile(); }, () =>
            {
                Check(f.State.SnapshotPostUploadJobs.ListAll().Count == 0 && f.Store.PreparedWriteAttempts == 0
                    && ((RaidEventPayload)f.State.Ledger.Find(f.Raid.EventId)!.Payload).TimeoutWork is { Completed: false, AttackerWasOffline: true },
                    "Offline evidence preparation published a job or cleanup before classification/preparation finished.");
                if (change is "newer" or "token")
                {
                    var envelope = change == "newer" ? original.Envelope with { Identity = original.Envelope.Identity with { SnapshotId = "newer" } }
                        : original.Envelope with { NextLineageToken = "replacement-token" };
                    f.Files.StoreLatest(original with { Envelope = envelope }, original.Index, f.Now);
                }
                if (change == "restored") f.Files.StoreLatest(defense, defense.Index, f.Now);
                if (change is "defender" or "defender-token")
                {
                    var package = f.Files.GetLatestPackage("defender", "defender-colony")!;
                    var envelope = change == "defender" ? package.Envelope with { Identity = package.Envelope.Identity with { SnapshotId = "new-defense" } }
                        : package.Envelope with { NextLineageToken = "new-defense-token" };
                    f.Files.StoreLatest(package with { Envelope = envelope }, package.Index, f.Now);
                }
                if (change == "removed") f.Files.RemoveLatest("attacker", "colony-a");
                if (change == "source") f.State.Ledger.ChangeStatus(f.Raid.EventId, ServerEventStatus.Cancelled);
                if (change == "owner") f.Sql("update events set actor_user_id = 'other' where event_id = 'raid-timeout-001';");
                if (change == "new-raid") f.State.Ledger.Append(f.Raid with { EventId = "new-raid", IdempotencyKey = "new-raid",
                    Payload = ((RaidEventPayload)f.Raid.Payload) with { StartedAtUtc = f.Now, AttackForce = new RaidAttackForceRecord("new", [], []) } });
                if (change is "job-review" or "job-prepared" or "competing")
                {
                    var package = f.Files.GetLatestPackage("attacker", "colony-a")!;
                    var context = new SnapshotPostUploadContext(f.State, SnapshotPostUploadKind.RaidSettlementEvidence,
                        new LatestSnapshotRecord(package.Envelope.Identity, package.Envelope, package.Index, f.Now),
                        "attacker", "colony-a", null, f.Now, SnapshotPostUploadExtraData.Empty, false);
                    var request = new RaidSettlementScheduleRequest(f.Raid.EventId, context, package.Payload, RaidSettlementOrigin.OfflineTimeout, "OfflineTimeout");
                    if (change == "job-prepared")
                    {
                        f.Sql("create trigger fail_ready before insert on server_snapshot_post_upload_jobs when NEW.job_state = 1 begin select raise(abort, 'ready'); end;");
                        Fails(() => RaidSettlementDeferredScheduler.Schedule(f.State, request), "Expected competing Prepared intent.");
                        f.Sql("drop trigger fail_ready;");
                    }
                    else
                    {
                        var job = RaidSettlementDeferredScheduler.Schedule(f.State, request);
                        if (change == "job-review") f.State.SnapshotPostUploadJobs.MarkManualReview(job.JobId, "review during preparation");
                    }
                }
                if (change == "artifact-deleted") f.Artifacts.Delete(RaidEvidenceRules.BuildArtifactId(f.Raid.EventId, original.Envelope.Identity.SnapshotId!));
            });
        if (change is "missing" or "restored")
            Check((f.State.Ledger.Find(f.Raid.EventId)!.LastApplicationResult == EventApplicationResultKind.NeedsManualReview) == (change == "missing"),
                "Missing-snapshot decision ignored a concurrently restored version.");
        bool committed = change is "none" or "competing" || (change == "artifact-deleted" && phase == "read");
        var source = f.State.Ledger.Find(f.Raid.EventId)!;
        Check(f.Store.PreparedWriteAttempts == (committed ? 1 : 0)
            && ((RaidEventPayload)source.Payload).TimeoutWork!.Completed == committed,
            "Stale offline evidence was published or acknowledged: " + phase + "/" + change);
        var jobs = f.State.SnapshotPostUploadJobs.ListAll();
        Check(jobs.Count == (committed || change is "job-review" or "job-prepared" ? 1 : 0)
            && (change != "job-review" || jobs.Single().State == SnapshotPostUploadJobState.ManualReview),
            "Offline preparation replaced a competing/manual-review job: " + phase + "/" + change);
        if (committed)
        {
            var payload = RaidSettlementDeferredPayload.Deserialize(jobs.Single().PayloadJson);
            Check(f.Artifacts.Read(payload.EvidenceArtifactId)!.Payload.SequenceEqual(original.Payload), "Offline evidence was not retained before cleanup.");
        }
        else Check(f.Files.GetLatest("attacker", "colony-a")?.Identity.SnapshotId ==
            (change == "removed" ? null : change == "newer" ? "newer" : original.Envelope.Identity.SnapshotId), "Stale scheduling changed the attacker snapshot.");
        Check(!Directory.EnumerateFiles(Path.Combine(f.Root, "artifacts"), "*.tmp").Any(), "Offline preparation leaked temporary files.");
        if (change == "job-prepared" || (change == "artifact-deleted" && phase == "artifact"))
        {
            f.Reconcile();
            Check(f.Store.PreparedWriteAttempts == 1 && RaidTimeoutProcessor.PendingWork(f.State.Ledger).Count == 0
                && f.State.SnapshotPostUploadJobs.ListReady(f.Now).Count == 1,
                "Discarded preparation or competing Prepared job could not resume on the next scan.");
        }
    }

    private static void VerifyPreparation(RaidTimeoutFixture f, SaveSnapshotPackage original, bool offline, bool recovery, string change)
    {
        f.Store.BeforePublication = prepared =>
        {
            if (prepared) Check(Monitor.IsEntered(f.State.SnapshotPostUploadJobs.SnapshotCommitGate),
                "New timeout publication did not serialize job state changes.");
        };
        if (!offline) f.State.OnlinePresence.TryConnectExclusive("attacker", out _);
        if (recovery) RaidTimeoutProcessor.ProcessExpiredRaids(f.State.Ledger, f.State.Ledger.ListAll(), f.Now, isAttackerOnline: _ => !offline);
        SnapshotPreparationGateProbe.Run(f.State.RaidSettlementSnapshotMutationGate, hook => f.Store.BeforePrepare = hook,
            () => { if (recovery) f.State.RaidTimeouts.RecoverPending(f.Now); else f.Reconcile(); }, () =>
            {
                Check(f.Store.PreparedWriteAttempts == 0 && ((RaidEventPayload)f.State.Ledger.Find(f.Raid.EventId)!.Payload).TimeoutWork is { Completed: false } work
                    && work.AttackerWasOffline == offline && f.State.Ledger.ListAll().Count == 3, "Timeout classification was not durable before preparation.");
                var job = f.State.SnapshotPostUploadJobs.ListAll().SingleOrDefault();
                Check((job is not null) == offline, "Offline cleanup ran before evidence scheduling, or online cleanup scheduled evidence.");
                if (job is not null)
                {
                    var payload = RaidSettlementDeferredPayload.Deserialize(job.PayloadJson);
                    Check(job.State == SnapshotPostUploadJobState.Ready
                        && f.State.SnapshotPostUploadArtifacts.Read(payload.EvidenceArtifactId)!.Payload.SequenceEqual(original.Payload),
                        "Offline cleanup lost its original evidence.");
                }
                if (change is "newer" or "token")
                {
                    var envelope = change == "newer" ? original.Envelope with { Identity = original.Envelope.Identity with { SnapshotId = "newer" } }
                        : original.Envelope with { NextLineageToken = "replacement-token" };
                    f.Files.StoreLatest(original with { Envelope = envelope }, original.Index, f.Now);
                }
                if (change == "job-review") f.State.SnapshotPostUploadJobs.MarkManualReview(job!.JobId, "review during preparation");
                if (change == "job-removed") f.State.SnapshotPostUploadJobs.MarkCompleted(job!.JobId);
                if (change == "removed") f.Files.RemoveLatest("attacker", "colony-a");
                if (change == "source") f.State.Ledger.ChangeStatus(f.Raid.EventId, ServerEventStatus.Cancelled);
                if (change == "owner") f.Sql("update events set actor_user_id = 'other' where event_id = 'raid-timeout-001';");
                if (change == "new-raid") f.State.Ledger.Append(f.Raid with { EventId = "new-raid", IdempotencyKey = "new-raid",
                    Payload = ((RaidEventPayload)f.Raid.Payload) with { StartedAtUtc = f.Now, AttackForce = new RaidAttackForceRecord("new", [], []) } });
            });
        var source = f.State.Ledger.Find(f.Raid.EventId)!;
        Check(f.Store.Preparations == 1 && f.Store.PreparedWriteAttempts == (change == "none" ? 1 : 0)
            && ((RaidEventPayload)source.Payload).TimeoutWork!.Completed == (change == "none")
            && f.State.Ledger.ListAll().Count == (change is "none" or "new-raid" ? 4 : 3)
            && !Directory.EnumerateFiles(f.Journal, "*.json").Any(), "Timeout committed or acknowledged stale preparation: " + change);
        if (change != "none") Check(f.Files.GetLatest("attacker", "colony-a")?.Identity.SnapshotId ==
            (change == "removed" ? null : change == "newer" ? "newer" : original.Envelope.Identity.SnapshotId)
            && (change != "token" || f.Files.GetLatest("attacker", "colony-a")!.Envelope.NextLineageToken == "replacement-token"),
            "Timeout overwrote a competing snapshot.");
    }

    private static void VerifyConcurrent(RaidTimeoutFixture f, bool offline)
    {
        if (!offline) f.State.OnlinePresence.TryConnectExclusive("attacker", out _);
        Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(f.Reconcile))).GetAwaiter().GetResult();
        Check(f.Store.PreparedWriteAttempts == 1 && f.State.Ledger.ListAll().Count == 4
            && RaidTimeoutProcessor.PendingWork(f.State.Ledger).Count == 0
            && f.State.SnapshotPostUploadJobs.ListAll().Count == (offline ? 1 : 0), "Concurrent timeouts duplicated cleanup, evidence or notifications.");
    }

    private static void VerifyRaidEntry(RaidTimeoutFixture f, SaveSnapshotPackage original, bool revoke)
    {
        f.State.OnlinePresence.TryConnectExclusive("attacker", out _);
        EventCreationResponse? reply = null;
        SnapshotPreparationGateProbe.Run(f.State.RaidSettlementSnapshotMutationGate, hook => f.Store.BeforePrepare = hook,
            () => reply = RaidCreationTestClient.Invoke(f.State, original, false).GetAwaiter().GetResult(),
            () => { if (revoke) f.State.LoginSessions.EndUser("userA"); });
        Check(f.Store.PreparedWriteAttempts == 1 && RaidTimeoutProcessor.PendingWork(f.State.Ledger).Count == 0
            && reply is { Result.Accepted: false } && (!revoke || reply.Result.ErrorCode == ProtocolErrorCode.SessionUnavailable)
            && f.State.Ledger.ListAll().Count == 4, "Raid entry held an outer lock or failed to reauthorize after timeout cleanup.");
    }

    private static void VerifyStaleCompletion(RaidTimeoutFixture f, SaveSnapshotPackage original, bool changedSnapshot)
    {
        // A second missing colony pauses the batch after the first cleanup committed but before acknowledgement.
        var later = f.Raid with { EventId = "later", IdempotencyKey = "later", Actor = new EventParty("later", "later-colony"), CreatedAtUtc = f.Raid.CreatedAtUtc.AddSeconds(1) };
        f.State.Ledger.Append(later);
        RaidTimeoutProcessor.ProcessExpiredRaids(f.State.Ledger, f.State.Ledger.ListAll(), f.Now, isAttackerOnline: _ => true);
        SnapshotPreparationGateProbe.Run(f.State.RaidSettlementSnapshotMutationGate,
            hook => f.Store.BeforeRead = owner => { if (owner == "later") hook?.Invoke(); },
            () => f.State.RaidTimeouts.RecoverPending(f.Now), () =>
            {
                Check(f.Store.PreparedWriteAttempts == 1, "Completion probe ran before the first cleanup.");
                if (changedSnapshot)
                {
                    f.Files.StoreLatest(original with { Envelope = original.Envelope with { Identity = original.Envelope.Identity with { SnapshotId = "later-upload" } } }, original.Index, f.Now);
                    return;
                }
                var raid = f.State.Ledger.Find(f.Raid.EventId)!;
                var payload = (RaidEventPayload)raid.Payload;
                f.State.Ledger.ApplyBatch(new LedgerBatch([], [new LedgerStatusChange(raid.EventId, raid.Status, raid.Status,
                    payload, payload with { TimeoutWork = payload.TimeoutWork! with { TimedOutAtUtc = f.Now.AddMinutes(1) } })]));
            });
        Check(((RaidEventPayload)f.State.Ledger.Find(f.Raid.EventId)!.Payload).TimeoutWork is { Completed: false }
            && f.Store.PreparedWriteAttempts == 1, "An old cleanup acknowledged a changed timeout marker.");
    }

    private static void Recovery(RaidTimeoutFixture f, bool offline, bool reopen, string fault)
    {
        if (!offline) Check(f.State.OnlinePresence.TryConnectExclusive("attacker", out _), "Could not seed online attacker.");
        if (fault == "notification") f.Sql("create trigger fail_write before insert on events when NEW.target_user_id = 'defender' and NEW.type = 'ServerNotification' begin select raise(abort, 'notification'); end;");
        if (fault == "mark") f.Sql("create trigger fail_write before update on events begin select raise(abort, 'timeout marker'); end;");
        if (fault == "schedule") f.Sql("create trigger fail_write before insert on server_snapshot_post_upload_jobs begin select raise(abort, 'job'); end;");
        if (fault == "loss") f.Sql("create trigger fail_write before insert on events when NEW.idempotency_key like 'raid-attacker-loss:%' begin select raise(abort, 'loss'); end;");
        if (fault is "ack" or "settled") f.Sql("create trigger fail_write before update on events when json_extract(NEW.payload_json, '$.TimeoutWork.Completed') = 1 begin select raise(abort, 'ack'); end;");
        f.Store.FailPublication = fault == "before"; f.Store.FailAfterPublication = fault == "after";
        Fails(f.Reconcile, "Expected timeout processing interruption.");
        var source = f.State.Ledger.Find(f.Raid.EventId)!;
        if (fault is "notification" or "mark")
            Check(source.Status == f.Raid.Status && ((RaidEventPayload)source.Payload).TimeoutWork is null
                && f.State.Ledger.ListAll().Count == 1, "Timeout notification failure leaked status, marker or a partial pair.");
        else
            Check(((RaidEventPayload)source.Payload).TimeoutWork is { Completed: false } work
                && work.AttackerWasOffline == offline && work.TimedOutAtUtc == f.Now
                && f.State.Ledger.ListAll().Count(item => item.Type == ServerEventType.ServerNotification) == 2,
                "Interrupted downstream work lost its durable timeout classification.");
        if (fault is "notification" or "mark" or "schedule" or "loss" or "ack" or "settled") f.Sql("drop trigger fail_write;");
        f.Store.FailPublication = f.Store.FailAfterPublication = false;
        if (fault == "settled")
        {
            Check(SnapshotPostUploadJobExecutor.ProcessReady(f.State, [new RaidSettlementPostUploadProcessor()], f.Now.AddMinutes(1)) == 1,
                "Could not complete settlement before timeout acknowledgement retry.");
            Check(f.State.Ledger.Find(f.Raid.EventId)!.Status == ServerEventStatus.AppliedToSnapshot, "Settlement did not advance source.");
        }
        if (reopen) f.Open();
        // Reverse current presence to prove retry uses the persisted decision, not the new login state.
        bool timeoutCommitted = fault is not ("notification" or "mark");
        if (offline && timeoutCommitted) f.State.OnlinePresence.TryConnectExclusive("attacker", out _);
        else if (!offline && reopen && !timeoutCommitted) f.State.OnlinePresence.TryConnectExclusive("attacker", out _);
        if (!timeoutCommitted) f.Reconcile();
        else f.State.RaidTimeouts.RecoverPending(f.Now);
        f.Reconcile();
        source = f.State.Ledger.Find(f.Raid.EventId)!;
        Check(((RaidEventPayload)source.Payload).TimeoutWork is { Completed: true } completed
            && completed.AttackerWasOffline == offline
            && f.State.Ledger.ListAll().Count(item => item.Type == ServerEventType.ServerNotification) == 2
            && f.State.Ledger.ListAll().Count(item => item.Payload is RaidEventPayload { AttackerLoss: not null }) == 1
            && !f.Files.GetLatestPackage("attacker", "colony-a")!.Index.WorldObjects.Any(item => item.Def == "ClashOfRim_RemoteRaidBattleMapParent")
            && !Directory.EnumerateFiles(f.Journal, "*.json").Any(), $"Timeout retry failed: offline={offline}, reopen={reopen}, fault={fault}, "
                + System.Text.Json.JsonSerializer.Serialize(source));
        Check(f.State.SnapshotPostUploadJobs.ListAll().Count == (offline && fault != "settled" ? 1 : 0),
            "Timeout retry lost or duplicated the durable settlement job.");
        f.Open();
        Check(RaidTimeoutProcessor.PendingWork(f.State.Ledger).Count == 0, "Timeout completion did not persist.");
    }

    private static void VerifyBackgroundRecovery(RaidTimeoutFixture f)
    {
        RaidTimeoutProcessor.ProcessExpiredRaids(f.State.Ledger, f.State.Ledger.ListAll(), f.Now, isAttackerOnline: _ => true);
        f.Open();
        using var service = new SnapshotPostUploadBackgroundService(f.State.SnapshotPostUploadBackground);
        service.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (RaidTimeoutProcessor.PendingWork(f.State.Ledger).Count != 0 && DateTime.UtcNow < deadline)
                Task.Delay(20).GetAwaiter().GetResult();
            Check(RaidTimeoutProcessor.PendingWork(f.State.Ledger).Count == 0
                && f.State.Ledger.ListAll().Count == 4
                && !f.Files.GetLatestPackage("attacker", "colony-a")!.Index.WorldObjects.Any(item => item.Def == "ClashOfRim_RemoteRaidBattleMapParent"),
                "Background service did not recover persisted timeout work without a new request.");
        }
        finally { service.StopAsync(CancellationToken.None).GetAwaiter().GetResult(); }
    }

    private static void VerifyBatch(RaidTimeoutFixture f, string kind)
    {
        string path = Path.Combine(f.Root, "ledger.json");
        IAuthoritativeEventLedger ledger = kind switch
        {
            "memory" => new InMemoryAuthoritativeEventLedger(),
            "file" => new FileAuthoritativeEventLedger(path),
            _ => new SqliteAuthoritativeEventLedger(f.Database)
        };
        ledger.Append(f.Raid);
        if (kind == "file" && OperatingSystem.IsWindows())
        {
            using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
                Fails(() => RaidTimeoutProcessor.ProcessExpiredRaids(ledger, ledger.ListAll(), f.Now), "Expected file publication failure.");
            Check(ledger.ListAll().Count == 1 && ledger.Find(f.Raid.EventId)!.Status == f.Raid.Status, "Failed file batch leaked timeout state.");
        }
        var result = RaidTimeoutProcessor.ProcessExpiredRaids(ledger, ledger.ListAll(), f.Now);
        Check(result.NotificationCount == 2 && RaidTimeoutProcessor.PendingWork(ledger).Count == 1, "Timeout did not atomically publish work and notices.");
        var current = ledger.Find(f.Raid.EventId)!;
        var wrongPayload = ((RaidEventPayload)current.Payload) with { DefenderSnapshotId = "wrong" };
        Fails(() => ledger.ApplyBatch(new LedgerBatch([], [new LedgerStatusChange(current.EventId, current.Status, current.Status,
            wrongPayload, wrongPayload with { TimeoutWork = null })])), "Stale payload update was accepted.");
        RaidTimeoutProcessor.CompleteWork(ledger, f.Raid.EventId);
        RaidTimeoutProcessor.CompleteWork(ledger, f.Raid.EventId);
        if (kind == "file") ledger = new FileAuthoritativeEventLedger(path);
        Check(ledger.ListAll().Count == 3 && RaidTimeoutProcessor.PendingWork(ledger).Count == 0, "Completion changed notification count or failed persistence.");
        ledger.Append(f.Raid with { EventId = "legacy-failed", IdempotencyKey = "legacy-failed", Status = ServerEventStatus.Failed });
        Check(RaidTimeoutProcessor.ProcessExpiredRaids(ledger, ledger.ListAll(), f.Now).NotificationCount == 0
            && RaidTimeoutProcessor.PendingWork(ledger).Count == 0, "Legacy failed raid was reclassified without a persisted timeout decision.");
    }

    private static void Fails(Action action, string message)
    {
        try { action(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or SqliteException) { return; }
        throw new Exception(message);
    }
}
