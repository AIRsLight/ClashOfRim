using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim;
using AIRsLight.ClashOfRim.Chat;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Chat;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Raids;
using HarmonyLib;
using Verse;

internal static class ClientChatOverlayTests
{
    internal static void Run() => RunAsync().GetAwaiter().GetResult();
    private static async Task RunAsync()
    {
        foreach (string change in new[] { "server", "user", "colony", "auth", "login", "identity", "work", "game", "expired" })
        {
            var f = new ClientChatTests.Fixture(); var model = new ClientChatOverlayModel(f.Module, f.Session, f.Ports, () => true);
            f.Inbox.Add(ClientChatTests.Message(1, "alice"));
            var view = model.Read()!; view.Input = "@bob retained"; view.Minimized = false; view.ScrollY = 500;
            await f.Ports.PumpUntil(view.PollAsync(100)); int calls = f.Requests.Count;
            f.Change(change);
            await f.Ports.PumpUntil(view.SendAsync()); await f.Ports.PumpUntil(view.PollAsync(110)); view.MarkRead();
            Check(!view.IsCurrent && f.Requests.Count == calls, "Old chat overlay crossed " + change);
            var next = model.Read();
            if (change == "expired") Check(next is null, "Unavailable session displayed a chat view.");
            else
            {
                Check(next is not null && !ReferenceEquals(view, next) && next.Minimized && next.Input == "" && next.ScrollY == 0,
                    "New chat view inherited old draft or layout: " + change);
                if (change == "game") Check(next!.Messages.Count == 1, "Game replacement cleared the account conversation.");
                await f.Ports.PumpUntil(next!.PollAsync(101)); Check(f.Requests.Count == calls + 1, "New chat view inherited polling deadline.");
            }
        }
        var fixture = new ClientChatTests.Fixture(); bool available = true; Action? eligibility = null;
        var overlay = new ClientChatOverlayModel(fixture.Module, fixture.Session, fixture.Ports, () => { eligibility?.Invoke(); return available; });
        fixture.Inbox.Add(ClientChatTests.Message(1, "alice"));
        var current = overlay.Read()!;
        await fixture.Ports.PumpUntil(current.PollAsync(5)); await fixture.Ports.PumpUntil(current.PollAsync(14.99f));
        Check(fixture.Requests.Count == 1 && current.Unread == 1, "Minimized chat changed cadence or marked private messages read.");
        var cached = current.Messages;
        Check(ReferenceEquals(cached, current.Messages), "Unchanged view recopied messages.");
        current.MarkRead(); Check(current.Unread == 0, "Expanded overlay failed to mark the account conversation read.");
        fixture.Inbox.Add(ClientChatTests.Message(2)); await fixture.Ports.PumpUntil(current.PollAsync(15));
        Check(fixture.Requests.Count == 2 && current.Messages.Count == 2 && !ReferenceEquals(cached, current.Messages), "Chat refresh boundary or version cache failed.");
        current.Input = "@bob 私聊 İ 😀"; current.Minimized = false;
        foreach (string change in new[] { "snapshot", "password", "steam" })
        { fixture.Change(change); Check(ReferenceEquals(current, overlay.Read()) && current.Input.Contains("私聊"), "Unrelated change discarded chat draft: " + change); }
        await fixture.Ports.PumpUntil(current.SendAsync());
        Check(fixture.Requests.Last().Target == "bob" && fixture.Requests.Last().Text == "私聊 İ 😀" && current.Input == "", "Overlay bypassed shared chat parsing or failed to consume draft.");
        current.Input = "@broken"; await fixture.Ports.PumpUntil(current.SendAsync());
        Check(fixture.Rejections == 1 && current.Input == "", "Invalid input bypassed shared rejection.");
        int count = fixture.Requests.Count;
        available = false; current.Input = "hidden"; await current.SendAsync(); await current.PollAsync(100);
        Check(overlay.Read() is null && fixture.Requests.Count == count, "Hidden world performed chat actions.");
        available = true; eligibility = () => { eligibility = null; fixture.Change("identity"); };
        Check(overlay.Read() is null && current.Input == "", "Eligibility callback adopted a replacement login.");
        var busy = overlay.Read()!; busy.Input = "retained while busy";
        var delayed = new ClientWorldPublicationTestPorts.DelayedReply(); fixture.Before = _ => delayed.Wait();
        Task pending = busy.PollAsync(0);
        await delayed.Complete(fixture.Ports, pending, () =>
        {
            busy.SendAsync().GetAwaiter().GetResult();
            Check(busy.Input == "retained while busy" && fixture.Requests.Count == count + 1, "Busy chat consumed or sent the draft.");
        });
        await Engine(); ClientSessionWork.Reset();
    }
    private static readonly List<Task> observed = new();
    private static async Task Engine()
    {
        using var engine = new ClientGameComponentTestRuntime("ClashOfRim.Tests.ChatOverlay");
        using var gui = new ClientOverlayTestRuntime("ClashOfRim.Tests.ChatOverlay");
        var harmony = new Harmony("ClashOfRim.Tests.ChatOverlay.Tasks");
        foreach (string name in new[] { nameof(ClientChatOverlayModel.View.PollAsync), nameof(ClientChatOverlayModel.View.SendAsync) })
            harmony.Patch(AccessTools.Method(typeof(ClientChatOverlayModel.View), name), postfix: new HarmonyMethod(typeof(ClientChatOverlayTests), nameof(Observe)));
        try
        {
            foreach (string condition in new[] { "active", "raid", "world", "entry", "old-game", "detached", "inactive", "eligibility-adapter", "send-auth", "send-game", "send-adapter" })
            {
                gui.Reset(); observed.Clear(); var f = new ClientChatTests.Fixture();
                f.Inbox.Add(ClientChatTests.Message(1, "alice")); var game = New<Game>(); Current.Game = game; f.Ports.GameOwner = game;
                var component = new ClashOfRimGameComponent(game);
                AccessTools.Field(typeof(Game), "components").SetValue(game, new List<GameComponent> { component });
                if (condition == "raid") ClashOfRimGameComponent.SetActiveRaidBattleSession(new() { EventId = "raid" });
                var model = new ClientChatOverlayModel(f.Module, f.Session, f.Ports, () =>
                {
                    if (condition == "eligibility-adapter") ClientChatOverlay.Attach(null);
                    return condition != "inactive";
                });
                var renderer = new ClientChatOverlay(model); ClientChatOverlay.Attach(renderer);
                if (condition == "world") gui.WorldSelected = true;
                if (condition == "entry") Current.ProgramState = ProgramState.Entry;
                if (condition == "old-game") { Current.Game = New<Game>(); f.Ports.GameOwner = Current.Game; }
                if (condition == "detached") ClientChatOverlay.Attach(null);
                component.GameComponentOnGUI(); await Pump(f);
                bool hidden = condition is "world" or "entry" or "old-game" or "detached" or "inactive" or "eligibility-adapter";
                Check(f.Requests.Count == (hidden ? 0 : 1) && gui.Buttons.Count == (hidden ? 0 : 1), "Chat visibility/refresh changed: " + condition);
                if (hidden) continue;
                Check(gui.Buttons.Single().Rect.y == (condition == "raid" ? 128f : 12f) && f.Module.UnreadPrivate == 1,
                    "Chat overlapped raid overlay or read minimized messages.");
                gui.Click = key => key.Contains("Minimized"); component.GameComponentOnGUI(); await Pump(f);
                Check(!model.Read()!.Minimized && f.Module.UnreadPrivate == 1, "Expand did not preserve read timing.");
                gui.Click = _ => false; component.GameComponentOnGUI(); await Pump(f);
                Check(f.Module.UnreadPrivate == 0 && gui.Labels.Contains("消息 İ 1") && gui.TextKeys.Contains("ClashOfRim.Chat.PrivateHeader"),
                    "Expanded chat omitted messages, header or read action.");
                gui.Edit = _ => "@bob rendered 😀";
                gui.Click = key =>
                {
                    if (key != "ClashOfRim.Chat.Send") return false;
                    if (condition == "send-auth") f.Change("auth");
                    if (condition == "send-game") { Current.Game = New<Game>(); f.Ports.GameOwner = Current.Game; }
                    if (condition == "send-adapter") ClientChatOverlay.Attach(new(model));
                    return true;
                };
                component.GameComponentOnGUI(); await Pump(f);
                bool stale = condition.StartsWith("send-");
                Check(f.Requests.Count == (stale ? 1 : 2), "Rendered input crossed its view ownership: " + condition);
                if (!stale)
                {
                    Check(f.Requests.Last().Target == "bob" && f.Requests.Last().Text == "rendered 😀" && model.Read()!.Input == "", "Renderer bypassed original send.");
                    gui.Click = key => key == "ClashOfRim.Chat.Minimize"; component.GameComponentOnGUI(); await Pump(f);
                    Check(model.Read()!.Minimized, "Minimize button lost view state.");
                }
            }
        }
        finally { harmony.UnpatchAll(harmony.Id); observed.Clear(); }
    }
    private static void Observe(Task __result) => observed.Add(__result);
    private static async Task Pump(ClientChatTests.Fixture fixture) { var tasks = Task.WhenAll(observed.ToArray()); observed.Clear(); await fixture.Ports.PumpUntil(tasks); }
    private static T New<T>() => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
