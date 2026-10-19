using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Network.Modules.Raids;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using System.Text.Json.Nodes;
using static SnapshotTestAssertions;

internal static class RaidEvidenceRequestBindingTests
{
    public static void Run()
    {
        SnapshotEndpointTestClient.LoadLocalization();
        Func<string, string, SaveSnapshotPackage> fixture = RaidEvidenceBindingSnapshot.Create;
        foreach (bool completed in new[] { false, true })
        foreach (bool reopen in new[] { false, true })
        foreach (bool legacyStore in new[] { false, true })
            RaidEvidenceFixture.With(fixture, f => VerifyBinding(f, completed, reopen), persistent: true, legacyArtifacts: legacyStore);
        foreach (string fault in new[] { "before", "after", "ledger" })
            RaidEvidenceFixture.With(fixture, f => VerifyBindingRecovery(f, fault), persistent: true);
        foreach (string change in new[] { "missing", "malformed", "actor", "target", "snapshot" })
            RaidEvidenceFixture.With(fixture, f => VerifyCompletedReceiptConflict(f, change), persistent: true);
        foreach (bool pending in new[] { false, true })
            RaidEvidenceFixture.With(fixture, f => VerifyLegacyEvidence(f, pending), persistent: true);
        foreach (bool artifact in new[] { false, true })
        foreach (bool conflict in new[] { false, true })
            RaidEvidenceFixture.With(fixture, f => VerifyCompletionRace(f, artifact, conflict), persistent: true);
    }

    private static void VerifyBinding(RaidEvidenceFixture f, bool completed, bool reopen)
    {
        var queued = f.Invoke(); Check(queued.Result.Accepted, "Could not queue bound evidence.");
        var job = f.State.SnapshotPostUploadJobs.ListAll().Single();
        Check(RaidSettlementDeferredPayload.Deserialize(job.PayloadJson).RequestHash == f.Binding(), "Job lost full request binding.");
        if (completed) f.Complete();
        if (reopen) f.Open(); // Renew both header and body credentials without changing the business request.
        string before = f.StateEvidence();
        foreach (var (name, mutation) in EvidenceMutations())
        {
            var reply = f.Invoke(mutate: mutation);
            Check(!reply.Result.Accepted && reply.AppliedSnapshotId is null && reply.NextLineageToken is null
                && f.StateEvidence() == before, "Changed evidence replay accepted or mutated state: " + completed + "/" + name);
        }
        var corrupted = f.Package.Payload.ToArray(); corrupted[^1] ^= 1;
        var rejected = f.Invoke(package: f.Package with { Payload = corrupted });
        Check(!rejected.Result.Accepted && rejected.NextLineageToken is null && before == f.StateEvidence(), "Evidence replay ignored actual bytes.");
        var exact = f.Invoke();
        Check(exact.Result.Accepted && exact.Result.ErrorCode == ProtocolErrorCode.DuplicateRequest
            && exact.AppliedSnapshotId == queued.AppliedSnapshotId && exact.NextLineageToken == queued.NextLineageToken
            && before == f.StateEvidence(), "Exact evidence retry lost original confirmation.");
        if (!completed) return;
        var newer = f.Package with { Envelope = f.Package.Envelope with
            { Identity = new("userA", "colonyA", "newer"), NextLineageToken = "newer-secret" } };
        f.Store.Inner.StoreLatest(newer, newer.Index, DateTimeOffset.UtcNow);
        before = f.StateEvidence(); exact = f.Invoke();
        Check(exact.Result.Accepted && exact.AppliedSnapshotId == queued.AppliedSnapshotId && exact.NextLineageToken is null
            && before == f.StateEvidence(), "Completed evidence retry exposed a newer snapshot credential.");
    }

    private static IEnumerable<(string, Action<JsonObject>)> EvidenceMutations()
    {
        foreach (string field in new[] { "IdempotencyKey", "SourceEventId", "BaseSnapshotId", "ClientApplicationResult" })
            yield return (field, root => root[field] = "changed");
        foreach (string field in new[] { "PackageVersion", "OwnerId", "ColonyId", "SnapshotId", "RimWorldVersion", "PayloadEncoding",
            "OriginalSha256", "PayloadSha256", "PreviousSnapshotId", "LineageToken", "NextLineageToken", "SnapshotUploadKind" })
            yield return (field, root => root["ConfirmedSnapshot"]![field] = "changed");
        foreach (string field in new[] { "OriginalSaveBytes", "PayloadBytes", "GameTicks", "DefenderThreatPoints" })
            yield return (field, root => root["ConfirmedSnapshot"]![field] = 987654);
    }

