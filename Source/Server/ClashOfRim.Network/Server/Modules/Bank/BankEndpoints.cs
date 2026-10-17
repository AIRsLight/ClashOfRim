using AIRsLight.ClashOfRim.Protocol;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using AIRsLight.ClashOfRim.Network.Modules.Snapshots;

namespace AIRsLight.ClashOfRim.Network.Modules.Bank;

internal static class BankEndpoints
{
    internal static void Map(IEndpointRouteBuilder endpoints, BankService bank)
    {
        endpoints.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.GetBankStatus).Route,
            (GetBankStatusRequest request) => Results.Ok(bank.GetBankStatus(request))).WithMetadata(EndpointSnapshotRecovery.Instance);
        endpoints.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.CreateBankLoan).Route,
            CreateBankLoan).WithMetadata(EndpointSnapshotRecovery.Instance);
        endpoints.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.CreateBankLoanWithSnapshot).Route,
            (HttpRequest http) => CreateBankLoanWithSnapshot(http, bank)).WithMetadata(EndpointSnapshotRecovery.Instance);
        endpoints.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.RepayBankLoan).Route,
            RepayBankLoan).WithMetadata(EndpointSnapshotRecovery.Instance);
        endpoints.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.RepayBankLoanWithSnapshot).Route,
            (HttpRequest http) => RepayBankLoanWithSnapshot(http, bank)).WithMetadata(EndpointSnapshotRecovery.Instance);
        endpoints.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.RepayBankDebt).Route,
            RepayBankDebt).WithMetadata(EndpointSnapshotRecovery.Instance);
        endpoints.MapPost(ProtocolContractManifest.Find(ProtocolMessageKind.RepayBankDebtWithSnapshot).Route,
            (HttpRequest http) => RepayBankDebtWithSnapshot(http, bank)).WithMetadata(EndpointSnapshotRecovery.Instance);
    }

    internal static async Task<IResult> CreateBankLoanWithSnapshot(HttpRequest httpRequest, BankService bank)
    {
        var multipart = await MultipartSnapshotReader.Read<CreateBankLoanWithSnapshotRequest>(httpRequest);
        if (multipart?.Request is null)
            return Results.Ok(new BankLoanResponse(ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed,
                ServerLocalization.Text("Bank.LoanTransactionMissingPayload")), null, 0, null));
        return Results.Ok(bank.CreateBankLoanWithSnapshot(multipart.Request, multipart.Payload,
            httpRequest.Headers[MultipartSnapshotAuthentication.HeaderName].FirstOrDefault()));
    }

    internal static async Task<IResult> RepayBankLoanWithSnapshot(HttpRequest httpRequest, BankService bank)
    {
        var multipart = await MultipartSnapshotReader.Read<RepayBankLoanWithSnapshotRequest>(httpRequest);
        if (multipart?.Request is null)
            return Results.Ok(new BankLoanResponse(ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed,
                ServerLocalization.Text("Bank.RepayLoanTransactionMissingPayload")), null, 0, null));
        return Results.Ok(bank.RepayBankLoanWithSnapshot(multipart.Request, multipart.Payload,
            httpRequest.Headers[MultipartSnapshotAuthentication.HeaderName].FirstOrDefault()));
    }

    internal static async Task<IResult> RepayBankDebtWithSnapshot(HttpRequest httpRequest, BankService bank)
    {
        var multipart = await MultipartSnapshotReader.Read<RepayBankDebtWithSnapshotRequest>(httpRequest);
        if (multipart?.Request is null)
            return Results.Ok(new BankDebtResponse(ProtocolResponse.Reject(ProtocolErrorCode.ValidationFailed,
                ServerLocalization.Text("Bank.DebtRepayTransactionMissingPayload")), null, 0, null));
        return Results.Ok(bank.RepayBankDebtWithSnapshot(multipart.Request, multipart.Payload,
            httpRequest.Headers[MultipartSnapshotAuthentication.HeaderName].FirstOrDefault()));
    }

    internal static IResult CreateBankLoan() => Results.Ok(new BankLoanResponse(
        ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, ServerLocalization.Text("Economy.SnapshotEndpointRequired")),
        loan: null, silverDelta: 0, status: null));

    internal static IResult RepayBankLoan() => Results.Ok(new BankLoanResponse(
        ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, ServerLocalization.Text("Economy.SnapshotEndpointRequired")),
        loan: null, silverDelta: 0, status: null));

    internal static IResult RepayBankDebt() => Results.Ok(new BankDebtResponse(
        ProtocolResponse.Reject(ProtocolErrorCode.ServerRejected, ServerLocalization.Text("Economy.SnapshotEndpointRequired")),
        debt: null, silverDelta: 0, status: null));
}
