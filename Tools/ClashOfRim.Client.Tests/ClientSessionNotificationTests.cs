using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Events;
using AIRsLight.ClashOfRim.Modules.Presence;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Protocol;

internal static class ClientSessionNotificationTests
{
    private static readonly string[] Names = { "presence", "ledgerChanged", "chatChanged", "worldConfigurationChanged", "playerOnline", "error" };
    internal static void Run() => RunAsync().GetAwaiter().GetResult();
    private static async Task RunAsync()
    {
        var culture = CultureInfo.CurrentCulture; int count = 0;
        try
        {
            foreach (string name in new[] { "en-US", "zh-CN", "fr-FR", "tr-TR" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
                foreach (string kind in Names)
                {
                    await Dispatch(kind); count++;
                    foreach (string change in new[] { "user", "auth", "colony", "server", "login", "work", "stop", "expired" }) { await BeforeDelivery(kind, change); count++; }
                    foreach (string phase in new[] { "text", "status" })
                    foreach (string change in new[] { "user", "login", "reset", "stop", "expired" })
                    { if (kind == "playerOnline" && phase == "status") continue; await Reentry(kind, phase, change); count++; }
                }
                foreach (string bad in new[] { "{", "[]", "null", "{}", "{\"UserId\":\"alice\",\"WorldConfigurationVersion\":1.25}",
                    "{\"UserId\":\"alice\",\"WorldConfigurationVersion\":-1}", "{\"UserId\":\"alice\",\"WorldConfigurationVersion\":9223372036854775808}",
                    "{\"UserId\":\"alice\",\"WorldConfigurationVersion\":\"12\"}", "{\"UserId\":\"alice\",\"Nested\":{\"WorldConfigurationVersion\":23}}" })
                { await Invalid(bad); count++; }
                foreach (string change in new[] { "user", "auth", "colony", "server", "login", "work", "stop", "expired", "reset", "replace" }) { await Queued(change); count++; }
                foreach (string phase in new[] { "text", "status", "chat", "ledger", "world", "queue", "message", "expired" }) { await CallbackFailure(phase); count++; }
                await Versions(); await JsonSemantics(); await WrongUser(); await Unknown(); await NaturalCompletion(); await SelfOnline(); await Discard(); count += 7;
                foreach (string ending in new[] { "stop", "replace" }) { await SubscriberReentry(ending); count++; }
            }
            Console.WriteLine($"PASS: session notifications ({count} scenarios)");
        }
        finally { CultureInfo.CurrentCulture = culture; ClientSessionWork.Reset(); }
    }
    private static async Task Dispatch(string kind)
    {
        await using var f = new Fixture { RepeatQueue = true }; var source = await f.Open(); source.Emit(kind, Payload(kind)); await f.Drain();
        string? expected = kind switch { "presence" => "chat-initial", "ledgerChanged" => "ledger", "chatChanged" => "chat", "worldConfigurationChanged" => "world", "error" => "expired", _ => null };
        Check(f.Effects.SequenceEqual(expected is null ? Array.Empty<string>() : new[] { expected }), "Wrong notification effects: " + kind);
        Check(f.Messages.Count == (kind == "playerOnline" ? 1 : 0) && f.Diagnostics == 0, "Wrong online message count/diagnostics.");
        Check(f.Events.NotificationVersion == (kind is "presence" or "ledgerChanged" or "error" ? 12 : 0)
            && f.Module.WorldVersion == (kind == "worldConfigurationChanged" ? 12 : 0), "Event wrote the wrong version cursor.");
        if (kind == "error") Check(f.Session.IsUnavailable && f.ExpiredContext?.AuthToken == "auth" && f.ExpiredId == "session", "Localized error lost original credentials or depended on English/Chinese text.");
        if (kind == "ledgerChanged") Check(f.Events.QueueStatus.Contains("StatusLedgerChangedSyncing"), "Ledger status did not reach event state.");
    }
    private static async Task BeforeDelivery(string kind, string change)
    {
        await using var f = new Fixture(); var source = await f.Open(); f.Change(change); source.Emit(kind, Payload(kind)); await f.Drain();
        Check(f.Effects.Count == 0 && f.Messages.Count == 0 && f.Statuses.Count == 0 && f.Module.WorldVersion == 0, "Obsolete source dispatched: " + kind + "/" + change);
    }
    private static async Task Reentry(string kind, string phase, string change)
    {
        await using var f = new Fixture(); var source = await f.Open(); bool changed = false; int statuses = 0;
        f.Hook = point => { if (point == phase && !changed) { changed = true; statuses = f.Statuses.Count; f.Change(change); } };
        source.Emit(kind, Payload(kind)); await f.Drain();
        Check(changed && f.Effects.Count == 0 && f.Messages.Count == 0 && f.Statuses.Count == statuses, "Reentrant notification continued: " + kind + "/" + phase + "/" + change);
    }
    private static async Task Invalid(string data)
    {
        await using var f = new Fixture(); var source = await f.Open(); source.Emit("worldConfigurationChanged", data); await f.Drain();
        Check(f.Effects.Count == 0 && f.Module.WorldVersion == 0 && f.Events.NotificationVersion == 0 && f.Diagnostics == 1, "Malformed JSON triggered an effect or consumed version.");
        source.Emit("worldConfigurationChanged", Payload("worldConfigurationChanged"));
        Check(f.Effects.SequenceEqual(new[] { "world" }), "Malformed frame prevented subsequent valid frames.");
    }
    private static async Task Queued(string change)
    {
        await using var f = new Fixture { RepeatQueue = true }; var source = await f.Open();
        source.Emit("playerOnline", Payload("playerOnline"));
        if (change == "replace") await f.Open(); else f.Change(change);
        await f.Drain(); Check(f.Messages.Count == 0, "Queued notification crossed " + change);
    }
    private static async Task CallbackFailure(string phase)
    {
        await using var f = new Fixture(); var source = await f.Open(); bool threw = false;
        f.Hook = point => { if (point == phase && !threw) { threw = true; throw new InvalidOperationException("expected"); } };
        string kind = phase switch { "ledger" => "ledgerChanged", "world" => "worldConfigurationChanged", "expired" => "error", "queue" or "message" => "playerOnline", _ => "chatChanged" };
        source.Emit(kind, Payload(kind)); await f.Drain();
        Check(threw && f.Diagnostics == 1 && f.Statuses.Last().Contains("StatusException"), "Notification callback escaped handling: " + phase);
    }
    private static async Task Versions()
    {
        await using var f = new Fixture(); var source = await f.Open();
        source.Emit("ledgerChanged", Payload("ledgerChanged", version: 50)); source.Emit("ledgerChanged", Payload("ledgerChanged", version: 2));
        source.Emit("worldConfigurationChanged", Payload("worldConfigurationChanged", version: 70)); source.Emit("worldConfigurationChanged", Payload("worldConfigurationChanged", version: 3));
        f.Ports.Snapshot = "next"; f.Ports.Password = "new";
        var reopened = await f.Open(); Check(reopened.NotificationVersion == 50 && reopened.WorldVersion == 70 && reopened.Context.CurrentSnapshotId == "next", "Restart lost cursor monotonicity or borrowed cached context.");
        f.Ports.GameOwner = new(); Check(f.Module.WorldVersion == 70, "World notification cursor bound to Game.");
        f.Change("login"); var nextIdentity = await f.Open();
        Check(nextIdentity.NotificationVersion == 0 && nextIdentity.WorldVersion == 0, "New identity inherited previous cursors.");
    }
    private static async Task JsonSemantics()
    {
        await using var f = new Fixture(); var source = await f.Open();
        source.Emit("worldConfigurationChanged", "{\n\"UserId\" : \"alice\", \"WorldConfigurationVersion\" : 9223372036854775807, \"Message\" : \"数字 1,25\"}");
        Check(f.Module.WorldVersion == long.MaxValue && f.Effects.SequenceEqual(new[] { "world" }), "Valid whitespace/integer/culture broke JSON decoding.");
        source.Emit("chatChanged", "{\"UserId\":\"alice\",\"ChatVersion\":1,\"NotificationVersion\":999,\"WorldConfigurationVersion\":999,\"Message\":\"\\\"NotificationVersion\\\":777\"}");
        Check(f.Events.NotificationVersion == 0 && f.Module.WorldVersion == long.MaxValue, "Unrelated/embedded version field changed a cursor.");
        source.Emit("error", "{\"UserId\":\"alice\",\"Online\":true,\"NotificationVersion\":1,\"Message\":\"session has expired 会话已过期\"}");
        Check(!f.Session.IsUnavailable && !f.Effects.Contains("expired"), "Free text overrode structured online state.");
    }
    private static async Task WrongUser()
    {
        await using var f = new Fixture(); var source = await f.Open();
        foreach (string kind in Names) source.Emit(kind, Payload(kind, user: "other"));
        await f.Drain(); Check(f.Effects.Count == 0 && f.Statuses.Count == 0 && f.Messages.Count == 0 && f.Module.WorldVersion == 0, "Payload for another recipient affected this identity.");
    }
    private static async Task Unknown()
    {
        await using var f = new Fixture(); var source = await f.Open();
        source.Emit("future", "not-json"); source.Emit("future", Payload("worldConfigurationChanged"));
        Check(f.Module.WorldVersion == 0 && f.Diagnostics == 0 && f.Effects.Count == 0, "Unknown event changed state or required known payload schema.");
    }
    private static async Task NaturalCompletion()
    {
        await using var f = new Fixture(); var source = await f.Open(); source.Emit("playerOnline", Payload("playerOnline"));
        source.Result.SetResult(ClashOfRimClientNetworkResult<ModSessionStreamClosedDto>.Ok(new(false)));
        await f.Works.Last().WaitAsync(TimeSpan.FromSeconds(5)); await f.Drain();
        Check(f.Messages.Count == 1, "Natural completion invalidated an already accepted message without replacement/exit.");
    }
    private static async Task SelfOnline()
    {
        await using var f = new Fixture(); var source = await f.Open(); source.Emit("playerOnline", Payload("playerOnline", player: "alice")); await f.Drain();
        Check(f.Messages.Count == 0, "Own login produced an online-player notification.");
    }
    private static async Task Discard()
    {
        await using var f = new Fixture { DropQueue = true, RepeatQueue = true }; var source = await f.Open();
        source.Emit("playerOnline", Payload("playerOnline")); await f.Drain();
        Check(f.Messages.Count == 0, "Discarded online message was replayed by a duplicate callback.");
    }
    private static async Task SubscriberReentry(string change)
    {
        ClientSessionWork.Reset(); var transport = new SessionConnectionTestTransport(); var connection = new ClientSessionConnection(transport);
        var context = new ClashOfRimClientNetworkContext("http://localhost", "alice", "colony", "snapshot", null, null, "auth");
        Task? replacement = null; int later = 0;
        connection.EventReceived += (_, _) => { if (change == "stop") connection.Stop(); else replacement = connection.Start(context, "session", 0, 0); };
        connection.EventReceived += (_, _) => later++;
        Task work = connection.Start(context, "session", 0, 0); var source = await transport.Next(); source.Emit();
        Check(later == 0, "A later subscriber received a frame after the first subscriber replaced/stopped its connection.");
        source.Result.SetResult(ClashOfRimClientNetworkResult<ModSessionStreamClosedDto>.Ok(new(true))); await work;
        if (replacement is not null) { var next = await transport.Next(); connection.Stop(); next.Result.SetResult(ClashOfRimClientNetworkResult<ModSessionStreamClosedDto>.Ok(new(true))); await replacement; }
    }
    private static string Payload(string kind, long version = 12, string user = "alice", string player = "玩家 İ") => kind switch
    {
        "chatChanged" => JsonSerializer.Serialize(new ChatStreamEventDto(user, version, "消息")),
        "worldConfigurationChanged" => JsonSerializer.Serialize(new WorldConfigurationStreamEventDto(user, version, "世界")),
        "playerOnline" => JsonSerializer.Serialize(new PlayerOnlineStreamEventDto(user, player, version, null)),
        _ => JsonSerializer.Serialize(new SessionStreamEventDto(user, kind != "error", version, "Votre session a expiré / oturum sona erdi / セッション終了"))
    };
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private sealed class Fixture : IAsyncDisposable, IClientSessionNotificationEffects, IClientSessionNotificationPresentation, IClientEventReadPresentation
    {
        internal readonly EventModuleTestPorts Ports = new();
        internal readonly SessionConnectionTestTransport Transport = new();
        internal readonly ClientSessionConnection Connection;
        internal readonly ClientSessionLifecycle Session;
        internal readonly ClientEventState Events;
        internal readonly ClientSessionNotifications Module;
        internal readonly List<Task> Works = new();
        private readonly List<SessionConnectionTestTransport.Pending> pending = new();
        internal readonly List<string> Effects = new(), Statuses = new(), Messages = new();
        internal Action<string>? Hook; internal bool RepeatQueue, DropQueue; internal int Diagnostics;
        internal ClashOfRimClientNetworkContext? ExpiredContext; internal string? ExpiredId;
        internal Fixture()
        {
            ClientSessionWork.Reset(); Connection = new(Transport); Session = new(Ports, Ports, Connection);
            Session.PublishLogin(new() { AuthToken = "auth", SessionId = "session" });
            Events = new(new ClientEventReader(Session, new(), new EventReadTestProtocol(), Ports), this); _ = Events.NotificationVersion;
            Module = new(Session, Connection, Events, this, this);
        }
        internal async Task<SessionConnectionTestTransport.Pending> Open()
        { Works.Add(Module.Start()); var source = await Transport.Next(); pending.Add(source); return source; }
        internal Task Drain() => Ports.PumpUntil(Task.CompletedTask);
        internal void Change(string kind)
        {
            switch (kind)
            {
                case "user": Ports.User = "other"; break; case "auth": Ports.Auth = "other"; break;
                case "server": Ports.Server = "http://other"; break; case "colony": Ports.Colony = "other"; break;
                case "login": Session.PublishLogin(new() { AuthToken = Ports.Auth, SessionId = "session" }); break;
                case "work": ClientSessionWork.Reset(); break; case "stop": Connection.Stop(); break;
                case "reset": Module.Reset(); break; case "expired": Session.BeginUnavailable(Ports.Auth, null); break;
            }
        }
        public void RefreshChat(bool initial) { Effects.Add(initial ? "chat-initial" : "chat"); Hook?.Invoke("chat"); }
        public void RefreshLedger(string reason) { Effects.Add("ledger"); Hook?.Invoke("ledger"); }
        public void RefreshWorld(string reason) { Effects.Add("world"); Hook?.Invoke("world"); }
        public void Expired(ClashOfRimClientNetworkContext context, string id)
        { ExpiredContext = context; ExpiredId = id; Effects.Add("expired"); Hook?.Invoke("expired"); Session.BeginUnavailable(context.AuthToken, id); }
        public string Text(string key, params (string Name, object? Value)[] args) { Hook?.Invoke("text"); return key + " " + string.Join(';', args.Select(a => a.Value)); }
        public void Status(string text) { Statuses.Add(text); Hook?.Invoke("status"); }
        public void Enqueue(Action action, Action discard) { Hook?.Invoke("queue"); Ports.Enqueue(() => { if (DropQueue) discard(); else action(); if (RepeatQueue) action(); }); }
        public void Message(string text) { Messages.Add(text); Hook?.Invoke("message"); }
        public void Diagnose(string eventName, Exception error) => Diagnostics++;
        public string MergedReason => "merged";
        public string ManualDetailsReason => "manual";
        public string Format(ClientEventRefreshNotice notice) => notice.Stage.ToString();
        public void Diagnose(ClientEventRefreshNotice notice) { }
        public async ValueTask DisposeAsync()
        {
            Connection.Stop();
            foreach (var item in pending) item.Result.TrySetResult(ClashOfRimClientNetworkResult<ModSessionStreamClosedDto>.Ok(new(true)));
            await Task.WhenAll(Works).WaitAsync(TimeSpan.FromSeconds(5));
        }
    }
}
