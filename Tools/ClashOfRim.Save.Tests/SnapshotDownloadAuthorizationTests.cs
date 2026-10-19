using static SnapshotTestAssertions;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Network.Modules.Snapshots;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using Microsoft.AspNetCore.Http.HttpResults;
using static SnapshotDownloadTestClient;

internal static class SnapshotDownloadAuthorizationTests
{
    public static void Run(SaveSnapshotPackage original)
    {
        SnapshotEndpointTestClient.LoadLocalization();
        VerifyDownloadContracts(original);
        foreach (bool payload in new[] { false, true })
        {
            foreach (string phase in new[] { "read", "cleanup", "initial-recovery", "cleanup-capture", "cleanup-commit" })
            {
                foreach (string change in new[] { "none", "logout", "replace", "revoke", "ally-loss" })
                    RaidCleanupFixture.With(original, false, f => VerifyDownloadAuthorization(f, payload, phase, change));
                if (phase is "read" or "cleanup")
                    foreach (string change in new[] { "snapshot", "token", "removed" })
                        RaidCleanupFixture.With(original, false, f => VerifyDownloadAuthorization(f, payload, phase, change));
            }
            RaidCleanupFixture.With(original, false, f => VerifyDownloadAuthorization(f, payload, "initial-recovery", "revoke-failure"));
            RaidCleanupFixture.With(original, false, f => VerifyDownloadAuthorization(f, payload, "read", "ally-valid"));
            foreach (bool wrongScope in new[] { false, true })
                RaidCleanupFixture.With(original, false, f => VerifyDownloadUnauthorizedEntry(f, payload, wrongScope));
        }
    }

    private static void VerifyDownloadContracts(SaveSnapshotPackage original)
    {
        var state = new ClashOfRimNetworkState();
        string token = SessionTestAuthentication.Token(state, "attacker", "colony-a");
        foreach (string missing in new[] { "user", "colony", "snapshot" })
        {
            string user = missing == "user" ? " " : "attacker", colony = missing == "colony" ? "" : "colony-a";
            var request = new DownloadLatestSnapshotPayloadRequest(user, colony, missing == "snapshot" ? "" : "snapshot", "invalid");
            var domain = state.SnapshotDownloads.Payload(request);
            var reply = ReadReply(SnapshotEndpoints.DownloadLatestSnapshotPayload(request, state.SnapshotDownloads));
            Check(domain.Status == SnapshotDownloadStatus.InvalidRequest && domain.Package is null && domain.Recovery is null
                && reply.HttpStatus == 400 && reply.ErrorCode == ProtocolErrorCode.ValidationFailed,
                "Missing payload identity did not take precedence over authorization: " + missing);
            if (missing != "snapshot")
            {
                var metadata = ReadReply(SnapshotEndpoints.DownloadLatestSnapshot(
                    new DownloadLatestSnapshotRequest(user, colony, "invalid"), state.SnapshotDownloads));
                Check(metadata.HttpStatus == 200 && metadata.ErrorCode == ProtocolErrorCode.ValidationFailed
                    && metadata.SnapshotId is null && metadata.Lineage is null && metadata.Recovery is null,
                    "Missing metadata identity changed its response contract.");
            }
        }
        var unsupported = new DownloadLatestSnapshotPayloadRequest("attacker", "colony-a", "snapshot", token);
        Check(state.SnapshotDownloads.Payload(unsupported).Status == SnapshotDownloadStatus.UnsupportedStore,
            "Index-only store was advertised as downloadable.");
        var unsupportedReply = ReadReply(SnapshotEndpoints.DownloadLatestSnapshotPayload(unsupported, state.SnapshotDownloads));
        Check(unsupportedReply.HttpStatus == 500 && unsupportedReply.ErrorCode == ProtocolErrorCode.ServerRejected
            && unsupportedReply.Bytes is null, "Unsupported payload storage changed its response contract.");

        RaidCleanupFixture.With(original, false, f =>
        {
            f.Files.StoreLatest(f.Cleaned, f.Cleaned.Index, f.Now);
            string access = SessionTestAuthentication.Token(f.State, "attacker", "colony-a");
            string id = f.Cleaned.Envelope.Identity.SnapshotId!;
            var request = new DownloadLatestSnapshotPayloadRequest("attacker", "colony-a", id, access);
            var captured = f.State.SnapshotDownloads.Payload(request);
            var result = SnapshotEndpoints.DownloadLatestSnapshotPayload(request, f.State.SnapshotDownloads);
            Check(captured.Status == SnapshotDownloadStatus.Available && captured.Package!.Payload.SequenceEqual(f.Cleaned.Payload)
                && result is FileContentHttpResult file && file.FileContents.Span.SequenceEqual(f.Cleaned.Payload)
                && file.ContentType == "application/octet-stream" && file.FileDownloadName == id + ".payload",
                "Payload response lost its captured bytes, media type or snapshot filename.");
            foreach (bool remove in new[] { false, true })
            {
                if (remove) f.Files.RemoveLatest("attacker", "colony-a");
                var missing = ReadReply(SnapshotEndpoints.DownloadLatestSnapshotPayload(
                    new DownloadLatestSnapshotPayloadRequest("attacker", "colony-a", remove ? id : "different", access), f.State.SnapshotDownloads));
                Check(missing.HttpStatus == 404 && missing.ErrorCode == ProtocolErrorCode.EventNotFound && missing.Bytes is null,
                    "Missing or replaced payload did not return the established response.");
            }
            var metadata = f.State.SnapshotDownloads.Metadata(new("attacker", "colony-a", access));
            Check(!metadata.Result.Accepted && metadata.Result.ErrorCode == ProtocolErrorCode.EventNotFound
                && metadata.Package is null && metadata.SnapshotId is null && metadata.ActiveRaidRecovery is null,
                "Missing metadata exposed a package or recovery.");
        });
    }

