using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Transport;
using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Modules.Trades;

// Owns trade wire contracts; session ownership, local mutation and retry decisions belong to callers.
internal sealed class ClientTradeProtocol
{
    private readonly ClientProtocolTransport transport;
    private readonly Func<string> notConfigured;

    internal ClientTradeProtocol(ClientProtocolTransport transport, Func<string> notConfigured)
    { this.transport = transport; this.notConfigured = notConfigured; }

    internal Task<ClashOfRimClientNetworkResult<ModTradeOrderFeeQuoteResponseDto>> QuoteTradeOrderFeeAsync(
        ClashOfRimClientNetworkContext context,
        IReadOnlyList<ModThingReferenceDto> offeredThings,
        IReadOnlyList<ModThingReferenceDto> requestedThings,
        CancellationToken cancellationToken = default)
    {
        if (!context.IsConfigured)
        {
            return NotConfigured<ModTradeOrderFeeQuoteResponseDto>();
        }

        var request = new ModQuoteTradeOrderFeeRequestDto
        {
            Owner = Identity(context),
            OfferedThings = offeredThings.ToList(),
            RequestedThings = requestedThings.ToList()
        };

        return transport.ForContext(context).PostAsync<ModQuoteTradeOrderFeeRequestDto, ModTradeOrderFeeQuoteResponseDto>(
            ProtocolContractManifest.Find(ProtocolMessageKind.QuoteTradeOrderFee).Route,
            request,
            cancellationToken);
    }

    internal Task<ClashOfRimClientNetworkResult<ModEventCreationResponseDto>> CreateTradeOrderWithSnapshotAsync(
        ClashOfRimClientNetworkContext context,
        string idempotencyKey,
        IReadOnlyList<ModThingReferenceDto> offeredThings,
        IReadOnlyList<ModThingReferenceDto> requestedThings,
        int feeSilver,
        bool allowSelfPickup,
        bool allowServerDropPod,
        ModSnapshotPackageMetadataDto confirmedSnapshot,
        byte[] confirmedPayload,
        CancellationToken cancellationToken = default)
    {
        if (!context.IsConfigured)
        {
            return NotConfigured<ModEventCreationResponseDto>();
        }

        var tradeOrder = new ModCreateTradeOrderRequestDto
        {
            IdempotencyKey = idempotencyKey,
            Owner = Identity(context),
            OfferedThings = offeredThings.ToList(),
            RequestedThings = requestedThings.ToList(),
            FeeSilver = feeSilver,
            AllowSelfPickup = allowSelfPickup,
            AllowServerDropPod = allowServerDropPod
        };
        confirmedSnapshot.SnapshotUploadKind = ModSnapshotUploadKinds.TradeOrderCreationConfirmation;
        var request = new ModCreateTradeOrderWithSnapshotRequestDto
        {
            TradeOrder = tradeOrder,
            ConfirmedSnapshot = confirmedSnapshot
        };

        return transport.ForContext(context).PostSnapshotMultipartAsync<ModCreateTradeOrderWithSnapshotRequestDto, ModEventCreationResponseDto>(
            ProtocolContractManifest.Find(ProtocolMessageKind.CreateTradeOrderWithSnapshot).Route,
            request,
            confirmedPayload,
            cancellationToken);
    }

    internal Task<ClashOfRimClientNetworkResult<ModListTradeOrdersResponseDto>> ListTradeOrdersAsync(
        ClashOfRimClientNetworkContext context,
        string scope = "Open",
        int offset = 0,
        int limit = 10,
        CancellationToken cancellationToken = default)
    {
        if (!context.IsConfigured)
        {
            return NotConfigured<ModListTradeOrdersResponseDto>();
        }

        var request = new ModListTradeOrdersRequestDto
        {
            AuthToken = context.AuthToken,
            UserId = context.UserId,
            ColonyId = context.ColonyId,
            CurrentSnapshotId = context.CurrentSnapshotId ?? string.Empty,
            Scope = scope,
            Offset = Math.Max(0, offset),
            Limit = limit <= 0 ? 10 : limit
        };

        return transport.ForContext(context).PostAsync<ModListTradeOrdersRequestDto, ModListTradeOrdersResponseDto>(
            ProtocolContractManifest.Find(ProtocolMessageKind.ListTradeOrders).Route,
            request,
            cancellationToken);
    }

