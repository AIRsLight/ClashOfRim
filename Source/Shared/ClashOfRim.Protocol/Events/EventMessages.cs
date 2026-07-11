using System;
using System.Collections.Generic;

namespace AIRsLight.ClashOfRim.Protocol;

public sealed class PullPendingEventsRequest
{
    public PullPendingEventsRequest(string userId, string colonyId, string currentSnapshotId)
    {
        UserId = userId;
        ColonyId = colonyId;
        CurrentSnapshotId = currentSnapshotId;
    }

    public string UserId { get; }

    public string ColonyId { get; }

    public string CurrentSnapshotId { get; }
}

public sealed class PullPendingEventsResponse
{
    public PullPendingEventsResponse(ProtocolResponse result, EventQueueSummaryDto eventQueue)
    {
        Result = result;
        EventQueue = eventQueue;
    }

    public ProtocolResponse Result { get; }

    public EventQueueSummaryDto EventQueue { get; }
}

public sealed class WaitForEventsRequest
{
    public WaitForEventsRequest(
        string userId,
        string colonyId,
        string currentSnapshotId,
        long knownNotificationVersion,
        int timeoutSeconds)
    {
        UserId = userId;
        ColonyId = colonyId;
        CurrentSnapshotId = currentSnapshotId;
        KnownNotificationVersion = knownNotificationVersion;
        TimeoutSeconds = timeoutSeconds;
    }

    public string UserId { get; }

    public string ColonyId { get; }

    public string CurrentSnapshotId { get; }

    public long KnownNotificationVersion { get; }

    public int TimeoutSeconds { get; }
}

public sealed class WaitForEventsResponse
{
    public WaitForEventsResponse(
        ProtocolResponse result,
        bool changed,
        long notificationVersion,
        EventQueueSummaryDto eventQueue)
    {
        Result = result;
        Changed = changed;
        NotificationVersion = notificationVersion;
        EventQueue = eventQueue;
    }

    public ProtocolResponse Result { get; }

    public bool Changed { get; }

    public long NotificationVersion { get; }

    public EventQueueSummaryDto EventQueue { get; }
}

public sealed class PullEventDetailsRequest
{
    public PullEventDetailsRequest(
        string userId,
        string colonyId,
        string currentSnapshotId,
        IReadOnlyList<string> eventIds)
    {
        UserId = userId;
        ColonyId = colonyId;
        CurrentSnapshotId = currentSnapshotId;
        EventIds = eventIds;
    }

    public string UserId { get; }

    public string ColonyId { get; }

    public string CurrentSnapshotId { get; }

    public IReadOnlyList<string> EventIds { get; }
}

public sealed class PullEventDetailsResponse
{
    public PullEventDetailsResponse(
        ProtocolResponse result,
        IReadOnlyList<EventDetailDto> events)
    {
        Result = result;
        Events = events;
    }

    public ProtocolResponse Result { get; }

    public IReadOnlyList<EventDetailDto> Events { get; }
}

public sealed class EventDetailDto
{
    public EventDetailDto(
        string eventId,
        ServerEventType eventType,
        string status,
        ProtocolIdentity actor,
        ProtocolIdentity target,
        EventTargetContextDto? targetContext,
        EventPayloadType payloadType,
        string payloadSummary)
    {
        EventId = eventId;
        EventType = eventType;
        Status = status;
        Actor = actor;
        Target = target;
        TargetContext = targetContext;
        PayloadType = payloadType;
        PayloadSummary = payloadSummary;
    }

    public string EventId { get; }

    public ServerEventType EventType { get; }

    public string Status { get; }

    public ProtocolIdentity Actor { get; }

    public ProtocolIdentity Target { get; }

    public EventTargetContextDto? TargetContext { get; }

    public EventPayloadType PayloadType { get; }

    public string PayloadSummary { get; }
}

public sealed class EventTargetContextDto
{
    public EventTargetContextDto(
        string? worldObjectId,
        string? mapUniqueId,
        int? tile,
        string landingMode)
    {
        WorldObjectId = worldObjectId;
        MapUniqueId = mapUniqueId;
        Tile = tile;
        LandingMode = landingMode;
    }

    public string? WorldObjectId { get; }

    public string? MapUniqueId { get; }

    public int? Tile { get; }

    public string LandingMode { get; }
}

public sealed class ConfirmEventApplicationResponse
{
    public ConfirmEventApplicationResponse(
        ProtocolResponse result,
        string eventId,
        string? appliedSnapshotId,
        string serverValidationResult,
        string? nextLineageToken = null)
    {
        Result = result;
        EventId = eventId;
        AppliedSnapshotId = appliedSnapshotId;
        ServerValidationResult = serverValidationResult;
        NextLineageToken = nextLineageToken;
    }

    public ProtocolResponse Result { get; }

    public string EventId { get; }

    public string? AppliedSnapshotId { get; }

    public string ServerValidationResult { get; }

    public string? NextLineageToken { get; }
}

public sealed class ConfirmEventApplicationMetadataRequest
{
    public ConfirmEventApplicationMetadataRequest(
        string idempotencyKey,
        string eventId,
        string? sourceEventId,
        string userId,
        string colonyId,
        string baseSnapshotId,
        SnapshotPackageMetadataDto confirmedSnapshot,
        string clientApplicationResult,
        string? authToken = null)
    {
        IdempotencyKey = idempotencyKey;
        EventId = eventId;
        SourceEventId = sourceEventId;
        UserId = userId;
        ColonyId = colonyId;
        BaseSnapshotId = baseSnapshotId;
        ConfirmedSnapshot = confirmedSnapshot;
        ClientApplicationResult = clientApplicationResult;
        AuthToken = authToken;
    }

    public string IdempotencyKey { get; }

    public string EventId { get; }

    public string? SourceEventId { get; }

    public string UserId { get; }

    public string ColonyId { get; }

    public string BaseSnapshotId { get; }

    public SnapshotPackageMetadataDto ConfirmedSnapshot { get; }

    public string ClientApplicationResult { get; }

    public string? AuthToken { get; }
}

public sealed class ConfirmEventApplicationEntry
{
    public ConfirmEventApplicationEntry(
        string eventId,
        string? sourceEventId,
        string clientApplicationResult)
    {
        EventId = eventId;
        SourceEventId = sourceEventId;
        ClientApplicationResult = clientApplicationResult;
    }

