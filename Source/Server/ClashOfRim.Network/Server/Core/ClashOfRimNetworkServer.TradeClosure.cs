using AIRsLight.ClashOfRim.Events;

namespace AIRsLight.ClashOfRim.Network;

public static partial class ClashOfRimNetworkServer
{
    private static LedgerBatch BuildTradeClosureBatch(AuthoritativeEvent order,
        IReadOnlyList<AuthoritativeEvent> memos, ServerEventStatus orderStatus, ServerEventStatus memoStatus,
        IReadOnlyList<AuthoritativeEvent> events)
        => new(events, memos.Select(memo => new LedgerStatusChange(memo.EventId, memo.Status, memoStatus))
            .Prepend(new LedgerStatusChange(order.EventId, order.Status, orderStatus)).ToArray());
}
