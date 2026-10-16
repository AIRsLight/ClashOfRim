using System.Net;
using System.Text;
using System.Text.Json;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;

internal static class RetiredEconomyEndpointTests
{
    private static readonly ProtocolMessageKind[] Routes = [ProtocolMessageKind.CreateBankLoan,
        ProtocolMessageKind.RepayBankLoan, ProtocolMessageKind.RepayBankDebt, ProtocolMessageKind.HireMercenary];
    public static void Run(Func<string, SaveSnapshotPackage> factory)
    {
        int count = 0;
        foreach (string phase in new[] { "foreign", "other-colony", "new", "pending", "active", "repayment", "repaid" })
        using (var e = new Env(factory, phase))
        {
            foreach (var kind in Routes)
            foreach (string variant in new[] { "valid", "fresh-key", "repayment-key", "missing-token", "bad-token", "wrong-user", "wrong-colony", "missing-snapshot", "logout", "replaced", "expired" })
            {
                using var response = e.Call(kind, variant); Reject(response, kind); e.Unchanged(); count++;
            }
        }
        using (var e = new Env(factory, "pending"))
        {
            foreach (var kind in Routes)
            foreach (string body in new[] { "", "null", "{}", "{\"", "[]", "{\"principalSilver\":\"bad-type\"}" })
            {
                using var message = new HttpRequestMessage(HttpMethod.Post, Route(kind)) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
                message.Headers.Add(MultipartSnapshotTransport.AuthenticationHeaderName, e.F.Token);
                using var response = e.F.Client.Send(message); Reject(response, kind); e.Unchanged(); count++;
            }
            Task.WaitAll(Routes.Select(kind => Task.Run(() => { using var response = e.Call(kind, "valid", renew: false); Reject(response, kind); })).ToArray());
            e.Unchanged(); count++;
        }
        Console.WriteLine($"Retired economy endpoint scenarios passed: {count}");
    }

