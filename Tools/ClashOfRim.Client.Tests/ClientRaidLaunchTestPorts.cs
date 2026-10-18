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
using AIRsLight.ClashOfRim.Modules.Transport;
using AIRsLight.ClashOfRim.RemoteMaps;

internal sealed class ClientRaidLaunchTestPorts : IClientRaidLaunchRuntime, IClientRaidLaunchTransport, IClientRaidPreparationTransport, IClientRemoteSnapshotTransport, IClientRaidLaunchPresentation
{
    internal readonly EventModuleTestPorts Ports = new();
    internal readonly ClientSynchronizationCoordinator Sync = new();
    internal readonly ClientLocalMutationCoordinator Mutations = new();
    internal readonly EventModuleFailureView Failure;
    internal readonly ClientSessionLifecycle Session;
    internal readonly ClientRaidLaunching Launch;
    internal readonly ModWorldMapMarkerDto Target = new() { OwnerUserId = "defender", OwnerColonyId = "there", WorldObjectId = "world", MapId = "map", Tile = 7, TileLayerId = 2, CanRaid = true };
    internal string Fault = "", DeadlineKind = "valid";
    internal int Captured, Prepared, Downloaded, Opened, Submissions, Applied, Completed;
    internal bool DiscardOpen;
    internal Action? AfterCapture, AfterOpen, OnCompleted;
    internal ClientRaidLaunchCommand? Sent;
    internal ModWorldMapMarkerDto? CapturedTarget;
    internal ClientRaidPreparationCommand? Preparation;
    internal ClashOfRimClientNetworkContext? PreparationContext;
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
    internal ClientRaidLaunchTestPorts()
    {
        ClientSessionWork.Reset(); Session = new(Ports, Ports, new ClientSessionConnection(Ports)); Failure = new(Ports);
        var transactions = new ClientSnapshotTransaction(Session, Ports, new ClientSnapshotPublication(Ports, Ports));
        Launch = new(Session, new ClientSnapshotMutationAdmission(Session, Sync, Mutations, transactions), Sync, Mutations,
            transactions, new ClientSnapshotFailureRecovery(Session, Mutations, Sync, Ports, Failure), new(this, this), this, this, () => Fault != "pvp");
        Launch.Completed += () => { Check(!Sync.IsBusy && !Mutations.IsPending, "Completion preceded release."); Completed++; Done.TrySetResult(); OnCompleted?.Invoke(); };
    }
    internal IClientRaidLaunchRuntime BindLaunch() { gameThread = Environment.CurrentManagedThreadId; return this; }
    internal Task Start() => Launch.StartAsync(BindLaunch(), Target);
    internal async Task Open() { (await Ports.Next())(); var callback = await NextLong(); gameThread = Environment.CurrentManagedThreadId; callback(); }
    internal async Task<Action> NextLong()
    { if (!await available.WaitAsync(TimeSpan.FromSeconds(5)) || !longEvents.TryDequeue(out var action)) throw new Exception("Missing long event."); return action; }
    public bool TryCapture(ClashOfRimClientNetworkContext context, ModWorldMapMarkerDto target, out ClientRaidForceManifest? force, out string failure)
    {
        Check(Environment.CurrentManagedThreadId == gameThread, "Force capture left game thread.");
        CapturedTarget = ClientProtocolJson.Copy(target); Captured++; AfterCapture?.Invoke();
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
    { Preparation = command; PreparationContext = context; Prepared++; Preparing.TrySetResult(); return PrepareHold?.Task ?? Task.FromResult(PrepareReply()); }
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
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