    public string EventId { get; }

    public string? SourceEventId { get; }

    public string ClientApplicationResult { get; }
}

public sealed class ConfirmEventApplicationsMetadataRequest
{
    public ConfirmEventApplicationsMetadataRequest(
        string idempotencyKey,
        string userId,
        string colonyId,
        string baseSnapshotId,
        SnapshotPackageMetadataDto confirmedSnapshot,
        IReadOnlyList<ConfirmEventApplicationEntry> applications,
        string? authToken = null)
    {
        IdempotencyKey = idempotencyKey;
        UserId = userId;
        ColonyId = colonyId;
        BaseSnapshotId = baseSnapshotId;
        ConfirmedSnapshot = confirmedSnapshot;
        Applications = applications;
        AuthToken = authToken;
    }

    public string IdempotencyKey { get; }

    public string UserId { get; }

    public string ColonyId { get; }

    public string BaseSnapshotId { get; }

    public SnapshotPackageMetadataDto ConfirmedSnapshot { get; }

    public IReadOnlyList<ConfirmEventApplicationEntry> Applications { get; }

    public string? AuthToken { get; }
}

public sealed class ConfirmEventApplicationResultDto
{
    public ConfirmEventApplicationResultDto(
        string eventId,
        ProtocolResponse result,
        string? appliedSnapshotId,
        string serverValidationResult)
    {
        EventId = eventId;
        Result = result;
        AppliedSnapshotId = appliedSnapshotId;
        ServerValidationResult = serverValidationResult;
    }

    public string EventId { get; }

    public ProtocolResponse Result { get; }

    public string? AppliedSnapshotId { get; }

    public string ServerValidationResult { get; }
}

public sealed class ConfirmEventApplicationsResponse
{
    public ConfirmEventApplicationsResponse(
        ProtocolResponse result,
        string? appliedSnapshotId,
        IReadOnlyList<ConfirmEventApplicationResultDto> applications,
        string? nextLineageToken = null)
    {
        Result = result;
        AppliedSnapshotId = appliedSnapshotId;
        Applications = applications;
        NextLineageToken = nextLineageToken;
    }

    public ProtocolResponse Result { get; }

    public string? AppliedSnapshotId { get; }

    public IReadOnlyList<ConfirmEventApplicationResultDto> Applications { get; }

    public string? NextLineageToken { get; }
}

public sealed class ReportEventApplicationFailureRequest
{
    public ReportEventApplicationFailureRequest(
        string idempotencyKey,
        string eventId,
        string userId,
        string colonyId,
        string currentSnapshotId,
        string reason,
        string? sourceEventId = null,
        string? authToken = null)
    {
        IdempotencyKey = idempotencyKey;
        EventId = eventId;
        UserId = userId;
        ColonyId = colonyId;
        CurrentSnapshotId = currentSnapshotId;
        Reason = reason;
        SourceEventId = sourceEventId;
        AuthToken = authToken;
    }

    public string IdempotencyKey { get; }

    public string EventId { get; }

    public string UserId { get; }

    public string ColonyId { get; }

    public string CurrentSnapshotId { get; }

    public string Reason { get; }

    public string? SourceEventId { get; }

    public string? AuthToken { get; }
}

public sealed class ReportEventApplicationFailureResponse
{
    public ReportEventApplicationFailureResponse(
        ProtocolResponse result,
        string eventId,
        string terminalStatus,
        int affectedEventCount)
    {
        Result = result;
        EventId = eventId;
        TerminalStatus = terminalStatus;
        AffectedEventCount = affectedEventCount;
    }

    public ProtocolResponse Result { get; }

    public string EventId { get; }

    public string TerminalStatus { get; }

    public int AffectedEventCount { get; }
}

public sealed class CreateGiftRequest
{
    public CreateGiftRequest(
        string idempotencyKey,
        ProtocolIdentity actor,
        ProtocolIdentity target,
        IReadOnlyList<ThingReferenceDto> things,
        string? message,
        EventTargetContextDto? targetContext = null,
        string? deliveryKind = null)
    {
        IdempotencyKey = idempotencyKey;
        Actor = actor;
        Target = target;
        Things = things;
        Message = message;
        TargetContext = targetContext;
        DeliveryKind = deliveryKind;
    }

    public string IdempotencyKey { get; }

    public ProtocolIdentity Actor { get; }

    public ProtocolIdentity Target { get; }

    public IReadOnlyList<ThingReferenceDto> Things { get; }

    public string? Message { get; }

    public EventTargetContextDto? TargetContext { get; }

    public string? DeliveryKind { get; }
}

public sealed class CreateGiftWithSnapshotRequest
{
    public CreateGiftWithSnapshotRequest(
        CreateGiftRequest gift,
        SnapshotPackageMetadataDto confirmedSnapshot)
    {
        Gift = gift;
        ConfirmedSnapshot = confirmedSnapshot;
    }

    public CreateGiftRequest Gift { get; }

    public SnapshotPackageMetadataDto ConfirmedSnapshot { get; }
}

public sealed class RejectGiftRequest
{
    public RejectGiftRequest(
        string eventId,
        string userId,
        string colonyId,
        string currentSnapshotId,
        string? reason)
    {
        EventId = eventId;
        UserId = userId;
        ColonyId = colonyId;
        CurrentSnapshotId = currentSnapshotId;
        Reason = reason;
    }

    public string EventId { get; }

    public string UserId { get; }

    public string ColonyId { get; }

    public string CurrentSnapshotId { get; }

    public string? Reason { get; }
}

public sealed class RejectGiftResponse
{
    public RejectGiftResponse(
        ProtocolResponse result,
        string? eventId,
        string? returnEventId,
        bool returnEventCreated)
    {
        Result = result;
        EventId = eventId;
        ReturnEventId = returnEventId;
        ReturnEventCreated = returnEventCreated;
    }

    public ProtocolResponse Result { get; }

    public string? EventId { get; }

    public string? ReturnEventId { get; }

    public bool ReturnEventCreated { get; }
}

public sealed class StorePawnPackageRequest
{
    public StorePawnPackageRequest(
        string idempotencyKey,
        ProtocolIdentity owner,
        PawnExchangePackageDto pawnPackage)
    {
        IdempotencyKey = idempotencyKey;
        Owner = owner;
        PawnPackage = pawnPackage;
    }