    private static void VerifyBindingRecovery(RaidEvidenceFixture f, string fault)
    {
        Check(f.Invoke().Result.Accepted, "Could not queue interrupted settlement.");
        f.Publication.PreparedFault = fault;
        if (fault == "ledger") f.Sql("create trigger fail_receipt before insert on events when NEW.idempotency_key like 'raid-settlement:%' begin select raise(abort, 'receipt'); end;");
        bool failed = false;
        try { f.Complete(); } catch (Exception ex) when (ex is IOException or Microsoft.Data.Sqlite.SqliteException) { failed = true; }
        Check(failed && f.State.SnapshotCommits.HasPending, "Settlement failure lost its receipt intent.");
        if (fault == "ledger") f.Sql("drop trigger fail_receipt;");
        f.Open(); f.State.SnapshotMutations.Recover();
        f.Complete(); // Finish the interrupted job and delete its artifact after ledger recovery.
        f.Open();
        var reply = f.Invoke();
        Check(reply.Result.Accepted && reply.AppliedSnapshotId == "evidence" && !f.State.SnapshotCommits.HasPending
            && f.Receipt().EvidenceRequestHash == f.Binding(), "Recovered settlement lost its original request receipt.");
        string before = f.StateEvidence();
        Check(!f.Invoke(result: "changed").Result.Accepted && before == f.StateEvidence(), "Recovered receipt accepted changed request.");
    }

    private static void VerifyCompletedReceiptConflict(RaidEvidenceFixture f, string change)
    {
        Check(f.Invoke().Result.Accepted, "Could not queue receipt conflict."); f.Complete();
        var original = f.State.Ledger.FindByIdempotencyKey("raid-settlement:" + f.Raid.EventId)!;
        var payload = (RaidEventPayload)original.Payload;
        if (change is "missing" or "malformed" or "snapshot")
        {
            var changed = change == "snapshot" ? payload with { ReturnedSnapshotId = "unrelated" }
                : payload with { EvidenceRequestHash = change == "missing" ? null : "v2:invalid" };
            f.State.Ledger.ApplyBatch(new LedgerBatch([], [new(original.EventId, original.Status, original.Status, payload, changed, original)]));
        }
        else f.Sql("update events set " + (change == "actor" ? "actor_user_id" : "target_user_id")
            + " = 'unrelated' where idempotency_key like 'raid-settlement:%';");
        f.Open(); string before = f.StateEvidence(); var reply = f.Invoke();
        Check(!reply.Result.Accepted && reply.AppliedSnapshotId is null && reply.NextLineageToken is null
            && before == f.StateEvidence(), "Legacy or inconsistent settlement disclosed confirmation: " + change);
    }

    private static void VerifyLegacyEvidence(RaidEvidenceFixture f, bool pending)
    {
        if (pending) f.Publication.PreparedFault = "before";
        Check(f.Invoke().Result.Accepted != pending, "Legacy job fixture was not persisted.");
        f.EraseJobBinding(); f.Open();
        string before = f.StateEvidence();
        Check(!f.Invoke().Result.Accepted && before == f.StateEvidence(), "Old evidence silently adopted a new request.");
        var job = f.State.SnapshotPostUploadJobs.ListAll().Single();
        if (pending) RaidSettlementDeferredScheduler.RecoverPrepared(f.State, job, DateTimeOffset.UtcNow);
        f.Complete(); f.Open();
        Check(f.Receipt().EvidenceRequestHash is null && f.State.Ledger.Find(f.Raid.EventId)!.Status == ServerEventStatus.AppliedToSnapshot
            && !f.Invoke().Result.Accepted, "Legacy recovery lost business or fabricated an HTTP receipt.");
    }

    private static void VerifyCompletionRace(RaidEvidenceFixture f, bool artifact, bool conflict)
    {
        Check(f.Invoke().Result.Accepted, "Could not queue completion race.");
        var commit = f.PrepareCompletion(conflict);
        ConfirmEventApplicationResponse? reply = null;
        SnapshotPreparationGateProbe.Run(f.State.RaidSettlementSnapshotMutationGate,
            hook => { if (artifact) f.ArtifactStore.AfterRead = hook; else f.Store.BeforePrepare = hook; },
            () => reply = f.Invoke(), commit);
        Check(reply!.Result.Accepted != conflict && (conflict ? reply.AppliedSnapshotId is null && reply.NextLineageToken is null
            : reply.AppliedSnapshotId == "evidence" && reply.ServerValidationResult == "AlreadyApplied"),
            "Completion raced past the final request binding: " + artifact + "/" + conflict);
    }
}