    internal Task<ClashOfRimClientNetworkResult<ModAcceptTradeOrderResponseDto>> AcceptTradeOrderAsync(
        ClashOfRimClientNetworkContext context,
        string idempotencyKey,
        string tradeEventId,
        bool postagePaidByAcceptor,
        CancellationToken cancellationToken = default)
    {
        if (!context.IsConfigured)
        {
            return NotConfigured<ModAcceptTradeOrderResponseDto>();
        }

        var request = new ModAcceptTradeOrderRequestDto
        {
            AuthToken = context.AuthToken,
            IdempotencyKey = idempotencyKey,
            TradeEventId = tradeEventId,
            Acceptor = Identity(context),
            PostagePaidByAcceptor = postagePaidByAcceptor
        };

        return transport.ForContext(context).PostAsync<ModAcceptTradeOrderRequestDto, ModAcceptTradeOrderResponseDto>(
            ProtocolContractManifest.Find(ProtocolMessageKind.AcceptTradeOrder).Route,
            request,
            cancellationToken);
    }

    internal Task<ClashOfRimClientNetworkResult<ModFulfillTradeOrderResponseDto>> FulfillTradeOrderWithSnapshotAsync(
        ClashOfRimClientNetworkContext context,
        string idempotencyKey,
        string tradeEventId,
        string acceptedMemoEventId,
        IReadOnlyList<ModThingReferenceDto> deliveredThings,
        string fulfillmentMode,
        ModSnapshotPackageMetadataDto confirmedSnapshot,
        byte[] confirmedPayload,
        CancellationToken cancellationToken = default)
    {
        if (!context.IsConfigured)
        {
            return NotConfigured<ModFulfillTradeOrderResponseDto>();
        }

        var fulfillment = new ModFulfillTradeOrderRequestDto
        {
            IdempotencyKey = idempotencyKey,
            TradeEventId = tradeEventId,
            AcceptedMemoEventId = acceptedMemoEventId,
            Acceptor = Identity(context),
            DeliveredThings = deliveredThings.ToList(),
            FulfillmentMode = fulfillmentMode
        };
        confirmedSnapshot.SnapshotUploadKind = ModSnapshotUploadKinds.TradeFulfillmentConfirmation;
        var request = new ModFulfillTradeOrderWithSnapshotRequestDto
        {
            Fulfillment = fulfillment,
            ConfirmedSnapshot = confirmedSnapshot
        };

        return transport.ForContext(context).PostSnapshotMultipartAsync<ModFulfillTradeOrderWithSnapshotRequestDto, ModFulfillTradeOrderResponseDto>(
            ProtocolContractManifest.Find(ProtocolMessageKind.FulfillTradeOrderWithSnapshot).Route,
            request,
            confirmedPayload,
            cancellationToken);
    }

    internal Task<ClashOfRimClientNetworkResult<ModCloseTradeOrderResponseDto>> CancelTradeOrderAsync(
        ClashOfRimClientNetworkContext context,
        string idempotencyKey,
        string tradeEventId,
        string? reason = null,
        CancellationToken cancellationToken = default)
        => SendOrderCommandAsync(context, ProtocolMessageKind.CancelTradeOrder, idempotencyKey, tradeEventId, reason, cancellationToken);

    internal Task<ClashOfRimClientNetworkResult<ModCloseTradeOrderResponseDto>> ClaimTradeOrderAsync(
        ClashOfRimClientNetworkContext context, string idempotencyKey, string tradeEventId, CancellationToken cancellationToken = default)
        => SendOrderCommandAsync(context, ProtocolMessageKind.ClaimTradeOrder, idempotencyKey, tradeEventId, null, cancellationToken);

    private Task<ClashOfRimClientNetworkResult<ModCloseTradeOrderResponseDto>> SendOrderCommandAsync(
        ClashOfRimClientNetworkContext context, ProtocolMessageKind kind, string idempotencyKey,
        string tradeEventId, string? reason, CancellationToken cancellationToken)
    {
        if (!context.IsConfigured)
        {
            return NotConfigured<ModCloseTradeOrderResponseDto>();
        }

        var request = new ModCloseTradeOrderRequestDto
        {
            AuthToken = context.AuthToken,
            IdempotencyKey = idempotencyKey,
            TradeEventId = tradeEventId,
            Owner = Identity(context),
            Reason = reason
        };

        return transport.ForContext(context).PostAsync<ModCloseTradeOrderRequestDto, ModCloseTradeOrderResponseDto>(
            ProtocolContractManifest.Find(kind).Route,
            request,
            cancellationToken);
    }

    private Task<ClashOfRimClientNetworkResult<T>> NotConfigured<T>() =>
        Task.FromResult(ClashOfRimClientNetworkResult<T>.Failed("NotConfigured", notConfigured()));

    private static ModProtocolIdentityDto Identity(ClashOfRimClientNetworkContext context) => new()
    {
        UserId = context.UserId,
        ColonyId = context.ColonyId,
        SnapshotId = context.CurrentSnapshotId
    };
}
