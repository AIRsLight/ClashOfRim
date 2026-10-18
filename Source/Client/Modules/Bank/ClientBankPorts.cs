using System;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Synchronization;

namespace AIRsLight.ClashOfRim.Modules.Bank;

internal enum ClientBankOperation { Borrow, RepayLoan, RepayDebt }
internal sealed record ClientBankCommand(ClientBankOperation Operation, string IdempotencyKey, long GameTicks,
    int ColonyWealth, ModBankLoanSummaryDto? Loan, ModBankDebtSummaryDto? Debt)
{
    internal int Silver => Operation == ClientBankOperation.Borrow ? Loan!.PrincipalSilver
        : Operation == ClientBankOperation.RepayLoan ? Loan!.TotalDueSilver : Debt!.AmountSilver;
}
internal sealed record ClientBankReply(ModProtocolResponseDto? Result, ModBankStatusResponseDto? Status,
    ModBankLoanSummaryDto? Loan, string? SnapshotId, string? LineageToken);
internal sealed record ClientBankFacts(long Ticks, int Wealth, int Silver, bool HasLoanMap);
internal sealed record ClientBankApplication(bool Success, string Message);

internal interface IClientBankTransport
{
    Task<ClashOfRimClientNetworkResult<ModBankStatusResponseDto>> ReadAsync(
        ClashOfRimClientNetworkContext context, long ticks, int wealth, CancellationToken token);
    Task<ClashOfRimClientNetworkResult<ClientBankReply>> SubmitAsync(ClashOfRimClientNetworkContext context,
        ClientBankCommand command, ModSnapshotPackageMetadataDto snapshot, byte[] payload, CancellationToken token);
}

internal interface IClientBankLoanBalance
{
    // Includes local overdue charges and never falls below the server balance.
    int LoanTotalDue(string loanId, int serverTotal);
}

internal interface IClientBankRuntime : IClientBankLoanBalance
{
    ClientBankFacts ReadFacts();
    ClientBankApplication Apply(ClientBankCommand command, ModBankStatusResponseDto pendingStatus);
    void ConfirmLoan(ModBankLoanSummaryDto loan, ModBankStatusResponseDto status);
    void RefreshDebts(ModBankStatusResponseDto status);
}

internal interface IClientBankPresentation
{
    string Text(string key, params (string Name, object? Value)[] arguments);
    string Summary(ModBankStatusResponseDto? status);
    string Admission(LocalMutationAdmission admission, LocalMutationState? pending);
    void Message(string message, bool rejected);
    void Diagnose(Exception exception);
}
