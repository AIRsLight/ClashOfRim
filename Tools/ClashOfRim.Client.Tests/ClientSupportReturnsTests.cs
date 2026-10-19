using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.ClientSnapshots;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using AIRsLight.ClashOfRim.Modules.Support;
using AIRsLight.ClashOfRim.Modules.Synchronization;

internal static class ClientSupportReturnsTests
{
    internal static void Run() => RunAsync().GetAwaiter().GetResult();
    private static async Task RunAsync()
    {
        var culture = CultureInfo.CurrentCulture;
        try
        {
            foreach (string name in new[] { "en-US", "zh-CN", "tr-TR", "fr-FR" })
            foreach (bool dead in new[] { false, true })
            { CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name); await Success(dead); }
            await Sequence(); await WaitForForeignWork(); await RetryReport(); await RetryConfirmation();
            foreach (Fault fault in Enum.GetValues<Fault>()) if (fault != Fault.None) await Failure(fault);
            foreach (string change in new[] { "user", "colony", "auth", "server", "snapshot", "game", "login", "generation", "reset" })
            foreach (bool queued in new[] { false, true }) await Late(change, queued);
            await Replacement(); await RetryBase(); await Exit(); await Admission(); await Reentry();
        }
        finally { CultureInfo.CurrentCulture = culture; ClientSessionWork.Reset(); }
    }
    private static async Task Success(bool dead)
    {
        using var f = new Fixture(); f.Source.Dead = dead;
        Task first = f.Confirm();
        Require(ReferenceEquals(first, f.Confirm()), "Repeated quest tick enqueued another return.");
        await f.Steps(2);
        Require(f.Source.Prepares == 1 && f.Source.Completions == 0 && f.Captured.SequenceEqual(new[] { "event" }), "Return outbox was not retained in confirmation save.");
        await f.Steps(3); await first;
        Require(f.Source.Prepares == 1 && f.Source.Completions == 1 && f.Ports.Captures == 1 && f.Refreshes == 1
            && f.Contexts.Single().CurrentSnapshotId == "accepted-1" && f.Commands.Single().Payload.Dead == dead
            && (f.Commands[0].Payload.Package is null) == dead && !f.Sync.IsBusy && !f.Mutations.IsPending, "Return lifecycle or dead notification changed.");
        Require(f.Commands[0].IdempotencyKey == "support-finish:alice:colony:event:EarlyReturn", "Return key lost persisted reason.");
    }
    private static async Task Sequence()
    {
        using var f = new Fixture(); f.Sources.Add("second", new Source("second"));
        Task first = f.Confirm(), second = f.Confirm("second");
        await f.Steps(4);
        Require(f.Sources["second"].Prepares == 0 && f.Source.Completions == 0, "Second return overtook final report.");
        await f.Step(); await first; await f.Steps(5); await second;
        Require(f.Contexts.Select(c => c.CurrentSnapshotId).SequenceEqual(new[] { "accepted-1", "accepted-2" })
            && f.Source.Prepares == 1 && f.Sources["second"].Prepares == 1, "Sequential returns reused stale snapshot or mutated twice.");
    }
    private static async Task WaitForForeignWork()
    {
        using var f = new Fixture(); f.Mutations.Begin(LocalMutationKind.Bank, "bank"); f.Sync.TryBeginManual(out var lease);
        Task first = f.Confirm(); await Task.Yield();
        Require(f.Source.Prepares == 0 && lease!.IsCurrent && f.Mutations.Current!.Kind == LocalMutationKind.Bank, "Return disturbed another transaction.");
        lease!.Dispose(); Require(f.Source.Prepares == 0, "Return ignored unresolved mutation."); f.Mutations.Reset();
        await f.Steps(5); await first;
    }
    private static async Task RetryReport()
    {
        using var f = new Fixture { Failure = Fault.Transport }; f.Sources.Add("second", new Source("second"));
        Task first = f.Confirm(), second = f.Confirm("second"); await f.Steps(4); await first; await f.Step();
        Require(f.View.Retry is not null && f.Source.Completions == 0 && f.Sources["second"].Prepares == 0, "Report failure lost pending return.");
        f.Source.Package.Scribe!.Xml = "mutated engine package";
        f.Failure = Fault.None; var retry = f.View.Retry!; retry(); retry();
        await f.Steps(2); await f.Steps(5); await second;
        Require(f.Commands.Count == 3 && ReferenceEquals(f.Commands[0], f.Commands[1]) && f.Contexts[0] == f.Contexts[1]
            && f.Commands[1].Payload.Package!.Scribe!.Xml == "pawn" && f.Source.Prepares == 1 && f.Uploads == 2, "Report retry recaptured pawn, uploaded again or changed command.");
    }
    private static async Task RetryConfirmation()
    {
        using var f = new Fixture { Failure = Fault.Upload };
        Task first = f.Confirm(); await f.Steps(3); await first; await f.Step();
        Require(f.View.Retry is not null && f.Commands.Count == 0 && f.Source.Prepares == 1, "Failed snapshot submitted final report.");
        f.Failure = Fault.None; f.View.Retry!();
        // Capture, publish, confirmation completion, previous dialog close, report.
        await f.Steps(5);
        Require(f.Source.Prepares == 1 && f.Source.Completions == 1 && f.Commands.Count == 1 && f.Uploads == 2, "Confirmation retry repeated domain effects.");
    }
    private static async Task Failure(Fault fault)
    {
        using var f = new Fixture { Failure = fault }; Task first = f.Confirm();
        int steps = fault == Fault.Prepare ? 1 : fault == Fault.Upload ? 3 : fault is Fault.MissingReceipt or Fault.MissingResult or Fault.Complete ? 5 : 4;
        try { await f.Steps(steps); } catch (IOException) when (fault is Fault.Prepare or Fault.Complete) { }
        await first; await f.Step();
        Require(f.Refreshes == 0 && f.Mutations.IsPending && !f.Sync.IsBusy, "Failure cleared pending return: " + fault);
        Require((f.View.Retry is not null) == (fault is Fault.Transport or Fault.Exception or Fault.Upload), "Wrong retry classification: " + fault);
        if (fault != Fault.Complete) Require(f.Source.Completions == 0, "Failed return completed quest.");
    }
    private static async Task Late(string change, bool queued)
    {
        using var f = new Fixture();
        var reply = new TaskCompletionSource<ClashOfRimClientNetworkResult<ModFinishSupportPawnResponseDto>>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!queued) f.Hold = reply;
        Task first = f.Confirm(); await f.Steps(4);
        Action? delayed = queued ? await f.Ports.Next() : null;
        if (!queued) await f.Sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        f.Change(change); if (queued) delayed!(); else reply.SetResult(f.Response()); await first;
        Require(f.Source.Completions == 0 && f.Refreshes == 0, "Late finish crossed ownership: " + change);
    }
    private static async Task Replacement()
    {
        using var f = new Fixture(); f.Sources.Add("second", new Source("second"));
        var reply = new TaskCompletionSource<ClashOfRimClientNetworkResult<ModFinishSupportPawnResponseDto>>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Hold = reply; Task first = f.Confirm(), queued = f.Confirm("second"); await f.Steps(4); await f.Sent.Task;
        f.Change("login"); f.Mutations.Reset(); await queued.WaitAsync(TimeSpan.FromSeconds(5));
        Task current = f.Confirm("second"); await f.Steps(5); await current;
        reply.SetResult(f.Response()); await first;
        Require(f.Source.Completions == 0 && f.Sources["second"].Completions == 1 && f.Refreshes == 1 && !f.Sync.IsBusy, "Old report blocked or disturbed new login.");
    }
    private static async Task RetryBase()
    {
        using var f = new Fixture { Failure = Fault.Transport }; Task first = f.Confirm(); await f.Steps(4); await first; await f.Step();
        f.Ports.Snapshot = "changed"; f.View.Retry!(); await f.Step();
        Require(f.View.Retry is null && f.View.Message == "base-changed" && f.Commands.Count == 1, "Retry rebound to unrelated snapshot.");
    }
    private static async Task Exit()
    {
        using var f = new Fixture { Failure = Fault.Transport }; f.Sources.Add("second", new Source("second"));
        Task first = f.Confirm(), second = f.Confirm("second"); await f.Steps(4); await first; await f.Step(); f.View.Exit!();
        await second.WaitAsync(TimeSpan.FromSeconds(5));
        Require(f.View.Menus == 1 && f.Sources["second"].Prepares == 0, "Exit dispatched pending return.");
    }
    private static async Task Admission()
    {
        foreach (string invalid in new[] { "empty", "missing", "permanent", "walking", "unconfigured", "snapshot" })
        {
            using var f = new Fixture();
            if (invalid == "permanent") f.Source.Permanent = true;
            if (invalid == "walking") f.Source.Ready = false;
            if (invalid == "unconfigured") f.Ports.User = "";
            if (invalid == "snapshot") f.Ports.Snapshot = "";
            Require(f.Returns.ConfirmAsync(invalid == "empty" ? "" : invalid == "missing" ? "missing" : "event", "EarlyReturn") is null
                && f.Source.Prepares == 0 && !f.Sync.IsBusy, "Invalid return entered queue: " + invalid);
        }
        using var walking = new Fixture(); walking.Source.Departing = false; walking.Source.Ready = false;
        Require(walking.Returns.TryStartDeparture("event", "EarlyReturn") && walking.Source.Departures == 1 && !walking.Sync.IsBusy
            && !walking.Mutations.IsPending && !walking.Returns.TryStartDeparture("event", "EarlyReturn"), "Departure duplicated or held admission during walk.");
        walking.Source.Ready = true; Task ready = walking.Confirm(); await walking.Steps(5); await ready;
        using var busy = new Fixture(); busy.Source.Departing = false; busy.Sync.TryBeginManual(out var lease);
        Require(!busy.Returns.TryStartDeparture("event", "EarlyReturn") && busy.Source.Departures == 0, "Departure raced active snapshot."); lease!.Dispose();
    }
    private static async Task Reentry()
    {
        using var f = new Fixture(); f.Sources.Add("second", new Source("second")); Task? next = null;
        f.Returns.Completed += () => { if (next is null) next = f.Confirm("second"); };
        Task first = f.Confirm(); await f.Steps(5); await first; await f.Steps(5); await next!;
        Require(f.Refreshes == 2 && !f.Sync.IsBusy && !f.Mutations.IsPending, "Old finally ended completion callback work.");
    }
    internal enum Fault { None, Transport, Exception, Rejected, Forbidden, MissingReceipt, MissingResult, Prepare, Complete, Upload }
    internal sealed class Source : IClientSupportReturnSource
    {
        private readonly string eventId;
        internal Source(string eventId) => this.eventId = eventId;
        internal bool Dead, Permanent, Departing = true, Ready = true, Prepared;
        internal int Prepares, Completions, Departures;
        internal Fault Failure;
        internal readonly ModPawnExchangePackageDto Package = new() { Reference = new() { GlobalId = "global" }, Scribe = new() { Xml = "pawn" } };
        public ClientSupportReturnView View => new(Permanent, Departing, true, Ready);
        public bool Depart(string reason, Action beforeMutation) { beforeMutation(); Departures++; Departing = true; return Ready; }
        public ClientSupportReturnPayload Prepare(ClashOfRimClientNetworkContext context, string reason, Action beforeMutation)
        {
            beforeMutation(); Prepares++; Prepared = true;
            if (Failure == Fault.Prepare) throw new IOException("partial remove");
            return new(eventId, "global", "支援者", "EarlyReturn", Dead, Dead ? null : Package);
        }
        public void Complete() { Completions++; if (Failure == Fault.Complete) throw new IOException("partial quest completion"); }
    }
    internal sealed class Fixture : IDisposable, IClientSupportReturnRuntime, IClientSupportReturnTransport, IClientSupportPresentation,
        IClientSnapshotTransport, IClientLocalSnapshotPresentation
    {
        internal readonly EventModuleTestPorts Ports = new();
        internal readonly ClientSessionLifecycle Session;
        internal readonly ClientSynchronizationCoordinator Sync = new();
        internal readonly ClientLocalMutationCoordinator Mutations = new();
        internal readonly ClientSupportReturns Returns;
        internal readonly EventModuleFailureView View;
        internal readonly Dictionary<string, Source> Sources = new() { ["event"] = new("event") };
        internal Source Source => Sources["event"];
        internal readonly List<ClientSupportReturnCommand> Commands = new();
        internal readonly List<ClashOfRimClientNetworkContext> Contexts = new();
        internal readonly List<string> Captured = new();
        internal int Uploads, Refreshes;
        internal Fault Failure;
        internal TaskCompletionSource<ClashOfRimClientNetworkResult<ModFinishSupportPawnResponseDto>>? Hold;
        internal readonly TaskCompletionSource Sent = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Fixture()
        {
            ClientSessionWork.Reset(); Session = new(Ports, Ports, new ClientSessionConnection(Ports)); View = new(Ports);
            var transactions = new ClientSnapshotTransaction(Session, Ports, new ClientSnapshotPublication(Ports, Ports));
            var admission = new ClientSnapshotMutationAdmission(Session, Sync, Mutations, transactions);
            var recovery = new ClientSnapshotFailureRecovery(Session, Mutations, Sync, Ports, View);
            var local = new ClientLocalSnapshotConfirmation(Session, Ports, admission, Sync, Mutations, transactions, recovery, new ClientSnapshotUploader(Ports, Ports, this, Sync), this);
            Returns = new(Session, Ports, admission, Mutations, transactions, recovery, local, this, this, this);
            Returns.Completed += () => { Require(!Sync.IsBusy && !Mutations.IsPending, "Return completion retained admission."); Refreshes++; };
            Ports.OnCapture = () => Captured.AddRange(Sources.Where(p => p.Value.Prepared && p.Value.Completions == 0).Select(p => p.Key));
        }
        public IClientSupportReturnSource? Find(string eventId)
        { if (!Sources.TryGetValue(eventId, out var source) || source.Completions != 0) return null; source.Failure = Failure; return source; }
        internal Task Confirm(string eventId = "event") => Returns.ConfirmAsync(eventId, "QuestExpired") ?? throw new InvalidOperationException("Return did not enqueue.");
        internal async Task Step() => (await Ports.Next())();
        internal async Task Steps(int count) { for (int i = 0; i < count; i++) await Step(); }
        public Task<ClashOfRimClientNetworkResult<ModFinishSupportPawnResponseDto>> FinishAsync(ClashOfRimClientNetworkContext context, ClientSupportReturnCommand command, CancellationToken token)
        {
            Require(Sync.IsBusy && Mutations.IsPending && Ports.Published > 0 && Sources[command.Payload.EventId].Completions == 0, "Finish ran before confirmation or outside admission.");
            Commands.Add(command); Contexts.Add(context); Sent.TrySetResult();
            if (Failure == Fault.Exception) throw new IOException("network");
            var hold = Hold; Hold = null; return hold?.Task ?? Task.FromResult(Response());
        }
        internal ClashOfRimClientNetworkResult<ModFinishSupportPawnResponseDto> Response()
        {
            if (Failure is Fault.Transport or Fault.Forbidden)
                return ClashOfRimClientNetworkResult<ModFinishSupportPawnResponseDto>.Failed("HttpError", Failure == Fault.Transport ? "503 unavailable" : "403 forbidden");
            return ClashOfRimClientNetworkResult<ModFinishSupportPawnResponseDto>.Ok(new() { Result = Failure == Fault.MissingResult ? null : new() { Accepted = Failure != Fault.Rejected },
                ReturnEventId = Source.Dead || Failure == Fault.MissingReceipt ? null : "return", NotificationEventId = Failure == Fault.MissingReceipt ? null : "notification" });
        }
        public Task<ClashOfRimClientNetworkResult<ModUploadSnapshotResponseDto>> UploadAsync(ClashOfRimClientNetworkContext context, string key,
            ModSnapshotPackageMetadataDto package, byte[] payload, string? operation, IReadOnlyList<ModSnapshotAchievementCandidateDto> achievements, CancellationToken token)
        {
            Uploads++;
            return Task.FromResult(Failure == Fault.Upload ? ClashOfRimClientNetworkResult<ModUploadSnapshotResponseDto>.Failed("HttpError", "503 unavailable")
                : ClashOfRimClientNetworkResult<ModUploadSnapshotResponseDto>.Ok(new() { Result = new() { Accepted = true }, AcceptedSnapshotId = "accepted-" + Uploads, NextLineageToken = "lineage" }));
        }
        internal void Change(string change)
        {
            switch (change)
            {
                case "user": Ports.User = "new"; break; case "colony": Ports.Colony = "new"; break;
                case "auth": Ports.Auth = "new"; break; case "server": Ports.Server = "http://new"; break;
                case "snapshot": Ports.Snapshot = "new"; break; case "game": Ports.GameOwner = new(); break;
                case "login": Session.PublishLogin(new() { AuthenticatedUserId = Ports.User, AuthToken = Ports.Auth }); break;
                case "generation": ClientSessionWork.Reset(); Sync.Reset(); break; case "reset": Returns.Reset(); break;
            }
        }
        public string Busy => "busy";
        public string BaseSnapshotChanged => "base-changed";
        public string Admission(LocalMutationAdmission admission, LocalMutationState? pending) => admission.ToString();
        public string Text(string key, params (string Name, object? Value)[] args) => key + string.Join("", args.Select(a => ":" + a.Name + "=" + a.Value));
        public void Status(string message) { }
        public void Message(string message, bool rejected) { }
        public void Diagnose(Exception exception) { }
        public ModSnapshotUploadResult Result(ClientSnapshotUploadResult result) => result.Failure is { } failure
            ? ModSnapshotUploadResult.Failed(failure.Code, failure.Message ?? "fallback") : ModSnapshotUploadResult.Ok(result.AcceptedSnapshotId!, result.SourcePath!);
        public string Failed(string status, string? code, string? reason) => "failure:" + code;
        public string Succeeded(string? id) => "success:" + id;
        public string Exception(string status, Exception ex) => "exception:" + ex.Message;
        public void DefaultStatus(string message) { }
        public void Message(string message, bool accepted, bool bestEffort) { }
        public void Diagnose(LocalMutationKind operation, string message, Exception? exception) { }
        public void Dispose() { Returns.Reset(); Sync.Reset(); ClientSessionWork.Reset(); }
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
