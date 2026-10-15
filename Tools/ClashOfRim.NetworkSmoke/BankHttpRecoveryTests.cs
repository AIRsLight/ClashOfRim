using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

internal static class BankHttpRecoveryTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task RunAsync(Func<string, string?, string?, SaveSnapshotPackage> fixture)
    {
        foreach (string operation in new[] { "borrow", "loan", "debt" })
            foreach (bool injectFailure in new[] { true, false })
                await VerifyRecoveryAsync(operation, fixture, injectFailure);
        Console.WriteLine("通过：银行真实 HTTP 写入失败、重启恢复、认证、并发提交和幂等重试");
    }

    private static async Task VerifyRecoveryAsync(string operation,
        Func<string, string?, string?, SaveSnapshotPackage> fixture, bool injectFailure)
    {
        string root = Path.Combine(Path.GetTempPath(), "cor-bank-http-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string database = Path.Combine(root, "server.sqlite");
        string intents = Path.Combine(root, "intents");
        try
        {
            ServerDatabaseMigrator.Migrate(database);
            ClashOfRimNetworkState state = OpenState();
            SaveSnapshotPackage parent = fixture("before", null, null);
            LatestSnapshotRecord baseline = new SnapshotUploadReceiver(state.SnapshotStore).Receive(
                new SnapshotUploadContext("bank-user", "bank-colony", "before"), parent, DateTimeOffset.UtcNow).AcceptedSnapshot!;
            SaveSnapshotPackage child = fixture("confirmed", "before", baseline.Envelope.NextLineageToken);
            string debtId = "";
            if (operation == "loan")
            {
                state.BankLoans.Create("seed", "bank-user", "bank-colony", "before", 100, 10, 1, 1000, DateTimeOffset.UtcNow, "loan-id");
                state.BankLoans.ConfirmPendingForSnapshot("bank-user", "bank-colony", "before", 1000, DateTimeOffset.UtcNow);
            }
            if (operation == "debt")
                debtId = state.BankLoans.CreateDebt("seed", "bank-user", "bank-colony", "before", 110,
                    "Test", "test debt", "test-source", 1000, DateTimeOffset.UtcNow).DebtId;

            string route = operation == "borrow" ? "/bank/loans/create-with-snapshot"
                : operation == "loan" ? "/bank/loans/repay-with-snapshot" : "/bank/debts/repay-with-snapshot";
            string token = IssueToken(state, "bank-user");
            JsonObject request = MakeRequest(token);
            await using (var host = await Host.StartAsync(state))
            {
                // Authentication must reject before any durable business or snapshot mutation.
                using HttpResponseMessage unauthorized = await SendAsync(host.Client, route, request, child.Payload, null);
                Require(unauthorized.StatusCode == HttpStatusCode.Unauthorized, "missing header was not rejected");
                Require(state.SnapshotStore.GetLatest("bank-user", "bank-colony")!.Identity.SnapshotId == "before", "unauthorized upload advanced snapshot");
                using (HttpResponseMessage invalid = await SendAsync(host.Client, route, request, new byte[] { 1, 2, 3 }, token))
                    Require(!(await invalid.Content.ReadFromJsonAsync<JsonObject>())!["result"]!["accepted"]!.GetValue<bool>(), "invalid snapshot accepted");
                if (operation == "borrow")
                {
                    state.BankLoans.Create("foreign-key", "other-user", "other-colony", "foreign-snapshot", 100, 0, 1, 1000, DateTimeOffset.UtcNow, "taken-id");
                    JsonObject collision = MakeRequest(token);
                    collision["requestedLoanId"] = "taken-id";
                    JsonObject rejected = await ReadAsync(host.Client, collision, token);
                    Require(!rejected["result"]!["accepted"]!.GetValue<bool>(), "occupied loan ID silently replaced");
                }
                Require(state.SnapshotStore.GetLatest("bank-user", "bank-colony")!.Identity.SnapshotId == "before", "rejected request advanced snapshot");
                if (injectFailure)
                {
                    Execute(database, $"create trigger fail_bank_http before insert on server_bank_{(operation == "debt" ? "debts" : "loans")} begin select raise(abort, 'injected bank HTTP failure'); end;");
                    using HttpResponseMessage failed = await SendAsync(host.Client, route, request, child.Payload, token);
                    Require(failed.StatusCode == HttpStatusCode.InternalServerError, "bank SQL fault did not reach HTTP boundary: " + await failed.Content.ReadAsStringAsync());
                    Require(Directory.GetFiles(intents, "*.json").Length == 1, "failed commit lost recovery intent");
                    Require(operation == "borrow" ? state.BankLoans.GetActive("bank-user", "bank-colony") is null
                        : operation == "loan" ? state.BankLoans.GetActive("bank-user", "bank-colony")?.Status == BankLoanRegistry.StatusActive
                        : state.BankLoans.FindDebt("bank-user", "bank-colony", debtId)?.Status == BankLoanRegistry.StatusActive,
                        "failed SQL write leaked into bank cache");
                }
                else
                {
                    // Concurrent same-key requests must replay the winner even if their preparation saw the advanced snapshot.
                    JsonObject[] first = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => ReadAsync(host.Client, MakeRequest(token), token)));
                    Require(first.All(reply => reply["result"]!["accepted"]!.GetValue<bool>())
                        && first.Count(reply => reply["result"]!["accepted"]!.GetValue<bool>()
                        && reply["result"]!["errorCode"]!.GetValue<int>() == (int)ProtocolErrorCode.None) == 1, "concurrent first requests did not commit exactly once");
                }
            }

            if (injectFailure) Execute(database, "drop trigger fail_bank_http;");
            // New registries, file stores, journal and Kestrel instance; no old bank cache survives.
            state = OpenState();
            await using (var host = await Host.StartAsync(state))
            {
                Require(!Directory.EnumerateFiles(intents, "*.json").Any(), "startup did not finish recovery");
                LatestSnapshotRecord recovered = state.SnapshotStore.GetLatest("bank-user", "bank-colony")!;
                Require(recovered.Identity.SnapshotId == "confirmed", "startup did not recover snapshot");
                Require(((IColonySnapshotPackageStore)state.SnapshotStore).GetLatestPackage("bank-user", "bank-colony")!.Payload.SequenceEqual(child.Payload), "recovery changed snapshot payload");
                token = IssueToken(state, "bank-user");
                request = MakeRequest(token);
                JsonObject[] replies = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => ReadAsync(host.Client, request, token)));
                foreach (JsonObject reply in replies)
                {
                    Require(reply["result"]!["accepted"]!.GetValue<bool>(), "committed retry rejected: " + reply);
                    Require(reply["result"]!["errorCode"]!.GetValue<int>() == (int)ProtocolErrorCode.DuplicateRequest, "retry was applied twice");
                    Require(reply["appliedSnapshotId"]!.GetValue<string>() == "confirmed"
                        && reply["nextLineageToken"]!.GetValue<string>() == recovered.Envelope.NextLineageToken, "retry lost lineage");
                    Require(reply["silverDelta"]!.GetValue<int>() == (operation == "borrow" ? 100 : -110), "retry changed bank amount");
                    // Exercise the actual HTTP response constructor contracts too.
                    if (operation == "debt") Require(reply.Deserialize<BankDebtResponse>(JsonOptions)!.Debt!.Status == BankLoanRegistry.StatusRepaid, "debt response deserialization failed");
                    else Require(reply.Deserialize<BankLoanResponse>(JsonOptions)!.Loan!.Status == (operation == "borrow" ? BankLoanRegistry.StatusActive : BankLoanRegistry.StatusRepaid), "loan response deserialization failed");
                }
                string foreignToken = IssueToken(state, "other-user");
                JsonObject forged = MakeRequest(foreignToken);
                using HttpResponseMessage denied = await SendAsync(host.Client, route, forged, child.Payload, foreignToken);
                Require(!denied.IsSuccessStatusCode || !(await denied.Content.ReadFromJsonAsync<JsonObject>())!["result"]!["accepted"]!.GetValue<bool>(), "foreign principal replay accepted");
                foreach ((string field, JsonNode value) in ChangedTerms())
                {
                    JsonObject changed = MakeRequest(token);
                    changed[field] = value;
                    JsonObject reply = await ReadAsync(host.Client, changed, token);
                    Require(!reply["result"]!["accepted"]!.GetValue<bool>(), "changed retry accepted: " + field);
                }
                JsonObject changedSnapshot = MakeRequest(token);
                changedSnapshot["confirmedSnapshot"]!["snapshotId"] = "other-snapshot";
                Require(!(await ReadAsync(host.Client, changedSnapshot, token))["result"]!["accepted"]!.GetValue<bool>(), "changed snapshot replay accepted");
                Require(state.SnapshotStore.GetLatest("bank-user", "bank-colony")!.Envelope.NextLineageToken == recovered.Envelope.NextLineageToken, "retry rewrote snapshot");
            }
            // A second clean restart must not repeat business effects or recreate the intent.
            BankLoanRecord? committedLoan = state.BankLoans.FindByIdempotencyKey("operation");
            BankDebtRecord? committedDebt = state.BankLoans.FindDebtByIdempotencyKey("operation");
            Require(operation == "debt" ? committedDebt?.Status == BankLoanRegistry.StatusRepaid
                : committedLoan?.Status == (operation == "borrow" ? BankLoanRegistry.StatusActive : BankLoanRegistry.StatusRepaid), "recovered business state missing");
            state = OpenState();
            await using (var host = await Host.StartAsync(state))
            {
                Require(!Directory.EnumerateFiles(intents, "*.json").Any(), "clean restart recreated an intent");
                Require(state.BankLoans.FindByIdempotencyKey("operation") == committedLoan
                    && state.BankLoans.FindDebtByIdempotencyKey("operation") == committedDebt, "clean restart changed bank records");
            }

            JsonObject MakeRequest(string auth)
            {
                SnapshotPackageMetadataDto metadata = ProtocolDtoMapper.ToMetadataDto(child);
                object dto = operation == "borrow"
                    ? new CreateBankLoanWithSnapshotRequest("operation", "bank-user", "bank-colony", "before", auth, 1100, 10000, 100, 1, "loan-id", 0, metadata)
                    : operation == "loan"
                        ? new RepayBankLoanWithSnapshotRequest("operation", "bank-user", "bank-colony", "before", auth, 1100, "loan-id", 110, metadata)
                        : new RepayBankDebtWithSnapshotRequest("operation", "bank-user", "bank-colony", "before", auth, 1100, debtId, 110, metadata);
                return JsonSerializer.SerializeToNode(dto, JsonOptions)!.AsObject();
            }
            IEnumerable<(string, JsonNode)> ChangedTerms() => operation == "borrow"
                ? new (string, JsonNode)[] { ("requestedLoanId", JsonValue.Create("other-loan")!), ("principalSilver", JsonValue.Create(101)!), ("durationDays", JsonValue.Create(2)!), ("expectedInterestSilver", JsonValue.Create(1)!) }
                : new (string, JsonNode)[] { (operation == "loan" ? "loanId" : "debtId", JsonValue.Create("other-id")!), ("silverPaid", JsonValue.Create(0)!) };
            async Task<JsonObject> ReadAsync(HttpClient client, JsonObject body, string auth)
            {
                using HttpResponseMessage response = await SendAsync(client, route, body, child.Payload, auth);
                response.EnsureSuccessStatusCode();
                return (await response.Content.ReadFromJsonAsync<JsonObject>())!;
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }

        ClashOfRimNetworkState OpenState() => new(
            snapshotStore: new FileColonySnapshotIndexStore(Path.Combine(root, "snapshots")),
            bankLoans: new BankLoanRegistry(new SqliteDomainKeyedJsonRecordStore(database, SqliteDomainRegistrySchema.BankLoans), null),
            snapshotCommits: new SnapshotCommitService(intents),
            serverConfiguration: new ClashOfRimServerConfiguration(bankLoansEnabled: true, bankMinLoanSilver: 1,
                bankMinDurationDays: 1, bankBaseAnnualInterestRate: 0));
    }

    private static string IssueToken(ClashOfRimNetworkState state, string user)
    {
        state.LoginSessions.TryCreate(user, "bank-colony", DateTimeOffset.UtcNow, out string session);
        return state.AuthTokens.Issue(user, user, "bank-colony", session, DateTimeOffset.UtcNow);
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string route, JsonObject request, byte[] payload, string? token)
    {
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json"), "request");
        content.Add(new ByteArrayContent(payload), "payload", "snapshot.payload");
        using var message = new HttpRequestMessage(HttpMethod.Post, route) { Content = content };
        if (token is not null) message.Headers.Add(MultipartSnapshotTransport.AuthenticationHeaderName, token);
        return await client.SendAsync(message);
    }

    private static void Execute(string database, string sql)
    {
        using var connection = new SqliteConnection("Data Source=" + database);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception("Bank HTTP: " + message);
    }

    private sealed class Host(WebApplication app, HttpClient client) : IAsyncDisposable
    {
        public HttpClient Client { get; } = client;
        public static async Task<Host> StartAsync(ClashOfRimNetworkState state)
        {
            WebApplication app = ClashOfRimNetworkServer.Build(Array.Empty<string>(), state);
            try
            {
                app.Urls.Add("http://127.0.0.1:0");
                await app.StartAsync();
                string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
                return new Host(app, new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(30) });
            }
            catch { await app.DisposeAsync(); throw; }
        }
        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
