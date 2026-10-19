using System.Text.Json;
using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Protocol;
using ClashOfRim.GameTests;
using RimTest.Protocol;
using RimTest.Testing;

internal static class GameOfflineScenarioTests
{
    internal static void Run() => RunAsync().GetAwaiter().GetResult();
    private static async Task RunAsync()
    {
        NotificationMessages();
        await Logout();
        foreach (string fault in new[] { "none", "not-queued", "read-stock", "read-ack", "reconnect-stock", "decision-rejected", "replay-stock" }) await DeferredAccept(fault);
        foreach (string fault in new[] { "none", "online-return", "recipient-stock", "caravan-return", "reload-return" }) await RejectReturn(fault);
        foreach (string fault in new[] { "none", "durable-not-queued", "transient-queued", "duplicate-notice", "reopened-notice", "transient-client" }) await Notifications(fault);
    }
    private static void NotificationMessages()
    {
        const string message = "通知 中文 é İ \\";
        foreach (var encoder in new[] { System.Text.Encodings.Web.JavaScriptEncoder.Default, System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping })
        {
            var value = Wire.Element(new { data = new { eventType = "serverNotification", payloadSummary = JsonSerializer.Serialize(new { Message = message }, new JsonSerializerOptions { Encoder = encoder }) } });
            Check(NotificationScenario.HasMessage(value, message) && !NotificationScenario.HasMessage(value, "通知"), "Notification comparison depends on JSON encoding or accepts a substring.");
        }
        Check(!NotificationScenario.HasMessage(Wire.Element(new { data = new { eventType = "itemDelivery", payloadSummary = "unrelated" } }), message),
            "Notification parser inspected another event type.");
    }
    private static async Task Logout()
    {
        using var f = await Fixture.Create(); int presenceChecks = 0;
        var entry = f.Runtime.CreateEntry(isUserOnline: user =>
        {
            Check(user == "rimtest-A" && !f.Active["A"], "Presence was queried for the wrong user or before leaving the Game.");
            return ++presenceChecks < 3;
        });
        await entry.LogoutWithSnapshot("A");
        var actions = f.Runtime.Calls.Where(c => c.Method is "clash.invoke" or "game.returnToMenu").ToArray();
        Check(presenceChecks == 4 && actions.Length == 2 && actions[0].Args.String("action") == "StartManualSnapshotUpload"
            && actions[1].Method == "game.returnToMenu" && actions.All(c => c.Client == "A"), "Logout skipped server presence or changed upload/exit order.");
        using var failed = await Fixture.Create();
        failed.OnUpload = _ => throw new IOException("scripted upload failure");
        await Reject<IOException>(() => failed.Entry.LogoutWithSnapshot("B"));
        Check(failed.Active["B"] && failed.Runtime.Count("game.returnToMenu") == 0, "Failed upload still discarded the loaded Game.");
    }
    private static async Task DeferredAccept(string fault)
    {
        using var f = await Fixture.Create(); string id = ""; int decisions = 0;
        f.OnGift = (args, count) =>
        {
            Check(!f.Active["B"] && count == 3, "Deferred gift was sent online or with the wrong amount.");
            id = f.Add(ServerEventType.ItemDelivery, "rimtest-B", "deferred", new ItemDeliveryEventPayload([], args.String("message")), fault == "not-queued").EventId;
            f.Cargo = 7;
        };
        f.OnRefresh = client =>
        {
            if (client != "B" || decisions != 0) return;
            if (fault == "read-stock") f.Wood["B"] = 23;
            if (fault == "read-ack") f.Apply(id);
        };
        f.OnLogin = (client, count) => { if (client == "B" && count == 2 && fault == "reconnect-stock") f.Wood["B"] = 23; };
        f.OnDecision = (client, args) =>
        {
            Check(client == "B" && args.String("eventId") == id && args.String("actionKind") == "Accept", "Deferred gift decided the wrong event.");
            if (fault == "decision-rejected") return false;
            decisions++; f.Wood["B"] = fault == "replay-stock" && decisions == 2 ? 26 : 23; f.Apply(id); return true;
        };
        if (fault != "none") { await Reject<AssertionException>(f.Gift.OfflineAccept); return; }
        await f.Gift.OfflineAccept();
        Check(f.Logins["B"] == 3 && decisions == 2 && f.Wood["B"] == 23 && f.Cargo == 7
            && f.Runtime.Count("game.returnToMenu") == 3, "Deferred gift lost reconnect/decision replay coverage.");
        Check(f.Runtime.Calls.Where(c => c.Method == "time.step" && c.Args.Int("ticks") == 300).Count() == 2,
            "Deferred gift omitted one undecided observation period.");
    }
    private static async Task RejectReturn(string fault)
    {
        using var f = await Fixture.Create(); string id = "", returned = ""; int decisions = 0;
        f.OnGift = (args, count) =>
        {
            Check(f.Active["B"] && count == 2, "Rejected gift changed its original send conditions.");
            id = f.Add(ServerEventType.ItemDelivery, "rimtest-B", "rejected", new ItemDeliveryEventPayload([], args.String("message")), true).EventId;
            f.Cargo = 8;
        };
        f.OnDecision = (client, args) =>
        {
            Check(client == "B" && !f.Active["A"] && args.String("eventId") == id && args.String("actionKind") == "Reject", "Return did not exercise offline sender rejection.");
            returned = f.Ledger.RejectGiftAndCreateReturn(id, DateTimeOffset.UnixEpoch, null, fault == "online-return").ReturnEvent.EventId;
            if (++decisions == 2 && fault == "recipient-stock") f.Wood["B"] = 22;
            return true;
        };
        f.OnLogin = (client, count) =>
        {
            if (client != "A") return;
            f.Wood["A"] = fault == "reload-return" && count == 2 ? 24 : 22; f.Apply(returned);
            if (fault == "caravan-return") f.Cargo = 10;
        };
        if (fault != "none") { await Reject<AssertionException>(f.Gift.OfflineReject); return; }
        await f.Gift.OfflineReject();
        Check(decisions == 2 && f.Logins["A"] == 2 && f.Wood["A"] == 22 && f.Wood["B"] == 20 && f.Cargo == 8,
            "Return did not conserve both colonies and departed cargo through replay.");
        Check(f.Caravan.Handle == "caravan-2" && f.Runtime.Calls.Where(c => c.Method == "caravan.state").Last().Args.String("caravan") == "caravan-2",
            "Return inspected a pre-login caravan handle.");
    }
    private static async Task Notifications(string fault)
    {
        using var f = await Fixture.Create(); string id = "", transient = ""; int broadcasts = 0;
        f.OnAdmin = args =>
        {
            Check(!f.Active["B"] && args.String("actionKind") == "Broadcast" && args.String("targetUserId") == "rimtest-B", "Notification did not target the offline player.");
            broadcasts++; bool persistent = args.Bool("persistentNotification"); string message = args.String("message");
            if (!persistent) transient = message;
            if (persistent || fault == "transient-queued")
            {
                var notice = f.Add(ServerEventType.ServerNotification, "rimtest-B", persistent ? "durable" : "transient",
                    new ServerNotificationEventPayload("notice", "title", message, (ServerNotificationSeverity)0, true), fault == "durable-not-queued");
                if (persistent) id = notice.EventId;
            }
        };
        f.OnLogin = (client, count) =>
        {
            Check(client == "B", "Notification reconnected the administrator.");
            f.Apply(id);
            if (count != 2) return;
            if (fault == "duplicate-notice") f.Add(ServerEventType.ServerNotification, "rimtest-B", "duplicate", f.Ledger.Find(id)!.Payload, true);
            if (fault == "reopened-notice") f.Ledger.ChangeStatus(id, ServerEventStatus.PendingOfflineDelivery);
            if (fault == "transient-client") f.ExtraClientNotice = transient;
        };
        if (fault != "none") { await Reject<AssertionException>(f.Notifications.OfflineDelivery, fault); return; }
        await f.Notifications.OfflineDelivery();
        Check(broadcasts == 2 && f.Logins["B"] == 2 && f.Ledger.ListAll().Count == 1 && f.Ledger.Find(id)!.Status == ServerEventStatus.AppliedToSnapshot,
            "Notification scenario lost durable/online-only/reconnect checks.");
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly GameScenarioTestRuntime Runtime;
        internal readonly InMemoryAuthoritativeEventLedger Ledger = new();
        internal readonly Dictionary<string, bool> Active = new() { ["A"] = true, ["B"] = true };
        internal readonly Dictionary<string, int> Wood = new() { ["A"] = 20, ["B"] = 20 }, Logins = new() { ["A"] = 0, ["B"] = 0 };
        internal readonly CaravanScenario Caravan;
        internal readonly ServerEntryScenario Entry;
        internal readonly GiftScenario Gift;
        internal readonly NotificationScenario Notifications;
        internal int Cargo = 10;
        internal string? ExtraClientNotice;
        internal Action<JsonElement, int>? OnGift;
        internal Action<JsonElement>? OnAdmin;
        internal Action<string>? OnUpload, OnRefresh;
        internal Action<string, int>? OnLogin;
        internal Func<string, JsonElement, bool>? OnDecision;
        private int selected;
        private Fixture(GameScenarioTestRuntime runtime)
        {
            Runtime = runtime;
            var colony = new ColonyScenarioFixture(runtime.Run, runtime.Polling); var events = new EventScenarioFixture(runtime.Polling, Ledger);
            Entry = runtime.CreateEntry(isUserOnline: user => Active[user == "rimtest-A" ? "A" : "B"]);
            Caravan = new(runtime.Run, runtime.Polling, runtime.Dialogs, colony);
            Caravan.Use(Wire.Element(new { reference = new { handle = "caravan-0" } }));
            Gift = new(runtime.Run, runtime.Polling, runtime.Dialogs, colony, Caravan, events, Ledger, Entry);
            Notifications = new(runtime.Run, runtime.Polling, Entry, events, Ledger);
            runtime.Script = Reply;
        }
        internal static async Task<Fixture> Create() => new(await GameScenarioTestRuntime.Create());
        private object? Reply(string client, string method, JsonElement args)
        {
            switch (method)
            {
                case "clash.state": return new { isInActiveMultiplayerSession = Active[client] };
                case "game.state": return new { epoch = Logins[client].ToString(), playerHasControl = Active[client], hasGame = Active[client], playing = Active[client], startupReady = !Active[client], mapCount = Active[client] ? 1 : 0 };
                case "game.returnToMenu": Active[client] = false; return new { };
                case "clash.inventory": return new { maps = new[] { new { items = new Dictionary<string, int> { ["WoodLog"] = Wood[client] }, colonists = new[] { new { reference = new { handle = "pawn-" + client } } } } } };
                case "caravans.list": return new { items = new[] { new { reference = new { handle = "caravan-" + Logins["A"] }, pawns = new[] { new { handle = "pawn-A" } } } } };
                case "clash.gift.select": selected = args.Int("count"); return new { };
                case "clash.gift.submit": OnGift!(args, selected); return new { };
                case "clash.events":
                    var events = Ledger.ListAll().Where(e => e.Target.UserId == "rimtest-" + client).Select(e => Event(e.EventId, e.Payload is ServerNotificationEventPayload p ? p.Message : "gift")).ToList();
                    if (ExtraClientNotice is { } text) events.Add(Event("client-only", text));
                    return new { items = events };
                case "clash.invoke":
                    var input = args.GetProperty("arguments");
                    switch (args.String("action"))
                    {
                        case "StartManualSnapshotUpload": OnUpload?.Invoke(client); break;
                        case "StartMainMenuServerEntryFlow": Active[client] = true; OnLogin?.Invoke(client, ++Logins[client]); break;
                        case "StartRefreshEventsForTest": OnRefresh?.Invoke(client); break;
                        case "HandleEventLetterAction": return new { returnValue = OnDecision!(client, input) };
                        case "StartAdminAction": Check(client == "A", "Notification used non-admin client."); OnAdmin!(input); break;
                        case "StartSyncWorldMapMarkers": break;
                        default: throw new InvalidOperationException("Unexpected client action: " + args.String("action"));
                    }
                    return new { returnValue = true };
                default: return GameGiftTestUi.Reply(method, "赠礼", Cargo);
            }
        }
        private static object Event(string id, string message) => new { data = new { eventId = id, eventType = "serverNotification", payloadSummary = JsonSerializer.Serialize(new { Message = message }) } };
        internal AuthoritativeEvent Add(ServerEventType type, string target, string key, LedgerEventPayload payload, bool online)
        {
            var e = AuthoritativeEventFactory.Create(type, new("rimtest-A"), new(target), key, online, payload, DateTimeOffset.UnixEpoch);
            Ledger.Append(e); return e;
        }
        internal void Apply(string id) { Ledger.MarkDelivered(id, "delivered", DateTimeOffset.UnixEpoch); Ledger.MarkApplied(id, "applied", DateTimeOffset.UnixEpoch); }
        public void Dispose() => Runtime.Dispose();
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static async Task Reject<T>(Func<Task> action, string? context = null) where T : Exception
    {
        try { await action(); } catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name + (context is null ? "" : ": " + context));
    }
}