    public string IdempotencyKey { get; }

    public ProtocolIdentity Owner { get; }

    public PawnExchangePackageDto PawnPackage { get; }
}

public sealed class StorePawnPackageResponse
{
    public StorePawnPackageResponse(
        ProtocolResponse result,
        string? pawnPackageId,
        string? pawnGlobalId)
    {
        Result = result;
        PawnPackageId = pawnPackageId;
        PawnGlobalId = pawnGlobalId;
    }

    public ProtocolResponse Result { get; }

    public string? PawnPackageId { get; }

    public string? PawnGlobalId { get; }
}

public sealed class GetPawnPackageRequest
{
    public GetPawnPackageRequest(
        ProtocolIdentity requester,
        string pawnPackageId)
    {
        Requester = requester;
        PawnPackageId = pawnPackageId;
    }

    public ProtocolIdentity Requester { get; }

    public string PawnPackageId { get; }
}

public sealed class GetPawnPackageResponse
{
    public GetPawnPackageResponse(
        ProtocolResponse result,
        string? pawnPackageId,
        PawnExchangePackageDto? pawnPackage)
    {
        Result = result;
        PawnPackageId = pawnPackageId;
        PawnPackage = pawnPackage;
    }

    public ProtocolResponse Result { get; }

    public string? PawnPackageId { get; }

    public PawnExchangePackageDto? PawnPackage { get; }
}

public sealed class StoreThingPackageRequest
{
    public StoreThingPackageRequest(
        string idempotencyKey,
        ProtocolIdentity owner,
        ThingStatePackageDto thingPackage)
    {
        IdempotencyKey = idempotencyKey;
        Owner = owner;
        ThingPackage = thingPackage;
    }

    public string IdempotencyKey { get; }

    public ProtocolIdentity Owner { get; }

    public ThingStatePackageDto ThingPackage { get; }
}

public sealed class StoreThingPackageResponse
{
    public StoreThingPackageResponse(
        ProtocolResponse result,
        string? thingPackageId,
        string? fingerprint)
    {
        Result = result;
        ThingPackageId = thingPackageId;
        Fingerprint = fingerprint;
    }

    public ProtocolResponse Result { get; }

    public string? ThingPackageId { get; }

    public string? Fingerprint { get; }
}

public sealed class GetThingPackageRequest
{
    public GetThingPackageRequest(
        ProtocolIdentity requester,
        string thingPackageId)
    {
        Requester = requester;
        ThingPackageId = thingPackageId;
    }

    public ProtocolIdentity Requester { get; }

    public string ThingPackageId { get; }
}

public sealed class GetThingPackageResponse
{
    public GetThingPackageResponse(
        ProtocolResponse result,
        string? thingPackageId,
        ThingStatePackageDto? thingPackage)
    {
        Result = result;
        ThingPackageId = thingPackageId;
        ThingPackage = thingPackage;
    }

    public ProtocolResponse Result { get; }

    public string? ThingPackageId { get; }

    public ThingStatePackageDto? ThingPackage { get; }
}

public sealed class CreateTradeOrderRequest
{
    public CreateTradeOrderRequest(
        string idempotencyKey,
        ProtocolIdentity owner,
        IReadOnlyList<ThingReferenceDto> offeredThings,
        IReadOnlyList<ThingReferenceDto> requestedThings,
        int feeSilver,
        bool allowSelfPickup,
        bool allowServerDropPod)
    {
        IdempotencyKey = idempotencyKey;
        Owner = owner;
        OfferedThings = offeredThings;
        RequestedThings = requestedThings;
        FeeSilver = feeSilver;
        AllowSelfPickup = allowSelfPickup;
        AllowServerDropPod = allowServerDropPod;
    }

    public string IdempotencyKey { get; }

    public ProtocolIdentity Owner { get; }

    public IReadOnlyList<ThingReferenceDto> OfferedThings { get; }

    public IReadOnlyList<ThingReferenceDto> RequestedThings { get; }

    public int FeeSilver { get; }

    public bool AllowSelfPickup { get; }

    public bool AllowServerDropPod { get; }
}

public sealed class QuoteTradeOrderFeeRequest
{
    public QuoteTradeOrderFeeRequest(
        ProtocolIdentity owner,
        IReadOnlyList<ThingReferenceDto> offeredThings,
        IReadOnlyList<ThingReferenceDto>? requestedThings = null)
    {
        Owner = owner;
        OfferedThings = offeredThings;
        RequestedThings = requestedThings ?? Array.Empty<ThingReferenceDto>();
    }

    public ProtocolIdentity Owner { get; }

    public IReadOnlyList<ThingReferenceDto> OfferedThings { get; }

    public IReadOnlyList<ThingReferenceDto> RequestedThings { get; }
}

public sealed class TradeOrderFeeQuoteResponse
{
    public TradeOrderFeeQuoteResponse(
        ProtocolResponse result,
        int requiredFeeSilver,
        IReadOnlyList<string>? missingMarketValueDefs = null)
    {
        Result = result;
        RequiredFeeSilver = requiredFeeSilver;
        MissingMarketValueDefs = missingMarketValueDefs ?? Array.Empty<string>();
    }

    public ProtocolResponse Result { get; }

    public int RequiredFeeSilver { get; }

    public IReadOnlyList<string> MissingMarketValueDefs { get; }
}

public sealed class CreateTradeOrderWithSnapshotRequest
{
    public CreateTradeOrderWithSnapshotRequest(
        CreateTradeOrderRequest tradeOrder,
        SnapshotPackageMetadataDto confirmedSnapshot)
    {
        TradeOrder = tradeOrder;
        ConfirmedSnapshot = confirmedSnapshot;
    }

    public CreateTradeOrderRequest TradeOrder { get; }

    public SnapshotPackageMetadataDto ConfirmedSnapshot { get; }
}

public sealed class ListTradeOrdersRequest
{
    public ListTradeOrdersRequest(
        string userId,
        string colonyId,
        string currentSnapshotId,
        string scope = "Open",
        int offset = 0,
        int limit = 10)
    {
        UserId = userId;
        ColonyId = colonyId;
        CurrentSnapshotId = currentSnapshotId;
        Scope = scope;
        Offset = offset;
        Limit = limit;
    }

