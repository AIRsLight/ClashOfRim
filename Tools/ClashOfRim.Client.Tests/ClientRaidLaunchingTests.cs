using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Maps;
using AIRsLight.ClashOfRim.Modules.Raids;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using AIRsLight.ClashOfRim.Modules.Synchronization;
using AIRsLight.ClashOfRim.RemoteMaps;

internal static class ClientRaidLaunchingTests
{
    internal static void Run() => RunAsync().GetAwaiter().GetResult();
    private static async Task RunAsync()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            foreach (string language in new[] { "en-US", "zh-CN", "fr-FR", "tr-TR" })
            foreach (string deadlines in new[] { "valid", "missing", "invalid" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(language);
                var f = new Fixture { DeadlineKind = deadlines }; Task work = f.Start();
                f.Target.OwnerUserId = "changed"; f.Target.MapId = "other"; f.Target.Tile++;
                await f.Open();
                Check(f.Sync.IsBusy && f.Mutations.IsPending && f.Ports.Published == 0, "Launch lost admission before receipt publication.");
                (await f.Ports.Next())(); await work;
                Check(f.Completed == 1 && f.Applied == 1 && f.Captured == 1 && f.Opened == 1 && f.Prepared == 1 && f.Downloaded == 1
                    && f.Ports.Captures == 1 && f.Ports.Published == 1 && f.Ports.GamePublished == 1 && !f.Sync.IsBusy && !f.Mutations.IsPending,
                    "Launch repeated an effect or omitted completion.");
                Check(f.Sent!.Target == new ClientRaidTarget("defender", "there", "world", "map", 7)
                    && f.Sent.DefenderSnapshotId == "downloaded" && f.Sent.PreparationId == "preparation" && f.Sent.GuardDeploymentId == "guard"
                    && f.Context!.UserId == "alice" && f.Context.CurrentSnapshotId == "base" && f.Context.AuthToken == "auth", "Launch drifted from captured authority/target.");
                Check((f.Deadlines is not null) == (deadlines == "valid"), "Partial/missing deadline replaced local fallback.");
                if (f.Deadlines is { } time) Check(time == new ClientRaidServerDeadlines(Utc(0), Utc(15), Utc(17)), "Server time parsed with local culture/time zone.");
            }
            foreach (bool build in new[] { false, true })
            {
                var f = new Fixture { Fault = build ? "" : "network" };
                if (build) f.Ports.CaptureFailure = new("capture", "failure", SnapshotUploadMessage.SaveFailed);
                Task work = f.Start(); await f.Open(); await work; (await f.Ports.Next())();
                Check(f.Failure.Retry is not null && f.Mutations.IsPending && !f.Sync.IsBusy, "Recoverable failure lost pending intent.");
                var command = f.Sent; var snapshot = f.Snapshot; var payload = f.Payload; var context = f.Context;
                f.Fault = ""; f.Ports.CaptureFailure = null; var retry = f.Failure.Retry!; retry();
                (await f.Ports.Next())(); await f.Done.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Check(f.Captured == 1 && f.Prepared == 1 && f.Downloaded == 1 && f.Opened == 1 && f.Ports.Captures == (build ? 2 : 1), "Retry repeated source capture, preparation, download or movement.");
                if (!build) Check(ReferenceEquals(command, f.Sent) && ReferenceEquals(snapshot, f.Snapshot)
                    && ReferenceEquals(payload, f.Payload) && ReferenceEquals(context, f.Context), "Retry changed intent, identity or snapshot bytes.");
                int sent = f.Submissions; retry(); Check(f.Submissions == sent, "Consumed window retried twice.");
            }
            foreach (string fault in new[] { "rejected", "permanent", "missing-result", "wrong-event", "missing-event", "wrong-snapshot", "missing-snapshot", "no-battle", "open-failed" })
            {
                var f = new Fixture { Fault = fault }; Task work = f.Start(); await f.Open();
                if (fault is "missing-result" or "wrong-event" or "missing-event" or "wrong-snapshot" or "missing-snapshot") (await f.Ports.Next())();
                await work; (await f.Ports.Next())();
                Check(f.Failure.Retry is null && f.Mutations.IsPending && !f.Sync.IsBusy && f.Ports.Published == 0 && f.Applied == 0 && f.Completed == 0, "Unsafe failure completed or allowed replay: " + fault);
                if (fault is "no-battle" or "open-failed") Check(f.Submissions == 0, "Missing battle submitted a start snapshot.");
            }
            foreach (string fault in new[] { "pvp", "target", "pending", "busy", "capture", "capture-throw", "prepare", "entry" })
            {
                var f = new Fixture { Fault = fault };
                if (fault == "target") f.Target.MapId = "";
                if (fault == "pending") f.Mutations.Begin(LocalMutationKind.Bank, "bank");
                if (fault == "busy") f.Sync.TryBeginManual(out _);
                Task work = f.Start();
                if (fault is "prepare" or "entry") (await f.Ports.Next())();
                if (fault == "entry") (await f.NextLong())();
                await work;
                Check(f.Opened == 0 && f.Ports.Captures == 0 && f.Submissions == 0 && f.Mutations.IsPending == (fault == "pending"), "Pre-mutation refusal touched game or pending state: " + fault);
                f.Sync.Reset();
            }
            foreach (string change in new[] { "server", "auth", "user", "colony", "snapshot", "game", "login", "generation" })
            foreach (string stage in new[] { "capture", "prepare", "game-queue", "long-queue", "open", "snapshot", "http", "publication" })
            {
                var f = new Fixture(); Action changeOwner = () => Change(f, change);
                if (stage == "capture") f.AfterCapture = changeOwner;
                if (stage == "prepare") f.PrepareHold = new(TaskCreationOptions.RunContinuationsAsynchronously);
                if (stage == "open") f.AfterOpen = changeOwner;
                if (stage == "snapshot") f.Ports.OnCapture = changeOwner;
                if (stage == "http") f.Hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
                Task work = f.Start(); ClientSynchronizationCoordinator.Lease? replacement = null;
                if (stage == "prepare")
                { await f.Preparing.Task.WaitAsync(TimeSpan.FromSeconds(5)); changeOwner(); f.PrepareHold!.SetResult(f.PrepareReply()); }
                else if (stage == "game-queue") { var callback = await f.Ports.Next(); changeOwner(); callback(); }
                else if (stage == "long-queue") { (await f.Ports.Next())(); var callback = await f.NextLong(); changeOwner(); callback(); }
                else if (stage is "open" or "snapshot" or "http" or "publication")
                {
                    await f.Open();
                    if (stage == "http")
                    {
                        await f.Submitted.Task.WaitAsync(TimeSpan.FromSeconds(5)); changeOwner();
                        f.Sync.Reset(); Check(f.Sync.TryBeginManual(out replacement), "Replacement admission unavailable."); f.Hold!.SetResult(f.Reply());
                    }
                    if (stage == "publication") { var callback = await f.Ports.Next(); changeOwner(); callback(); }
                }
                await work.WaitAsync(TimeSpan.FromSeconds(5));
                Check(f.Ports.Published == 0 && f.Applied == 0 && f.Completed == 0 && f.Failure.Shown == 0, "Late launch crossed ownership: " + stage + "/" + change);
                if (replacement is not null) { Check(replacement.IsCurrent, "Old launch released replacement admission."); replacement.Dispose(); }
            }
            var changedBase = new Fixture { Fault = "network" }; var failed = changedBase.Start(); await changedBase.Open(); await failed; (await changedBase.Ports.Next())();
            changedBase.Ports.Snapshot = "other"; changedBase.Failure.Retry!(); (await changedBase.Ports.Next())();
            Check(changedBase.Submissions == 1 && changedBase.Failure.Retry is null, "Retry rebound to a newer base.");
            var partial = new Fixture(); partial.Ports.OnPublish = () => throw new InvalidOperationException("partial publication");
            Task publishing = partial.Start(); await partial.Open();
            try { (await partial.Ports.Next())(); } catch (InvalidOperationException) { }
            await publishing; (await partial.Ports.Next())();
            Check(partial.Ports.Published == 1 && partial.Applied == 0 && partial.Mutations.IsPending && partial.Failure.Retry is null, "Partial publication enabled resubmission.");
            var changedPublication = new Fixture(); changedPublication.Ports.OnPublish = () => changedPublication.Ports.Snapshot = "unrelated";
            Task changing = changedPublication.Start(); await changedPublication.Open(); (await changedPublication.Ports.Next())(); await changing;
            Check(changedPublication.Applied == 0 && changedPublication.Completed == 0 && changedPublication.Mutations.IsPending, "Publication cleared unrelated snapshot state.");
            var lostBattle = new Fixture(); Task losing = lostBattle.Start(); await lostBattle.Open(); var receipt = await lostBattle.Ports.Next(); lostBattle.Fault = "no-battle"; receipt(); await losing; (await lostBattle.Ports.Next())();
            Check(lostBattle.Ports.Published == 0 && lostBattle.Failure.Retry is null, "Receipt published after the local battle disappeared.");
            var discarded = new Fixture { DiscardOpen = true }; Task discarding = discarded.Start(); await discarded.Open(); await discarding;
            Check(discarded.Opened == 0 && !discarded.Sync.IsBusy && !discarded.Mutations.IsPending, "Discarded long event stranded admission.");
            foreach (bool beforeOpen in new[] { true, false })
            {
                var f = new Fixture { Fault = beforeOpen ? "enqueue-throw" : "open-throw" }; Task work = f.Start();
                if (beforeOpen)
                { try { (await f.Ports.Next())(); } catch (InvalidOperationException) { } }
                else { try { await f.Open(); } catch (InvalidOperationException) { } }
                (await f.Ports.Next())(); await work;
                if (!beforeOpen) (await f.Ports.Next())();
                Check(!f.Sync.IsBusy && f.Submissions == 0 && f.Mutations.IsPending == !beforeOpen
                    && f.Failure.Retry is null, "Open/queue exception released unconfirmed changes or stranded admission.");
            }
            var reentrant = new Fixture(); ClientSynchronizationCoordinator.Lease? nextLease = null;
            reentrant.OnCompleted = () => { Check(reentrant.Sync.TryBeginManual(out nextLease), "Completion could not start new work."); reentrant.Mutations.Begin(LocalMutationKind.Bank, "new"); };
            Task completing = reentrant.Start(); await reentrant.Open(); (await reentrant.Ports.Next())(); await completing;
            Check(nextLease!.IsCurrent && reentrant.Mutations.Current?.Kind == LocalMutationKind.Bank, "Old finally cleared reentrant work."); nextLease.Dispose();
        }
        finally { CultureInfo.CurrentCulture = previous; ClientSessionWork.Reset(); }
    }

    private static long Utc(int minute) => new DateTime(2026, 10, 18, 1, minute, 0, DateTimeKind.Utc).Ticks;
    private static void Change(Fixture f, string kind)
    {
        switch (kind)
        {
            case "server": f.Ports.Server = "http://new"; break;
            case "auth": f.Ports.Auth = "new"; break;
            case "user": f.Ports.User = "new"; break;
            case "colony": f.Ports.Colony = "new"; break;
            case "snapshot": f.Ports.Snapshot = "new"; break;
            case "game": f.Ports.GameOwner = new(); break;
            case "login": f.Session.PublishLogin(new() { AuthenticatedUserId = "alice", AuthToken = "auth", SessionId = "new-login" }); break;
            case "generation": ClientSessionWork.Reset(); break;
        }
    }

    private sealed class Fixture : IClientRaidLaunchRuntime, IClientRaidLaunchTransport, IClientRaidPreparationTransport, IClientRemoteSnapshotTransport, IClientRaidLaunchPresentation
    {
        internal readonly EventModuleTestPorts Ports = new();
        internal readonly ClientSynchronizationCoordinator Sync = new();
        internal readonly ClientLocalMutationCoordinator Mutations = new();
        internal readonly EventModuleFailureView Failure;
        internal readonly ClientSessionLifecycle Session;
        internal readonly ClientRaidLaunching Launch;
        internal readonly ModWorldMapMarkerDto Target = new() { OwnerUserId = "defender", OwnerColonyId = "there", WorldObjectId = "world", MapId = "map", Tile = 7, TileLayerId = 2 };
        internal string Fault = "", DeadlineKind = "valid";
        internal int Captured, Prepared, Downloaded, Opened, Submissions, Applied, Completed;
        internal bool DiscardOpen;
        internal Action? AfterCapture, AfterOpen, OnCompleted;
        internal ClientRaidLaunchCommand? Sent;
        internal ClashOfRimClientNetworkContext? Context;
        internal ModSnapshotPackageMetadataDto? Snapshot;
        internal byte[]? Payload;
        internal ClientRaidServerDeadlines? Deadlines;
        internal TaskCompletionSource<ClashOfRimClientNetworkResult<ModPrepareRaidResponseDto>>? PrepareHold;
        internal TaskCompletionSource<ClashOfRimClientNetworkResult<ModEventCreationResponseDto>>? Hold;
        internal readonly TaskCompletionSource Preparing = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Submitted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentQueue<Action> longEvents = new();
        private readonly SemaphoreSlim available = new(0);
        private int gameThread;
        internal Fixture()
        {
            ClientSessionWork.Reset(); Session = new(Ports, Ports, new ClientSessionConnection(Ports)); Failure = new(Ports);
            var transactions = new ClientSnapshotTransaction(Session, Ports, new ClientSnapshotPublication(Ports, Ports));
            Launch = new(Session, new ClientSnapshotMutationAdmission(Session, Sync, Mutations, transactions), Sync, Mutations,
                transactions, new ClientSnapshotFailureRecovery(Session, Mutations, Sync, Ports, Failure), new(this, this), this, this, () => Fault != "pvp");
            Launch.Completed += () => { Check(!Sync.IsBusy && !Mutations.IsPending, "Completion preceded release."); Completed++; Done.TrySetResult(); OnCompleted?.Invoke(); };
        }
        internal Task Start() { gameThread = Environment.CurrentManagedThreadId; return Launch.StartAsync(this, Target); }
        internal async Task Open() { (await Ports.Next())(); var callback = await NextLong(); gameThread = Environment.CurrentManagedThreadId; callback(); }
        internal async Task<Action> NextLong()
        { if (!await available.WaitAsync(TimeSpan.FromSeconds(5)) || !longEvents.TryDequeue(out var action)) throw new Exception("Missing long event."); return action; }
        public bool TryCapture(ClashOfRimClientNetworkContext context, ModWorldMapMarkerDto target, out ClientRaidForceManifest? force, out string failure)
        {
            Check(Environment.CurrentManagedThreadId == gameThread, "Force capture left game thread."); Captured++; AfterCapture?.Invoke();
            if (Fault == "capture-throw") throw new InvalidOperationException("capture");
            force = new("caravan", new[] { "pawn" }, new[] { new ModThingReferenceDto { GlobalKey = "cargo", Metadata = new() { ["state"] = "complex" } } });
            failure = "capture"; return Fault != "capture";
        }
        public string? EntryFailure() => Fault == "entry" ? "moved" : null;
        public void EnqueueOpen(Action action, Action discarded)
        { if (Fault == "enqueue-throw") throw new InvalidOperationException("enqueue"); longEvents.Enqueue(DiscardOpen ? discarded : action); available.Release(); }
        public string? Open(ClashOfRimClientNetworkContext context, ModWorldMapMarkerDto target, ClientPreparedRaid prepared)
        {
            Check(Environment.CurrentManagedThreadId == gameThread && Mutations.IsPending && Sync.IsBusy, "Open ran outside game admission/mutation.");
            Check(target.OwnerUserId == "defender" && target.MapId == "map" && target.Tile == 7 && target.TileLayerId == 2
                && prepared.EventId == "raid" && prepared.PreparationId == "preparation", "Open lost captured target/authorization.");
            Opened++; AfterOpen?.Invoke();
            if (Fault == "open-throw") throw new InvalidOperationException("partial open");
            return Fault == "open-failed" ? "partial open" : null;
        }
        public bool HasBattle => Opened == 1 && Fault != "no-battle";
        public void ApplyReceipt(string snapshot, ClientRaidServerDeadlines? deadlines)
        { Check(snapshot == Snapshot!.SnapshotId && Ports.Published == 1 && Sync.IsBusy, "Battle receipt preceded publication or lost admission."); Applied++; Deadlines = deadlines; }
        public Task<ClashOfRimClientNetworkResult<ModPrepareRaidResponseDto>> PrepareAsync(ClashOfRimClientNetworkContext context, ClientRaidPreparationCommand command, CancellationToken token)
        { Prepared++; Preparing.TrySetResult(); return PrepareHold?.Task ?? Task.FromResult(PrepareReply()); }
        internal ClashOfRimClientNetworkResult<ModPrepareRaidResponseDto> PrepareReply() => ClashOfRimClientNetworkResult<ModPrepareRaidResponseDto>.Ok(new()
        { Result = new() { Accepted = Fault != "prepare" }, RaidEventId = "raid", RaidPreparationId = "preparation", GuardDeployment = new() { ContractId = "guard" } });
        public Task<RemoteSnapshotDownloadResult> DownloadAsync(ClashOfRimClientNetworkContext context, ClientSnapshotDownloadTarget target, CancellationToken token, Action<string>? payloadStarting = null)
        {
            Downloaded++; Check(target == new ClientSnapshotDownloadTarget("defender", "there", "preparation", ClientRemoteMapModes.RaidPreparation), "Download authorization changed.");
            return Task.FromResult(new RemoteSnapshotDownloadResult(ClashOfRimClientNetworkResult<ModDownloadLatestSnapshotResponseDto>.Ok(new()
            { Result = new() { Accepted = true }, SnapshotId = "downloaded", Package = new() { NextLineageToken = "defender-lineage" } }), ClashOfRimClientNetworkResult<byte[]>.Ok(new byte[] { 1, 2, 3 })));
        }
        public Task<ClashOfRimClientNetworkResult<ModEventCreationResponseDto>> SubmitAsync(ClashOfRimClientNetworkContext context, ClientRaidLaunchCommand command,
            ModSnapshotPackageMetadataDto snapshot, byte[] payload, CancellationToken token)
        {
            Check(Sync.SnapshotInProgress && Mutations.IsPending, "HTTP submission lost snapshot admission.");
            Context = context; Sent = command; Snapshot = snapshot; Payload = payload; Submissions++; Submitted.TrySetResult();
            return Hold?.Task ?? Task.FromResult(Fault == "network" ? ClashOfRimClientNetworkResult<ModEventCreationResponseDto>.Failed("HttpRequestException", "offline")
                : Fault == "permanent" ? ClashOfRimClientNetworkResult<ModEventCreationResponseDto>.Failed("HttpError", "400 Bad Request") : Reply());
        }
        internal ClashOfRimClientNetworkResult<ModEventCreationResponseDto> Reply() => ClashOfRimClientNetworkResult<ModEventCreationResponseDto>.Ok(new()
        {
            Result = Fault == "missing-result" ? null : new() { Accepted = Fault != "rejected" },
            EventId = Fault == "missing-event" ? null : Fault == "wrong-event" ? "other" : "raid",
            AppliedSnapshotId = Fault == "missing-snapshot" ? null : Fault == "wrong-snapshot" ? "other" : Snapshot!.SnapshotId,
            NextLineageToken = "next", RaidStartedAtUtc = DeadlineKind == "missing" ? null : "2026-10-18T09:00:00+08:00",
            RaidDeadlineUtc = DeadlineKind == "invalid" ? "invalid" : "2026-10-18T01:15:00Z", RaidFinalDeadlineUtc = "2026-10-18T01:17:00"
        });
        public string Text(string key, params (string Name, object? Value)[] arguments) => key;
        public string Admission(LocalMutationAdmission admission, LocalMutationState? pending) => admission.ToString();
        public string PreparationFailure(ClientRaidPreparationFailure failure) => failure.Kind.ToString();
        public string TargetLabel(ModWorldMapMarkerDto target) => "target";
        public void Status(string message) { }
        public void Message(string message, bool rejected) { }
        public void Progress(string message) { }
        public void CloseProgress() { }
        public void Packaging() { }
        public void Diagnose(Exception ex) { }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
