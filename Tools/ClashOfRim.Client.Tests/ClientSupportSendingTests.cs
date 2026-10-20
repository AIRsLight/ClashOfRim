using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using AIRsLight.ClashOfRim.Modules.Support;
using AIRsLight.ClashOfRim.Modules.Synchronization;

internal static class ClientSupportSendingTests
{
    internal static void Run() => RunAsync().GetAwaiter().GetResult();
    private static async Task RunAsync()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            foreach (string culture in new[] { "en-US", "zh-CN", "tr-TR", "fr-FR" })
            foreach (bool permanent in new[] { false, true })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                var f = new Fixture(); Task work = f.Start(permanent);
                await f.Submitted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                f.Package.Scribe!.Xml = "changed"; f.Package.Reference!.Metadata["state"] = "changed";
                Check(f.Captured == 1 && f.Removed == 1 && f.Ports.Captures == 1 && f.Sync.IsBusy && f.Mutations.IsPending, "Support preparation lost its lease.");
                (await f.Ports.Next())(); await work;
                Check(f.Sent!.Pawn.Package.Scribe!.Xml == "original" && f.Sent.Pawn.Package.Reference!.Metadata["state"] == "original"
                    && f.Sent.Selection.Permanent == permanent && f.Sent.Selection.Days == 7 && f.Sent.Target.AutoReturnOnSettlement
                    && f.Context!.CurrentSnapshotId == "base" && f.Completed == 1 && f.Ports.Snapshot == f.Snapshot!.SnapshotId, "Captured intent or publication changed.");
            }
            foreach (bool build in new[] { false, true })
            {
                var f = new Fixture { Fault = build ? "" : "network" };
                if (build) f.Ports.CaptureFailure = new("capture", "failed", SnapshotUploadMessage.BuildFailed);
                await f.Start(); (await f.Ports.Next())();
                Check(f.Failure.Retry is not null && f.Mutations.IsPending && !f.Sync.IsBusy, "Recoverable failure lost pending state.");
                var command = f.Sent; var snapshot = f.Snapshot; var payload = f.Payload; var retry = f.Failure.Retry!;
                f.Fault = ""; f.Ports.CaptureFailure = null; retry(); (await f.Ports.Next())(); await f.Done.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Check(f.Captured == 1 && f.Removed == 1 && f.Ports.Captures == (build ? 2 : 1), "Retry captured or removed the pawn twice.");
                if (!build) Check(ReferenceEquals(command, f.Sent) && ReferenceEquals(snapshot, f.Snapshot) && ReferenceEquals(payload, f.Payload), "Retry changed intent or bytes.");
                int count = f.Submissions; retry(); Check(f.Submissions == count, "Consumed failure window submitted twice.");
            }
            foreach (string fault in new[] { "rejected", "permanent", "missing-event", "wrong-snapshot", "missing-result", "remove-throw", "remove-partial" })
            {
                var f = new Fixture { Fault = fault }; Task work = f.Start();
                if (fault is "missing-event" or "wrong-snapshot" or "missing-result") (await f.Ports.Next())();
                await work; (await f.Ports.Next())();
                Check(f.Failure.Retry is null && f.Mutations.IsPending && !f.Sync.IsBusy && f.Completed == 0 && f.Ports.Published == 0, "Unsafe failure allowed replay/success: " + fault);
            }
            foreach (string rejection in new[] { "capture", "capture-throw", "remove-before", "target", "pending", "busy" })
            {
                var f = new Fixture { Fault = rejection };
                if (rejection == "target") f.Target = f.Target with { MapId = "" };
                if (rejection == "pending") f.Mutations.Begin(LocalMutationKind.Bank, "bank");
                if (rejection == "busy") f.Sync.TryBeginManual(out _);
                await f.Start();
                Check(f.Removed == 0 && f.Submissions == 0 && f.Ports.Captures == 0, "Rejected support mutated source: " + rejection);
                Check(f.Mutations.IsPending == (rejection == "pending"), "Pre-mutation failure invented or cleared pending state."); f.Sync.Reset();
            }
            foreach (string change in new[] { "snapshot", "server", "auth", "user", "colony", "game", "login", "generation" })
            {
                var f = new Fixture { Hold = new(TaskCreationOptions.RunContinuationsAsynchronously) };
                Task work = f.Start(); await f.Submitted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Change(f, change); f.Sync.Reset(); Check(f.Sync.TryBeginManual(out var replacement), "Replacement lease unavailable.");
                f.Hold.SetResult(f.Reply()); await work;
                Check(f.Ports.Published == 0 && replacement!.IsCurrent && f.Completed == 0, "Late response touched new work: " + change); replacement!.Dispose();
                var queued = new Fixture(); work = queued.Start(); var deliver = await queued.Ports.Next(); Change(queued, change); deliver(); await work;
                Check(queued.Ports.Published == 0 && queued.Completed == 0, "Queued response crossed ownership: " + change);
                var capture = new Fixture(); capture.AfterCapture = () => Change(capture, change); await capture.Start();
                Check(capture.Removed == 0 && capture.Submissions == 0, "Owner changed during capture before removal: " + change);
                var ui = new Fixture(); var scope = ui.Sending.CaptureScope(); Change(ui, change); bool invoked = false;
                ui.Sending.InScope(scope, () => invoked = true);
                Check(invoked == (change == "snapshot"), "UI scope borrowed another owner or rejected normal snapshot advance: " + change);
            }
            var changedBase = new Fixture { Fault = "network" }; await changedBase.Start(); (await changedBase.Ports.Next())();
            changedBase.Ports.Snapshot = "other"; changedBase.Failure.Retry!(); (await changedBase.Ports.Next())();
            Check(changedBase.Failure.Retry is null && changedBase.Submissions == 1, "Retry changed its base.");
            var partial = new Fixture(); partial.Ports.OnPublish = () => throw new InvalidOperationException("partial");
            Task publishing = partial.Start();
            try { (await partial.Ports.Next())(); } catch (InvalidOperationException ex) when (ex.Message == "partial") { }
            await publishing; (await partial.Ports.Next())();
            Check(partial.Ports.Published == 1 && partial.Mutations.IsPending && partial.Failure.Retry is null, "Partial publication allowed resending.");
            var changedPublish = new Fixture(); changedPublish.Ports.OnPublish = () => changedPublish.Ports.Snapshot = "unrelated";
            Task changing = changedPublish.Start(); (await changedPublish.Ports.Next())(); await changing;
            Check(changedPublish.Completed == 0 && changedPublish.Mutations.IsPending, "Completion cleared another snapshot's pending work.");
            var reentrant = new Fixture(); Task? next = null;
            reentrant.OnCompleted = () => { reentrant.Hold = new(TaskCreationOptions.RunContinuationsAsynchronously); next = reentrant.Start(); };
            Task first = reentrant.Start(); (await reentrant.Ports.Next())(); await first;
            Check(reentrant.Sync.IsBusy && reentrant.Mutations.IsPending, "Old finally ended new work.");
            reentrant.OnCompleted = null; reentrant.Hold!.SetResult(reentrant.Reply()); (await reentrant.Ports.Next())(); await next!;
            Check(reentrant.Completed == 2, "Reentrant support did not finish.");
        }
        finally { CultureInfo.CurrentCulture = previous; ClientSessionWork.Reset(); }
    }
    private static void Change(Fixture f, string kind)
    {
        switch (kind)
        {
            case "snapshot": f.Ports.Snapshot = "newer"; break;
            case "server": f.Ports.Server = "http://new"; break;
            case "auth": f.Ports.Auth = "other"; break;
            case "user": f.Ports.User = "other"; break;
            case "colony": f.Ports.Colony = "other"; break;
            case "game": f.Ports.GameOwner = new(); break;
            case "login": f.Session.PublishLogin(new() { AuthenticatedUserId = "alice", AuthToken = "auth", SessionId = "new-login" }); break;
            case "generation": ClientSessionWork.Reset(); break;
        }
    }
    private sealed class Fixture : IClientSupportSource, IClientSupportSendingTransport, IClientSupportPresentation
    {
        internal readonly EventModuleTestPorts Ports = new();
        internal readonly ClientSynchronizationCoordinator Sync = new();
        internal readonly ClientLocalMutationCoordinator Mutations = new();
        internal readonly EventModuleFailureView Failure;
        internal readonly ClientSessionLifecycle Session;
        internal readonly ClientSupportSending Sending;
        internal ClientSupportTarget Target = new("bob", "there", "map", "world", 0, 0, "目标", true);
        internal readonly ModPawnExchangePackageDto Package = new() { Reference = new() { GlobalId = "pawn", Metadata = new() { ["state"] = "original" } }, Scribe = new() { Xml = "original" } };
        internal string Fault = "";
        internal int Captured, Removed, Submissions, Completed;
        internal Action? AfterCapture, OnCompleted;
        internal ClientSupportCommand? Sent;
        internal ClashOfRimClientNetworkContext? Context;
        internal ModSnapshotPackageMetadataDto? Snapshot;
        internal byte[]? Payload;
        internal TaskCompletionSource<ClashOfRimClientNetworkResult<ModEventCreationResponseDto>>? Hold;
        internal readonly TaskCompletionSource Submitted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int caller;
        internal Fixture()
        {
            ClientSessionWork.Reset(); Session = new(Ports, Ports, new ClientSessionConnection(Ports)); Failure = new(Ports);
            var transactions = new ClientSnapshotTransaction(Session, Ports, new ClientSnapshotPublication(Ports));
            Sending = new(Session, Ports, new ClientSnapshotMutationAdmission(Session, Sync, Mutations, transactions), Sync, Mutations,
                transactions, new ClientSnapshotFailureRecovery(Session, Mutations, Sync, Ports, Failure), this, this);
            Sending.Completed += () => { Check(!Sync.IsBusy && !Mutations.IsPending, "Completion preceded cleanup."); Completed++; Done.TrySetResult(); OnCompleted?.Invoke(); };
        }
        internal Task Start(bool permanent = false)
        { caller = Environment.CurrentManagedThreadId; return Sending.SendAsync(this, Target, new(7, permanent)); }
        public ClientSupportCapture Capture(ClashOfRimClientNetworkContext context, ClientSupportTarget target)
        {
            Check(Environment.CurrentManagedThreadId == caller, "Capture left the game thread."); Captured++; AfterCapture?.Invoke();
            if (Fault == "capture-throw") throw new InvalidOperationException("capture");
            return Fault == "capture" ? new(null, "capture rejected") : new(new("pawn", "支援者", 0, "Caravan1", Package));
        }
        public string? Remove(ClientSupportCommand command, Action beforeMutation)
        {
            if (Fault == "remove-before") return "source moved";
            Check(Mutations.IsPending, "Removal preceded mutation registration."); beforeMutation(); Removed++;
            if (Fault == "remove-throw") throw new InvalidOperationException("partial remove");
            return Fault == "remove-partial" ? "partial remove" : null;
        }
        public Task<ClashOfRimClientNetworkResult<ModEventCreationResponseDto>> SendAsync(ClashOfRimClientNetworkContext context, ClientSupportCommand command,
            ModSnapshotPackageMetadataDto snapshot, byte[] payload, CancellationToken token)
        {
            Context = context; Sent = command; Snapshot = snapshot; Payload = payload; Submissions++; Submitted.TrySetResult();
            return Hold?.Task ?? Task.FromResult(Fault == "network" ? ClashOfRimClientNetworkResult<ModEventCreationResponseDto>.Failed("HttpRequestException", "offline")
                : Fault == "permanent" ? ClashOfRimClientNetworkResult<ModEventCreationResponseDto>.Failed("HttpError", "400 Bad Request") : Reply());
        }
        internal ClashOfRimClientNetworkResult<ModEventCreationResponseDto> Reply() => ClashOfRimClientNetworkResult<ModEventCreationResponseDto>.Ok(new()
        { Result = Fault == "missing-result" ? null : new() { Accepted = Fault != "rejected" }, EventId = Fault == "missing-event" ? null : "support",
            AppliedSnapshotId = Fault == "wrong-snapshot" ? "other" : Snapshot!.SnapshotId, NextLineageToken = "next" });
        public string Busy => "busy";
        public string BaseSnapshotChanged => "base changed";
        public string Admission(LocalMutationAdmission admission, LocalMutationState? pending) => admission.ToString();
        public string Text(string key, params (string Name, object? Value)[] arguments) => key;
        public void Status(string message) { }
        public void Message(string message, bool rejected) { }
        public void Diagnose(Exception exception) { }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
