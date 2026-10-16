using System.Text.Json;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Protocol;

internal static class EventPayloadWireTests
{
    public static void Run()
    {
        foreach (string message in new[] { new string('测', 800), new string('a', 5000), string.Concat(Enumerable.Repeat("中文😀\"\\\n", 800)) })
            Verify(ServerEventType.ServerNotification, new ServerNotificationEventPayload("notice", "Title", message,
                ServerNotificationSeverity.Info, false), 4096, root => root.GetProperty("Message").GetString() == message);
        var items = Enumerable.Range(0, 6000).Select(i => new EventThingReference("wood-" + i, "WoodLog", 75)).ToArray();
        Verify(ServerEventType.Trade, new TradeEventPayload("trade", TradeStage.MarketOrder, items.Take(10).ToArray(), [], 0),
            4096, root => root.GetProperty("OfferedItems").GetArrayLength() == 10);
        Verify(ServerEventType.ItemDelivery, new ItemDeliveryEventPayload(items, "tail"), 3 * 1024 * 1024,
            root => root.GetProperty("Items").GetArrayLength() == items.Length && root.GetProperty("Message").GetString() == "tail");
        string reason = new('退', 400000);
        Verify(ServerEventType.SupportPawn, new SupportPawnEventPayload("pawn", "snapshot", "Name", false, null,
                ReturnToSender: true, RejectionReason: reason, ReturnReason: "Rejected"), 2 * 1024 * 1024,
            root => root.GetProperty("rejectionReason").GetString() == reason && root.GetProperty("returnReason").GetString() == "Rejected");
        var losses = Enumerable.Range(0, 8000).Select(i => new RaidSettlementLossRecord("item-" + i, "WoodLog", "(1,0,1)",
            "Map_7", true, 75, null, 75, 30, 0, 0, 30, 30)).ToArray();
        Verify(ServerEventType.Raid, new RaidEventPayload("before", "after", null, DateTimeOffset.UnixEpoch,
                new RaidSettlementRecord("before", "after", 0.4, [], losses, 0)), 2 * 1024 * 1024,
            root => root.GetProperty("Settlement").GetProperty("Losses").GetArrayLength() == losses.Length);
        Console.WriteLine("通过：事件载荷完整往返（中文/转义通知、交易、3MB 礼物、2MB 支援与结算）");
    }

    private static void Verify(ServerEventType type, LedgerEventPayload payload, int oldLimit, Func<JsonElement, bool> check)
    {
        var evt = AuthoritativeEventFactory.Create(type, new("sender", "home"), new("receiver", "away"), "wire", false, payload, DateTimeOffset.UnixEpoch);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        // Exercise both the payload and the containing HTTP response serialization.
        var wire = JsonSerializer.Deserialize<EventDetailDto>(JsonSerializer.Serialize(ProtocolDtoMapper.ToDetailDto(evt), options), options)!;
        using var document = JsonDocument.Parse(wire.PayloadSummary);
        if (wire.PayloadSummary.Length <= oldLimit || !check(document.RootElement))
            throw new Exception("Event payload lost fields or did not exercise the old limit: " + type);
    }
}