    private static void VerifyDownloadAuthorization(RaidCleanupFixture f, bool payload, string phase, string change)
    {
        bool flat = phase is "read" or "initial-recovery";
        if (flat) f.Files.StoreLatest(f.Cleaned, f.Cleaned.Index, f.Now);
        f.PrepareDownloadAccess(change.StartsWith("ally"));
        var foreign = f.DownloadForeignIntent();
        bool recovering = phase.Contains("recovery") || phase is "cleanup-capture" or "cleanup-commit";
        Action? hook = null;
        void Inject()
        {
            using (f.State.EnterSnapshotMutation()) f.SeedDownloadRecovery(foreign, false);
            f.Store.BeforePrepare = () => hook?.Invoke();
        }
        if (phase == "read") f.Store.AfterRead = () => hook?.Invoke();
        else if (phase == "cleanup") f.Store.BeforePrepare = () => hook?.Invoke();
        else if (phase == "initial-recovery") Inject();
        else if (phase == "cleanup-capture") f.Store.AfterRead = () => { f.Store.AfterRead = null; Inject(); };
        else f.Store.BeforePrepare = Inject;
        Reply? reply = null;
        string? before = null;
        int captures = 0;
        SnapshotPreparationGateProbe.Run(f.State.RaidSettlementSnapshotMutationGate, h => hook = h,
            () => reply = f.Download(payload), () =>
            {
                captures++;
                Check(f.Store.WritesByOwner.Count == 0 && f.State.Ledger.ListAll().Count == 2,
                    "Download published before its access could be revalidated.");
                if (change is "logout" or "replace") f.State.LoginSessions.EndUser(f.DownloadReader);
                if (change == "replace") f.State.LoginSessions.TryCreate(f.DownloadReader, "colony-a", DateTimeOffset.UtcNow, out _);
                if (change is "revoke" or "revoke-failure") f.State.AuthTokens.RevokeForUser(f.DownloadReader);
                if (change == "revoke-failure") f.Store.FailPreparation = true;
                if (change == "ally-loss") f.SetDownloadRelation(DiplomacyRelationRegistry.RelationNeutral);
                if (change == "removed") f.Files.RemoveLatest("attacker", "colony-a");
                if (change is "snapshot" or "token")
                {
                    var current = f.Files.GetLatestPackage("attacker", "colony-a")!;
                    f.Files.StoreLatest(current with { Envelope = current.Envelope with { NextLineageToken = "newer-token",
                        Identity = change == "snapshot" ? current.Envelope.Identity with { SnapshotId = "newer" } : current.Envelope.Identity } }, current.Index, f.Now);
                }
                before = f.DownloadRecoveryEvidence();
            });
        Check(captures == 1, "Download did not reach its intended boundary: " + phase);
        bool allowed = change is "none" or "ally-valid";
        bool denied = change is "logout" or "replace" or "revoke" or "revoke-failure" or "ally-loss";
        if (!allowed)
        {
            Check(reply is { Accepted: false, SnapshotId: null, Lineage: null, Bytes: null, Recovery: null }
                && before == f.DownloadRecoveryEvidence() && f.Store.WritesByOwner.Count == 0
                && f.State.SnapshotCommits.HasPending == recovering,
                "Rejected download disclosed data or changed business: " + payload + "/" + phase + "/" + change);
            if (payload) Check(reply!.HttpStatus == (denied ? 401 : 404), "Payload rejection did not preserve its HTTP contract.");
            if (denied || !payload) Check(reply!.ErrorCode == (change == "ally-loss" ? ProtocolErrorCode.ServerRejected : denied ? ProtocolErrorCode.SessionUnavailable : ProtocolErrorCode.SnapshotMismatch),
                "Metadata rejection did not identify stale authorization or version.");
        }
        else
        {
            Check(reply!.Accepted == (!payload || flat) && !f.State.SnapshotCommits.HasPending
                && f.Store.WritesByOwner.GetValueOrDefault("attacker") == (flat ? 0 : 1)
                && f.Store.WritesByOwner.GetValueOrDefault("foreign") == (recovering ? 1 : 0),
                "Authorized download lost cleanup or recovery: " + payload + "/" + phase);
            if (payload && flat) Check(reply.Bytes!.SequenceEqual(f.Cleaned.Payload), "Payload download did not return the captured current bytes.");
            else if (payload) Check(reply.HttpStatus == 404 && reply.Bytes is null, "Payload download accepted an ID replaced by cleanup.");
            else Check(reply.SnapshotId == f.Files.GetLatest("attacker", "colony-a")!.Identity.SnapshotId,
                "Metadata did not return the current cleaned snapshot.");
        }
        GateReleased(f.State.RaidSettlementSnapshotMutationGate, "Download leaked the mutation gate.");
    }

