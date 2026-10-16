using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Network.Plugins;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using TestStore = SnapshotEndpointRecoveryPreparationTests.Store;

internal static class HttpSnapshotRecoveryTests
{
    // Exercise routing and middleware through Kestrel, not direct handler invocation.
    private static readonly ProtocolMessageKind[] OwnedRoutes =
    [
        ProtocolMessageKind.UploadSnapshot, ProtocolMessageKind.DownloadLatestSnapshot,
        ProtocolMessageKind.DownloadLatestSnapshotPayload, ProtocolMessageKind.ConfirmEventApplication,
        ProtocolMessageKind.ConfirmEventApplications, ProtocolMessageKind.ReportEventApplicationFailure,
        ProtocolMessageKind.CreateGift, ProtocolMessageKind.CreateGiftWithSnapshot, ProtocolMessageKind.RejectGift,
        ProtocolMessageKind.CreateTradeOrder, ProtocolMessageKind.CreateTradeOrderWithSnapshot,
        ProtocolMessageKind.AcceptTradeOrder, ProtocolMessageKind.FulfillTradeOrder,
        ProtocolMessageKind.FulfillTradeOrderWithSnapshot, ProtocolMessageKind.CancelTradeOrder,
        ProtocolMessageKind.CompleteTradeOrder, ProtocolMessageKind.PurchaseServerShopListingWithSnapshot,
        ProtocolMessageKind.CreateRaid, ProtocolMessageKind.CreateRaidWithSnapshot,
        ProtocolMessageKind.CreateSupportPawn, ProtocolMessageKind.CreateSupportPawnWithSnapshot,
        ProtocolMessageKind.RejectSupportPawn, ProtocolMessageKind.FinishSupportPawn,
        ProtocolMessageKind.CreateBankLoanWithSnapshot, ProtocolMessageKind.RepayBankLoanWithSnapshot,
        ProtocolMessageKind.RepayBankDebtWithSnapshot, ProtocolMessageKind.HireMercenaryWithSnapshot,
        ProtocolMessageKind.HireMercenaryGuardWithSnapshot
    ];