    public string UserId { get; }

    public string ColonyId { get; }

    public string CurrentSnapshotId { get; }

    public string Scope { get; }

    public int Offset { get; }

    public int Limit { get; }
}

public sealed class TradeOrderSummaryDto
{
    public TradeOrderSummaryDto(
        string eventId,
        ProtocolIdentity owner,
        IReadOnlyList<ThingReferenceDto> offeredThings,
        IReadOnlyList<ThingReferenceDto> requestedThings,
        int feeSilver,
        bool allowSelfPickup,
        bool allowServerDropPod,
        int acceptedMemoCount,
        DateTimeOffset createdAtUtc,
        string status = "",
        bool viewerHasAccepted = false,
        string? viewerAcceptedMemoEventId = null,
        TradePostageQuoteDto? serverDropPodPostage = null,
        EventTargetContextDto? targetContext = null,
        DateTimeOffset? expiresAtUtc = null,
        ProtocolIdentity? counterparty = null)
    {
        EventId = eventId;
        Owner = owner;
        Counterparty = counterparty;
        OfferedThings = offeredThings;
        RequestedThings = requestedThings;
        FeeSilver = feeSilver;
        AllowSelfPickup = allowSelfPickup;
        AllowServerDropPod = allowServerDropPod;
        AcceptedMemoCount = acceptedMemoCount;
        CreatedAtUtc = createdAtUtc;
        Status = status;
        ViewerHasAccepted = viewerHasAccepted;
        ViewerAcceptedMemoEventId = viewerAcceptedMemoEventId;
        ServerDropPodPostage = serverDropPodPostage;
        TargetContext = targetContext;
        ExpiresAtUtc = expiresAtUtc;
    }

    public string EventId { get; }

    public ProtocolIdentity Owner { get; }

    public ProtocolIdentity? Counterparty { get; }

    public IReadOnlyList<ThingReferenceDto> OfferedThings { get; }

    public IReadOnlyList<ThingReferenceDto> RequestedThings { get; }

    public int FeeSilver { get; }

    public bool AllowSelfPickup { get; }

    public bool AllowServerDropPod { get; }

    public int AcceptedMemoCount { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    public string Status { get; }

    public bool ViewerHasAccepted { get; }

    public string? ViewerAcceptedMemoEventId { get; }

    public TradePostageQuoteDto? ServerDropPodPostage { get; }

    public EventTargetContextDto? TargetContext { get; }

    public DateTimeOffset? ExpiresAtUtc { get; }
}

public sealed class TradePostageQuoteDto
{
    public TradePostageQuoteDto(
        bool reachable,
        int? postageSilver,
        int? distanceTiles,
        string status)
    {
        Reachable = reachable;
        PostageSilver = postageSilver;
        DistanceTiles = distanceTiles;
        Status = status;
    }

    public bool Reachable { get; }

    public int? PostageSilver { get; }

    public int? DistanceTiles { get; }

    public string Status { get; }
}

public sealed class ListTradeOrdersResponse
{
    public ListTradeOrdersResponse(
        ProtocolResponse result,
        IReadOnlyList<TradeOrderSummaryDto> orders,
        bool tradeMarketplaceEnabled = true,
        int totalCount = 0,
        int offset = 0,
        int limit = 0,
        bool hasMore = false)
    {
        Result = result;
        Orders = orders;
        TradeMarketplaceEnabled = tradeMarketplaceEnabled;
        TotalCount = totalCount;
        Offset = offset;
        Limit = limit;
        HasMore = hasMore;
    }

    public ProtocolResponse Result { get; }

    public IReadOnlyList<TradeOrderSummaryDto> Orders { get; }

    public bool TradeMarketplaceEnabled { get; }

    public int TotalCount { get; }

    public int Offset { get; }

    public int Limit { get; }

    public bool HasMore { get; }
}

public sealed class AcceptTradeOrderRequest
{
    public AcceptTradeOrderRequest(
        string idempotencyKey,
        string tradeEventId,
        ProtocolIdentity acceptor,
        bool postagePaidByAcceptor)
    {
        IdempotencyKey = idempotencyKey;
        TradeEventId = tradeEventId;
        Acceptor = acceptor;
        PostagePaidByAcceptor = postagePaidByAcceptor;
    }

    public string IdempotencyKey { get; }

    public string TradeEventId { get; }

    public ProtocolIdentity Acceptor { get; }

    public bool PostagePaidByAcceptor { get; }
}

public sealed class AcceptTradeOrderResponse
{
    public AcceptTradeOrderResponse(
        ProtocolResponse result,
        string? tradeEventId,
        string? memoEventId,
        bool memoCreated)
    {
        Result = result;
        TradeEventId = tradeEventId;
        MemoEventId = memoEventId;
        MemoCreated = memoCreated;
    }

    public ProtocolResponse Result { get; }

    public string? TradeEventId { get; }

    public string? MemoEventId { get; }

    public bool MemoCreated { get; }
}

public sealed class FulfillTradeOrderRequest
{
    public FulfillTradeOrderRequest(
        string idempotencyKey,
        string tradeEventId,
        string acceptedMemoEventId,
        ProtocolIdentity acceptor,
        IReadOnlyList<ThingReferenceDto> deliveredThings,
        string fulfillmentMode = "SelfDelivery")
    {
        IdempotencyKey = idempotencyKey;
        TradeEventId = tradeEventId;
        AcceptedMemoEventId = acceptedMemoEventId;
        Acceptor = acceptor;
        DeliveredThings = deliveredThings;
        FulfillmentMode = fulfillmentMode;
    }

    public string IdempotencyKey { get; }

    public string TradeEventId { get; }

    public string AcceptedMemoEventId { get; }

    public ProtocolIdentity Acceptor { get; }

    public IReadOnlyList<ThingReferenceDto> DeliveredThings { get; }

    public string FulfillmentMode { get; }
}

public sealed class FulfillTradeOrderWithSnapshotRequest
{
    public FulfillTradeOrderWithSnapshotRequest(
        FulfillTradeOrderRequest fulfillment,
        SnapshotPackageMetadataDto confirmedSnapshot)
    {
        Fulfillment = fulfillment;
        ConfirmedSnapshot = confirmedSnapshot;
    }

    public FulfillTradeOrderRequest Fulfillment { get; }

