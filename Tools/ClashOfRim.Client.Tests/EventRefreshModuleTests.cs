using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Events;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Synchronization;

internal static class EventRefreshModuleTests
{
    internal static void Run() => RunAsync().GetAwaiter().GetResult();
    private static async Task RunAsync()
    {
        try
        {
            await ManualAndCursor();
            await CoalescingAndDeferral();
            await SnapshotAdvance();
            await Replacement();
            await DelayedDelivery();
            await ContinuationAndAdmission();
        }
        finally { ClientSessionWork.Reset(); }
    }

    private static async Task ManualAndCursor()
    {
        foreach (var kind in new[] { ClientEventReadKind.Queue, ClientEventReadKind.Wait, ClientEventReadKind.Details, ClientEventReadKind.Refresh })
        {
            var f = new Fixture(); f.Refresh.AdoptQueue(EventReadTestProtocol.QueueOf("cached"));
            f.State.ObserveNotificationVersion(9007199254740991);
            await f.Refresh.StartManual(kind);
            Require(!f.Sync.IsBusy && f.Automatic.Count == 0, "Manual read leaked admission or auto-applied.");
            if (kind is ClientEventReadKind.Details or ClientEventReadKind.Refresh)
            {
                Require(f.Letters.Count == 0 && f.Ports.Queued == 1, "Letter effect ran on HTTP thread.");
                (await f.Ports.Next())(); Require(f.Letters.Count == 1, "Owned letter effect was not delivered.");
                Require(f.State.DetailsStatus.StartsWith("Updated:"), "Detail state was not published.");
            }
            if (kind == ClientEventReadKind.Wait)
                Require(f.Protocol.Calls.Single() == "wait:9007199254740991:15" && f.State.NotificationVersion == 9007199254740992,
                    "Long-poll cursor changed precision or diverged from stream cursor.");
        }
        var wait = new Fixture();
        var held = Signal<ClashOfRimClientNetworkResult<ModWaitForEventsResponseDto>>();
        wait.Protocol.Wait = (_, _) => held.Task;
        var work = wait.Refresh.StartManual(ClientEventReadKind.Wait);
        await wait.Protocol.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        wait.State.ObserveNotificationVersion(100);
        held.SetResult(Ok(new ModWaitForEventsResponseDto { NotificationVersion = 42 }));
        await work;
        Require(wait.State.NotificationVersion == 100, "Late poll regressed the newer stream cursor.");
        wait.State.ActionStatus = "old-action";
        wait.Ports.Auth = "new";
        Require(wait.State.NotificationVersion == 0 && wait.State.QueueStatus.StartsWith("InitialQueue") && wait.State.ActionStatus.StartsWith("InitialAction"), "State survived identity context replacement.");
    }

