using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Network.Plugins;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using Fixture = HttpSnapshotRecoveryTests.Fixture;

internal static class UploadConfirmationRecoveryTests
{
    public static void Run(Func<string, SaveSnapshotPackage> factory)
    {
        foreach (string kind in new[] { "upload", "single", "batch" })
        {
            foreach (string invalid in new[] { "token", "user", "colony" })
            {
                using var f = CreateFixture(factory); f.Start(); f.Seed(); var before = f.Evidence();
                using var response = Call(f, kind, Incoming(factory), invalid);
                Require(!Accepted(response), "Invalid upload identity was accepted.");
                f.AssertPending(before, kind + "/" + invalid);
            }
            foreach (string stage in new[] { "payload", "recovery" })
            foreach (string change in new[] { "none", "logout", "replace", "revoke", "failure", "revoke-failure", "compete", "newer" })
                Verify(factory, kind, stage, change);
            VerifyPublishedRevocation(factory, kind);
        }
        foreach (string change in new[] { "none", "actor", "published-actor", "logout" }) VerifyDelegated(factory, change);
    }

    private static void Verify(Func<string, SaveSnapshotPackage> factory, string kind, string stage, string change)
    {
        using var f = CreateFixture(factory); f.Start();
        var incoming = Incoming(factory);
        if (stage == "recovery") f.Seed();
        string? before = null; HttpResponseMessage? response = null; int preparations = 0;
        // Confirmation prepares the submitted payload before recovering an older intent.
        int target = stage == "recovery" && kind != "upload" ? 2 : 1;
        SnapshotPreparationGateProbe.Run(f.State.RaidSettlementSnapshotMutationGate,
            hook => f.Store.BeforePrepare = hook is null ? null : () => { if (++preparations == target) hook(); },
            () => response = Call(f, kind, incoming), () =>
            {
                if (stage == "payload") f.Seed();
                if (change is "logout" or "replace") f.State.LoginSessions.EndUser("receiver");
                if (change == "replace") f.State.LoginSessions.TryCreate("receiver", "away", DateTimeOffset.UtcNow, out _);
                if (change is "revoke" or "revoke-failure") f.State.AuthTokens.RevokeForUser("receiver");
                if (change is "failure" or "revoke-failure")
                {
                    // Fail only recovery preparation, not the submitted payload's already-started preparation.
                    if (stage == "payload") f.Store.BeforePrepare = () => throw new IOException("recovery preparation failed");
                    else f.Store.FailPreparation = true;
                }
                if (change == "compete") ClashOfRimNetworkServer.RecoverSnapshotCommits(f.State);
                if (change == "newer") f.Store.Inner.StoreLatest(f.Package with { Envelope = f.Package.Envelope with
                    { Identity = new("foreign", "foreign-colony", "newer"), NextLineageToken = "newer-token" } }, f.Package.Index, DateTimeOffset.UtcNow);
                before = f.Evidence();
            });
        using (response)
        {
            bool denied = change is "logout" or "replace" or "revoke" or "revoke-failure";
            bool failed = change is "failure" or "newer";
            if (failed) Require(response!.StatusCode == HttpStatusCode.InternalServerError, "Recovery failure did not reach HTTP: " + kind + "/" + stage);
            else Require(Accepted(response!) == !denied, "Wrong upload authorization outcome: " + kind + "/" + stage + "/" + change);
            if (denied || failed)
            {
                f.AssertPending(before!, kind + "/" + stage + "/" + change);
                Require(f.Store.GetLatest("receiver", "away") is null, "Denied request published its submitted snapshot.");
                if (change != "newer")
                {
                    f.Store.FailPreparation = false; f.Store.BeforePrepare = null; f.RenewToken();
                    using var retry = Call(f, kind, incoming);
                    Require(Accepted(retry), "Fresh HTTP request did not recover and resume.");
                    AssertCompleted(f, retry);
                }
            }
            else
            {
                AssertCompleted(f, response!);
                string receipt = response!.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                string ledger = JsonSerializer.Serialize(f.State.Ledger.ListAll());
                using var replay = Call(f, kind, incoming);
                Require(Accepted(replay) && receipt == replay.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                    && ledger == JsonSerializer.Serialize(f.State.Ledger.ListAll()), "Upload/confirmation retry changed its receipt or events.");
                AssertCompleted(f, replay);
            }
        }
    }

