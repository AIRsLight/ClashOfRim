using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.ClientSnapshots;
using AIRsLight.ClashOfRim.Modules.Mercenaries;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using AIRsLight.ClashOfRim.Modules.Synchronization;

internal static class LocalSnapshotConfirmationModuleTests
{
    internal static void Run() => RunAsync().GetAwaiter().GetResult();
    private static async Task RunAsync()
    {
        var culture = CultureInfo.CurrentCulture;
        try
        {
            foreach (string name in new[] { "en-US", "zh-CN", "tr-TR", "fr-FR" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
                foreach (LocalMutationKind kind in new[] { LocalMutationKind.Mercenary, LocalMutationKind.Support, LocalMutationKind.Raid, LocalMutationKind.RaidAttacker })
                    await Success(kind);
            }
            foreach (bool bestEffort in new[] { false, true })
            foreach (Fault failure in Enum.GetValues<Fault>()) if (failure != Fault.None) await Failed(failure, bestEffort);
            await Retry(); await RetryOwnership(); await Admission(); await Continuation();
            foreach (string change in new[] { "user", "colony", "auth", "server", "snapshot", "game", "login", "generation" })
            foreach (string phase in new[] { "capture", "transport", "publish", "complete" }) await Late(change, phase);
            foreach (string? id in new string?[] { null, "", "contract" }) await Mercenary(id, null);
            await Mercenary("contract", "explicit-completion");
        }
        finally { CultureInfo.CurrentCulture = culture; ClientSessionWork.Reset(); }
    }
    private static async Task Success(LocalMutationKind kind)
    {
        using var f = new Fixture();
        Task work = f.Flow.StartAsync(f.Request(kind));
        Require(f.Sync.SnapshotInProgress && f.Mutations.Current?.Kind == kind, "Confirmation did not retain admission/pending mutation.");
        await f.Step(); await f.Step();
        Require(f.Completed == 0 && f.Sync.IsBusy && f.Mutations.IsPending, "Confirmation released admission before its domain callback.");
        await f.Step(); await work;
        Require(f.Ports.Published == 1 && f.Ports.GamePublished == 1 && f.Accepted == 1 && f.Completed == 1 && !f.Sync.IsBusy && !f.Mutations.IsPending, "Confirmation did not finish publication and cleanup.");
        Require(f.RemovedRaid == (kind == LocalMutationKind.RaidAttacker) && f.SentAchievements == 1 && f.PublishedAchievements == 1
            && f.UploadKind == ModSnapshotUploadKinds.EventApplicationConfirmation && f.Context!.AuthToken == "auth", "Confirmation changed capture/achievement/wire semantics.");
        Require(f.Status == "success:accepted" && f.LastMessage == "success:accepted", "Confirmation lost domain status overrides.");
    }
    private static async Task Failed(Fault fault, bool bestEffort)
    {
        using var f = new Fixture { Failure = fault };
        if (fault == Fault.Capture) f.Ports.CaptureFailure = new("SaveToMemoryFailed", "capture", SnapshotUploadMessage.SaveFailed);
        if (fault == Fault.Publish) f.Ports.OnPublish = () => throw new IOException("partial publication");
        Task work = f.Flow.StartAsync(f.Request(LocalMutationKind.RaidAttacker, bestEffort));
        await f.Step();
        if (fault == Fault.Publish) await f.Step();
        await f.Step(); await work;
        if (!bestEffort) await f.Step();
        Require(f.Accepted == 0 && f.Completed == (bestEffort ? 1 : 0) && !f.Sync.IsBusy && f.Mutations.IsPending != bestEffort, "Failed confirmation used wrong cleanup policy: " + fault);
        Require(f.Ports.Published == (fault == Fault.Publish ? 1 : 0), "Failure published a new snapshot: " + fault);
        bool retry = fault is Fault.Transport or Fault.TransportException;
        Require((f.View.Retry is not null) == (!bestEffort && retry), "Failure offered an unsafe retry: " + fault);
        if (bestEffort) Require(f.Messages == 1 && f.Diagnosed >= 1 && f.View.Shown == 0, "Best-effort cleanup lost its neutral report.");
    }
    private static async Task Retry()
    {
        using var f = new Fixture { Failure = Fault.Transport };
        Task work = f.Flow.StartAsync(f.Request(LocalMutationKind.Support));
        await f.Step(); await f.Step(); await work; await f.Step();
        Require(f.View.Retry is not null, "Transient confirmation lost retry.");
        f.Failure = Fault.None; var retry = f.View.Retry!; retry(); retry();
        await f.Step(); await f.Step(); await f.Step(); await f.Done.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Require(f.Uploads == 2 && f.Ports.Captures == 2 && f.Accepted == 1 && f.Completed == 1 && !f.Sync.IsBusy && !f.Mutations.IsPending, "Retry repeated domain completion or lost captured request.");
    }
    private static async Task RetryOwnership()
    {
        using var f = new Fixture { Failure = Fault.Transport };
        Task work = f.Flow.StartAsync(f.Request(LocalMutationKind.Support));
        await f.Step(); await f.Step(); await work; await f.Step();
        f.Ports.Snapshot = "new-base"; f.View.Retry!(); await f.Step();
        Require(f.View.Retry is null && f.Status == "base-changed" && f.Uploads == 1, "Retry borrowed new base snapshot.");
        using var busy = new Fixture { Failure = Fault.Transport };
        work = busy.Flow.StartAsync(busy.Request(LocalMutationKind.Support));
        await busy.Step(); await busy.Step(); await work; await busy.Step();
        busy.Sync.TryBeginManual(out var other); busy.View.Retry!(); await busy.Step();
        Require(busy.View.Retry is not null && other!.IsCurrent && busy.Uploads == 1, "Busy retry released another request.");
        other!.Dispose(); var old = busy.View.Retry!; busy.Change("login"); old();
        Require(busy.Uploads == 1, "Old failure window borrowed a new login.");
    }
    private static async Task Admission()
    {
        foreach (string reason in new[] { "unconfigured", "snapshot", "pending", "busy" })
        {
            using var f = new Fixture();
            if (reason == "unconfigured") f.Ports.User = "";
            if (reason == "snapshot") f.Ports.Snapshot = "";
            if (reason == "pending") f.Mutations.Begin(LocalMutationKind.Bank, "bank");
            if (reason == "busy") f.Sync.TryBeginManual(out _);
            await f.Flow.StartAsync(f.Request(LocalMutationKind.Support));
            Require(f.Uploads == 0 && f.Ports.Captures == 0 && f.Completed == 0, "Rejected confirmation performed work.");
            if (reason == "pending") Require(f.Mutations.Current?.Kind == LocalMutationKind.Bank, "Confirmation overwrote another mutation.");
            if (reason == "busy") Require(f.Sync.IsBusy, "Confirmation disposed another lease.");
        }
    }
    private static async Task Continuation()
    {
        using var f = new Fixture();
        ClientSynchronizationCoordinator.Lease? newer = null;
        var request = f.Request(LocalMutationKind.Mercenary) with { OnSuccessOnMainThread = _ =>
        {
            Require(!f.Mutations.IsPending && f.Sync.TryBeginManual(out newer), "Domain continuation started before cleanup.");
            f.Accepted++; throw new InvalidOperationException("domain follow-up");
        } };
        Task work = f.Flow.StartAsync(request);
        await f.Step(); await f.Step(); await f.Step(); await work;
        Require(f.Accepted == 1 && f.Completed == 1 && f.Diagnosed == 1 && newer!.IsCurrent && f.View.Shown == 0, "Old finally/follow-up exception damaged a new operation.");
        newer!.Dispose();
    }
    private static async Task Late(string change, string phase)
    {
        using var f = new Fixture();
        if (phase == "transport") f.Hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task work = f.Flow.StartAsync(f.Request(LocalMutationKind.Mercenary));
        Action? delayed = null;
        if (phase == "capture") delayed = await f.Ports.Next();
        else
        {
            await f.Step();
            if (phase == "transport") await f.Sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
            else
            {
                if (phase == "complete") await f.Step();
                delayed = await f.Ports.Next();
            }
        }
        f.Change(change);
        if (delayed is not null) delayed(); else f.Hold!.SetResult(f.Response());
        await work;
        Require(f.Accepted == 0 && f.Completed == 0 && f.Ports.Published == (phase == "complete" ? 1 : 0), "Old " + change + "/" + phase + " crossed ownership.");
        // Prove a fresh command can normalize and release the obsolete request.
        f.Ports.Snapshot = "base"; f.Mutations.Reset();
        Task replacement = f.Flow.StartAsync(f.Request(LocalMutationKind.Support));
        await f.Step(); await f.Step(); await f.Step(); await replacement;
        Require(f.Accepted == 1 && f.Completed == 1 && !f.Sync.IsBusy, "Obsolete confirmation stranded new work: " + change + "/" + phase);
    }
    private static async Task Mercenary(string? contract, string? key)
    {
        using var f = new Fixture();
        var state = new ClientMercenaryState(f.Session, f, f);
        var module = new ClientMercenaryConfirmation(f.Flow, state, f);
        int reports = 0, refreshes = 0; string? sentId = null, sentKey = null;
        module.ContractCompleted += (id, idempotencyKey) => { reports++; sentId = id; sentKey = idempotencyKey; Require(!f.Sync.IsBusy, "Completion incident retained upload lease."); };
        module.BankRefreshRequested += () => refreshes++;
        Task work = module.ConfirmAsync(contract, key);
        await f.Step(); await f.Step(); await f.Step(); await work;
        bool hasContract = !string.IsNullOrWhiteSpace(contract);
        Require(reports == (hasContract ? 1 : 0) && refreshes == (hasContract ? 0 : 1) && state.Message.StartsWith("StatusConfirmationSucceeded", StringComparison.Ordinal), "Mercenary completion selected the wrong continuation.");
        if (hasContract) Require(sentId == contract && sentKey == (key ?? "mercenary-completed:" + contract), "Mercenary completion lost stable identity/key.");
    }

    private enum Fault { None, Capture, Transport, Forbidden, Rejected, TransportException, Publish }
    private sealed class Fixture : IDisposable, IClientSnapshotGame, IClientSnapshotTransport, IClientLocalSnapshotPresentation, IClientMercenaryHiringPresentation
    {
        internal readonly EventModuleTestPorts Ports = new();
        internal readonly ClientSessionLifecycle Session;
        internal readonly ClientSynchronizationCoordinator Sync = new();
        internal readonly ClientLocalMutationCoordinator Mutations = new();
        internal readonly ClientLocalSnapshotConfirmation Flow;
        internal readonly EventModuleFailureView View;
        internal Fault Failure;
        internal int Uploads, Accepted, Completed, Messages, Diagnosed, SentAchievements, PublishedAchievements;
        internal bool RemovedRaid;
        internal string Status = "", LastMessage = "";
        internal string? UploadKind;
        internal ClashOfRimClientNetworkContext? Context;
        internal TaskCompletionSource<ClashOfRimClientNetworkResult<ModUploadSnapshotResponseDto>>? Hold;
        internal readonly TaskCompletionSource Sent = new(TaskCreationOptions.RunContinuationsAsynchronously), Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Fixture()
        {
            ClientSessionWork.Reset(); Session = new(Ports, Ports, new ClientSessionConnection(Ports)); View = new(Ports);
            var transaction = new ClientSnapshotTransaction(Session, this, new ClientSnapshotPublication(Ports));
            Flow = new(Session, this, new ClientSnapshotMutationAdmission(Session, Sync, Mutations, transaction), Sync, Mutations, transaction,
                new ClientSnapshotFailureRecovery(Session, Mutations, Sync, this, View), new ClientSnapshotUploader(Ports, this, this, Sync), this);
            Flow.Completed += () => { Completed++; Done.TrySetResult(); };
        }
        internal ClientLocalSnapshotRequest Request(LocalMutationKind kind, bool bestEffort = false) => new()
        {
            Operation = kind, UploadingStatus = "uploading", RemoveRaidBattleSessions = kind == LocalMutationKind.RaidAttacker, BestEffort = bestEffort,
            SetStatus = message => Status = message, BuildFailureStatus = r => "failure:" + r.ErrorCode, BuildSuccessStatus = r => "success:" + r.AcceptedSnapshotId,
            BuildExceptionStatus = ex => "exception:" + ex.Message, OnSuccessOnMainThread = _ => { Require(!Sync.IsBusy && !Mutations.IsPending, "Domain success ran before cleanup."); Accepted++; }
        };
        internal async Task Step() => (await Ports.Next())();
        public IClientSnapshotGameSession BindCurrentGame() => new Game(this, Ports.BindCurrentGame());
        private sealed record Game(Fixture F, IClientSnapshotGameSession Inner) : IClientSnapshotGameSession
        {
            public bool HasGame => Inner.HasGame;
            public bool IsCurrent => Inner.IsCurrent;
            public string RuntimeSnapshotId => Inner.RuntimeSnapshotId;
            public void Enqueue(Action action, Action discard) => Inner.Enqueue(action, discard);
            public SnapshotSaveCapture Capture(bool removeRaidBattleSessions)
            { F.RemovedRaid = removeRaidBattleSessions; return Inner.Capture(false) with { Achievements = new[] { new ModSnapshotAchievementCandidateDto { AchievementId = "achievement", EventKey = "event" } } }; }
            public void Publish(string snapshot, string? token, IReadOnlyList<ModSnapshotAchievementCandidateDto> achievements)
            { F.PublishedAchievements = achievements.Count; Inner.Publish(snapshot, token, Array.Empty<ModSnapshotAchievementCandidateDto>()); }
        }
        public Task<ClashOfRimClientNetworkResult<ModUploadSnapshotResponseDto>> UploadAsync(ClashOfRimClientNetworkContext context, string key,
            ModSnapshotPackageMetadataDto package, byte[] payload, string? operation, IReadOnlyList<ModSnapshotAchievementCandidateDto> achievements, CancellationToken token)
        {
            Uploads++; Context = context; UploadKind = package.SnapshotUploadKind; SentAchievements = achievements.Count; Sent.TrySetResult();
            if (Failure == Fault.TransportException) throw new IOException("network");
            return Hold?.Task ?? Task.FromResult(Response());
        }
        internal ClashOfRimClientNetworkResult<ModUploadSnapshotResponseDto> Response() => Failure is Fault.Transport or Fault.Forbidden
            ? ClashOfRimClientNetworkResult<ModUploadSnapshotResponseDto>.Failed("HttpError", Failure == Fault.Transport ? "503 unavailable" : "403 forbidden")
            : ClashOfRimClientNetworkResult<ModUploadSnapshotResponseDto>.Ok(new() { Result = new() { Accepted = Failure != Fault.Rejected }, AcceptedSnapshotId = "accepted", NextLineageToken = "lineage" });
        internal void Change(string change)
        {
            switch (change)
            {
                case "user": Ports.User = "new"; break; case "colony": Ports.Colony = "new"; break;
                case "auth": Ports.Auth = "new"; break; case "server": Ports.Server = "http://new"; break;
                case "snapshot": Ports.Snapshot = "new-base"; break; case "game": Ports.GameOwner = new(); break;
                case "login": Session.PublishLogin(new() { AuthenticatedUserId = Ports.User, AuthToken = Ports.Auth }); break;
                case "generation": ClientSessionWork.Reset(); Sync.Reset(); break;
            }
        }
        public string Idle => "idle";
        public string Busy => "busy";
        public string BaseSnapshotChanged => "base-changed";
        public string Admission(LocalMutationAdmission admission, LocalMutationState? pending) => admission.ToString();
        public ModSnapshotUploadResult Result(ClientSnapshotUploadResult result) => result.Failure is { } failure
            ? ModSnapshotUploadResult.Failed(failure.Code, failure.Message ?? "fallback") : ModSnapshotUploadResult.Ok(result.AcceptedSnapshotId!, result.SourcePath!);
        public string Failed(string status, string? code, string? message) => "failure:" + code;
        public string Succeeded(string? id) => "success:" + id;
        public string Exception(string status, Exception ex) => "exception:" + ex.Message;
        public void DefaultStatus(string message) => Status = message;
        public void Message(string message, bool accepted, bool bestEffort) { LastMessage = message; Messages++; }
        public void Diagnose(LocalMutationKind operation, string message, Exception? ex) => Diagnosed++;
        public string Text(string key, params (string Name, object? Value)[] args) => key + string.Join("", args.Select(a => ":" + a.Name + "=" + a.Value));
        public void Message(string message, bool rejected) { }
        public void Diagnose(Exception exception) { }
        public void Dispose() { Sync.Reset(); ClientSessionWork.Reset(); }
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