    public SnapshotPackageMetadataDto ConfirmedSnapshot { get; }
}

public sealed class FulfillTradeOrderResponse
{
    public FulfillTradeOrderResponse(
        ProtocolResponse result,
        string? tradeEventId,
        string? acceptedMemoEventId,
        string? exchangeEventId,
        bool exchangeCreated,
        IReadOnlyList<ThingReferenceDto> receivedThings,
        IReadOnlyList<string> missingRequirements,
        string tradeStatus,
        string? acceptorDeliveryEventId = null,
        string? ownerDeliveryEventId = null,
        string? appliedSnapshotId = null,
        string? nextLineageToken = null)
    {
        Result = result;
        TradeEventId = tradeEventId;
        AcceptedMemoEventId = acceptedMemoEventId;
        ExchangeEventId = exchangeEventId;
        ExchangeCreated = exchangeCreated;
        ReceivedThings = receivedThings;
        MissingRequirements = missingRequirements;
        TradeStatus = tradeStatus;
        AcceptorDeliveryEventId = acceptorDeliveryEventId;
        OwnerDeliveryEventId = ownerDeliveryEventId;
        AppliedSnapshotId = appliedSnapshotId;
        NextLineageToken = nextLineageToken;
    }

    public ProtocolResponse Result { get; }

    public string? TradeEventId { get; }

    public string? AcceptedMemoEventId { get; }

    public string? ExchangeEventId { get; }

    public bool ExchangeCreated { get; }

    public IReadOnlyList<ThingReferenceDto> ReceivedThings { get; }

    public IReadOnlyList<string> MissingRequirements { get; }

    public string TradeStatus { get; }

    public string? AcceptorDeliveryEventId { get; }

    public string? OwnerDeliveryEventId { get; }

    public string? AppliedSnapshotId { get; }

    public string? NextLineageToken { get; }
}

public sealed class CloseTradeOrderRequest
{
    public CloseTradeOrderRequest(
        string idempotencyKey,
        string tradeEventId,
        ProtocolIdentity owner,
        string? reason)
    {
        IdempotencyKey = idempotencyKey;
        TradeEventId = tradeEventId;
        Owner = owner;
        Reason = reason;
    }

    public string IdempotencyKey { get; }

    public string TradeEventId { get; }

    public ProtocolIdentity Owner { get; }

    public string? Reason { get; }
}

public sealed class CloseTradeOrderResponse
{
    public CloseTradeOrderResponse(
        ProtocolResponse result,
        string? tradeEventId,
        string terminalStatus,
        int notifiedAcceptorCount)
    {
        Result = result;
        TradeEventId = tradeEventId;
        TerminalStatus = terminalStatus;
        NotifiedAcceptorCount = notifiedAcceptorCount;
    }

    public ProtocolResponse Result { get; }

    public string? TradeEventId { get; }

    public string TerminalStatus { get; }

    public int NotifiedAcceptorCount { get; }
}

public sealed class CreateRaidRequest
{
    public CreateRaidRequest(
        string idempotencyKey,
        ProtocolIdentity attacker,
        ProtocolIdentity defender,
        bool isHostile,
        bool defenderOnline,
        int defenderWealth,
        DateTimeOffset? defenderRaidCooldownUntilUtc,
        string? raidPreparationId,
        string targetWorldObjectId,
        string targetMapId,
        string defenderSnapshotId,
        IReadOnlyList<string> pawnGlobalKeys,
        IReadOnlyList<ThingReferenceDto> carriedThings,
        int? targetTile = null,
        string opponentKind = "Player",
        string? guardDeploymentId = null)
    {
        IdempotencyKey = idempotencyKey;
        Attacker = attacker;
        Defender = defender;
        IsHostile = isHostile;
        DefenderOnline = defenderOnline;
        DefenderWealth = defenderWealth;
        DefenderRaidCooldownUntilUtc = defenderRaidCooldownUntilUtc;
        RaidPreparationId = raidPreparationId;
        TargetWorldObjectId = targetWorldObjectId;
        TargetMapId = targetMapId;
        DefenderSnapshotId = defenderSnapshotId;
        PawnGlobalKeys = pawnGlobalKeys;
        CarriedThings = carriedThings;
        TargetTile = targetTile;
        OpponentKind = opponentKind;
        GuardDeploymentId = guardDeploymentId;
    }

    public string IdempotencyKey { get; }

    public ProtocolIdentity Attacker { get; }

    public ProtocolIdentity Defender { get; }

    public bool IsHostile { get; }

    public bool DefenderOnline { get; }

    public int DefenderWealth { get; }

    public DateTimeOffset? DefenderRaidCooldownUntilUtc { get; }

    public string? RaidPreparationId { get; }

    public string TargetWorldObjectId { get; }

    public string TargetMapId { get; }

    public string DefenderSnapshotId { get; }

    public IReadOnlyList<string> PawnGlobalKeys { get; }

    public IReadOnlyList<ThingReferenceDto> CarriedThings { get; }

    public int? TargetTile { get; }

    public string OpponentKind { get; }

    public string? GuardDeploymentId { get; }
}

public sealed class CreateRaidWithSnapshotRequest
{
    public CreateRaidWithSnapshotRequest(
        CreateRaidRequest raid,
        SnapshotPackageMetadataDto confirmedSnapshot)
    {
        Raid = raid;
        ConfirmedSnapshot = confirmedSnapshot;
    }

    public CreateRaidRequest Raid { get; }

    public SnapshotPackageMetadataDto ConfirmedSnapshot { get; }
}

public sealed class PrepareRaidRequest
{
    public PrepareRaidRequest(
        string idempotencyKey,
        ProtocolIdentity attacker,
        ProtocolIdentity defender,
        bool isHostile,
        string targetWorldObjectId,
        string targetMapId,
        int? targetTile = null,
        string opponentKind = "Player")
    {
        IdempotencyKey = idempotencyKey;
        Attacker = attacker;
        Defender = defender;
        IsHostile = isHostile;
        TargetWorldObjectId = targetWorldObjectId;
        TargetMapId = targetMapId;
        TargetTile = targetTile;
        OpponentKind = opponentKind;
    }

    public string IdempotencyKey { get; }

    public ProtocolIdentity Attacker { get; }

    public ProtocolIdentity Defender { get; }