    private static void VerifyPublishedRevocation(Func<string, SaveSnapshotPackage> factory, string kind)
    {
        Action? revoke = null;
        var plugins = new ServerPluginRegistry().WithBuiltInPlugins([new("test.upload-recovery", "recovery", "1", "test", "", [])
            { SnapshotPostUploadProcessors = [new Publication(() => revoke?.Invoke())] }]);
        using var f = CreateFixture(factory, plugins); f.Start(); f.Seed();
        revoke = () => f.State.AuthTokens.RevokeForUser("receiver");
        using var response = Call(f, kind, Incoming(factory));
        Require(!Accepted(response) && f.Store.GetLatest("receiver", "away") is null, "Recovery publication revocation allowed a submitted snapshot.");
        f.AssertRecovered(); revoke = null; f.RenewToken();
        using var retry = Call(f, kind, Incoming(factory));
        Require(Accepted(retry), "New session did not resume after recovery revoked the old one."); AssertCompleted(f, retry);
    }

    private static void VerifyDelegated(Func<string, SaveSnapshotPackage> factory, string change)
    {
        Action? published = null;
        var plugins = new ServerPluginRegistry().WithBuiltInPlugins([new("test.upload-recovery", "recovery", "1", "test", "", [])
            { SnapshotPostUploadProcessors = [new Publication(() => published?.Invoke())] }]);
        using var f = CreateFixture(factory, plugins); f.Start();
        var loss = AuthoritativeEventFactory.Create(ServerEventType.Raid, new("attacker", "attack-colony"), new("receiver", "away"),
            "loss", false, new RaidEventPayload("base", null, null, null, null, AttackerLoss: new("raid-source", "base", [], [], "loss")), DateTimeOffset.UtcNow)
            with { DeliveredToSnapshotId = "base", DeliveredAtUtc = DateTimeOffset.UtcNow };
        f.State.Ledger.Append(loss);
        f.State.LoginSessions.TryCreate("attacker", "attack-colony", DateTimeOffset.UtcNow, out var session);
        f.Token = f.State.AuthTokens.Issue("attacker", "attacker", "attack-colony", session, DateTimeOffset.UtcNow);
        void ChangeActor()
        {
            using var connection = new SqliteConnection("Data Source=" + f.Database); connection.Open();
            using var sql = connection.CreateCommand(); sql.CommandText = "update events set actor_user_id = 'other' where event_id = $id";
            sql.Parameters.AddWithValue("$id", loss.EventId); sql.ExecuteNonQuery();
        }
        f.Seed(); string? before = null; int count = 0; HttpResponseMessage? response = null;
        SnapshotPreparationGateProbe.Run(f.State.RaidSettlementSnapshotMutationGate,
            hook => f.Store.BeforePrepare = hook is null ? null : () => { if (++count == 2) hook(); },
            () => response = Call(f, "single", Incoming(factory), eventId: loss.EventId, sourceId: "raid-source"), () =>
            {
                if (change == "actor") ChangeActor();
                if (change == "published-actor") published = ChangeActor;
                if (change == "logout") f.State.LoginSessions.EndUser("attacker");
                before = f.Evidence();
            });
        using (response)
        {
            Require(Accepted(response!) == (change == "none"), "Single confirmation lost or retained the wrong event grant.");
            if (change == "none")
            {
                AssertCompleted(f, response!);
                Require(f.State.Ledger.Find(loss.EventId)!.Status == ServerEventStatus.AppliedToSnapshot, "Delegated loss confirmation did not apply.");
            }
            else
            {
                if (change == "published-actor") f.AssertRecovered();
                else f.AssertPending(before!, "Delegated confirmation " + change);
                Require(f.Store.GetLatest("receiver", "away") is null && f.State.Ledger.Find(loss.EventId)!.Status != ServerEventStatus.AppliedToSnapshot,
                    "Revoked event grant still published or applied the confirmation.");
            }
        }
    }