    public static void Run(Func<string, SaveSnapshotPackage> factory)
    {
        using (var f = new Fixture(factory))
        {
            f.Start(); f.Seed(); string before = f.Evidence();
            foreach (var kind in OwnedRoutes)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, Route(kind))
                {
                    Content = new StringContent("{\"", Encoding.UTF8, "application/json")
                };
                // Valid header must not authorize recovery for an invalid/missing body.
                request.Headers.Add(MultipartSnapshotAuthentication.HeaderName, f.Token);
                using var response = f.Client.Send(request);
                Require(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.BadRequest,
                    $"Unexpected malformed request result for {kind}: {response.StatusCode}");
                if (response.IsSuccessStatusCode) AssertRejected(response);
                f.AssertPending(before, "Malformed or retired route " + kind);
            }
            foreach (string change in new[] { "invalid-token", "wrong-user", "missing-snapshot" })
            {
                using var response = f.Post(ProtocolMessageKind.RejectGift, new RejectGiftRequest(f.Source.EventId,
                    change == "wrong-user" ? "intruder" : "receiver", "away", change == "missing-snapshot" ? "" : "base", "first",
                    change == "invalid-token" ? "invalid" : f.Token));
                AssertRejected(response); f.AssertPending(before, "Invalid JSON identity " + change);
            }
            foreach (string change in new[] { "invalid-token", "wrong-user", "invalid-header" })
            {
                using var content = new MultipartFormDataContent();
                content.Add(JsonContent.Create(new
                {
                    idempotencyKey = "borrow", userId = change == "wrong-user" ? "intruder" : "receiver", colonyId = "away",
                    currentSnapshotId = "base", authToken = change == "invalid-token" ? "invalid" : f.Token,
                    currentGameTicks = 1000, colonyWealth = 10000, principalSilver = 100, durationDays = 1,
                    requestedLoanId = "loan", expectedInterestSilver = 0
                }), "request");
                content.Add(new ByteArrayContent(f.Package.Payload), "payload", "snapshot.gz");
                using var request = new HttpRequestMessage(HttpMethod.Post, Route(ProtocolMessageKind.CreateBankLoanWithSnapshot)) { Content = content };
                request.Headers.Add(MultipartSnapshotAuthentication.HeaderName, change == "invalid-header" ? "invalid" : f.Token);
                using var response = f.Client.Send(request);
                if (change == "invalid-header") Require(response.StatusCode == HttpStatusCode.Unauthorized, "Invalid multipart header was accepted.");
                else AssertRejected(response);
                f.AssertPending(before, "Invalid multipart identity " + change);
            }
            // Existing routes retain recovery until explicitly migrated.
            using var fallback = f.Client.GetAsync(Route(ProtocolMessageKind.ServerHello)).GetAwaiter().GetResult();
            fallback.EnsureSuccessStatusCode(); f.AssertRecovered();
            using var again = f.Client.GetAsync(Route(ProtocolMessageKind.ServerHello)).GetAwaiter().GetResult();
            again.EnsureSuccessStatusCode(); f.AssertRecovered();
        }
        foreach (string change in new[] { "none", "logout", "replace", "revoke", "failure", "revoke-failure" })
        {
            using var f = new Fixture(factory); f.Start(); f.Seed(); string before = f.Evidence();
            HttpResponseMessage? response = null;
            SnapshotPreparationGateProbe.Run(f.State.RaidSettlementSnapshotMutationGate, hook => f.Store.BeforePrepare = hook,
                () => response = f.Reject(), () =>
                {
                    if (change is "logout" or "replace") f.State.LoginSessions.EndUser("receiver");
                    if (change == "replace") f.State.LoginSessions.TryCreate("receiver", "away", DateTimeOffset.UtcNow, out _);
                    if (change is "revoke" or "revoke-failure") f.State.AuthTokens.RevokeForUser("receiver");
                    if (change is "failure" or "revoke-failure") f.Store.FailPreparation = true;
                });
            using (response)
            {
                if (change == "none")
                {
                    var result = response!.Content.ReadFromJsonAsync<RejectGiftResponse>().GetAwaiter().GetResult()!;
                    Require(result.Result.Accepted && result.ReturnEventCreated, "Authorized HTTP rejection did not complete.");
                    f.AssertRecovered();
                    using var replay = f.Reject();
                    var repeated = replay.Content.ReadFromJsonAsync<RejectGiftResponse>().GetAwaiter().GetResult()!;
                    Require(repeated.Result.Accepted && !repeated.ReturnEventCreated && repeated.ReturnEventId == result.ReturnEventId
                        && f.State.Ledger.ListAll().Count == 3, "HTTP replay duplicated or replaced a return.");
                    f.AssertRecovered();
                }
                else
                {
                    if (change == "failure") Require(response!.StatusCode == HttpStatusCode.InternalServerError, "Preparation failure did not propagate.");
                    else AssertRejected(response!);
                    f.AssertPending(before, "HTTP recovery interleaving " + change);
                    // A new valid session can finish the same persisted intent after rejection/failure.
                    f.Store.FailPreparation = false; f.RenewToken();
                    using var retry = f.Reject();
                    Require(retry.Content.ReadFromJsonAsync<RejectGiftResponse>().GetAwaiter().GetResult()!.ReturnEventCreated,
                        "Valid HTTP retry did not resume pending recovery.");
                    f.AssertRecovered();
                }
            }
        }
        using (var f = new Fixture(factory))
        {
            f.Seed(); f.Start(); f.AssertRecovered(); // Startup recovery remains independent of request ownership.
        }
    }

    private static string Route(ProtocolMessageKind kind) => ProtocolContractManifest.Find(kind).Route;
    private static void AssertRejected(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
        Require(!json.RootElement.GetProperty("result").GetProperty("accepted").GetBoolean(), "Invalid HTTP request was accepted.");
    }
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }

    internal sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "cor-http-recovery-" + Guid.NewGuid().ToString("N"));
        private string Journal => Path.Combine(root, "intents");
        public string Database => Path.Combine(root, "server.sqlite");
        private WebApplication? app;
        public HttpClient Client = null!;
        public TestStore Store { get; }
        public ClashOfRimNetworkState State { get; }
        public SaveSnapshotPackage Package { get; }
        public AuthoritativeEvent Source { get; }
        private readonly AuthoritativeEvent foreignGift;
        public string Token = "";
        public Fixture(Func<string, SaveSnapshotPackage> factory, ServerPluginRegistry? plugins = null)
        {
            Directory.CreateDirectory(root);
            string database = Database; ServerDatabaseMigrator.Migrate(database);
            Store = new(Path.Combine(root, "snapshots"));
            State = new(ledger: new SqliteAuthoritativeEventLedger(database), snapshotStore: Store,
                playerRegistry: new PlayerRegistry(new SqlitePlayerRegistryStore(database), null), snapshotCommits: new SnapshotCommitService(Journal), plugins: plugins);
            Store.Gate = State.RaidSettlementSnapshotMutationGate;
            var package = factory("foreign");
            Package = package with { Envelope = package.Envelope with { Identity = new("foreign", "foreign-colony", "foreign"),
                PreviousSnapshotId = null, LineageToken = null, NextLineageToken = "foreign-token" } };
            Source = AuthoritativeEventFactory.Create(ServerEventType.ItemDelivery, new("sender", "home"), new("receiver", "away"),
                "gift", false, new ItemDeliveryEventPayload([new("cargo", "Silver", 1, "source")], "gift"), DateTimeOffset.UtcNow);
            State.Ledger.Append(Source);
            foreignGift = AuthoritativeEventFactory.Create(ServerEventType.ItemDelivery, new("foreign", "foreign-colony"), new("recipient", "recipient-colony"),
                "foreign-gift", false, new ItemDeliveryEventPayload([new("cargo", "Silver", 1, "foreign")], "foreign"), DateTimeOffset.UtcNow);
            RenewToken();
        }
        public void RenewToken()
        {
            var now = DateTimeOffset.UtcNow; State.LoginSessions.EndUser("receiver");
            State.LoginSessions.TryCreate("receiver", "away", now, out var session);
            Token = State.AuthTokens.Issue("receiver", "receiver", "away", session, now);
        }
        public void Start()
        {
            Directory.CreateDirectory(Path.Combine(root, "Localization"));
            File.Copy(Path.Combine(AppContext.BaseDirectory, "TestLocalization", "English.json"), Path.Combine(root, "Localization", "English.json"), true);
            app = ClashOfRimNetworkServer.Build(["--contentRoot", root, "--urls", "http://127.0.0.1:0", "--language=English",
                "--Logging:LogLevel:Default", "Warning"], State);
            app.StartAsync().GetAwaiter().GetResult();
            Client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()), Timeout = TimeSpan.FromSeconds(30) };
        }
        public void Seed()
        {
            using (State.EnterSnapshotMutation())
            {
                Store.FailPublication = true;
                try { State.SnapshotCommits.CommitGift(Store, State.BankLoans, State.Ledger, Package,
                    new(Package.Envelope.Identity, Package.Envelope, Package.Index, DateTimeOffset.UtcNow), foreignGift, (_, _) => { }, _ => { }); }
                catch (IOException) { }
                finally { Store.FailPublication = false; }
            }
            Require(State.SnapshotCommits.HasPending && !State.SnapshotCommits.HasPendingUploads,
                "Fixture must retain a gift intent that background upload recovery does not consume.");
        }
        public HttpResponseMessage Post(ProtocolMessageKind kind, object request)
            => Client.PostAsJsonAsync(Route(kind), request).GetAwaiter().GetResult();
        public HttpResponseMessage Reject() => Post(ProtocolMessageKind.RejectGift,
            new RejectGiftRequest(Source.EventId, "receiver", "away", "base", "first", Token));
        public string Evidence() => JsonSerializer.Serialize(new { snapshots = Store.ListLatest(), events = State.Ledger.ListAll(),
            journal = Directory.GetFiles(Journal, "*.json").Order().Select(File.ReadAllText).ToArray(),
            notifications = new[] { State.EventNotifications.GetVersion("sender"), State.EventNotifications.GetVersion("receiver"), State.EventNotifications.GetVersion("recipient") } });
        public void AssertPending(string before, string context) => Require(State.SnapshotCommits.HasPending && Store.Writes == 0
            && before == Evidence(), context + " recovered pending business before authorization.");
        public void AssertRecovered() => Require(!State.SnapshotCommits.HasPending && Store.Writes == 1
            && Store.GetLatest("foreign", "foreign-colony")!.Envelope.NextLineageToken == "foreign-token"
            && JsonSerializer.Serialize(State.Ledger.Find(foreignGift.EventId)) == JsonSerializer.Serialize(foreignGift), "HTTP recovery lost or duplicated the original intent.");
        public void Dispose()
        {
            Client?.Dispose();
            if (app is not null)
            {
                app.StopAsync().GetAwaiter().GetResult(); app.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            SqliteConnection.ClearAllPools(); Directory.Delete(root, recursive: true);
        }
    }
}
