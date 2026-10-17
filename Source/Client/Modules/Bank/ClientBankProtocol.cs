using System;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Transport;
using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Modules.Bank;

internal sealed class ClientBankProtocol
{
    private readonly ClashOfRimClientNetworkContext context;
    private readonly ClientProtocolTransport transport;
    private readonly Func<string> notConfigured;

    internal ClientBankProtocol(ClashOfRimClientNetworkContext context, ClientProtocolTransport transport, Func<string> notConfigured)
    {
        this.context = context;
        this.transport = transport;
        this.notConfigured = notConfigured;
    }

    internal async Task<ClashOfRimClientNetworkResult<ClientBankReply>> SubmitAsync(ClientBankCommand command,
        ModSnapshotPackageMetadataDto snapshot, byte[] payload, CancellationToken token)
    {
        if (command.Operation == ClientBankOperation.RepayDebt)
        {
            var result = await RepayBankDebtWithSnapshotAsync(command.IdempotencyKey, command.GameTicks, command.Debt!.DebtId,
                command.Silver, snapshot, payload, token);
            return Map(result, r => new(r.Result, r.Status, null, r.AppliedSnapshotId, r.NextLineageToken));
        }
        var loan = command.Loan!;
        var response = command.Operation == ClientBankOperation.Borrow
            ? await CreateBankLoanWithSnapshotAsync(command.IdempotencyKey, command.GameTicks, command.ColonyWealth,
                loan.PrincipalSilver, loan.DurationDays, loan.LoanId, loan.InterestSilver, snapshot, payload, token)
            : await RepayBankLoanWithSnapshotAsync(command.IdempotencyKey, command.GameTicks, loan.LoanId,
                command.Silver, snapshot, payload, token);
        return Map(response, r => new(r.Result, r.Status, r.Loan, r.AppliedSnapshotId, r.NextLineageToken));
    }

    private static ClashOfRimClientNetworkResult<ClientBankReply> Map<T>(ClashOfRimClientNetworkResult<T> result, Func<T, ClientBankReply> convert) =>
        result.Success && result.Response is not null ? ClashOfRimClientNetworkResult<ClientBankReply>.Ok(convert(result.Response))
            : ClashOfRimClientNetworkResult<ClientBankReply>.Failed(result.ErrorCode ?? string.Empty, result.Message ?? string.Empty);

    public Task<ClashOfRimClientNetworkResult<ModBankStatusResponseDto>> GetBankStatusAsync(
        long currentGameTicks,
        int colonyWealth,
        CancellationToken cancellationToken = default)
    {
        if (!context.IsConfigured)
        {
            return NotConfigured<ModBankStatusResponseDto>();
        }

        var request = new ModGetBankStatusRequestDto
        {
            UserId = context.UserId,
            ColonyId = context.ColonyId,
            CurrentSnapshotId = context.CurrentSnapshotId ?? string.Empty,
            AuthToken = context.AuthToken,
            CurrentGameTicks = currentGameTicks,
            ColonyWealth = colonyWealth
        };

        return transport.ForContext(context).PostAsync<ModGetBankStatusRequestDto, ModBankStatusResponseDto>(
            ProtocolContractManifest.Find(ProtocolMessageKind.GetBankStatus).Route,
            request,
            cancellationToken);
    }

    public Task<ClashOfRimClientNetworkResult<ModBankLoanResponseDto>> CreateBankLoanAsync(
        string idempotencyKey,
        long currentGameTicks,
        int colonyWealth,
        int principalSilver,
        int durationDays,
        CancellationToken cancellationToken = default)
    {
        if (!context.IsConfigured)
        {
            return NotConfigured<ModBankLoanResponseDto>();
        }

        var request = new ModCreateBankLoanRequestDto
        {
            IdempotencyKey = idempotencyKey,
            UserId = context.UserId,
            ColonyId = context.ColonyId,
            CurrentSnapshotId = context.CurrentSnapshotId ?? string.Empty,
            AuthToken = context.AuthToken,
            CurrentGameTicks = currentGameTicks,
            ColonyWealth = colonyWealth,
            PrincipalSilver = principalSilver,
            DurationDays = durationDays
        };

        return transport.ForContext(context).PostAsync<ModCreateBankLoanRequestDto, ModBankLoanResponseDto>(
            ProtocolContractManifest.Find(ProtocolMessageKind.CreateBankLoan).Route,
            request,
            cancellationToken);
    }

    public Task<ClashOfRimClientNetworkResult<ModBankLoanResponseDto>> CreateBankLoanWithSnapshotAsync(
        string idempotencyKey,
        long currentGameTicks,
        int colonyWealth,
        int principalSilver,
        int durationDays,
        string requestedLoanId,
        int expectedInterestSilver,
        ModSnapshotPackageMetadataDto confirmedSnapshot,
        byte[] confirmedPayload,
        CancellationToken cancellationToken = default)
    {
        if (!context.IsConfigured)
        {
            return NotConfigured<ModBankLoanResponseDto>();
        }

        confirmedSnapshot.SnapshotUploadKind = ModSnapshotUploadKinds.BankLoanCreationConfirmation;
        var request = new ModCreateBankLoanWithSnapshotRequestDto
        {
            IdempotencyKey = idempotencyKey,
            UserId = context.UserId,
            ColonyId = context.ColonyId,
            CurrentSnapshotId = context.CurrentSnapshotId ?? string.Empty,
            AuthToken = context.AuthToken,
            CurrentGameTicks = currentGameTicks,
            ColonyWealth = colonyWealth,
            PrincipalSilver = principalSilver,
            DurationDays = durationDays,
            RequestedLoanId = requestedLoanId ?? string.Empty,
            ExpectedInterestSilver = expectedInterestSilver,
            ConfirmedSnapshot = confirmedSnapshot
        };

        return transport.ForContext(context).PostSnapshotMultipartAsync<ModCreateBankLoanWithSnapshotRequestDto, ModBankLoanResponseDto>(
            ProtocolContractManifest.Find(ProtocolMessageKind.CreateBankLoanWithSnapshot).Route,
            request,
            confirmedPayload,
            cancellationToken);
    }