    private static Fixture CreateFixture(Func<string, SaveSnapshotPackage> factory, ServerPluginRegistry? plugins = null)
    {
        var f = new Fixture(factory, plugins);
        f.State.Ledger.MarkDelivered(f.Source.EventId, "base", DateTimeOffset.UtcNow);
        return f;
    }

    private static SaveSnapshotPackage Incoming(Func<string, SaveSnapshotPackage> factory)
    {
        var p = factory("incoming");
        return p with { Envelope = p.Envelope with { Identity = new("receiver", "away", "incoming"),
            PreviousSnapshotId = null, LineageToken = null, NextLineageToken = null } };
    }
    private static HttpResponseMessage Call(Fixture f, string kind, SaveSnapshotPackage package, string? invalid = null, string? eventId = null, string? sourceId = null)
    {
        string user = invalid == "user" ? "other" : "receiver", colony = invalid == "colony" ? "other" : "away";
        string token = invalid == "token" ? "invalid" : f.Token;
        var metadata = ProtocolDtoMapper.ToMetadataDto(package);
        object body = kind switch
        {
            "upload" => new UploadSnapshotMetadataRequest("upload", user, colony, "incoming", metadata, token),
            "single" => new ConfirmEventApplicationMetadataRequest("confirm", eventId ?? f.Source.EventId, sourceId, user, colony, "base", metadata, "ItemDeliveryAnchored", token),
            _ => new ConfirmEventApplicationsMetadataRequest("confirm", user, colony, "base", metadata, [new(f.Source.EventId, null, "ItemDeliveryAnchored")], token)
        };
        var message = kind == "upload" ? ProtocolMessageKind.UploadSnapshot : kind == "single"
            ? ProtocolMessageKind.ConfirmEventApplication : ProtocolMessageKind.ConfirmEventApplications;
        using var content = new MultipartFormDataContent(); content.Add(JsonContent.Create(body), "request");
        content.Add(new ByteArrayContent(package.Payload), "payload", "snapshot.gz");
        using var request = new HttpRequestMessage(HttpMethod.Post, ProtocolContractManifest.Find(message).Route) { Content = content };
        request.Headers.Add(MultipartSnapshotAuthentication.HeaderName, f.Token);
        return f.Client.Send(request);
    }
    private static bool Accepted(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
        return json.RootElement.GetProperty("result").GetProperty("accepted").GetBoolean();
    }
    private static void AssertCompleted(Fixture f, HttpResponseMessage response)
    {
        Require(!f.State.SnapshotCommits.HasPending && f.Store.Writes == 2
            && f.Store.GetLatest("foreign", "foreign-colony")!.Envelope.NextLineageToken == "foreign-token"
            && f.Store.GetLatest("receiver", "away")!.Identity.SnapshotId == "incoming", "Recovery or submitted publication lost/duplicated a snapshot.");
        using var json = JsonDocument.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
        Require(json.RootElement.GetProperty("nextLineageToken").GetString() == f.Store.GetLatest("receiver", "away")!.Envelope.NextLineageToken,
            "Response lost the published lineage token.");
    }
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    private sealed class Publication(Action run) : IInlineSnapshotPostUploadProcessor
    {
        public string Id => "test.upload-recovery";
        public int Order => 350;
        public SnapshotPostUploadStage Stage => SnapshotPostUploadStage.EventReconciliation;
        public SnapshotPostUploadFailureMode FailureMode => SnapshotPostUploadFailureMode.AbortPipeline;
        public SnapshotPostUploadExecutionMode ExecutionMode => SnapshotPostUploadExecutionMode.Inline;
        public bool Supports(SnapshotPostUploadKind kind) => true;
        public void Process(SnapshotPostUploadContext context) { if (context.UserId == "foreign") run(); }
    }
}
