using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Support;
using AIRsLight.ClashOfRim.Quests;
using AIRsLight.ClashOfRim.Support;
using HarmonyLib;
using RimWorld;
using Verse;

internal static class ClientSupportQuestLifecycleTests
{
    private static Task? dispatched;
    private static string? confirmedReason;
    internal static void Run() => RunAsync().GetAwaiter().GetResult();
    private static async Task RunAsync()
    {
        var binding = ClientSupportQuestCallbacks.Current; var game = Current.Game;
        using var runtime = new ClientQuestTestRuntime("ClashOfRim.Tests.SupportQuestLifecycle");
        var harmony = new Harmony("ClashOfRim.Tests.SupportQuestLifecycle");
        void Prefix(System.Reflection.MethodBase method, string prefix) => harmony.Patch(method, prefix: new HarmonyMethod(typeof(ClientSupportQuestLifecycleTests), prefix));
        Prefix(AccessTools.Method(typeof(ClientSupportReturns), nameof(ClientSupportReturns.ConfirmAsync)), nameof(Confirm));
        harmony.Patch(AccessTools.Method(typeof(ClientSupportQuestCallbacks), nameof(ClientSupportQuestCallbacks.TickAsync)), postfix: new HarmonyMethod(typeof(ClientSupportQuestLifecycleTests), nameof(Observe)));
        try
        {
            await Transitions(); await Ownership(); await Engine(runtime);
        }
        finally { harmony.UnpatchAll(harmony.Id); ClientSupportQuestCallbacks.Attach(binding); Current.Game = game; dispatched = null; ClientSessionWork.Reset(); }
    }
    private static async Task Transitions()
    {
        foreach (string state in new[] { "completed", "empty", "permanent", "missing", "active", "expired", "departing", "obsolete" })
        {
            using var f = new ClientSupportReturnsTests.Fixture(); var flow = Flow(f);
            var q = new State(); f.Source.Departing = false; f.Source.Ready = false;
            switch (state)
            {
                case "completed": q.Completed = true; break; case "empty": q.EventId = ""; break;
                case "permanent": q.Permanent = true; break; case "missing": q.Assignment = false; break;
                case "expired": q.Due = true; break; case "departing": q.FinishRequested = true; break;
                case "obsolete": q.IsCurrent = false; q.Due = true; break;
            }
            confirmedReason = null; await flow.TickAsync(q); await flow.TickAsync(q);
            Check(q.Completions == (state is "permanent" or "missing" ? 1 : 0) && f.Source.Departures == (state == "expired" ? 1 : 0),
                "Quest transition changed or repeated: " + state);
            if (state == "expired") Check(q.FinishRequested && q.FinishReason == "QuestExpired", "Expired departure did not persist its reason.");
            if (state == "departing") Check(confirmedReason == "QuestExpired", "Legacy blank finish reason lost its expiry fallback.");
            Check(f.Source.Prepares == 0 && f.Commands.Count == 0 && !f.Sync.IsBusy, "Walking/local completion started a snapshot or report.");
        }
        using (var f = new ClientSupportReturnsTests.Fixture())
        {
            var flow = Flow(f); var q = new State(); f.Source.Departing = false; f.Source.Ready = false;
            f.Sync.TryBeginManual(out var lease);
            Check(!flow.TryStartEarlyReturn(q) && !q.FinishRequested && f.Source.Departures == 0, "Busy return consumed quest intent.");
            lease!.Dispose(); Check(flow.TryStartEarlyReturn(q) && !flow.TryStartEarlyReturn(q) && q.FinishReason == "EarlyReturn", "Early return did not use one original departure.");
            f.Source.Ready = true;
            Task first = flow.TickAsync(q), again = flow.TickAsync(q);
            Check(ReferenceEquals(first, again) && confirmedReason == "EarlyReturn", "Repeated quest ticks did not reuse the same pending return.");
            await f.Ports.PumpUntil(first);
            Check(f.Source.Prepares == 1 && f.Source.Completions == 1 && f.Uploads == 1 && f.Commands.Count == 1
                && f.Commands[0].IdempotencyKey == "support-finish:alice:colony:event:EarlyReturn" && !f.Sync.IsBusy && !f.Mutations.IsPending,
                "Quest return bypassed outbox, snapshot or idempotent completion.");
            q.Assignment = false; await flow.TickAsync(q); Check(q.Completions == 1, "Finished assignment left a live quest.");
        }
        foreach (bool permanent in new[] { false, true })
        {
            using var f = new ClientSupportReturnsTests.Fixture(); var q = new State { Permanent = permanent, Completed = !permanent };
            Check(!Flow(f).TryStartEarlyReturn(q) && f.Source.Departures == 0, "Completed/permanent quest allowed early return.");
        }
    }
    private static async Task Ownership()
    {
        foreach (string change in new[] { "user", "colony", "auth", "server", "login", "generation", "game", "snapshot", "target", "binding" })
        foreach (bool completing in new[] { false, true })
        {
            using var f = new ClientSupportReturnsTests.Fixture(); var flow = Flow(f);
            var q = new State { Assignment = !completing, Due = true }; f.Source.Departing = false; f.Source.Ready = false;
            q.Read = () =>
            {
                q.Read = null;
                if (change == "target") q.EventId = "other";
                else if (change == "binding") q.IsCurrent = false;
                else f.Change(change);
            };
            await flow.TickAsync(q);
            Check(q.Completions == (completing && change == "snapshot" ? 1 : 0)
                && f.Source.Departures == (!completing && change == "snapshot" ? 1 : 0), "Quest callback crossed " + change);
        }
    }
    private static async Task Engine(ClientQuestTestRuntime runtime)
    {
        foreach (string state in new[] { "before-due", "due", "no-deadline", "early", "missing", "permanent", "unbound", "removed-quest", "removed-part", "old-game", "detached", "relogin" })
        {
            using var f = new ClientSupportReturnsTests.Fixture(); f.Source.Departing = false; f.Source.Ready = false;
            var source = runtime.NewGame(); f.Ports.GameOwner = source;
            var component = new ClashOfRimGameComponent(source);
            AccessTools.Field(typeof(Game), "components").SetValue(source, new List<GameComponent> { component });
            AccessTools.Field(typeof(ClashOfRimGameComponent), "activeSupportAssignments").SetValue(component,
                new List<ActiveSupportPawnAssignment> { new() { EventId = "event" } });
            var part = new QuestPart_ClashSupportPawn { EventId = "event", ExpiresAtGameTicks = 100 };
            var quest = runtime.Add(part);
            var callbacks = new ClientSupportQuestCallbacks(Flow(f)); ClientSupportQuestCallbacks.Attach(callbacks);
            runtime.Ticks = 99; dispatched = null; part.QuestPartTick(); await f.Ports.PumpUntil(dispatched ?? Task.CompletedTask);
            Check(!part.FinishRequested && f.Source.Departures == 0, "Quest expired before its deadline.");
            runtime.Ticks = state == "before-due" ? 99 : 100;
            switch (state)
            {
                case "no-deadline": part.ExpiresAtGameTicks = 0; break;
                case "missing": component.MarkSupportAssignmentFinished("event"); break;
                case "permanent": part.PermanentSupport = true; break;
                case "unbound": ClientSupportQuestCallbacks.Attach(null); break;
                case "removed-quest": runtime.Manager.QuestsListForReading.Clear(); break;
                case "removed-part": quest.PartsListForReading.Remove(part); break;
                case "old-game": Current.Game = New<Game>(); f.Ports.GameOwner = Current.Game; break;
                case "detached": ClientSupportQuestCallbacks.Attach(new(Flow(f))); break;
                case "relogin": f.Session.PublishLogin(new() { AuthToken = f.Ports.Auth, SessionId = "new" }); break;
            }
            if (state == "early") Check(part.TryStartEarlyReturnNow() && !part.TryStartEarlyReturnNow(), "Engine early return duplicated departure.");
            else if (state == "detached") await callbacks.TickAsync(part);
            else { dispatched = null; part.QuestPartTick(); await f.Ports.PumpUntil(dispatched ?? Task.CompletedTask); }
            Check(f.Source.Departures == (state is "due" or "early" or "relogin" ? 1 : 0)
                && runtime.Endings.Count == (state is "missing" or "permanent" ? 1 : 0), "Engine quest crossed source ownership or changed transition: " + state);
            if (state is "due" or "early" or "relogin") Check(part.FinishRequested && part.FinishReason == (state == "early" ? "EarlyReturn" : "QuestExpired"), "Serialized quest return fields changed.");
            if (state is "missing" or "permanent") Check(part.Completed && runtime.Endings[0] == (part, QuestEndOutcome.Success, false, false), "Completion changed serialized fields, outcome or notifications.");
        }
    }
    private sealed class State : IClientSupportQuest
    {
        public bool IsCurrent { get; set; } = true;
        public string EventId { get; set; } = "event";
        public bool Completed { get; set; }
        public bool Permanent { get; set; }
        internal bool Assignment = true, Due;
        internal Action? Read;
        internal int Completions;
        public bool HasAssignment { get { Read?.Invoke(); return Assignment; } }
        public bool Expired => Due;
        public bool FinishRequested { get; set; }
        public string FinishReason { get; set; } = "";
        public void Complete() { Completed = true; Completions++; }
        public void MarkDeparting(string reason) { FinishRequested = true; FinishReason = reason; }
    }
    private static ClientSupportQuestLifecycle Flow(ClientSupportReturnsTests.Fixture f) => new(f.Session, f.Ports, f.Returns);
    private static T New<T>() => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
    private static void Observe(Task __result) => dispatched = __result;
    private static void Confirm(string reason) => confirmedReason = reason;
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
