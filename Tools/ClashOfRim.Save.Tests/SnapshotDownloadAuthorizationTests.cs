using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Network.Modules.Snapshots;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using System.Text.Json;

internal static partial class RaidCleanupCommitTests
{
    private static void RunDownloadAuthorization(SaveSnapshotPackage original)
    {
        SnapshotEndpointTestClient.LoadLocalization();
        VerifyDownloadContracts(original);
        foreach (bool payload in new[] { false, true })
        {
            foreach (string phase in new[] { "read", "cleanup", "initial-recovery", "cleanup-capture", "cleanup-commit" })
            {
                foreach (string change in new[] { "none", "logout", "replace", "revoke", "ally-loss" })
                    WithFixture(original, false, f => VerifyDownloadAuthorization(f, payload, phase, change));
                if (phase is "read" or "cleanup")
                    foreach (string change in new[] { "snapshot", "token", "removed" })
                        WithFixture(original, false, f => VerifyDownloadAuthorization(f, payload, phase, change));
            }
            WithFixture(original, false, f => VerifyDownloadAuthorization(f, payload, "initial-recovery", "revoke-failure"));
            WithFixture(original, false, f => VerifyDownloadAuthorization(f, payload, "read", "ally-valid"));
            foreach (bool wrongScope in new[] { false, true })
                WithFixture(original, false, f => VerifyDownloadUnauthorizedEntry(f, payload, wrongScope));
        }
    }

    private static void VerifyDownloadContracts(SaveSnapshotPackage original)
    {
        var state = new ClashOfRimNetworkState();
        string token = TradeCommandAuthTests.Token(state, "attacker", "colony-a");
        foreach (string missing in new[] { "user", "colony", "snapshot" })
        {
            string user = missing == "user" ? " " : "attacker", colony = missing == "colony" ? "" : "colony-a";
            var request = new DownloadLatestSnapshotPayloadRequest(user, colony, missing == "snapshot" ? "" : "snapshot", "invalid");
            var domain = state.SnapshotDownloads.Payload(request);
            var reply = ReadDownloadReply(SnapshotEndpoints.DownloadLatestSnapshotPayload(request, state.SnapshotDownloads));
            Require(domain.Status == SnapshotDownloadStatus.InvalidRequest && domain.Package is null && domain.Recovery is null
                && reply.HttpStatus == 400 && reply.ErrorCode == ProtocolErrorCode.ValidationFailed,
                "Missing payload identity did not take precedence over authorization: " + missing);
            if (missing != "snapshot")
            {
                var metadata = ReadDownloadReply(SnapshotEndpoints.DownloadLatestSnapshot(
                    new DownloadLatestSnapshotRequest(user, colony, "invalid"), state.SnapshotDownloads));
                Require(metadata.HttpStatus == 200 && metadata.ErrorCode == ProtocolErrorCode.ValidationFailed
                    && metadata.SnapshotId is null && metadata.Lineage is null && metadata.Recovery is null,
                    "Missing metadata identity changed its response contract.");
            }
        }
        var unsupported = new DownloadLatestSnapshotPayloadRequest("attacker", "colony-a", "snapshot", token);
        Require(state.SnapshotDownloads.Payload(unsupported).Status == SnapshotDownloadStatus.UnsupportedStore,
            "Index-only store was advertised as downloadable.");
        var unsupportedReply = ReadDownloadReply(SnapshotEndpoints.DownloadLatestSnapshotPayload(unsupported, state.SnapshotDownloads));
        Require(unsupportedReply.HttpStatus == 500 && unsupportedReply.ErrorCode == ProtocolErrorCode.ServerRejected
            && unsupportedReply.Bytes is null, "Unsupported payload storage changed its response contract.");

        WithFixture(original, false, f =>
        {
            f.Files.StoreLatest(f.Cleaned, f.Cleaned.Index, f.Now);
            string access = TradeCommandAuthTests.Token(f.State, "attacker", "colony-a");
            string id = f.Cleaned.Envelope.Identity.SnapshotId!;
            var request = new DownloadLatestSnapshotPayloadRequest("attacker", "colony-a", id, access);
            var captured = f.State.SnapshotDownloads.Payload(request);
            var result = SnapshotEndpoints.DownloadLatestSnapshotPayload(request, f.State.SnapshotDownloads);
            Require(captured.Status == SnapshotDownloadStatus.Available && captured.Package!.Payload.SequenceEqual(f.Cleaned.Payload)
                && result is FileContentHttpResult file && file.FileContents.Span.SequenceEqual(f.Cleaned.Payload)
                && file.ContentType == "application/octet-stream" && file.FileDownloadName == id + ".payload",
                "Payload response lost its captured bytes, media type or snapshot filename.");
            foreach (bool remove in new[] { false, true })
            {
                if (remove) f.Files.RemoveLatest("attacker", "colony-a");
                var missing = ReadDownloadReply(SnapshotEndpoints.DownloadLatestSnapshotPayload(
                    new DownloadLatestSnapshotPayloadRequest("attacker", "colony-a", remove ? id : "different", access), f.State.SnapshotDownloads));
                Require(missing.HttpStatus == 404 && missing.ErrorCode == ProtocolErrorCode.EventNotFound && missing.Bytes is null,
                    "Missing or replaced payload did not return the established response.");
            }
            var metadata = f.State.SnapshotDownloads.Metadata(new("attacker", "colony-a", access));
            Require(!metadata.Result.Accepted && metadata.Result.ErrorCode == ProtocolErrorCode.EventNotFound
                && metadata.Package is null && metadata.SnapshotId is null && metadata.ActiveRaidRecovery is null,
                "Missing metadata exposed a package or recovery.");
        });
    }

