using System.Globalization;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Network.Modules.Bank;
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
        var f = new EconomicModuleTestContext(factory);
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
        var f = new EconomicModuleTestContext(factory);
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
        var f = new EconomicModuleTestContext(factory);
        f.Recovery.OnReplay = () => f.Sessions.EndUser("userA");
        var response = f.Bank.GetBankStatus(new("userA", "colonyA", "before", f.Token, 1100, 10000));
        Require(!response.Result.Accepted && response.ActiveLoan is null && f.Recovery.Commits == 0,
            "Status borrowed authorization revoked by recovery.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