    private static void Reject(HttpResponseMessage response, ProtocolMessageKind kind)
    {
        Require(response.StatusCode == HttpStatusCode.OK, "Retired economy route did not return the upgrade response: " + kind);
        using var json = JsonDocument.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
        var body = json.RootElement; var result = body.GetProperty("result");
        Require(!result.GetProperty("accepted").GetBoolean(), "Retired economy route accepted or disclosed an old transaction: " + kind);
        // Deserialize the actual response contract rather than relying on enum JSON representation.
        var protocol = result.Deserialize<ProtocolResponse>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Require(protocol.ErrorCode == ProtocolErrorCode.ServerRejected && protocol.Message?.Contains("confirmed save", StringComparison.Ordinal) == true, "Missing upgrade explanation: " + kind);
        foreach (string property in kind == ProtocolMessageKind.HireMercenary
            ? new[] { "contract", "bankStatus", "appliedSnapshotId", "nextLineageToken" }
            : new[] { kind == ProtocolMessageKind.RepayBankDebt ? "debt" : "loan", "status", "appliedSnapshotId", "nextLineageToken" })
            Require(!body.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null, "Retired response disclosed " + property);
        if (kind != ProtocolMessageKind.HireMercenary) Require(body.GetProperty("silverDelta").GetInt32() == 0, "Retired response transferred silver.");
    }
    private static string Route(ProtocolMessageKind kind) => ProtocolContractManifest.Find(kind).Route;
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }

    private sealed class Env : IDisposable
    {
        public readonly HttpSnapshotRecoveryTests.Fixture F;
        private readonly string bankBefore, before;
        private readonly string debtId;
        public Env(Func<string, SaveSnapshotPackage> factory, string phase)
        {
            F = new(factory); var now = DateTimeOffset.UtcNow;
            var p = factory("base"); var envelope = p.Envelope with { Identity = new("receiver", "away", "base"), GameTicks = 1000 };
            F.Store.StoreLatest(new LatestSnapshotRecord(envelope.Identity, envelope, p.Index, now));
            string user = phase == "foreign" ? "foreign-owner" : "receiver", colony = phase == "other-colony" ? "another" : "away";
            if (phase != "new")
            {
                var loan = F.State.BankLoans.Create("operation", user, colony, "base", 100, 10, 7, 1000, now);
                var debt = F.State.BankLoans.CreateDebt("operation", user, colony, "base", 100, "Fine", "fixture", "fixture", 1000, now, pendingActivation: true);
                debtId = debt.DebtId;
                F.State.MercenaryContracts.Create("operation", user, colony, "base", "Shooting", 7, 7, 100, 100, 100, 1000, now);
                if (phase is "active" or "repayment" or "repaid")
                {
                    F.State.BankLoans.ConfirmPendingForSnapshot(user, colony, "base", 1000, now);
                    F.State.MercenaryContracts.ConfirmPendingForSnapshot(user, colony, "base", now);
                }
                if (phase == "repayment")
                {
                    F.State.BankLoans.MarkRepaymentPending("repayment-key", user, colony, 1000, now);
                    F.State.BankLoans.MarkDebtRepaymentPending("repayment-key", user, colony, debtId, 1000, now);
                }
                if (phase == "repaid") F.State.BankLoans.MarkRepaidWithSnapshot("repayment-key", user, colony, loan.LoanId, "base", 110, 1000, now);
            }
            else debtId = "missing";
            F.Start(); F.Seed();
            before = F.Evidence(); bankBefore = BankEvidence();
        }
        public HttpResponseMessage Call(ProtocolMessageKind kind, string variant, bool renew = true)
        {
            if (renew) F.RenewToken();
            if (variant is "logout" or "replaced" or "expired") F.State.LoginSessions.EndUser("receiver");
            if (variant is "replaced" or "expired")
            {
                F.State.LoginSessions.TryCreate("receiver", "away", variant == "expired" ? DateTimeOffset.UtcNow.AddHours(-1) : DateTimeOffset.UtcNow, out var session);
                if (variant == "expired") F.Token = F.State.AuthTokens.Issue("receiver", "receiver", "away", session, DateTimeOffset.UtcNow);
            }
            string user = variant == "wrong-user" ? "intruder" : "receiver", colony = variant == "wrong-colony" ? "intruder" : "away";
            string snapshot = variant == "missing-snapshot" ? "" : "base", key = variant is "fresh-key" or "repayment-key" ? variant : "operation";
            string? token = variant == "missing-token" ? null : variant == "bad-token" ? "invalid" : F.Token;
            object request = kind switch
            {
                ProtocolMessageKind.CreateBankLoan => new CreateBankLoanRequest(key, user, colony, snapshot, token, 1000, 10000, 100, 7),
                ProtocolMessageKind.RepayBankLoan => new RepayBankLoanRequest(key, user, colony, snapshot, token, 1000, 1000),
                ProtocolMessageKind.RepayBankDebt => new RepayBankDebtRequest(key, user, colony, snapshot, token, 1000, debtId, 1000),
                _ => new HireMercenaryRequest(key, user, colony, snapshot, token, 1000, "Shooting", 7, 7)
            };
            return F.Post(kind, request);
        }
        private string BankEvidence() => JsonSerializer.Serialize(new
        {
            loans = new[] { "operation", "fresh-key", "repayment-key" }.Select(F.State.BankLoans.FindByIdempotencyKey),
            debts = new[] { "operation", "fresh-key", "repayment-key" }.Select(F.State.BankLoans.FindDebtByIdempotencyKey),
            contracts = new[] { "operation", "fresh-key", "repayment-key" }.Select(F.State.MercenaryContracts.FindByIdempotencyKey)
        });
        public void Unchanged()
        {
            F.AssertPending(before, "Retired economy HTTP request");
            Require(bankBefore == BankEvidence(), "Retired economy request changed loans, debts or mercenary contracts.");
        }
        public void Dispose() => F.Dispose();
    }
}