    public bool IsHostile { get; }

    public string TargetWorldObjectId { get; }

    public string TargetMapId { get; }

    public int? TargetTile { get; }

    public string OpponentKind { get; }
}

public sealed class PrepareRaidResponse
{
    public PrepareRaidResponse(
        ProtocolResponse result,
        string? raidEventId,
        string? raidPreparationId,
        string? defenderSnapshotId,
        SnapshotPackageMetadataDto? defenderPackage,
        DateTimeOffset? expiresAtUtc,
        double? raidMaxDurationMinutes = null,
        double? raidTimeoutGraceMinutes = null,
        RaidGuardDeploymentDto? guardDeployment = null)
    {
        Result = result;
        RaidEventId = raidEventId;
        RaidPreparationId = raidPreparationId;
        DefenderSnapshotId = defenderSnapshotId;
        DefenderPackage = defenderPackage;
        ExpiresAtUtc = expiresAtUtc;
        RaidMaxDurationMinutes = raidMaxDurationMinutes;
        RaidTimeoutGraceMinutes = raidTimeoutGraceMinutes;
        GuardDeployment = guardDeployment;
    }

    public ProtocolResponse Result { get; }

    public string? RaidEventId { get; }

    public string? RaidPreparationId { get; }

    public string? DefenderSnapshotId { get; }

    public SnapshotPackageMetadataDto? DefenderPackage { get; }

    public DateTimeOffset? ExpiresAtUtc { get; }

    public double? RaidMaxDurationMinutes { get; }

    public double? RaidTimeoutGraceMinutes { get; }

    public RaidGuardDeploymentDto? GuardDeployment { get; }
}

public sealed class RaidGuardDeploymentDto
{
    public RaidGuardDeploymentDto(
        string contractId,
        string tier,
        int priceSilver,
        float pointRatio,
        int points,
        int seed)
    {
        ContractId = contractId;
        Tier = tier;
        PriceSilver = priceSilver;
        PointRatio = pointRatio;
        Points = points;
        Seed = seed;
    }

    public string ContractId { get; }

    public string Tier { get; }

    public int PriceSilver { get; }

    public float PointRatio { get; }

    public int Points { get; }

    public int Seed { get; }
}

public sealed class CreateSupportPawnRequest
{
    public CreateSupportPawnRequest(
        string idempotencyKey,
        ProtocolIdentity actor,
        ProtocolIdentity target,
        string pawnGlobalKey,
        string sourceSnapshotId,
        string? pawnName,
        bool temporaryControl,
        DateTimeOffset? expectedReturnAtUtc,
        CrossMapPawnReferenceDto? pawnReference,
        PawnExchangePackageDto? pawnPackage,
        EventTargetContextDto? targetContext,
        int? sourceTile = null,
        string? sourceCaravanLoadId = null,
        bool permanentSupport = false,
        int? supportDurationDays = null,
        long? expiresAtGameTicks = null,
        bool autoReturnOnSettlement = false)
    {
        IdempotencyKey = idempotencyKey;
        Actor = actor;
        Target = target;
        PawnGlobalKey = pawnGlobalKey;
        SourceSnapshotId = sourceSnapshotId;
        PawnName = pawnName;
        TemporaryControl = temporaryControl;
        ExpectedReturnAtUtc = expectedReturnAtUtc;
        PawnReference = pawnReference;
        PawnPackage = pawnPackage;
        TargetContext = targetContext;
        SourceTile = sourceTile;
        SourceCaravanLoadId = sourceCaravanLoadId;
        PermanentSupport = permanentSupport;
        SupportDurationDays = supportDurationDays;
        ExpiresAtGameTicks = expiresAtGameTicks;
        AutoReturnOnSettlement = autoReturnOnSettlement;
    }

    public string IdempotencyKey { get; }

    public ProtocolIdentity Actor { get; }

    public ProtocolIdentity Target { get; }

    public string PawnGlobalKey { get; }

    public string SourceSnapshotId { get; }

    public string? PawnName { get; }

    public bool TemporaryControl { get; }

    public DateTimeOffset? ExpectedReturnAtUtc { get; }

    public CrossMapPawnReferenceDto? PawnReference { get; }

    public PawnExchangePackageDto? PawnPackage { get; }

    public EventTargetContextDto? TargetContext { get; }

    public int? SourceTile { get; }

    public string? SourceCaravanLoadId { get; }

    public bool PermanentSupport { get; }

    public int? SupportDurationDays { get; }

    public long? ExpiresAtGameTicks { get; }

    public bool AutoReturnOnSettlement { get; }
}

public sealed class CreateSupportPawnWithSnapshotRequest
{
    public CreateSupportPawnWithSnapshotRequest(
        CreateSupportPawnRequest supportPawn,
        SnapshotPackageMetadataDto confirmedSnapshot)
    {
        SupportPawn = supportPawn;
        ConfirmedSnapshot = confirmedSnapshot;
    }

    public CreateSupportPawnRequest SupportPawn { get; }

    public SnapshotPackageMetadataDto ConfirmedSnapshot { get; }
}

public sealed class CreateDiplomacyEventRequest
{
    public CreateDiplomacyEventRequest(
        string idempotencyKey,
        ProtocolIdentity actor,
        ProtocolIdentity target,
        string kind,
        string? message,
        DateTimeOffset? expiresAtUtc)
    {
        IdempotencyKey = idempotencyKey;
        Actor = actor;
        Target = target;
        Kind = kind;
        Message = message;
        ExpiresAtUtc = expiresAtUtc;
    }

    public string IdempotencyKey { get; }

    public ProtocolIdentity Actor { get; }

    public ProtocolIdentity Target { get; }

    public string Kind { get; }

    public string? Message { get; }

    public DateTimeOffset? ExpiresAtUtc { get; }
}

public sealed class RespondDiplomacyEventRequest
{
    public RespondDiplomacyEventRequest(
        string eventId,
        string userId,
        string colonyId,
        string currentSnapshotId,
        bool accepted,
        string? reason)
    {
        EventId = eventId;
        UserId = userId;
        ColonyId = colonyId;
        CurrentSnapshotId = currentSnapshotId;
        Accepted = accepted;
        Reason = reason;
    }

    public string EventId { get; }

    public string UserId { get; }

    public string ColonyId { get; }

    public string CurrentSnapshotId { get; }

