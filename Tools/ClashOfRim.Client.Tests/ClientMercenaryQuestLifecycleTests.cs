using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.Modules.Mercenaries;
using AIRsLight.ClashOfRim.Modules.Quests;

internal static class ClientMercenaryQuestLifecycleTests
{
    internal static void Run() => RunAsync().GetAwaiter().GetResult();
    private static async Task RunAsync()
    {
        await Recall(); await Shuttle(); await Signals(); await Overtime(); await Ownership(); await CompletionBeforeOvertime();
        await ReentrantTerminal(); await ConfirmationRecovery();
    }
    private static async Task Recall()
    {
        foreach (string mode in new[] { "idle", "completed", "missing", "waiting", "immediate", "failed", "empty-contract" })
        {
            using var f = new Fixture(); var q = new State();
            if (mode == "completed") q.Completed = true;
            if (mode == "missing") q.HasPawn = false;
            if (mode is "waiting" or "immediate" or "failed" or "empty-contract") q.GameTicks = 100;
            if (mode is "immediate" or "empty-contract") q.RecallResult = new(true, false);
            if (mode == "failed") q.RecallResult = new(false, false);
            if (mode == "empty-contract") q.ContractId = "";
            await f.Pump(f.Flow.TickAsync(q)); await f.Pump(f.Flow.TickAsync(q));
            Check(q.Recalls == (mode is "waiting" or "immediate" or "failed" or "empty-contract" ? 1 : 0), "Recall was repeated or started incorrectly: " + mode);
            Check(q.ArrivalSent == (mode is not ("completed" or "missing")), "Arrival notification changed: " + mode);
            if (mode == "waiting") Check(q.ShuttleWaiting && !q.Completed && f.Core.Uploads == 0, "Waiting shuttle prematurely confirmed completion.");
            if (mode is "immediate" or "empty-contract")
            {
                Check(q.Completed && q.Endings.Single() == (true, false, false) && f.Core.Uploads == 1
                    && f.Core.Commands.Count == (mode == "empty-contract" ? 0 : 1), "Successful recall lost snapshot-before-completion ordering.");
                if (mode == "immediate") Check(f.Core.Commands[0].Kind == "Completed" && f.Core.Commands[0].Key == "mercenary-completed:contract"
                    && f.Core.Contexts[0].CurrentSnapshotId == "accepted-1", "Completion used wrong idempotency key/base snapshot.");
            }
            if (mode == "missing") Check(q.Completed && q.Endings.Single() == (false, true, true) && f.Core.Uploads == 1 && f.Core.Commands.Count == 0,
                "Missing pawn fabricated a death fine or skipped snapshot confirmation.");
            if (mode == "failed") Check(q.RecallStarted && !q.ShuttleWaiting && !q.Completed, "Failed native recall changed its partial-effect state.");
        }
        using var manual = new Fixture(); var state = new State { ExpiresAt = 0 };
        Check(manual.Flow.TryStartRecall(state, out var work) && !manual.Flow.TryStartRecall(state, out _), "Manual recall duplicated the native command.");
        await manual.Pump(work); Check(state.Recalls == 1 && state.ShuttleWaiting, "Manual recall requires an expiry date.");
    }
    private static async Task Shuttle()
    {
        foreach (string state in new[] { "waiting", "loaded", "loaded-gone", "saved-loaded-gone", "loaded-destroyed", "empty-destroyed", "gone-and-pawn-missing" })
        {
            using var f = new Fixture(); var q = new State { RecallStarted = true, ShuttleWaiting = true };
            q.PawnLoaded = state is "loaded" or "loaded-gone" or "loaded-destroyed" or "gone-and-pawn-missing";
            q.ShuttleLoaded = state == "saved-loaded-gone";
            q.ShuttleGone = state is "loaded-gone" or "saved-loaded-gone" or "loaded-destroyed" or "gone-and-pawn-missing";
            q.ShuttleDestroyed = state is "loaded-destroyed" or "empty-destroyed";
            q.HasPawn = state != "gone-and-pawn-missing";
            object target = q.ShuttleTarget!;
            await f.Pump(f.Flow.TickAsync(q)); await f.Pump(f.Flow.TickAsync(q));
            bool success = state is "loaded-gone" or "saved-loaded-gone" or "loaded-destroyed" or "gone-and-pawn-missing";
            bool failure = state == "empty-destroyed";
            Check(q.Completed == (success || failure) && f.Core.Commands.Count == (success || failure ? 1 : 0), "Shuttle settlement branch changed: " + state);
            if (success) Check(f.Core.Commands[0].Kind == "Completed" && q.Releases == 0, "Loaded shuttle incorrectly charged destruction.");
            if (failure) Check(f.Core.Commands[0].Kind == "ShuttleDestroyed" && f.Core.Commands[0].Key == "mercenary-shuttle-destroyed:contract"
                && q.Releases == 1 && q.Notices.Single(n => n.Kind == ClientMercenaryQuestNotice.ShuttleDestroyed).Target == target,
                "Destroyed shuttle lost its original target/release/idempotent report.");
        }
    }
    private static async Task Signals()
    {
        foreach (string tag in new[] { "task.SurgeryViolation", "task.SentSatisfied", "task.Destroyed", "task.SentUnsatisfied", "other.Destroyed", "task.destroyed", "", " task.Destroyed" })
        {
            using var f = new Fixture(); var q = new State { RecallStarted = true, ShuttleWaiting = true };
            await f.Pump(f.Flow.SignalAsync(q, tag)); await f.Pump(f.Flow.SignalAsync(q, tag));
            string? expected = tag switch { "task.SurgeryViolation" => "HostileBehavior", "task.SentSatisfied" => "Completed", "task.Destroyed" or "task.SentUnsatisfied" => "ShuttleDestroyed", _ => null };
            Check(f.Core.Commands.Count == (expected is null ? 0 : 1) && (expected is null || f.Core.Commands[0].Kind == expected), "Quest signal semantics changed: " + tag);
        }
        foreach (bool wrongPawn in new[] { false, true })
        {
            using var f = new Fixture(); var q = new State(); object killed = wrongPawn ? new() : q.PawnIdentity!;
            await f.Pump(f.Flow.KilledAsync(q, killed)); await f.Pump(f.Flow.KilledAsync(q, killed));
            Check(q.DeathReported == !wrongPawn && q.Completed == !wrongPawn && f.Core.Commands.Count == (wrongPawn ? 0 : 1), "Death callback selected another pawn or repeated a fine.");
        }
        using var noTag = new Fixture(); var untagged = new State { QuestTag = "" };
        await noTag.Pump(noTag.Flow.SignalAsync(untagged, ".SurgeryViolation")); Check(!untagged.HostileReported, "Blank quest tag matched a foreign signal.");
    }
    private static async Task Overtime()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            foreach (string culture in new[] { "en-US", "zh-CN", "fr-FR", "tr-TR" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                using var f = new Fixture(); var q = new State { RecallStarted = true, ShuttleWaiting = true, GameTicks = 60099 };
                await f.Pump(f.Flow.TickAsync(q)); Check(q.ReportedOvertimeDays == 0, "Overtime charged before one full day.");
                q.GameTicks++; await f.Pump(f.Flow.TickAsync(q)); await f.Pump(f.Flow.TickAsync(q));
                q.GameTicks = 180100; await f.Pump(f.Flow.TickAsync(q));
                Check(q.ReportedOvertimeDays == 3 && f.Core.Commands.Select(c => c.Key).SequenceEqual(new[] { "mercenary-overtime:contract:1", "mercenary-overtime:contract:2", "mercenary-overtime:contract:3" })
                    && f.Core.Uploads == 3 && f.Core.Contexts[2].CurrentSnapshotId == "accepted-2", "Overtime lost day deduplication or ordered confirmation.");
            }
            Check(ClientQuestTiming.OverdueDays(0, long.MaxValue) == 0 && ClientQuestTiming.OverdueDays(1, long.MaxValue) == int.MaxValue
                && ClientQuestTiming.OverdueDays(100, -1) == 0, "Quest timing lost absent/future/saturated deadline boundaries.");
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }
    private static async Task Ownership()
    {
        foreach (string change in new[] { "user", "auth", "server", "colony", "login", "generation", "game", "snapshot", "contract", "pawn", "binding" })
        foreach (string stage in new[] { "Arrival", "recall", "release", "end" })
        {
            using var f = new Fixture(); var q = new State { GameTicks = 100 };
            if (stage == "release") { q.RecallStarted = true; q.ShuttleWaiting = true; q.ShuttleDestroyed = true; }
            if (stage == "end") q.HasPawn = false;
            q.Hook = point =>
            {
                if (point != stage) return; q.Hook = null;
                if (change == "contract") q.ContractId = "other";
                else if (change == "pawn") q.PawnIdentity = new();
                else if (change == "binding") q.IsCurrent = false;
                else f.Core.Change(change);
            };
            await f.Pump(f.Flow.TickAsync(q));
            bool allowed = change == "snapshot" || (stage == "end" && change == "binding");
            if (stage == "Arrival") Check(q.Recalls == (allowed ? 1 : 0), "Arrival callback recalled under replacement ownership: " + change);
            if (stage == "recall") Check(q.ShuttleWaiting == allowed, "Native recall published into replacement ownership: " + change);
            if (stage == "release") Check(f.Core.Commands.Count == (allowed ? 1 : 0), "Shuttle release reported to replacement ownership: " + change);
            if (stage == "end") Check(f.Core.Uploads == (allowed ? 1 : 0), "Quest end confirmed against a replacement identity: " + change);
        }
    }
    private static async Task CompletionBeforeOvertime()
    {
        using var f = new Fixture(); var q = new State { GameTicks = 60100 };
        q.Hook = point => { if (point == "recall") { q.PawnLoaded = true; q.ShuttleGone = true; } };
        await f.Pump(f.Flow.TickAsync(q));
        Check(q.Completed && q.ReportedOvertimeDays == 0 && f.Core.Commands.Count == 1 && f.Core.Commands[0].Kind == "Completed",
            "Recall completed during this tick but still charged overtime.");
    }
    private static async Task ReentrantTerminal()
    {
        using var f = new Fixture(); var q = new State(); Task death = Task.CompletedTask;
        q.Hook = point => { if (point == "Recall") death = f.Flow.KilledAsync(q, q.PawnIdentity!); };
        Check(f.Flow.TryStartRecall(q, out var work), "Native recall was not dispatched.");
        await f.Pump(Task.WhenAll(work, death));
        Check(q.Completed && !q.ShuttleWaiting && q.DeathReported && q.Endings.Count == 1 && f.Core.Commands.Single().Kind == "Death",
            "Reentrant death was reopened as a waiting recall.");
    }
    private static async Task ConfirmationRecovery()
    {
        using var f = new Fixture(); var q = new State { GameTicks = 100, RecallResult = new(true, false) };
        f.Core.Failure = ClientMercenaryIncidentsTests.Fault.Upload;
        await f.Pump(f.Flow.TickAsync(q)); await f.Pump(f.Flow.TickAsync(q));
        Check(q.Completed && f.Core.Commands.Count == 0 && f.Core.Uploads == 1 && f.Core.View.Retry is not null,
            "Failed completion snapshot reported the contract or restarted from the quest tick.");
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Core.Confirmation.ContractCompleted += (_, _) => done.TrySetResult();
        f.Core.Failure = ClientMercenaryIncidentsTests.Fault.None; f.Core.View.Retry!();
        await f.Pump(done.Task);
        Check(q.Recalls == 1 && q.Endings.Count == 1 && f.Core.Uploads == 2 && f.Core.Commands.Single().Kind == "Completed"
            && f.Core.Contexts.Single().CurrentSnapshotId == "accepted-2", "Completion recovery repeated native effects or reported before accepted save.");
    }
    internal sealed class Fixture : IDisposable
    {
        internal readonly ClientMercenaryIncidentsTests.Fixture Core = new();
        internal readonly ClientMercenaryQuestLifecycle Flow;
        private Task completedReport = Task.CompletedTask;
        internal Fixture()
        {
            Flow = new(Core.Session, Core.Ports, Core.Incidents, Core.Confirmation);
            Core.Confirmation.ContractCompleted += (id, key) => completedReport = Core.Incidents.ReportAsync(id, "Completed", key);
        }
        internal async Task Pump(Task task) { await Core.Ports.PumpUntil(task); await Core.Ports.PumpUntil(completedReport); }
        public void Dispose() => Core.Dispose();
    }
    internal sealed class State : IClientMercenaryQuest
    {
        public bool IsCurrent { get; set; } = true;
        public string ContractId { get; set; } = "contract";
        public string QuestTag { get; set; } = "task";
        public object? PawnIdentity { get; set; } = new();
        public object? ShuttleTarget { get; set; } = new();
        public bool HasPawn { get; set; } = true;
        public bool PawnSpawned { get; set; } = true;
        public bool PawnLoaded { get; set; }
        public bool ShuttleGone { get; set; }
        public bool ShuttleDestroyed { get; set; }
        public long ExpiresAt { get; set; } = 100;
        public long GameTicks { get; set; } = 99;
        public bool Completed { get; set; }
        public bool RecallStarted { get; set; }
        public bool ShuttleWaiting { get; set; }
        public bool ShuttleLoaded { get; set; }
        public bool ArrivalSent { get; set; }
        public bool HostileReported { get; set; }
        public bool DeathReported { get; set; }
        public bool ShuttleDestroyedReported { get; set; }
        public int ReportedOvertimeDays { get; set; }
        internal ClientMercenaryRecallResult RecallResult = new(true, true);
        internal int Recalls, Releases;
        internal readonly List<(bool Success, bool Letter, bool Sound)> Endings = new();
        internal readonly List<(ClientMercenaryQuestNotice Kind, object? Target)> Notices = new();
        internal Action<string>? Hook;
        public ClientMercenaryRecallResult Recall(Func<bool> current) { Recalls++; Hook?.Invoke("recall"); return RecallResult; }
        public void ClearShuttle() => ShuttleTarget = null;
        public void ReleasePawn() { Releases++; Hook?.Invoke("release"); }
        public void Notify(ClientMercenaryQuestNotice kind, object? target, Func<bool> current)
        { Hook?.Invoke(kind.ToString()); if (current()) Notices.Add((kind, target)); }
        public void End(bool success, bool sendLetter, bool playSound) { Endings.Add((success, sendLetter, playSound)); Hook?.Invoke("end"); }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