    private static void VerifyDownloadAuthorization(Fixture f, bool payload, string phase, string change)
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
        DownloadReply? reply = null;
        string? before = null;
        int captures = 0;
        SnapshotPreparationGateProbe.Run(f.State.RaidSettlementSnapshotMutationGate, h => hook = h,
            () => reply = f.Download(payload), () =>
            {
                captures++;
                Require(f.Store.WritesByOwner.Count == 0 && f.State.Ledger.ListAll().Count == 2,
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
        Require(captures == 1, "Download did not reach its intended boundary: " + phase);
        bool allowed = change is "none" or "ally-valid";
        bool denied = change is "logout" or "replace" or "revoke" or "revoke-failure" or "ally-loss";
        if (!allowed)
        {
            Require(reply is { Accepted: false, SnapshotId: null, Lineage: null, Bytes: null, Recovery: null }
                && before == f.DownloadRecoveryEvidence() && f.Store.WritesByOwner.Count == 0
                && f.State.SnapshotCommits.HasPending == recovering,
                "Rejected download disclosed data or changed business: " + payload + "/" + phase + "/" + change);
            if (payload) Require(reply!.HttpStatus == (denied ? 401 : 404), "Payload rejection did not preserve its HTTP contract.");
            if (denied || !payload) Require(reply!.ErrorCode == (change == "ally-loss" ? ProtocolErrorCode.ServerRejected : denied ? ProtocolErrorCode.SessionUnavailable : ProtocolErrorCode.SnapshotMismatch),
                "Metadata rejection did not identify stale authorization or version.");
        }
        else
        {
            Require(reply!.Accepted == (!payload || flat) && !f.State.SnapshotCommits.HasPending
                && f.Store.WritesByOwner.GetValueOrDefault("attacker") == (flat ? 0 : 1)
                && f.Store.WritesByOwner.GetValueOrDefault("foreign") == (recovering ? 1 : 0),
                "Authorized download lost cleanup or recovery: " + payload + "/" + phase);
            if (payload && flat) Require(reply.Bytes!.SequenceEqual(f.Cleaned.Payload), "Payload download did not return the captured current bytes.");
            else if (payload) Require(reply.HttpStatus == 404 && reply.Bytes is null, "Payload download accepted an ID replaced by cleanup.");
            else Require(reply.SnapshotId == f.Files.GetLatest("attacker", "colony-a")!.Identity.SnapshotId,
                "Metadata did not return the current cleaned snapshot.");
        }
        AssertDownloadGateReleased(f);
    }

    private static void VerifyDownloadUnauthorizedEntry(Fixture f, bool payload, bool wrongScope)
    {
        f.PrepareDownloadAccess(wrongScope);
        f.SeedDownloadRecovery(f.DownloadForeignIntent(), false);
        if (wrongScope) f.SetDownloadRelation(DiplomacyRelationRegistry.RelationNeutral);
        else f.State.AuthTokens.RevokeForUser(f.DownloadReader);
        string before = f.DownloadRecoveryEvidence();
        f.Store.BeforePrepare = () => throw new Exception("Unauthorized download prepared journal recovery.");
        f.Store.AfterRead = () => throw new Exception("Unauthorized download read snapshot bytes.");
        var reply = f.Download(payload);
        Require(!reply.Accepted && reply.SnapshotId is null && reply.Lineage is null && reply.Bytes is null && reply.Recovery is null
            && reply.ErrorCode == (wrongScope ? ProtocolErrorCode.ServerRejected : ProtocolErrorCode.SessionUnavailable)
            && before == f.DownloadRecoveryEvidence() && f.State.SnapshotCommits.HasPending && f.Store.WritesByOwner.Count == 0,
            "Unauthorized download triggered recovery or disclosed snapshot data.");
        AssertDownloadGateReleased(f);
    }

    private static void AssertDownloadGateReleased(Fixture f) => Task.Run(() =>
    {
        Require(Monitor.TryEnter(f.State.RaidSettlementSnapshotMutationGate, TimeSpan.FromSeconds(3)), "Download leaked the mutation gate.");
        Monitor.Exit(f.State.RaidSettlementSnapshotMutationGate);
    }).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();

    private sealed record DownloadReply(bool Accepted, int HttpStatus, ProtocolErrorCode? ErrorCode,
        string? SnapshotId = null, string? Lineage = null, byte[]? Bytes = null, ActiveRaidRecoveryDto? Recovery = null);

    private static DownloadReply ReadDownloadReply(IResult result)
    {
        if (result is FileContentHttpResult file) return new(true, 200, null, Bytes: file.FileContents.ToArray());
        if (result is IValueHttpResult { Value: DownloadLatestSnapshotResponse metadata })
            return new(metadata.Result.Accepted, 200, metadata.Result.ErrorCode, metadata.SnapshotId, metadata.Package?.NextLineageToken, Recovery: metadata.ActiveRaidRecovery);
        var body = JsonSerializer.SerializeToElement(((IValueHttpResult)result).Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return new(false, ((IStatusCodeHttpResult)result).StatusCode!.Value,
            (ProtocolErrorCode)body.GetProperty("result").GetProperty("errorCode").GetInt32());
    }

    private sealed partial class Fixture
    {
        public string DownloadReader = "attacker";
        private string downloadToken = "";
        private string? downloadScope;
        public void PrepareDownloadAccess(bool allied)
        {
            DownloadReader = allied ? "reader" : "attacker";
            string colony = allied ? "reader-colony" : "colony-a";
            State.LoginSessions.TryCreate(DownloadReader, colony, DateTimeOffset.UtcNow, out string session);
            downloadToken = State.AuthTokens.Issue(DownloadReader, DownloadReader, colony, session, DateTimeOffset.UtcNow);
            downloadScope = allied ? "FriendlyObservation" : null;
            if (allied) SetDownloadRelation(DiplomacyRelationRegistry.RelationAlly);
        }
        public void SetDownloadRelation(string relation) => State.DiplomacyRelations.SetRelationKind("reader", "reader-colony", "attacker", "colony-a",
            relation, null, DateTimeOffset.UtcNow);
        public DownloadReply Download(bool payload)
        {
            var result = payload
                ? SnapshotEndpoints.DownloadLatestSnapshotPayload(new DownloadLatestSnapshotPayloadRequest("attacker", "colony-a",
                    Files.GetLatest("attacker", "colony-a")!.Identity.SnapshotId!, downloadToken, authorizationScope: downloadScope), State.SnapshotDownloads)
                : SnapshotEndpoints.DownloadLatestSnapshot(new DownloadLatestSnapshotRequest("attacker", "colony-a", downloadToken, authorizationScope: downloadScope), State.SnapshotDownloads);
            return ReadDownloadReply(result);
        }
    }
}
