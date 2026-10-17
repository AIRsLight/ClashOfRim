using static RecoveryTestSupport;
using System.Net;
using System.Text.Json;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;

internal static class BankRequestRecoveryTests
{
    private static readonly ProtocolMessageKind[] Routes = [ProtocolMessageKind.GetBankStatus, ProtocolMessageKind.QuoteMercenary,
        ProtocolMessageKind.QuoteMercenaryGuard, ProtocolMessageKind.ReportMercenaryIncident];
    public static void Run(Func<string, SaveSnapshotPackage> factory)
    {
        int count = 0; void Case(Action action) { action(); count++; }
        foreach (var kind in Routes)
        {
            foreach (string invalid in new[] { "missing-token", "token", "user", "colony", "missing-user", "missing-colony", "missing-snapshot", "stale", "no-snapshot", "no-time", "logout", "replace", "expired", "malformed", "empty", "null" })
                Case(() => Invalid(factory, kind, invalid));
            foreach (string change in new[] { "none", "logout", "replace", "revoke", "expired", "snapshot", "time", "failure", "revoke-failure", "compete", "loan" })
                Case(() => Preparing(factory, kind, change));
            foreach (string change in new[] { "revoke", "replace", "snapshot", "time" }) Case(() => Published(factory, kind, change));
        }
        foreach (string invalid in new[] { "skill", "level", "duration" }) Case(() => Invalid(factory, ProtocolMessageKind.QuoteMercenary, invalid));
        foreach (string invalid in new[] { "key", "contract", "incident" }) Case(() => Invalid(factory, ProtocolMessageKind.ReportMercenaryIncident, invalid));
        Case(() => ReadOnlyContext(factory)); Case(() => GuardDefault(factory)); Case(() => Incidents(factory));
        foreach (string change in new[] { "disabled", "limit", "price", "duration", "guard-price", "contract" }) Case(() => Policy(factory, change));
        Case(() => CleanupFailure(factory));
        Console.WriteLine($"Bank request recovery scenarios passed: {count}");
    }
    private static void Invalid(Func<string, SaveSnapshotPackage> factory, ProtocolMessageKind kind, string invalid)
    {
        using var e = new Env(factory, invalid); Change(e, invalid);
        string before = e.F.Evidence(), bank = e.Bank(); using var r = e.Call(kind, invalid);
        if (invalid is "malformed" or "empty" or "null") Require(r.StatusCode == HttpStatusCode.BadRequest, "Malformed bank request accepted.");
        else Denied(r);
        e.F.AssertPending(before, "bank/" + kind + "/" + invalid); Require(bank == e.Bank(), "Invalid request reconciled pending bank records.");
    }
    private static void Preparing(Func<string, SaveSnapshotPackage> factory, ProtocolMessageKind kind, string change)
    {
        using var e = new Env(factory); string? before = null, bank = null; HttpResponseMessage? r = null;
        SnapshotPreparationGateProbe.Run(e.F.State.RaidSettlementSnapshotMutationGate, hook => e.F.Store.BeforePrepare = hook,
            () => r = e.Call(kind), () =>
            {
                Change(e, change);
                if (change is "failure" or "revoke-failure") e.F.Store.FailPreparation = true;
                if (change == "compete") ClashOfRimNetworkServer.RecoverSnapshotCommits(e.F.State);
                if (change == "loan") e.F.State.BankLoans.ConfirmPendingForSnapshot("receiver", "away", "base", 1000, DateTimeOffset.UtcNow);
                before = e.F.Evidence(); bank = e.Bank();
            });
        using (r)
        {
            if (change is "none" or "compete" or "loan")
            {
                Require(Accepted(r!), "Valid bank request failed after preparation: " + kind); e.F.AssertRecovered();
                e.AssertReconciled(change == "loan");
                using (var j = JsonDocument.Parse(r!.Content.ReadAsStringAsync().GetAwaiter().GetResult()))
                {
                    var status = kind == ProtocolMessageKind.GetBankStatus ? j.RootElement : j.RootElement.GetProperty("bankStatus");
                    Require(change == "loan" ? status.GetProperty("activeLoan").GetProperty("loanId").GetString() == e.LoanId
                        : !status.TryGetProperty("activeLoan", out var loan) || loan.ValueKind == JsonValueKind.Null, "Response used a stale active-loan context.");
                }
            }
            else
            {
                if (change == "failure") Require(r!.StatusCode == HttpStatusCode.InternalServerError, "Preparation failure swallowed."); else Denied(r!);
                e.F.AssertPending(before!, "bank/preparation/" + change); Require(e.Bank() == bank, "Failed request cleaned up bank records.");
                e.F.Store.FailPreparation = false; e.SetSnapshot(); e.F.RenewToken();
                using var retry = e.Call(kind); Require(Accepted(retry), "Fresh request failed to recover."); e.F.AssertRecovered(); e.AssertReconciled();
            }
        }
    }
    private static void Published(Func<string, SaveSnapshotPackage> factory, ProtocolMessageKind kind, string change)
    {
        using var e = new Env(factory); string bank = e.Bank(); e.Publication = () => Change(e, change);
        using var r = e.Call(kind); Denied(r); e.F.AssertRecovered(); Require(bank == e.Bank(), "Recovery publication bypassed bank revalidation.");
        e.Publication = null; e.SetSnapshot(); e.F.RenewToken(); using var retry = e.Call(kind); Require(Accepted(retry), "Post-publication retry failed."); e.AssertReconciled();
    }
    private static void ReadOnlyContext(Func<string, SaveSnapshotPackage> factory)
    {
        using var e = new Env(factory); string bank = e.Bank(), before = e.F.Evidence();
        var context = e.F.State.BankRequests.ReadBankRequestContext("receiver", "away", "base", e.F.Token,
            0L, 10000, DateTimeOffset.UtcNow, out var rejection);
        Require(context is not null && rejection is null, "Valid read-only bank context rejected.");
        e.F.AssertPending(before, "read-only context"); Require(e.Bank() == bank, "Read-only context reconciled bank state.");
        bool denied = false;
        try { e.F.State.PendingConfirmations.ReconcileSnapshot("receiver", "away", DateTimeOffset.UtcNow); }
        catch (InvalidOperationException) { denied = true; }
        Require(denied && bank == e.Bank(), "Bank reconciliation ran outside the mutation scope.");
    }
    private static void Policy(Func<string, SaveSnapshotPackage> factory, string change)
    {
        using var e = new Env(factory); HttpResponseMessage? r = null; string before = e.F.Evidence(), bank = e.Bank();
        var kind = change == "guard-price" ? ProtocolMessageKind.QuoteMercenaryGuard : change == "contract" ? ProtocolMessageKind.ReportMercenaryIncident : ProtocolMessageKind.QuoteMercenary;
        SnapshotPreparationGateProbe.Run(e.F.State.RaidSettlementSnapshotMutationGate, hook => e.F.Store.BeforePrepare = hook,
            () => r = e.Call(kind), () =>
            {
                if (change == "contract") e.F.State.MercenaryContracts.Complete("contract", "receiver", "away");
                else e.F.State.UpdateServerConfiguration(new(pvpEnabled: true, mercenariesEnabled: change != "disabled", maxActiveMercenariesPerColony: change == "limit" ? 1 : 10,
                    mercenaryApprenticeDailySilver: 999, mercenaryGuardApprenticeSilver: 999, mercenaryMinDurationDays: change == "duration" ? 8 : 1,
                    pendingConfirmationTimeout: TimeSpan.FromMinutes(1)));
            });
        using (r)
        {
            Require(Accepted(r!) == (change is "price" or "guard-price"), "Response ignored current policy or contract: " + change);
            if (change is "price" or "guard-price")
            {
                using var j = JsonDocument.Parse(r!.Content.ReadAsStringAsync().GetAwaiter().GetResult());
                Require(change == "price" ? j.RootElement.GetProperty("priceSilver").GetInt32() > 1000 : j.RootElement.GetProperty("priceSilver").GetInt32() == 999, "Quote retained stale prices.");
            }
        }
        if (change == "duration") { e.F.AssertPending(before, "changed quote duration"); Require(bank == e.Bank(), "Rejected duration reconciled records."); }
        else { e.F.AssertRecovered(); e.AssertReconciled(); }
    }
    private static void CleanupFailure(Func<string, SaveSnapshotPackage> factory)
    {
        using var e = new Env(factory); var slot = new Slot(); var bank = new BankLoanRegistry(slot);
        bank.Create("old", "receiver", "away", "base", 100, 1, 7, 1000, DateTimeOffset.UtcNow.AddDays(-2));
        var state = new ClashOfRimNetworkState(snapshotStore: e.F.Store, bankLoans: bank);
        state.LoginSessions.TryCreate("receiver", "away", DateTimeOffset.UtcNow, out var session);
        string token = state.AuthTokens.Issue("receiver", "receiver", "away", session, DateTimeOffset.UtcNow);
        var request = new GetBankStatusRequest("receiver", "away", "base", token, 1000, 10000);
        slot.Fail = true; bool failed = false;
        try { state.Bank.GetBankStatus(request); } catch (IOException) { failed = true; }
        Require(failed && bank.FindByIdempotencyKey("old") is not null, "Cleanup failure lost pending record.");
        bool released = Task.Run(() => { if (!Monitor.TryEnter(state.RaidSettlementSnapshotMutationGate, TimeSpan.FromSeconds(2))) return false;
            Monitor.Exit(state.RaidSettlementSnapshotMutationGate); return true; }).GetAwaiter().GetResult();
        Require(released, "Cleanup failure leaked the mutation scope.");
        slot.Fail = false; var result = state.Bank.GetBankStatus(request);
        Require(result.Result.Accepted && result.ActiveLoan is null && bank.FindByIdempotencyKey("old") is null, "Cleanup retry retained a stale context.");
    }
    private static void GuardDefault(Func<string, SaveSnapshotPackage> factory)
    {
        using var e = new Env(factory); using var r = e.Call(ProtocolMessageKind.QuoteMercenaryGuard, "unknown-tier");
        Require(Accepted(r), "Legacy guard tier fallback changed."); using var j = JsonDocument.Parse(r.Content.ReadAsStringAsync().GetAwaiter().GetResult());
        Require(j.RootElement.GetProperty("tier").GetString() == "Apprentice", "Unknown guard tier no longer defaults to Apprentice.");
    }
    private static void Incidents(Func<string, SaveSnapshotPackage> factory)
    {
        foreach (string incident in new[] { "Death", "HostileBehavior", "HarmfulSurgery", "OvertimeService", "ShuttleDestroyed", "Completed" })
        {
            using var e = new Env(factory); using var r = e.Call(ProtocolMessageKind.ReportMercenaryIncident, incident);
            Require(Accepted(r), "Incident failed: " + incident);
            using var retry = e.Call(ProtocolMessageKind.ReportMercenaryIncident, incident); Require(Accepted(retry), "Incident retry failed.");
            var debt = e.F.State.BankLoans.FindDebtByIdempotencyKey("incident");
            Require(incident == "Completed" ? debt is null && e.F.State.MercenaryContracts.Find("contract")!.Status == MercenaryContractRegistry.StatusCompleted
                : debt is { Status: BankLoanRegistry.StatusPendingActivation } && debt.Reason == "Mercenary:" + incident
                    && e.F.State.MercenaryContracts.FindActive("contract") is not null, "Incident lost confirmation semantics.");
        }
    }
    private static void Change(Env e, string change)
    {
        var f = e.F;
        if (change is "logout" or "replace" or "expired") f.State.LoginSessions.EndUser("receiver");
        if (change is "replace" or "expired") { f.State.LoginSessions.TryCreate("receiver", "away", change == "expired" ? DateTimeOffset.UtcNow.AddHours(-1) : DateTimeOffset.UtcNow, out var session);
            if (change == "expired") f.Token = f.State.AuthTokens.Issue("receiver", "receiver", "away", session, DateTimeOffset.UtcNow); }
        if (change is "revoke" or "revoke-failure") f.State.AuthTokens.RevokeForUser("receiver");
        if (change == "snapshot") e.SetSnapshot("newer");
        if (change == "time") e.SetSnapshot(ticks: 0);
    }
    private static bool Accepted(HttpResponseMessage r)
    { r.EnsureSuccessStatusCode(); using var j = JsonDocument.Parse(r.Content.ReadAsStringAsync().GetAwaiter().GetResult()); return j.RootElement.GetProperty("result").GetProperty("accepted").GetBoolean(); }
    private static void Denied(HttpResponseMessage r)
    {
        Require(!Accepted(r), "Invalid bank request accepted."); using var j = JsonDocument.Parse(r.Content.ReadAsStringAsync().GetAwaiter().GetResult());
        foreach (string field in new[] { "activeLoan", "debt", "bankStatus" }) Require(!j.RootElement.TryGetProperty(field, out var v) || v.ValueKind == JsonValueKind.Null, "Denied request disclosed " + field);
    }
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    private sealed class Env : IDisposable
    {
        public readonly HttpSnapshotRecoveryTests.Fixture F; public readonly string LoanId; private readonly SaveSnapshotPackage package; public Action? Publication;
        public Env(Func<string, SaveSnapshotPackage> factory, string invalid = "")
        {
            var plugins = PublicationPlugins("test.bank-read", () => Publication?.Invoke());
            F = new(factory, plugins); package = factory("base"); F.Start();
            F.State.UpdateServerConfiguration(new(pvpEnabled: true, mercenariesEnabled: true, mercenaryGuardsEnabled: true, pendingConfirmationTimeout: TimeSpan.FromMinutes(1)));
            if (invalid != "no-snapshot") SetSnapshot(ticks: invalid == "no-time" ? 0 : 1000);
            LoanId = F.State.BankLoans.Create("pending", "receiver", "away", "base", 100, 10, 7, 1000, DateTimeOffset.UtcNow.AddHours(-1)).LoanId;
            F.State.MercenaryContracts.ActivateWithSnapshot("hire", "receiver", "away", "contract", "base", "Shooting", 7, 7, 560, 1000, 1500, 1000, DateTimeOffset.UtcNow);
            F.Seed();
        }
        public void SetSnapshot(string id = "base", long ticks = 1000)
        { var envelope = package.Envelope with { Identity = new("receiver", "away", id), GameTicks = ticks }; F.Store.StoreLatest(new LatestSnapshotRecord(envelope.Identity, envelope, package.Index, DateTimeOffset.UtcNow)); }
        public string Bank() => JsonSerializer.Serialize(new { loan = F.State.BankLoans.FindByIdempotencyKey("pending"), contract = F.State.MercenaryContracts.Find("contract"), debt = F.State.BankLoans.FindDebtByIdempotencyKey("incident") });
        public void AssertReconciled(bool active = false) => Require(active ? F.State.BankLoans.FindByIdempotencyKey("pending")?.Status == BankLoanRegistry.StatusActive : F.State.BankLoans.FindByIdempotencyKey("pending") is null, "Authorized request did not reconcile the latest pending loan state.");
        public HttpResponseMessage Call(ProtocolMessageKind kind, string invalid = "")
        {
            if (invalid is "malformed" or "empty" or "null") return PostInvalidBody(F.Client, kind, invalid);
            return F.Post(kind, new { userId = invalid == "user" ? "intruder" : invalid == "missing-user" ? "" : "receiver",
                colonyId = invalid == "colony" ? "intruder" : invalid == "missing-colony" ? "" : "away", currentSnapshotId = invalid == "stale" ? "old" : invalid == "missing-snapshot" ? "" : "base",
                authToken = invalid == "missing-token" ? null : invalid == "token" ? "invalid" : F.Token, currentGameTicks = 0, colonyWealth = 10000,
                skillDefName = invalid == "skill" ? "unknown" : "Shooting", skillLevel = invalid == "level" ? 3 : 7, durationDays = invalid == "duration" ? -1 : 7,
                tier = invalid == "unknown-tier" ? "unknown" : "Apprentice", idempotencyKey = invalid == "key" ? "" : "incident", contractId = invalid == "contract" ? "" : "contract",
                incidentKind = invalid == "incident" ? "unknown" : invalid is "Death" or "HostileBehavior" or "HarmfulSurgery" or "OvertimeService" or "ShuttleDestroyed" or "Completed" ? invalid : "HarmfulSurgery" });
        }
        public void Dispose() => F.Dispose();
    }
}
