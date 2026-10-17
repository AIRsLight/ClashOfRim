using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Synchronization;

namespace AIRsLight.ClashOfRim.Modules.Trades;

internal enum ClientTradeOperation { Create, SelfDelivery, ServerDropPod }
internal sealed record ClientTradeCommand(ClientTradeOperation Operation, string Key, ModTradeOrderSummaryDto Order,
    IReadOnlyList<ModThingReferenceDto> Delivered);
internal sealed record ClientTradeLocalResult(bool Success, IReadOnlyList<ModThingReferenceDto> Delivered, string Message);
internal sealed record ClientTradeReceipt(ModProtocolResponseDto? Result, string? EventId, string? AppliedSnapshotId,
    string? NextLineageToken, string? AcceptorDeliveryId = null, string? OwnerDeliveryId = null, IReadOnlyList<string>? Missing = null);

internal interface IClientTradeSource
{
    ClientTradeOperation Operation { get; }
    string Key { get; }
    string? Validate(ClashOfRimClientNetworkContext context, ModTradeOrderSummaryDto order);
    // Called on the owning game thread. Signal immediately before the first
    // irreversible effect so validation failures can be distinguished from partial changes.
    ClientTradeLocalResult Apply(ClashOfRimClientNetworkContext context, ModTradeOrderSummaryDto order, Action mutationStarting);
}
internal interface IClientTradeOperationsTransport
{
    Task HydrateAsync(ClashOfRimClientNetworkContext context, IReadOnlyList<ModThingReferenceDto> things, CancellationToken token);
    Task<ClashOfRimClientNetworkResult<ClientTradeReceipt>> SubmitAsync(ClashOfRimClientNetworkContext context, ClientTradeCommand command,
        ModSnapshotPackageMetadataDto package, byte[] payload, CancellationToken token);
    Task<ClashOfRimClientNetworkResult<ModTradeOrderFeeQuoteResponseDto>> QuoteAsync(ClashOfRimClientNetworkContext context,
        IReadOnlyList<ModThingReferenceDto> offered, IReadOnlyList<ModThingReferenceDto> requested, CancellationToken token);
}
internal interface IClientTradeOperationsPresentation
{
    string Text(string key, params (string Name, object? Value)[] arguments);
    string Admission(LocalMutationAdmission admission, LocalMutationState? pending);
    void Message(string message, bool rejected);
    void Diagnose(Exception exception);
}
