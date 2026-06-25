namespace AIRsLight.ClashOfRim.Events;

public sealed record TradeEventPayload(
    string TradeId,
    TradeStage Stage,
    IReadOnlyList<EventThingReference> OfferedItems,
    IReadOnlyList<EventThingReference> RequestedItems,
    int FeeSilver,
    string? AcceptedByUserId = null,
    string? AcceptedMemoEventId = null,
    TradeFulfillmentMode FulfillmentMode = TradeFulfillmentMode.Unspecified,
    bool PostagePaidByAcceptor = false) : LedgerEventPayload;