    public bool Accepted { get; }

    public string? Reason { get; }
}

public sealed class DiplomacyEventResponse
{
    public DiplomacyEventResponse(
        ProtocolResponse result,
        string? eventId,
        string? notificationEventId,
        string? relationKind)
    {
        Result = result;
        EventId = eventId;
        NotificationEventId = notificationEventId;
        RelationKind = relationKind;
    }

    public ProtocolResponse Result { get; }

    public string? EventId { get; }

    public string? NotificationEventId { get; }

    public string? RelationKind { get; }
}

public sealed class RejectSupportPawnRequest
{
    public RejectSupportPawnRequest(
        string eventId,
        string userId,
        string colonyId,
        string currentSnapshotId,
        string? reason)
    {
        EventId = eventId;
        UserId = userId;
        ColonyId = colonyId;
        CurrentSnapshotId = currentSnapshotId;
        Reason = reason;
    }

    public string EventId { get; }

    public string UserId { get; }

    public string ColonyId { get; }

    public string CurrentSnapshotId { get; }

    public string? Reason { get; }
}

public sealed class RejectSupportPawnResponse
{
    public RejectSupportPawnResponse(
        ProtocolResponse result,
        string? eventId,
        string? returnEventId,
        bool returnEventCreated)
    {
        Result = result;
        EventId = eventId;
        ReturnEventId = returnEventId;
        ReturnEventCreated = returnEventCreated;
    }

    public ProtocolResponse Result { get; }

    public string? EventId { get; }

    public string? ReturnEventId { get; }

    public bool ReturnEventCreated { get; }
}

public sealed class FinishSupportPawnRequest
{
    public FinishSupportPawnRequest(
        string idempotencyKey,
        string eventId,
        string userId,
        string colonyId,
        string currentSnapshotId,
        string finishReason,
        string pawnGlobalKey,
        string? pawnName,
        bool pawnDead,
        PawnExchangePackageDto? pawnPackage)
    {
        IdempotencyKey = idempotencyKey;
        EventId = eventId;
        UserId = userId;
        ColonyId = colonyId;
        CurrentSnapshotId = currentSnapshotId;
        FinishReason = finishReason;
        PawnGlobalKey = pawnGlobalKey;
        PawnName = pawnName;
        PawnDead = pawnDead;
        PawnPackage = pawnPackage;
    }

    public string IdempotencyKey { get; }

    public string EventId { get; }

    public string UserId { get; }

    public string ColonyId { get; }

    public string CurrentSnapshotId { get; }

    public string FinishReason { get; }

    public string PawnGlobalKey { get; }

    public string? PawnName { get; }

    public bool PawnDead { get; }

    public PawnExchangePackageDto? PawnPackage { get; }
}

public sealed class FinishSupportPawnResponse
{
    public FinishSupportPawnResponse(
        ProtocolResponse result,
        string? eventId,
        string? returnEventId,
        string? notificationEventId,
        bool created)
    {
        Result = result;
        EventId = eventId;
        ReturnEventId = returnEventId;
        NotificationEventId = notificationEventId;
        Created = created;
    }

    public ProtocolResponse Result { get; }

    public string? EventId { get; }

    public string? ReturnEventId { get; }

    public string? NotificationEventId { get; }

    public bool Created { get; }
}

public sealed class CrossMapPawnReferenceDto
{
    public CrossMapPawnReferenceDto(
        string globalId,
        string? sourceSnapshotId,
        string? name,
        bool? dead,
        string? faction,
        Dictionary<string, string?>? metadata = null)
    {
        GlobalId = globalId;
        SourceSnapshotId = sourceSnapshotId;
        Name = name;
        Dead = dead;
        Faction = faction;
        Metadata = metadata ?? new Dictionary<string, string?>(StringComparer.Ordinal);
    }

    public string GlobalId { get; }

    public string? SourceSnapshotId { get; }

    public string? Name { get; }

    public bool? Dead { get; }

    public string? Faction { get; }

    public Dictionary<string, string?> Metadata { get; }
}

public sealed class PawnExchangePackageDto
{
    public PawnExchangePackageDto(
        int packageVersion,
        CrossMapPawnReferenceDto reference,
        PawnExchangeIdentityDto identity,
        PawnExchangeAppearanceDto appearance,
        PawnExchangeStatusDto status,
        IReadOnlyList<PawnExchangeEquipmentItemDto> apparel,
        IReadOnlyList<PawnExchangeEquipmentItemDto> equipment,
        IReadOnlyList<PawnExchangeRelationshipStubDto> relationships,
        PawnScribePayloadDto? scribe = null,
        IReadOnlyList<PawnExchangeExtensionPackageDto>? extensions = null)
    {
        PackageVersion = packageVersion;
        Reference = reference;
        Identity = identity;
        Appearance = appearance;
        Status = status;
        Apparel = apparel;
        Equipment = equipment;
        Relationships = relationships;
        Scribe = scribe;
        Extensions = extensions ?? Array.Empty<PawnExchangeExtensionPackageDto>();
    }

    public int PackageVersion { get; }

    public CrossMapPawnReferenceDto Reference { get; }

    public PawnExchangeIdentityDto Identity { get; }

    public PawnExchangeAppearanceDto Appearance { get; }

    public PawnExchangeStatusDto Status { get; }

    public IReadOnlyList<PawnExchangeEquipmentItemDto> Apparel { get; }

    public IReadOnlyList<PawnExchangeEquipmentItemDto> Equipment { get; }

    public IReadOnlyList<PawnExchangeRelationshipStubDto> Relationships { get; }

    public PawnScribePayloadDto? Scribe { get; }

    public IReadOnlyList<PawnExchangeExtensionPackageDto> Extensions { get; }
}

public sealed class PawnExchangeExtensionPackageDto
{
    public PawnExchangeExtensionPackageDto(
        string providerId,
        string kind,
        Dictionary<string, string?>? metadata = null,
        string? payloadJson = null)
    {
        ProviderId = providerId;
        Kind = kind;
        Metadata = metadata ?? new Dictionary<string, string?>(StringComparer.Ordinal);
        PayloadJson = payloadJson;
    }

    public string ProviderId { get; }

    public string Kind { get; }

    public Dictionary<string, string?> Metadata { get; }

