using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using AIRsLight.ClashOfRim.Modules.Synchronization;

// Shared controlled engine/identity boundary for client domain tests. The tested
// workflows, ownership checks and synchronization remain production code.
internal sealed class EventModuleTestPorts : IClientSessionIdentityStore, IClientSessionLogoutTransport, IClientSessionStreamTransport, IClientSnapshotStore, IClientSnapshotGame
{
    internal string User = "alice", Auth = "auth", Colony = "colony", Snapshot = "base", Server = "http://localhost";
    internal string? SteamTicket, Password;
    internal object GameOwner = new();
    internal int Published, GamePublished, Queued, Captures;
    internal byte[] SaveBytes = SnapshotPackageModuleTests.Save();
    internal SnapshotUploadFailure? CaptureFailure;
    internal Action? OnCapture;
    internal Action? OnPublish;
    internal Action? BeforeGameAction;
    internal bool AllowRaidRemoval;
    internal bool HasGame = true;
    internal readonly List<bool> CaptureModes = new();
    internal readonly List<string> Trace = new();
    private readonly ConcurrentQueue<Action> pending = new();
    private readonly SemaphoreSlim available = new(0);
    public ClashOfRimClientNetworkContext ReadContext() => new(Server, User, Colony, Snapshot, SteamTicket, Password, Auth);
    public void Write(string? user, string? displayName, string auth) { if (user is not null) User = user; Auth = auth; }
    public void Publish(string snapshot, string? token) { Snapshot = snapshot; Published++; Trace.Add("store"); OnPublish?.Invoke(); }
    public void Persist() => Trace.Add("persist");
    public IClientSnapshotGameSession BindCurrentGame() => new GameSession(this, GameOwner);
    internal void Enqueue(Action action) { Queued++; pending.Enqueue(action); available.Release(); }
    internal async Task<Action> Next()
    {
        if (!await available.WaitAsync(TimeSpan.FromSeconds(5))) throw new InvalidOperationException("Event module did not queue game work.");
        if (!pending.TryDequeue(out var action)) throw new InvalidOperationException("Queue lost work."); return action!;
    }
    internal async Task PumpUntil(Task work, Action<Exception>? gameFailure = null)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!work.IsCompleted)
        {
            using var next = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
            var ready = available.WaitAsync(next.Token);
            if (await Task.WhenAny(ready, work) == work && !ready.IsCompleted)
            { next.Cancel(); try { await ready; RunPending(); } catch (OperationCanceledException) { } break; }
            await ready;
            RunPending();
        }
        await work.WaitAsync(timeout.Token);
        while (available.Wait(0)) RunPending();
        void RunPending()
        {
            if (!pending.TryDequeue(out var action)) throw new InvalidOperationException("Queue lost work.");
            try { action(); } catch (Exception ex) when (gameFailure is not null) { gameFailure(ex); }
        }
    }
    private sealed record GameSession(EventModuleTestPorts Ports, object Owner) : IClientSnapshotGameSession
    {
        public bool HasGame => Ports.HasGame;
        public bool IsCurrent => ReferenceEquals(Owner, Ports.GameOwner);
        public string RuntimeSnapshotId => Ports.Snapshot;
        public SnapshotSaveCapture Capture(bool removeRaidBattleSessions)
        {
            if (removeRaidBattleSessions && !Ports.AllowRaidRemoval) throw new InvalidOperationException("Event confirmation changed snapshot capture mode.");
            Ports.CaptureModes.Add(removeRaidBattleSessions);
            Ports.Captures++; Ports.OnCapture?.Invoke();
            return new(Ports.SaveBytes, 42f, Array.Empty<ModSnapshotAchievementCandidateDto>(), Ports.CaptureFailure);
        }
        public void Publish(string snapshot, string? token, IReadOnlyList<ModSnapshotAchievementCandidateDto> candidates)
        { if (candidates.Count != 0) throw new InvalidOperationException("Confirmation consumed unrelated achievements."); Ports.GamePublished++; Ports.Trace.Add("game"); }
        public void Enqueue(Action action, Action discard)
        { Ports.Enqueue(() => { Ports.BeforeGameAction?.Invoke(); if (IsCurrent) action(); else discard(); }); }
    }
    public Task<ClashOfRimClientNetworkResult<ModLogoutResponseDto>> LogoutAsync(ClashOfRimClientNetworkContext context, string id) => throw new NotSupportedException();
    public Task<ClashOfRimClientNetworkResult<ModSessionStreamClosedDto>> StreamAsync(ClashOfRimClientNetworkContext context, string id, long version, long world, Action<ModSessionStreamEvent> onEvent, CancellationToken token) => throw new NotSupportedException();
}

internal sealed class EventModuleFailureView : IClientSnapshotFailureView
{
    private readonly EventModuleTestPorts ports;
    internal EventModuleFailureView(EventModuleTestPorts ports) => this.ports = ports;
    public void Enqueue(Action action) => ports.Enqueue(action);
    internal ClientLocalMutationCoordinator.Failure? Visible;
    internal Action? Retry, Exit, Closed;
    internal Func<bool>? CurrentSession;
    internal int Shown, Menus;
    internal Action? OnShown;
    internal string? Message => Visible?.State.Status;
    public void Show(ClientLocalMutationCoordinator.Failure failure, Action? retry, Action exit, Func<bool> belongsToCurrentSession, Action closed)
    {
        if (Visible is not null) Close(Visible);
        Visible = failure; Retry = retry; Exit = exit; CurrentSession = belongsToCurrentSession; Closed = closed; Shown++; OnShown?.Invoke();
    }
    public void Close(ClientLocalMutationCoordinator.Failure failure) { if (ReferenceEquals(Visible, failure)) { Visible = null; Closed?.Invoke(); } }
    public void ReturnToMenu() => Menus++;
}
