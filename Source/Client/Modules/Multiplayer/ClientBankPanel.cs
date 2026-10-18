using System;
using System.Linq;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Bank;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using AIRsLight.ClashOfRim.Modules.Synchronization;

namespace AIRsLight.ClashOfRim.Modules.Multiplayer;

internal sealed class ClientBankPanel
{
    private readonly ClientBankState state;
    private readonly ClientBankOperations operations;
    private readonly ClientSynchronizationCoordinator synchronization;
    private readonly IClientBankLoanBalance balance;
    private readonly ClientGameRequestTracker lifetime;
    private readonly PanelRefreshSchedule refresh = new();
    private ModBankStatusResponseDto? cached;
    private int cachedVersion = -1;
    private string principal = "1000", duration = "30";

    internal ClientBankPanel(ClientSessionLifecycle session, IClientSnapshotGame game, ClientBankState state,
        ClientBankOperations operations, ClientSynchronizationCoordinator synchronization, IClientBankLoanBalance balance)
    {
        this.state = state; this.operations = operations; this.synchronization = synchronization; this.balance = balance;
        lifetime = new(session, game, () =>
        { cached = null; cachedVersion = -1; principal = "1000"; duration = "30"; refresh.Request(); });
    }
    internal object Ownership => lifetime.Capture();
    internal string Status => state.Message;
    internal string PrincipalText { get => lifetime.Read(() => principal); set => lifetime.Read(() => principal = value); }
    internal string DurationText { get => lifetime.Read(() => duration); set => lifetime.Read(() => duration = value); }
    internal void Close() => lifetime.Reset();
    internal void RequestRefresh() => lifetime.Read(() => { refresh.Request(); return true; });
    internal Task RefreshAsync(float now, bool manual = false) => lifetime.Read(() =>
    {
        if (!refresh.TryStart(now, !manual && (state.Busy || synchronization.ManualInProgress), manual)) return Task.CompletedTask;
        return operations.RefreshAsync();
    });
    internal ModBankStatusResponseDto? Read() => lifetime.Read(() =>
    {
        int version = state.Version;
        if (version != cachedVersion) { cached = state.View; cachedVersion = version; }
        return cached;
    });
    internal PanelIntegerRange PrincipalRange => lifetime.Read(() =>
    { var status = Read(); return PanelIntegerRange.Normalize(status?.MinLoanSilver ?? 1, status?.MaxLoanSilver ?? 1); });
    internal PanelIntegerRange DurationRange => lifetime.Read(() =>
    { var status = Read(); return PanelIntegerRange.Normalize(status?.MinDurationDays ?? 1, status?.MaxDurationDays ?? 1); });
    internal ClientBankLoanQuote Quote() => lifetime.Read(() =>
    {
        bool principalValid = int.TryParse(principal, out int amount);
        bool durationValid = int.TryParse(duration, out int days);
        return Quote(Read(), amount, days, principalValid && durationValid);
    });
    private ClientBankLoanQuote Quote(ModBankStatusResponseDto? status, int amount, int days, bool parsed)
    {
        bool valid = status is not null && parsed && amount >= status.MinLoanSilver && amount <= status.MaxLoanSilver
            && days >= status.MinDurationDays && days <= status.MaxDurationDays;
        int interest = valid ? ClientBankTerms.CalculateInterestSilver(amount, days, status!) : 0;
        return new(amount, days, interest, amount + interest, valid,
            valid && status!.BankLoansEnabled && status.ActiveLoan is null && !state.Busy && !synchronization.ManualInProgress);
    }
    internal int LoanTotalDue(ModBankLoanSummaryDto loan) => balance.LoanTotalDue(loan.LoanId, loan.TotalDueSilver);
    internal bool CanRepay(string status) => status == "Active" && !state.Busy && !synchronization.ManualInProgress;

    internal Action PrepareBorrow(ClientBankLoanQuote quote) => Bind(() =>
    {
        var current = Quote(state.View, quote.Principal, quote.Duration, parsed: true);
        if (quote.Valid && current.CanBorrow && current.Interest == quote.Interest)
            _ = operations.BorrowAsync(quote.Principal, quote.Duration);
    });
    internal Action PrepareRepayLoan(ModBankLoanSummaryDto loan, int totalDue)
    {
        string id = loan.LoanId;
        return Bind(() =>
        {
            var current = state.View?.ActiveLoan;
            if (current is not null && current.LoanId == id && current.Status == "Active" && LoanTotalDue(current) == totalDue)
                _ = operations.RepayLoanAsync();
        });
    }
    internal Action PrepareRepayDebt(ModBankDebtSummaryDto debt)
    {
        string id = debt.DebtId; int amount = debt.AmountSilver;
        return Bind(() =>
        {
            var current = state.View?.OpenDebts.FirstOrDefault(item => item.DebtId == id && item.Status == "Active");
            if (current is not null && current.AmountSilver == amount) _ = operations.RepayDebtAsync(id);
        });
    }
    private Action Bind(Action action)
    {
        using var publication = ClientSessionWork.EnterPublication();
        var owner = lifetime.Capture();
        var command = state.Bind(action);
        return () => lifetime.TryApply(owner, command);
    }
}

internal sealed record ClientBankLoanQuote(int Principal, int Duration, int Interest, int TotalDue, bool Valid, bool CanBorrow);