    public string? PayloadJson { get; }
}

public sealed class PawnExchangeIdentityDto
{
    public PawnExchangeIdentityDto(string? thingDef, string? pawnKindDef, string? factionDef, string? gender)
    {
        ThingDef = thingDef;
        PawnKindDef = pawnKindDef;
        FactionDef = factionDef;
        Gender = gender;
    }

    public string? ThingDef { get; }

    public string? PawnKindDef { get; }

    public string? FactionDef { get; }

    public string? Gender { get; }
}

public sealed class PawnExchangeAppearanceDto
{
    public PawnExchangeAppearanceDto(
        string? displayName,
        string? bodyTypeDef,
        string? headTypeDef,
        string? hairDef,
        string? beardDef,
        string? skinColor,
        string? hairColor)
    {
        DisplayName = displayName;
        BodyTypeDef = bodyTypeDef;
        HeadTypeDef = headTypeDef;
        HairDef = hairDef;
        BeardDef = beardDef;
        SkinColor = skinColor;
        HairColor = hairColor;
    }

    public string? DisplayName { get; }

    public string? BodyTypeDef { get; }

    public string? HeadTypeDef { get; }

    public string? HairDef { get; }

    public string? BeardDef { get; }

    public string? SkinColor { get; }

    public string? HairColor { get; }
}

public sealed class PawnExchangeStatusDto
{
    public PawnExchangeStatusDto(
        bool dead,
        long? biologicalAgeTicks,
        long? chronologicalAgeTicks,
        string? deathCauseDef,
        string? healthState)
    {
        Dead = dead;
        BiologicalAgeTicks = biologicalAgeTicks;
        ChronologicalAgeTicks = chronologicalAgeTicks;
        DeathCauseDef = deathCauseDef;
        HealthState = healthState;
    }

    public bool Dead { get; }

    public long? BiologicalAgeTicks { get; }

    public long? ChronologicalAgeTicks { get; }

    public string? DeathCauseDef { get; }

    public string? HealthState { get; }
}

public sealed class PawnExchangeEquipmentItemDto
{
    public PawnExchangeEquipmentItemDto(
        string globalId,
        string? def,
        string? label,
        int stackCount,
        string? quality,
        int? hitPoints,
        bool? wornByCorpse,
        bool? biocoded,
        string? biocodedPawnGlobalId,
        bool? uniqueWeapon,
        string? uniqueWeaponName,
        IReadOnlyList<string>? uniqueWeaponTraits)
    {
        GlobalId = globalId;
        Def = def;
        Label = label;
        StackCount = stackCount;
        Quality = quality;
        HitPoints = hitPoints;
        WornByCorpse = wornByCorpse;
        Biocoded = biocoded;
        BiocodedPawnGlobalId = biocodedPawnGlobalId;
        UniqueWeapon = uniqueWeapon;
        UniqueWeaponName = uniqueWeaponName;
        UniqueWeaponTraits = uniqueWeaponTraits;
    }

    public string GlobalId { get; }

    public string? Def { get; }

    public string? Label { get; }

    public int StackCount { get; }

    public string? Quality { get; }

    public int? HitPoints { get; }

    public bool? WornByCorpse { get; }

    public bool? Biocoded { get; }

    public string? BiocodedPawnGlobalId { get; }

    public bool? UniqueWeapon { get; }

    public string? UniqueWeaponName { get; }

    public IReadOnlyList<string>? UniqueWeaponTraits { get; }
}

public sealed class PawnExchangeRelationshipStubDto
{
    public PawnExchangeRelationshipStubDto(string otherPawnGlobalId, string? otherPawnName, bool otherPawnDead, string? relationDef)
    {
        OtherPawnGlobalId = otherPawnGlobalId;
        OtherPawnName = otherPawnName;
        OtherPawnDead = otherPawnDead;
        RelationDef = relationDef;
    }

    public string OtherPawnGlobalId { get; }

    public string? OtherPawnName { get; }

    public bool OtherPawnDead { get; }

    public string? RelationDef { get; }
}

public sealed class PawnScribePayloadDto
{
    public PawnScribePayloadDto(string xml, string? xmlSha256, IReadOnlyList<PawnScribePawnReferenceReplacementDto> pawnReferenceReplacements)
    {
        Xml = xml;
        XmlSha256 = xmlSha256;
        PawnReferenceReplacements = pawnReferenceReplacements;
    }

    public string Xml { get; }

    public string? XmlSha256 { get; }

    public IReadOnlyList<PawnScribePawnReferenceReplacementDto> PawnReferenceReplacements { get; }
}

public sealed class PawnScribePawnReferenceReplacementDto
{
    public PawnScribePawnReferenceReplacementDto(string sourceLoadId, string placeholderLoadId, CrossMapPawnReferenceDto reference)
    {
        SourceLoadId = sourceLoadId;
        PlaceholderLoadId = placeholderLoadId;
        Reference = reference;
    }

    public string SourceLoadId { get; }

    public string PlaceholderLoadId { get; }

    public CrossMapPawnReferenceDto Reference { get; }
}

public sealed class EventCreationResponse
{
    public EventCreationResponse(
        ProtocolResponse result,
        string? eventId,
        ProtocolDeliverySemantics deliverySemantics,
        string? appliedSnapshotId = null,
        string? nextLineageToken = null,
        DateTimeOffset? raidStartedAtUtc = null,
        DateTimeOffset? raidDeadlineUtc = null,
        DateTimeOffset? raidFinalDeadlineUtc = null)
    {
        Result = result;
        EventId = eventId;
        DeliverySemantics = deliverySemantics;
        AppliedSnapshotId = appliedSnapshotId;
        NextLineageToken = nextLineageToken;
        RaidStartedAtUtc = raidStartedAtUtc;
        RaidDeadlineUtc = raidDeadlineUtc;
        RaidFinalDeadlineUtc = raidFinalDeadlineUtc;
    }

    public ProtocolResponse Result { get; }

    public string? EventId { get; }

    public ProtocolDeliverySemantics DeliverySemantics { get; }

    public string? AppliedSnapshotId { get; }

    public string? NextLineageToken { get; }

    public DateTimeOffset? RaidStartedAtUtc { get; }

    public DateTimeOffset? RaidDeadlineUtc { get; }

    public DateTimeOffset? RaidFinalDeadlineUtc { get; }
}
