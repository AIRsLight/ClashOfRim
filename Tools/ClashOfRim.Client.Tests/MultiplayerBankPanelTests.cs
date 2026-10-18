using System;
using System.Globalization;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Bank;
using AIRsLight.ClashOfRim.Modules.Multiplayer;

internal static class MultiplayerBankPanelTests
{
    internal static void Run() => RunAsync().GetAwaiter().GetResult();
    private static async Task RunAsync()
    {
        var culture = CultureInfo.CurrentCulture;
        try
        {
            foreach (string name in new[] { "en-US", "zh-CN", "tr-TR", "fr-FR" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
                DraftsAndProjection(); await Refresh();
                foreach (ClientBankOperation kind in Enum.GetValues<ClientBankOperation>()) await Commands(kind);
            }
            foreach (string change in new[] { "close", "user", "colony", "auth", "server", "game", "login", "reset", "generation" })
            foreach (ClientBankOperation kind in Enum.GetValues<ClientBankOperation>()) await Ownership(kind, change);
            await ChangedTermsAndTargets(); await LateRefresh();
        }
        finally { CultureInfo.CurrentCulture = culture; ClientSessionWork.Reset(); }
    }

    private static void DraftsAndProjection()
    {
        using var f = new Fixture();
        Check(f.Panel.PrincipalText == "1000" && f.Panel.DurationText == "30" && !f.Panel.Quote().Valid, "Default drafts were silently clamped into a valid loan.");
        Check(f.Panel.PrincipalRange.SliderValue(f.Panel.PrincipalText) == 500 && f.Panel.DurationRange.SliderValue(f.Panel.DurationText) == 7,
            "Slider did not clamp independently of draft text.");
        var view = f.Panel.Read()!;
        Check(ReferenceEquals(view, f.Panel.Read()), "Unchanged bank view copied all debts again.");
        view.OpenDebts[0].AmountSilver = 999; view.InterestDurationMultiplierCurve[0].Multiplier = 999;
        Check(f.Bank.State.View!.OpenDebts[0].AmountSilver == 70 && f.Bank.State.View.InterestDurationMultiplierCurve[0].Multiplier == 1,
            "Shared display projection mutated debts or loan terms.");
        f.Edit(status =>
        {
            status.BaseAnnualInterestRate = .5f;
            status.InterestDurationMultiplierCurve = new() { new() { DurationDays = 10, Multiplier = 2 }, new() { DurationDays = 0, Multiplier = 1 } };
        });
        Check(!ReferenceEquals(view, f.Panel.Read()), "Updated terms did not invalidate the display cache.");
        f.Panel.PrincipalText = "120"; f.Panel.DurationText = "6";
        Check(f.Panel.Quote() is { Principal: 120, Duration: 6, Interest: 10, TotalDue: 130, Valid: true, CanBorrow: true }, "Loan preview changed shared curve interpolation or rounding.");
        foreach (string invalid in new[] { "", "12x", "1.5", "2147483648", "-1", "9", "501" })
        {
            f.Panel.PrincipalText = invalid;
            Check(!f.Panel.Quote().Valid && f.Panel.PrincipalText == invalid, "Invalid principal was accepted or overwritten: " + invalid);
        }
        f.Panel.PrincipalText = "120";
        foreach (string invalid in new[] { "", "NaN", "0", "8", "2147483648" })
        { f.Panel.DurationText = invalid; Check(!f.Panel.Quote().CanBorrow, "Invalid duration enabled borrowing: " + invalid); }
        f.Panel.DurationText = "6";
        f.Edit(status => status.BankLoansEnabled = false);
        Check(f.Panel.Quote() is { Valid: true, CanBorrow: false }, "Disabled bank hid a valid preview or enabled borrowing.");
        f.Edit(status => { status.BankLoansEnabled = true; status.ActiveLoan = new() { LoanId = "existing", Status = "PendingActivation" }; });
        Check(!f.Panel.Quote().CanBorrow, "Pending loan enabled a second loan.");
        Check(f.Panel.CanRepay("Active") && !f.Panel.CanRepay("active") && !f.Panel.CanRepay("PendingRepayment"), "Repayment status became culture/case dependent.");
        f.Edit(status => { status.ActiveLoan = null; status.MinLoanSilver = 0; status.MaxLoanSilver = -4; status.MinDurationDays = 3; status.MaxDurationDays = 2; });
        Check(f.Panel.PrincipalRange == new PanelIntegerRange(1, 1) && f.Panel.DurationRange == new PanelIntegerRange(3, 3) && !f.Panel.Quote().Valid,
            "Display fallback relaxed invalid server lending bounds.");
        Check(PanelIntegerRange.Normalize(0, -1, 1, 180) == new PanelIntegerRange(1, 180)
            && PanelIntegerRange.Normalize(300, 2, 1, 180) == new PanelIntegerRange(300, 300)
            && new PanelIntegerRange(10, 50).SliderValue("bad") == 10, "Shared bank/mercenary range fallback changed.");
        object owner = f.Panel.Ownership; var current = f.Panel.Read();
        f.Bank.Ports.Snapshot = "advanced";
        Check(ReferenceEquals(owner, f.Panel.Ownership) && ReferenceEquals(current, f.Panel.Read()) && f.Panel.PrincipalText == "120", "Normal save discarded draft or cache.");
        f.Panel.Close();
        Check(!ReferenceEquals(owner, f.Panel.Ownership) && f.Panel.PrincipalText == "1000" && f.Panel.DurationText == "30" && f.Bank.State.View is not null,
            "Closing retained drafts or erased shared bank data.");
        f.Panel.PrincipalText = "55"; f.Bank.Ports.Auth = "replacement";
        Check(f.Panel.PrincipalText == "1000" && f.Panel.Read() is null && f.Panel.Status == "StatusNotPulled", "Replacement account retained bank page data.");
    }

    private static async Task Refresh()
    {
        using var f = new Fixture();
        Check(f.Bank.Sync.TryBeginManual(out var occupied), "Cannot occupy synchronization.");
        await f.Panel.RefreshAsync(10); Check(f.Bank.Reads == 0, "Periodic refresh crossed manual synchronization.");
        await f.Panel.RefreshAsync(10, manual: true);
        Check(f.Bank.Reads == 0 && occupied!.IsCurrent, "Explicit refresh bypassed business admission or released its owner."); occupied!.Dispose();
        await f.Panel.RefreshAsync(39.9f); Check(f.Bank.Reads == 0, "Explicit refresh did not advance its deadline.");
        await f.Bank.Ports.PumpUntil(f.Panel.RefreshAsync(40));
        Check(f.Bank.Reads == 1 && f.Bank.DebtRefreshes == 1 && !f.Bank.State.Busy, "Periodic refresh lost debt quest update or retained lease.");
        f.Panel.RequestRefresh(); await f.Bank.Ports.PumpUntil(f.Panel.RefreshAsync(40));
        await f.Bank.Ports.PumpUntil(f.Panel.RefreshAsync(40, manual: true));
        Check(f.Bank.Reads == 3 && f.Panel.Status == "summary", "Shared tab/manual refresh did not use the bank operation.");
        f.Bank.ReadHold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task held = f.Panel.RefreshAsync(41, manual: true);
        Check(f.Bank.State.Busy && !f.Panel.CanRepay("Active"), "In-flight bank request did not disable actions.");
        await f.Panel.RefreshAsync(100); await f.Panel.RefreshAsync(100, manual: true);
        f.Bank.ReadHold.SetResult(ClashOfRimClientNetworkResult<ModBankStatusResponseDto>.Failed("test", "offline")); await held;
        Check(f.Bank.Reads == 4 && !f.Bank.State.Busy && f.Panel.Read() is not null && f.Panel.Status == "StatusRefreshFailed", "Failed refresh duplicated requests, cleared view or retained admission.");
        f.Bank.ReadHold = null; f.Panel.Close();
        await f.Bank.Ports.PumpUntil(f.Panel.RefreshAsync(101));
        Check(f.Bank.Reads == 5, "Reopening inherited an old refresh deadline.");
    }

    private static async Task Commands(ClientBankOperation kind)
    {
        using var f = new Fixture(kind);
        f.Panel.PrincipalText = "100"; f.Panel.DurationText = "2";
        Action confirm = f.Prepare();
        f.Panel.PrincipalText = "400"; f.Panel.DurationText = "7";
        var visible = f.Panel.Read()!;
        if (visible.ActiveLoan is not null) visible.ActiveLoan.LoanId = "edited-display";
        visible.OpenDebts[0].DebtId = "edited-display";
        f.Bank.Ports.Snapshot = "advanced";
        await f.Complete(confirm);
        Check(f.Bank.Sent!.Operation == kind && f.Bank.Context!.CurrentSnapshotId == "advanced" && f.Bank.Submissions == 1 && f.Bank.Applied == 1,
            "Prepared command borrowed draft/display edits or duplicated submission.");
        Check(f.Bank.Amount == (kind == ClientBankOperation.Borrow ? 100 : kind == ClientBankOperation.RepayLoan ? 120 : 70), "Confirmation changed its authorized amount.");
        if (kind == ClientBankOperation.Borrow) Check(f.Bank.Sent.Loan!.DurationDays == 2, "Borrow read the edited duration.");
        if (kind == ClientBankOperation.RepayLoan) Check(f.Bank.Sent.Loan!.LoanId == "loan", "Repayment used a mutable display ID.");
        if (kind == ClientBankOperation.RepayDebt) Check(f.Bank.Sent.Debt!.DebtId == "debt", "Debt repayment used a mutable display ID.");
    }

    private static async Task Ownership(ClientBankOperation kind, string change)
    {
        using var f = new Fixture(kind);
        f.Panel.PrincipalText = "100"; f.Panel.DurationText = "2";
        Action confirm = f.Prepare();
        switch (change)
        {
            case "close": f.Panel.Close(); break;
            case "user": f.Bank.Ports.User = "other"; break;
            case "colony": f.Bank.Ports.Colony = "other"; break;
            case "auth": f.Bank.Ports.Auth = "other"; break;
            case "server": f.Bank.Ports.Server = "http://other"; break;
            case "game": f.Bank.Ports.GameOwner = new(); break;
            case "login": f.Bank.Session.PublishLogin(new() { AuthenticatedUserId = "alice", AuthToken = "auth" }); break;
            case "reset": f.Bank.State.Reset(); break;
            case "generation": ClientSessionWork.Reset(); f.Bank.Sync.Reset(); break;
        }
        confirm(); await f.Bank.Ports.PumpUntil(Task.CompletedTask);
        Check(f.Bank.Applied == 0 && f.Bank.Submissions == 0 && !f.Bank.Mutations.IsPending && !f.Bank.Sync.IsBusy, $"Old {kind} confirmation crossed {change}.");
    }

    private static async Task ChangedTermsAndTargets()
    {
        foreach (string change in new[] { "interest", "limit", "disabled", "new-loan" })
        {
            using var f = new Fixture(); f.Panel.PrincipalText = "100"; f.Panel.DurationText = "2";
            Action confirm = f.Prepare();
            f.Edit(status =>
            {
                if (change == "interest") status.BaseAnnualInterestRate = 6;
                if (change == "limit") status.MaxLoanSilver = 99;
                if (change == "disabled") status.BankLoansEnabled = false;
                if (change == "new-loan") status.ActiveLoan = new() { LoanId = "new", Status = "Active" };
            });
            confirm(); Check(f.Bank.Applied == 0, "Old borrowing confirmation accepted changed terms: " + change);
        }
        foreach (ClientBankOperation kind in new[] { ClientBankOperation.RepayLoan, ClientBankOperation.RepayDebt })
        foreach (string change in new[] { "id", "amount", "status", "missing" })
        {
            using var f = new Fixture(kind); Action confirm = f.Prepare();
            f.Edit(status =>
            {
                if (kind == ClientBankOperation.RepayLoan)
                {
                    if (change == "id") status.ActiveLoan!.LoanId = "replacement";
                    if (change == "amount") status.ActiveLoan!.TotalDueSilver = 150;
                    if (change == "status") status.ActiveLoan!.Status = "PendingRepayment";
                    if (change == "missing") status.ActiveLoan = null;
                }
                else
                {
                    if (change == "id") status.OpenDebts[0].DebtId = "replacement";
                    if (change == "amount") status.OpenDebts[0].AmountSilver = 150;
                    if (change == "status") status.OpenDebts[0].Status = "PendingRepayment";
                    if (change == "missing") status.OpenDebts.Clear();
                }
            });
            confirm(); Check(f.Bank.Applied == 0 && f.Bank.Submissions == 0, "Repayment silently switched target/amount: " + kind + "/" + change);
            if (change is "id" or "amount") await f.Complete(f.Prepare());
        }
        using (var f = new Fixture(ClientBankOperation.RepayLoan))
        {
            Action confirm = f.Prepare(); f.Bank.LocalLoanBalance = 140;
            confirm(); Check(f.Bank.Applied == 0 && f.Panel.LoanTotalDue(f.Panel.Read()!.ActiveLoan!) == 140, "New local overdue fees were charged under an old confirmation.");
            await f.Complete(f.Prepare()); Check(f.Bank.Amount == 140, "New overdue balance was not repayable after reconfirmation.");
        }
        using (var f = new Fixture())
        {
            f.Panel.PrincipalText = "100"; f.Panel.DurationText = "2"; Action confirm = f.Prepare();
            f.Edit(status => status.MercenaryMinDurationDays = 10);
            await f.Complete(confirm); Check(f.Bank.Applied == 1, "Unrelated mercenary configuration invalidated a bank confirmation.");
        }
    }

    private static async Task LateRefresh()
    {
        using var f = new Fixture(); var oldView = f.Bank.State.View!;
        var hold = new TaskCompletionSource<ClashOfRimClientNetworkResult<ModBankStatusResponseDto>>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Bank.ReadHold = hold; Task old = f.Panel.RefreshAsync(1);
        await f.Bank.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        f.Bank.Ports.Auth = "replacement"; f.Bank.ReadHold = null;
        oldView.MinLoanSilver = 77; f.Bank.State.View = oldView;
        Task current = f.Panel.RefreshAsync(2);
        var stale = ClientBankView.Copy(oldView); stale.MinLoanSilver = 999;
        hold.SetResult(ClashOfRimClientNetworkResult<ModBankStatusResponseDto>.Ok(stale)); await old;
        Check(f.Bank.State.Busy && f.Panel.Read()!.MinLoanSilver == 77, "Obsolete refresh changed replacement data or released its admission.");
        await f.Bank.Ports.PumpUntil(current);
        Check(f.Panel.Read()!.MinLoanSilver == 77 && f.Bank.DebtRefreshes == 1 && !f.Bank.State.Busy, "Current refresh failed after old cleanup.");
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly ClientBankModuleTests.Fixture Bank;
        internal readonly ClientBankPanel Panel;
        internal Fixture(ClientBankOperation kind = ClientBankOperation.Borrow)
        {
            Bank = new(kind);
            var view = Bank.State.View!; view.MaxLoanSilver = 500; Bank.State.View = view;
            Panel = new(Bank.Session, Bank.Ports, Bank.State, Bank.Bank, Bank.Sync, Bank);
        }
        internal void Edit(Action<ModBankStatusResponseDto> edit) { var view = Bank.State.View!; edit(view); Bank.State.View = view; }
        internal Action Prepare() => Bank.Kind switch
        {
            ClientBankOperation.Borrow => Panel.PrepareBorrow(Panel.Quote()),
            ClientBankOperation.RepayLoan => Panel.PrepareRepayLoan(Panel.Read()!.ActiveLoan!, Panel.LoanTotalDue(Panel.Read()!.ActiveLoan!)),
            _ => Panel.PrepareRepayDebt(Panel.Read()!.OpenDebts[0])
        };
        internal async Task Complete(Action confirm)
        {
            var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void Completed() => complete.TrySetResult();
            Bank.Bank.Completed += Completed;
            try { confirm(); await Bank.Ports.PumpUntil(complete.Task); }
            finally { Bank.Bank.Completed -= Completed; }
        }
        public void Dispose() { Bank.Sync.Reset(); ClientSessionWork.Reset(); }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
