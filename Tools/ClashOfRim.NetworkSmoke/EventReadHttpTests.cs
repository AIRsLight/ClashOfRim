using System.Net.Http.Json;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

internal static class EventReadHttpTests
{
    public static async Task RunAsync(Func<string, string, string, SaveSnapshotPackage> fixture)
    {
        EventPayloadWireTests.Run();
        EventDeliveryBatchTests.Run();
        var state = new ClashOfRimNetworkState();
        void Snapshot(string user, string colony, string id)
        {
            var package = fixture(user, colony, id);
            state.SnapshotStore.StoreLatest(new LatestSnapshotRecord(package.Envelope.Identity, package.Envelope, package.Index, DateTimeOffset.UtcNow));
        }
        Snapshot("reader", "home", "current");
        Snapshot("other", "away", "other-current");
        string token = Token(state, "reader", "home"), other = Token(state, "other", "away");
        var notice = AuthoritativeEventFactory.Create(ServerEventType.ServerNotification, new("server"), new("reader", "home"),
            "notice", false, new ServerNotificationEventPayload("notice", "Title", "Text", ServerNotificationSeverity.Info, false), DateTimeOffset.UtcNow);
        var gift = AuthoritativeEventFactory.Create(ServerEventType.ItemDelivery, new("other", "away"), new("reader", "home"),
            "gift", false, new ItemDeliveryEventPayload([new("wood", "WoodLog", 1)], null), DateTimeOffset.UtcNow);
        state.Ledger.Append(notice); state.Ledger.Append(gift);
        state.Ledger.Append(notice with { EventId = "old-colony", IdempotencyKey = "old-colony", Target = new("reader", "retired") });
        await using var app = ClashOfRimNetworkServer.Build([], state);
        app.Urls.Add("http://127.0.0.1:0");
        await app.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
            var client = new ClashOfRimNetworkClient(http);
            foreach (var invalid in new[]
            {
                (Token: (string?)null, Colony: "home", Snapshot: "current", Code: ProtocolErrorCode.Unauthorized),
                (Token: "invalid", Colony: "home", Snapshot: "current", Code: ProtocolErrorCode.Unauthorized),
                (Token: other, Colony: "home", Snapshot: "current", Code: ProtocolErrorCode.Unauthorized),
                (Token: token, Colony: "retired", Snapshot: "current", Code: ProtocolErrorCode.Unauthorized),
                (Token: token, Colony: "home", Snapshot: "stale", Code: ProtocolErrorCode.SnapshotMismatch),
                (Token: token, Colony: "home", Snapshot: "", Code: ProtocolErrorCode.SnapshotMismatch)
            })
            {
                var queue = await client.PullPendingEventsAsync(new("reader", invalid.Colony, invalid.Snapshot, invalid.Token));
                var details = await client.PullEventDetailsAsync(new("reader", invalid.Colony, invalid.Snapshot, [notice.EventId, gift.EventId], invalid.Token));
                var wait = await client.WaitForEventsAsync(new("reader", invalid.Colony, invalid.Snapshot, 0, 0, invalid.Token));
                Require(queue.Result.ErrorCode == invalid.Code && details.Result.ErrorCode == invalid.Code && wait.Result.ErrorCode == invalid.Code,
                    "event reads did not reject invalid identity/baseline");
                Require(details.Events.Count == 0 && Empty(queue.EventQueue) && Empty(wait.EventQueue), "denied read leaked event data");
                Require(state.Ledger.Find(notice.EventId) == notice && state.Ledger.Find(gift.EventId) == gift, "denied read mutated ledger");
            }
            var validQueue = await client.PullPendingEventsAsync(new("reader", "home", "current", token));
            Require(validQueue.Result.Accepted && validQueue.EventQueue.DirectlyProcessable.Count == 1, "queue leaked old colony events");
            var valid = await client.PullEventDetailsAsync(new("reader", "home", "current", [notice.EventId, gift.EventId], token));
            Require(valid.Result.Accepted && valid.Events.Count == 2 && state.Ledger.Find(gift.EventId)!.DeliveredToSnapshotId == "current", "valid delivery failed");
            string longMessage = new('测', 800);
            var longNotice = notice with { EventId = "long-notice", IdempotencyKey = "long-notice",
                Payload = new ServerNotificationEventPayload("long-notice", "Title", longMessage, ServerNotificationSeverity.Info, false) };
            state.Ledger.Append(longNotice);
            var longResponse = await client.PullEventDetailsAsync(new("reader", "home", "current", [longNotice.EventId], token));
            using var longJson = System.Text.Json.JsonDocument.Parse(longResponse.Events.Single().PayloadSummary);
            Require(longJson.RootElement.GetProperty("Message").GetString() == longMessage, "HTTP notification lost text");

            // The sender has moved since sending; the recipient's map must never be reused for the return.
            var sender = state.SnapshotStore.GetLatest("other", "away")!;
            var senderMap = sender.Index.Maps.First() with { UniqueId = "Map_7", ParentWorldObjectId = "sender-site" };
            state.SnapshotStore.StoreLatest(sender with { Index = sender.Index with { Maps = [senderMap] } });
            var routedGift = gift with { EventId = "routed-gift", IdempotencyKey = "routed-gift",
                TargetContext = new("receiver-site", "Map_9", 99, EventLandingMode.MapEdge) };
            state.Ledger.Append(routedGift);
            var rejection = await client.RejectGiftAsync(new(routedGift.EventId, "reader", "home", "current", "declined", token));
            var returned = state.Ledger.Find(rejection.ReturnEventId!)!;
            Require(rejection.Result.Accepted && returned.Target.UserId == "other" && returned.TargetContext?.MapUniqueId == "Map_7"
                && returned.TargetContext.WorldObjectId == "sender-site" && returned.TargetContext.Tile != 99, "HTTP return used receiver routing");
            var returnDetails = await client.PullEventDetailsAsync(new("other", "away", "other-current", [returned.EventId], other));
            Require(returnDetails.Result.Accepted && returnDetails.Events.Single().TargetContext?.MapUniqueId == "Map_7", "return details lost sender routing");

            var batchNotice = notice with { EventId = "batch-notice", IdempotencyKey = "batch-notice" };
            var batchGift = gift with { EventId = "batch-gift", IdempotencyKey = "batch-gift" };
            var conflict = gift with { EventId = "conflict", IdempotencyKey = "conflict", Status = ServerEventStatus.Conflict };
            state.Ledger.Append(batchNotice); state.Ledger.Append(batchGift); state.Ledger.Append(conflict);
            string beforeBatch = System.Text.Json.JsonSerializer.Serialize(state.Ledger.ListAll());
            foreach (string invalidId in new[] { "missing", "old-colony" })
            {
                var rejectedBatch = await client.PullEventDetailsAsync(new("reader", "home", "current",
                    [batchNotice.EventId, conflict.EventId, batchGift.EventId, invalidId], token));
                Require(!rejectedBatch.Result.Accepted && rejectedBatch.Events.Count == 0
                    && System.Text.Json.JsonSerializer.Serialize(state.Ledger.ListAll()) == beforeBatch,
                    "invalid batch consumed notifications, cancelled terminal events or marked delivery");
            }
            var completedBatch = await client.PullEventDetailsAsync(new("reader", "home", "current",
                [batchNotice.EventId, conflict.EventId, batchGift.EventId, batchNotice.EventId], token));
            Require(completedBatch.Result.Accepted && completedBatch.Events.Count == 3
                && completedBatch.Events.Single(e => e.EventId == conflict.EventId).Status == nameof(ServerEventStatus.Conflict)
                && state.Ledger.Find(conflict.EventId)!.Status == ServerEventStatus.Cancelled, "valid batch lost terminal details or duplicated records");

            foreach (bool replaceSession in new[] { true, false })
            {
                var wait = client.WaitForEventsAsync(new("reader", "home", "current", state.EventNotifications.GetVersion("reader"), 10, token));
                await Task.Delay(100);
                if (replaceSession)
                {
                    state.LoginSessions.EndUser("reader");
                    string old = token; token = Token(state, "reader", "home");
                    Require(!(await client.PullPendingEventsAsync(new("reader", "home", "current", old))).Result.Accepted, "old session retained access");
                }
                else Snapshot("reader", "home", "new-current");
                state.EventNotifications.SignalUser("reader");
                var completed = await wait.WaitAsync(TimeSpan.FromSeconds(5));
                Require(completed.Result.ErrorCode == (replaceSession ? ProtocolErrorCode.Unauthorized : ProtocolErrorCode.SnapshotMismatch)
                    && Empty(completed.EventQueue), "long poll did not revalidate after waiting");
            }
        }
        finally { await app.StopAsync(); }
        Console.WriteLine("通过：事件读取 HTTP 身份、快照、殖民地隔离及长轮询会话替换");
    }

    private static bool Empty(EventQueueSummaryDto queue) => queue.DirectlyProcessable.Count + queue.WaitingForUserChoice.Count
        + queue.DeliveredUnconfirmed.Count + queue.Conflicts.Count + queue.Rejected.Count == 0;
    private static string Token(ClashOfRimNetworkState state, string user, string colony)
    {
        state.LoginSessions.TryCreate(user, colony, DateTimeOffset.UtcNow, out string session);
        return state.AuthTokens.Issue(user, user, colony, session, DateTimeOffset.UtcNow);
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
}
