using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using static SnapshotTestAssertions;

internal static class RaidEvidenceAuthorizationTests
{
    public static void Run(Func<string, string, SaveSnapshotPackage> fixture)
    {
        SnapshotEndpointTestClient.LoadLocalization();
        foreach (string change in new[] { "none", "logout", "replace", "revoke", "cancel", "source-content" })
            RaidEvidenceFixture.With(fixture, f => VerifyPreparing(f, change));
        foreach (bool applied in new[] { false, true })
        foreach (string identity in new[] { "attacker", "defender", "delegated", "header", "metadata" })
            RaidEvidenceFixture.With(fixture, f => VerifyIdentity(f, applied, identity));
    }

    private static void VerifyPreparing(RaidEvidenceFixture f, string change)
    {
        ConfirmEventApplicationResponse? reply = null;
        SnapshotPreparationGateProbe.Run(f.State.RaidSettlementSnapshotMutationGate, hook => f.Store.BeforePrepare = hook,
            () => reply = f.Invoke(), () =>
            {
                Check(f.State.SnapshotPostUploadJobs.ListAll().Count == 0 && f.Store.Inner.GetLatest("userA", "colonyA") is null,
                    "Evidence published before preparation finished.");
                if (change is "logout" or "replace") f.State.LoginSessions.EndUser("userA");
                if (change == "replace") f.State.LoginSessions.TryCreate("userA", "colonyA", DateTimeOffset.UtcNow, out _);
                if (change == "revoke") f.State.AuthTokens.RevokeForUser("userA");
                if (change == "source-content")
                {
                    var source = f.State.Ledger.Find(f.Raid.EventId)!;
                    var payload = (RaidEventPayload)source.Payload;
                    f.State.Ledger.ApplyBatch(new LedgerBatch([], [new LedgerStatusChange(source.EventId, source.Status, source.Status, payload,
                        payload with { StartedAtUtc = payload.StartedAtUtc!.Value.AddMinutes(1) })]));
                }
                if (change == "cancel") f.State.Ledger.ChangeStatus(f.Raid.EventId, ServerEventStatus.Cancelled);
            });
        bool accepted = change == "none";
        Check(reply!.Result.Accepted == accepted, "Raid evidence accepted a revoked/cancelled preparation: " + change);
        Check(f.State.SnapshotPostUploadJobs.ListAll().Count == (accepted ? 1 : 0)
            && (f.Store.Inner.GetLatest("userA", "colonyA") is not null) == accepted
            && Directory.EnumerateFiles(f.Artifacts, "*.snapshot.gz").Count() == (accepted ? 1 : 0)
            && f.State.Ledger.ListAll().Count == 1, "Rejected evidence published an artifact, snapshot or job.");
        if (!accepted) Check(reply.AppliedSnapshotId is null && reply.NextLineageToken is null, "Rejected evidence disclosed snapshot credentials.");
    }

    private static void VerifyIdentity(RaidEvidenceFixture f, bool applied, string identity)
    {
        if (applied)
        {
            f.State.Ledger.ChangeStatus(f.Raid.EventId, ServerEventStatus.AppliedToSnapshot);
            f.Store.Inner.StoreLatest(f.Package with { Envelope = f.Package.Envelope with { NextLineageToken = "attacker-next" } }, f.Package.Index, DateTimeOffset.UtcNow);
        }
        string before = f.StateEvidence();
        var reply = f.Invoke(identity);
        if (applied && identity == "delegated")
            Check(reply.NextLineageToken != "defender-next", "Cross-party raid replay disclosed the defender lineage token.");
        bool accepted = identity == "attacker" && !applied;
        Check(reply.Result.Accepted == accepted, "Raid evidence did not bind its caller/metadata to the attacker: " + applied + "/" + identity);
        if (!accepted)
            Check(reply.AppliedSnapshotId is null && reply.NextLineageToken is null && f.StateEvidence() == before,
                "Rejected or wrong-party replay leaked snapshot credentials or changed persistent state.");

    }
}