    private static void VerifyDownloadUnauthorizedEntry(RaidCleanupFixture f, bool payload, bool wrongScope)
    {
        f.PrepareDownloadAccess(wrongScope);
        f.SeedDownloadRecovery(f.DownloadForeignIntent(), false);
        if (wrongScope) f.SetDownloadRelation(DiplomacyRelationRegistry.RelationNeutral);
        else f.State.AuthTokens.RevokeForUser(f.DownloadReader);
        string before = f.DownloadRecoveryEvidence();
        f.Store.BeforePrepare = () => throw new Exception("Unauthorized download prepared journal recovery.");
        f.Store.AfterRead = () => throw new Exception("Unauthorized download read snapshot bytes.");
        var reply = f.Download(payload);
        Check(!reply.Accepted && reply.SnapshotId is null && reply.Lineage is null && reply.Bytes is null && reply.Recovery is null
            && reply.ErrorCode == (wrongScope ? ProtocolErrorCode.ServerRejected : ProtocolErrorCode.SessionUnavailable)
            && before == f.DownloadRecoveryEvidence() && f.State.SnapshotCommits.HasPending && f.Store.WritesByOwner.Count == 0,
            "Unauthorized download triggered recovery or disclosed snapshot data.");
        GateReleased(f.State.RaidSettlementSnapshotMutationGate, "Download leaked the mutation gate.");
    }
}