    private static async Task CoalescingAndDeferral()
    {
        var f = new Fixture(); var held = Signal<ClashOfRimClientNetworkResult<ModPullPendingEventsResponseDto>>();
        int reads = 0;
        f.Protocol.Queue = _ => ++reads == 1 ? held.Task : Task.FromResult(Ok(new ModPullPendingEventsResponseDto { EventQueue = EventReadTestProtocol.QueueOf("next") }));
        Task work = f.Refresh.StartAutomatic("first");
        await f.Protocol.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() => f.Refresh.StartAutomatic("notification"))));
        Require(reads == 1, "Notifications started parallel pipelines.");
        held.SetResult(Ok(new ModPullPendingEventsResponseDto { EventQueue = EventReadTestProtocol.QueueOf("first") }));
        await work.WaitAsync(TimeSpan.FromSeconds(5));
        Require(reads == 2 && f.Automatic.SequenceEqual(new[] { "first", "next" }), "Merged notifications did not yield exactly one follow-up.");
        var deferred = new Fixture(); deferred.Mutations.Begin(LocalMutationKind.Bank, "pending");
        await deferred.Refresh.StartAutomatic("notification");
        Require(deferred.Protocol.Calls.Count == 0 && deferred.State.QueueStatus.Contains("PendingMutation"), "Blocked refresh issued HTTP.");
        deferred.Mutations.Reset(); await deferred.Refresh.ResumeDeferred(); await deferred.Refresh.ResumeDeferred();
        Require(deferred.Protocol.Calls.Count(c => c == "queue") == 1 && deferred.Automatic.Count == 1, "Deferred refresh was lost or duplicated.");

        var manual = new Fixture(); var poll = Signal<ClashOfRimClientNetworkResult<ModWaitForEventsResponseDto>>();
        manual.Protocol.Wait = (_, _) => poll.Task;
        Task manualWork = manual.Refresh.StartManual(ClientEventReadKind.Wait);
        await manual.Protocol.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        manual.Refresh.Defer("application-busy", "busy");
        poll.SetResult(Ok(new ModWaitForEventsResponseDto())); await manualWork;
        await manual.AutomaticDelivered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Require(manual.Protocol.Calls.Count(c => c == "queue") == 1, "Manual read completion stranded a busy application's deferral.");
        foreach (string failure in new[] { "http", "reject", "throw" })
        {
            var failed = new Fixture(); failed.Protocol.FailStage = "queue"; failed.Protocol.Failure = failure;
            await failed.Refresh.StartAutomatic("first"); await failed.Refresh.StartAutomatic("second");
            Require(failed.Protocol.Calls.Count == 2 && failed.Automatic.Count == 0, "Failure leaked refresh admission or spun retries: " + failure);
        }
    }

    private static async Task SnapshotAdvance()
    {
        foreach (bool details in new[] { false, true })
        foreach (bool throws in new[] { false, true })
        {
            var f = new Fixture();
            var queue = Signal<ClashOfRimClientNetworkResult<ModPullPendingEventsResponseDto>>();
            var detail = Signal<ClashOfRimClientNetworkResult<ModPullEventDetailsResponseDto>>();
            var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int calls = 0;
            if (details) f.Protocol.Detail = ids => { if (++calls == 1) { sent.SetResult(); return detail.Task; } return Task.FromResult(Details("new")); };
            else f.Protocol.Queue = _ => { if (++calls == 1) { sent.SetResult(); return queue.Task; } return Task.FromResult(Queue("new")); };
            var work = f.Refresh.StartAutomatic("first"); await sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
            f.Ports.Snapshot = "advanced";
            if (details)
            {
                if (throws) detail.SetException(new InvalidOperationException("old")); else detail.SetResult(Details("old"));
            }
            else if (throws) queue.SetException(new InvalidOperationException("old")); else queue.SetResult(Queue("old"));
            await work.WaitAsync(TimeSpan.FromSeconds(5));
            Require(calls == 2 && f.Automatic.SequenceEqual(new[] { "new" }) && f.Inbox.CopyDetails().Single().EventId == "new",
                "Snapshot advance lost refresh or applied stale data: " + details + "/" + throws);
            Require(!f.Notices.Any(n => n.Update?.Failure is not null), "Obsolete failure overwrote current status.");
            Require(f.Protocol.Contexts.Last().CurrentSnapshotId == "advanced", "Follow-up reused old snapshot.");
        }
    }

    private static async Task Replacement()
    {
        foreach (string change in new[] { "login", "server", "auth", "colony", "game", "generation" })
        {
            var f = new Fixture(); var oldResponse = Signal<ClashOfRimClientNetworkResult<ModPullPendingEventsResponseDto>>();
            var newResponse = Signal<ClashOfRimClientNetworkResult<ModPullPendingEventsResponseDto>>();
            f.Protocol.Queue = _ => oldResponse.Task;
            var old = f.Refresh.StartAutomatic("old"); await f.Protocol.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            f.Replace(change); var newSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            f.Protocol.Queue = _ => { newSent.TrySetResult(); return newResponse.Task; };
            var current = f.Refresh.StartAutomatic("new"); await newSent.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await f.Refresh.StartAutomatic("merged-new");
            f.State.SetQueueStatus("replacement-status");
            oldResponse.SetResult(Queue("old")); await old;
            Require(f.State.QueueStatus == "replacement-status" && f.Automatic.Count == 0 && !current.IsCompleted,
                "Old completion changed replacement work: " + change);
            f.Protocol.Queue = _ => Task.FromResult(Queue("follow-up"));
            newResponse.SetResult(Queue("new")); await current.WaitAsync(TimeSpan.FromSeconds(5));
            Require(f.Automatic.SequenceEqual(new[] { "new", "follow-up" }) && f.Protocol.Calls.Count(c => c == "queue") == 3,
                "Old completion consumed new coalescing state: " + change);
        }
        var manual = new Fixture(); var held = Signal<ClashOfRimClientNetworkResult<ModPullPendingEventsResponseDto>>();
        manual.Protocol.Queue = _ => held.Task; var oldManual = manual.Refresh.StartManual(ClientEventReadKind.Queue);
        await manual.Protocol.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        manual.Replace("login"); manual.Sync.Reset(); manual.Mutations.Begin(LocalMutationKind.Bank, "new");
        manual.Sync.TryAcquireSnapshot(out var next);
        manual.Refresh.Defer("new", "new-deferred");
        held.SetResult(Queue("old")); await oldManual;
        Require(next!.IsCurrent && manual.State.QueueStatus == "new-deferred", "Old manual finalizer released new lease or consumed deferral.");
        next.Dispose(); manual.Mutations.Reset(); manual.Protocol.Queue = _ => Task.FromResult(Queue("new"));
        await manual.Refresh.ResumeDeferred(); Require(manual.Automatic.Single() == "new", "New session lost deferred work.");
    }

    private static async Task DelayedDelivery()
    {
        foreach (string change in new[] { "snapshot", "game", "login" })
        {
            var f = new Fixture(); f.Refresh.AdoptQueue(EventReadTestProtocol.QueueOf("event"));
            await f.Refresh.StartManual(ClientEventReadKind.Details);
            var queued = await f.Ports.Next(); f.Replace(change); queued();
            Require(f.Letters.Count == 0, "Delayed letter crossed read ownership: " + change);
        }
        foreach (string change in new[] { "snapshot", "game", "login" })
        {
            var f = new Fixture(); bool changed = false;
            f.OnNotice = notice =>
            {
                if (changed || notice.Update?.Stage != ClientEventReadStage.Details) return;
                changed = true; f.Replace(change);
            };
            await f.Refresh.StartAutomatic("read");
            Require(f.Automatic.Count == (change == "snapshot" ? 1 : 0)
                && f.Protocol.Calls.Count(c => c == "queue") == (change == "snapshot" ? 2 : 1),
                "Reentrant presentation queued a stale application or lost snapshot follow-up: " + change);
        }
    }

    private static async Task ContinuationAndAdmission()
    {
        var f = new Fixture(); f.Sync.TryBeginManual(out var lease);
        var request = f.Refresh.CaptureQueueRead(); await f.Refresh.ReadQueueAsync(request);
        Require(lease!.IsCurrent && f.Protocol.Calls.Count == 1, "Domain continuation re-acquired or released its caller's lease.");
        await f.Refresh.StartManual(ClientEventReadKind.Refresh);
        Require(f.Protocol.Calls.Count == 1 && f.State.QueueStatus.StartsWith("Busy"), "Busy manual read bypassed shared admission.");
        lease.Dispose();
        request = f.Refresh.CaptureQueueRead(); f.Replace("game"); await f.Refresh.ReadQueueAsync(request);
        Require(f.Protocol.Calls.Count == 1, "Delayed domain continuation borrowed a new game.");
        foreach (string missing in new[] { "identity", "snapshot" })
        {
            var invalid = new Fixture(); if (missing == "identity") invalid.Ports.User = ""; else invalid.Ports.Snapshot = "";
            await invalid.Refresh.StartManual(ClientEventReadKind.Details); await invalid.Refresh.StartAutomatic("read");
            Require(invalid.Protocol.Calls.Count == 0 && !invalid.Sync.IsBusy, "Missing read prerequisites reached protocol.");
        }
    }

    private sealed class Fixture : IClientAutomaticEventApplications, IClientEventLetterDelivery, IClientEventReadPresentation
    {
        internal readonly EventModuleTestPorts Ports = new();
        internal readonly EventReadTestProtocol Protocol = new();
        internal readonly ClientEventInbox Inbox = new();
        internal readonly ClientSynchronizationCoordinator Sync = new();
        internal readonly ClientLocalMutationCoordinator Mutations = new();
        internal readonly ClientSessionLifecycle Session;
        internal readonly ClientEventRefresh Refresh;
        internal readonly ClientEventState State;
        internal readonly List<string> Automatic = new(), Letters = new();
        internal readonly List<ClientEventRefreshNotice> Notices = new();
        internal Action<ClientEventRefreshNotice>? OnNotice;
        internal readonly TaskCompletionSource AutomaticDelivered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Fixture()
        {
            ClientSessionWork.Reset(); Session = new(Ports, Ports, new ClientSessionConnection(Ports));
            var reader = new ClientEventReader(Session, Inbox, Protocol, Ports);
            State = new(reader, this); Refresh = new(Session, reader, Inbox, Sync, Mutations, this, this, this, State);
        }
        internal void Replace(string change)
        {
            switch (change)
            {
                case "snapshot": Ports.Snapshot = "new"; break;
                case "game": Ports.GameOwner = new(); break;
                case "server": Ports.Server += "/new"; break;
                case "auth": Ports.Auth = "new"; break;
                case "colony": Ports.Colony = "new"; break;
                case "generation": ClientSessionWork.Reset(); break;
                case "login": Session.PublishLogin(new() { AuthenticatedUserId = Ports.User, AuthToken = Ports.Auth, SessionId = "new" }); break;
            }
        }
        public string MergedReason => "merged";
        public string ManualDetailsReason => "manual-details";
        public string Format(ClientEventRefreshNotice notice) => notice.Stage == ClientEventRefreshStage.Deferred ? notice.Status! : $"{notice.Stage}:{notice.Kind}:{notice.Admission}";
        public void Diagnose(ClientEventRefreshNotice notice) { Notices.Add(notice); OnNotice?.Invoke(notice); }
        public void QueueAutomatic(IReadOnlyCollection<ModEventDetailDto> details, string reason)
        { Automatic.AddRange(details.Select(d => d.EventId)); AutomaticDelivered.TrySetResult(); }
        public void Post(IReadOnlyCollection<ModEventDetailDto> details, string source) => Letters.AddRange(details.Select(d => d.EventId));
    }
    private static TaskCompletionSource<T> Signal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static ClashOfRimClientNetworkResult<T> Ok<T>(T value) => ClashOfRimClientNetworkResult<T>.Ok(value);
    private static ClashOfRimClientNetworkResult<ModPullPendingEventsResponseDto> Queue(string id) => Ok(new ModPullPendingEventsResponseDto { EventQueue = EventReadTestProtocol.QueueOf(id) });
    private static ClashOfRimClientNetworkResult<ModPullEventDetailsResponseDto> Details(string id) => Ok(new ModPullEventDetailsResponseDto { Events = new() { new() { EventId = id } } });
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
