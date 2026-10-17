using System.Globalization;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Network.Modules.Authorization;
using AIRsLight.ClashOfRim.Network.Modules.Bank;
using AIRsLight.ClashOfRim.Network.Modules.Economy;
using AIRsLight.ClashOfRim.Network.Modules.Snapshots;
using AIRsLight.ClashOfRim.Network.Plugins;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using Microsoft.AspNetCore.Http;

internal static class BankModuleTests
{
    public static void Run(Func<string, SaveSnapshotPackage> factory)
    {
        SnapshotEndpointTestClient.LoadLocalization();
        var culture = CultureInfo.CurrentCulture;
        try
        {
            foreach (string name in new[] { "en-US", "zh-CN", "tr-TR", "fr-FR" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
                ReadAndBorrow(factory);
                Repay(factory, debt: false);
                Repay(factory, debt: true);
                RecoveryInvalidatesAuthorization(factory);
            }
        }
        finally { CultureInfo.CurrentCulture = culture; }
        foreach (var result in new[] { BankEndpoints.CreateBankLoan(), BankEndpoints.RepayBankLoan(), BankEndpoints.RepayBankDebt() })
        {
            object value = ((IValueHttpResult)result).Value!;
            var response = value is BankLoanResponse loan ? loan.Result : ((BankDebtResponse)value).Result;
            Require(!response.Accepted && response.ErrorCode == ProtocolErrorCode.ServerRejected, "Legacy bank route accepted a write.");
        }
    }

    private static void ReadAndBorrow(Func<string, SaveSnapshotPackage> factory)
    {
        var f = new Fixture(factory);
        var statusRequest = new GetBankStatusRequest("userA", "colonyA", "before", f.Token, 1100, 10000);
        var rejected = f.Bank.GetBankStatus(new("userA", "colonyA", "before", "invalid", 1100, 10000));
        Require(!rejected.Result.Accepted && f.Recovery.Replays == 0, "Invalid status request started recovery.");
        var context = f.Access.ReadBankRequestContext("userA", "colonyA", "before", f.Token, 1100, 10000,
            DateTimeOffset.UtcNow, out var rejection);
        Require(context is not null && rejection is null && f.Recovery.Replays == 0, "Read-only context started recovery.");
        Require(f.Bank.GetBankStatus(statusRequest).Result.Accepted && f.Recovery.Replays == 1, "Status did not use recovered admission.");

        var request = new CreateBankLoanWithSnapshotRequest("borrow", "userA", "colonyA", "before", f.Token,
            1100, 10000, 100, 1, "loan", 0, ProtocolDtoMapper.ToMetadataDto(f.Package));
        int before = f.Recovery.Replays;
        Require(!f.Bank.CreateBankLoanWithSnapshot(request, f.Package.Payload, "invalid").Result.Accepted
            && f.Recovery.Replays == before, "Invalid transport credential started recovery.");
        var invalidBody = new CreateBankLoanWithSnapshotRequest("borrow", "userA", "colonyA", "before", "invalid",
            1100, 10000, 100, 1, "loan", 0, request.ConfirmedSnapshot);
        Require(!f.Bank.CreateBankLoanWithSnapshot(invalidBody, f.Package.Payload, f.Token).Result.Accepted
            && f.Recovery.Replays == before, "Invalid body credential started recovery.");
        var success = f.Bank.CreateBankLoanWithSnapshot(request, f.Package.Payload, f.Token);
        Require(success.Result.Accepted && success.SilverDelta == 100 && success.Loan?.LoanId == "loan"
            && f.Recovery.Commits == 1 && f.Snapshots.GetLatest("userA", "colonyA")?.Identity.SnapshotId == "paid",
            "Standalone bank service failed to commit a loan and snapshot.");
        f.Configuration = new(bankLoansEnabled: false);
        var replay = f.Bank.CreateBankLoanWithSnapshot(request, [], f.Token);
        Require(replay.Result.ErrorCode == ProtocolErrorCode.DuplicateRequest && replay.SilverDelta == success.SilverDelta
            && replay.AppliedSnapshotId == success.AppliedSnapshotId && f.Recovery.Commits == 1,
            "Committed receipt was replaced by new terms or payload validation.");
    }

    private static void Repay(Func<string, SaveSnapshotPackage> factory, bool debt)
    {
        var f = new Fixture(factory);
        var now = DateTimeOffset.UtcNow;
        var metadata = ProtocolDtoMapper.ToMetadataDto(f.Package);
        if (debt)
        {
            var record = f.Loans.CreateDebt("seed", "userA", "colonyA", "before", 110, "Test", "fine", "source", 1000, now);
            var request = new RepayBankDebtWithSnapshotRequest("repay", "userA", "colonyA", "before", f.Token, 1100, record.DebtId, 110, metadata);
            var response = f.Bank.RepayBankDebtWithSnapshot(request, f.Package.Payload, f.Token);
            var replay = f.Bank.RepayBankDebtWithSnapshot(request, [], f.Token);
            Require(response.Result.Accepted && response.SilverDelta == -110 && response.Debt?.Status == BankLoanRegistry.StatusRepaid
                && replay.Result.ErrorCode == ProtocolErrorCode.DuplicateRequest && replay.Debt?.DebtId == record.DebtId,
                "Standalone debt repayment or receipt failed.");
        }
        else
        {
            f.Loans.Create("seed", "userA", "colonyA", "before", 100, 10, 1, 1000, now, "loan");
            f.Loans.ConfirmPendingForSnapshot("userA", "colonyA", "before", 1000, now);
            var request = new RepayBankLoanWithSnapshotRequest("repay", "userA", "colonyA", "before", f.Token, 1100, "loan", 110, metadata);
            var response = f.Bank.RepayBankLoanWithSnapshot(request, f.Package.Payload, f.Token);
            var replay = f.Bank.RepayBankLoanWithSnapshot(request, [], f.Token);
            Require(response.Result.Accepted && response.SilverDelta == -110 && response.Loan?.Status == BankLoanRegistry.StatusRepaid
                && replay.Result.ErrorCode == ProtocolErrorCode.DuplicateRequest && replay.Loan?.LoanId == "loan",
                "Standalone loan repayment or receipt failed.");
        }
        Require(f.Recovery.Commits == 1, "Repayment repeated post-commit effects.");
    }

    private static void RecoveryInvalidatesAuthorization(Func<string, SaveSnapshotPackage> factory)
    {
        var f = new Fixture(factory);
        f.Recovery.OnReplay = () => f.Sessions.EndUser("userA");
        var response = f.Bank.GetBankStatus(new("userA", "colonyA", "before", f.Token, 1100, 10000));
        Require(!response.Result.Accepted && response.ActiveLoan is null && f.Recovery.Commits == 0,
            "Status borrowed authorization revoked by recovery.");
    }

    // This fixture deliberately composes the module without a network host or its aggregate state.
    private sealed class Fixture
    {
        internal readonly InMemoryColonySnapshotIndexStore Snapshots = new();
        internal readonly BankLoanRegistry Loans = new();
        internal readonly LoginSessionRegistry Sessions = new();
        internal readonly RecoveryProbe Recovery = new();
        internal ClashOfRimServerConfiguration Configuration = new(bankLoansEnabled: true, bankMinLoanSilver: 1,
            bankMinDurationDays: 1, bankBaseAnnualInterestRate: 0);
        internal readonly BankRequestAccess Access;
        internal readonly BankService Bank;
        internal readonly SaveSnapshotPackage Package;
        internal readonly string Token;

        internal Fixture(Func<string, SaveSnapshotPackage> factory)
        {
            var tokens = new AuthTokenRegistry();
            var mercenaries = new MercenaryContractRegistry();
            var gate = new object();
            var mutations = new SnapshotMutationCoordinator(gate, Recovery);
            var colonies = new ColonyAccessAuthorization(tokens, Sessions, new DiplomacyRelationRegistry(),
                new RaidPreparationRegistry(), new InMemoryAuthoritativeEventLedger(), () => false);
            var pending = new PendingConfirmationReconciler(Loans, mercenaries, () => Configuration, gate);
            Access = new BankRequestAccess(colonies, mutations, Snapshots, Loans, pending);
            var principals = new SnapshotPrincipalAuthorization(tokens, Sessions);
            Bank = new BankService(Loans, mercenaries, Snapshots, new SnapshotCommitService(), Access,
                new SnapshotRequestAuthorization(principals, colonies, mutations),
                new SnapshotTransactions(Snapshots, SnapshotUploadPolicy.AllowAnyVersion, new ServerPluginRegistry(), new CompatibilityBaselineRegistry()),
                Recovery, () => Configuration);
            Package = MercenaryCommitCrashTests.PrepareEndpoint(Snapshots, factory, new("userA", "colonyA", "before"), "paid");
            var now = DateTimeOffset.UtcNow;
            Sessions.TryCreate("userA", "colonyA", now, out string session);
            Token = tokens.Issue("userA", "userA", "colonyA", session, now);
        }
    }

    private sealed class RecoveryProbe : ISnapshotRecovery, ISnapshotRecoveryPreparation, ISnapshotCommitEffects
    {
        internal int Replays, Commits;
        internal Action? OnReplay;
        public void RecoverPrerequisites() { }
        public ISnapshotRecoveryPreparation CapturePreparation() => this;
        public void Replay() { Replays++; OnReplay?.Invoke(); }
        public bool NeedsPreparation => false;
        public bool IsCurrent => true;
        public void Prepare() { }
        public bool TryInstall() => true;
        public void AfterCommit(LatestSnapshotRecord snapshot, string? uploadKind) => Commits++;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