    public Task<ClashOfRimClientNetworkResult<ModBankLoanResponseDto>> RepayBankLoanAsync(
        string idempotencyKey,
        long currentGameTicks,
        int silverPaid,
        CancellationToken cancellationToken = default)
    {
        if (!context.IsConfigured)
        {
            return NotConfigured<ModBankLoanResponseDto>();
        }

        var request = new ModRepayBankLoanRequestDto
        {
            IdempotencyKey = idempotencyKey,
            UserId = context.UserId,
            ColonyId = context.ColonyId,
            CurrentSnapshotId = context.CurrentSnapshotId ?? string.Empty,
            AuthToken = context.AuthToken,
            CurrentGameTicks = currentGameTicks,
            SilverPaid = silverPaid
        };

        return transport.ForContext(context).PostAsync<ModRepayBankLoanRequestDto, ModBankLoanResponseDto>(
            ProtocolContractManifest.Find(ProtocolMessageKind.RepayBankLoan).Route,
            request,
            cancellationToken);
    }

    public Task<ClashOfRimClientNetworkResult<ModBankLoanResponseDto>> RepayBankLoanWithSnapshotAsync(
        string idempotencyKey,
        long currentGameTicks,
        string loanId,
        int silverPaid,
        ModSnapshotPackageMetadataDto confirmedSnapshot,
        byte[] confirmedPayload,
        CancellationToken cancellationToken = default)
    {
        if (!context.IsConfigured)
        {
            return NotConfigured<ModBankLoanResponseDto>();
        }

        confirmedSnapshot.SnapshotUploadKind = ModSnapshotUploadKinds.BankLoanRepaymentConfirmation;
        var request = new ModRepayBankLoanWithSnapshotRequestDto
        {
            IdempotencyKey = idempotencyKey,
            UserId = context.UserId,
            ColonyId = context.ColonyId,
            CurrentSnapshotId = context.CurrentSnapshotId ?? string.Empty,
            AuthToken = context.AuthToken,
            CurrentGameTicks = currentGameTicks,
            LoanId = loanId ?? string.Empty,
            SilverPaid = silverPaid,
            ConfirmedSnapshot = confirmedSnapshot
        };

        return transport.ForContext(context).PostSnapshotMultipartAsync<ModRepayBankLoanWithSnapshotRequestDto, ModBankLoanResponseDto>(
            ProtocolContractManifest.Find(ProtocolMessageKind.RepayBankLoanWithSnapshot).Route,
            request,
            confirmedPayload,
            cancellationToken);
    }

    public Task<ClashOfRimClientNetworkResult<ModBankDebtResponseDto>> RepayBankDebtAsync(
        string idempotencyKey,
        long currentGameTicks,
        string debtId,
        int silverPaid,
        CancellationToken cancellationToken = default)
    {
        if (!context.IsConfigured)
        {
            return NotConfigured<ModBankDebtResponseDto>();
        }

        var request = new ModRepayBankDebtRequestDto
        {
            IdempotencyKey = idempotencyKey,
            UserId = context.UserId,
            ColonyId = context.ColonyId,
            CurrentSnapshotId = context.CurrentSnapshotId ?? string.Empty,
            AuthToken = context.AuthToken,
            CurrentGameTicks = currentGameTicks,
            DebtId = debtId,
            SilverPaid = silverPaid
        };

        return transport.ForContext(context).PostAsync<ModRepayBankDebtRequestDto, ModBankDebtResponseDto>(
            ProtocolContractManifest.Find(ProtocolMessageKind.RepayBankDebt).Route,
            request,
            cancellationToken);
    }

    public Task<ClashOfRimClientNetworkResult<ModBankDebtResponseDto>> RepayBankDebtWithSnapshotAsync(
        string idempotencyKey,
        long currentGameTicks,
        string debtId,
        int silverPaid,
        ModSnapshotPackageMetadataDto confirmedSnapshot,
        byte[] confirmedPayload,
        CancellationToken cancellationToken = default)
    {
        if (!context.IsConfigured)
        {
            return NotConfigured<ModBankDebtResponseDto>();
        }

        confirmedSnapshot.SnapshotUploadKind = ModSnapshotUploadKinds.BankDebtRepaymentConfirmation;
        var request = new ModRepayBankDebtWithSnapshotRequestDto
        {
            IdempotencyKey = idempotencyKey,
            UserId = context.UserId,
            ColonyId = context.ColonyId,
            CurrentSnapshotId = context.CurrentSnapshotId ?? string.Empty,
            AuthToken = context.AuthToken,
            CurrentGameTicks = currentGameTicks,
            DebtId = debtId ?? string.Empty,
            SilverPaid = silverPaid,
            ConfirmedSnapshot = confirmedSnapshot
        };

        return transport.ForContext(context).PostSnapshotMultipartAsync<ModRepayBankDebtWithSnapshotRequestDto, ModBankDebtResponseDto>(
            ProtocolContractManifest.Find(ProtocolMessageKind.RepayBankDebtWithSnapshot).Route,
            request,
            confirmedPayload,
            cancellationToken);
    }

    private Task<ClashOfRimClientNetworkResult<T>> NotConfigured<T>() => Task.FromResult(
        ClashOfRimClientNetworkResult<T>.Failed("NotConfigured", notConfigured()));
}
