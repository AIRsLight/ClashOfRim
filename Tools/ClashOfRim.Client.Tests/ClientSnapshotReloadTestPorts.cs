using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Maps;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using AIRsLight.ClashOfRim.Modules.Synchronization;
using AIRsLight.ClashOfRim.RemoteMaps;

// Shared controlled loading and transport ports; reload ownership stays in production.
internal sealed class ClientSnapshotReloadTestPorts : IClientSnapshotReloadRuntime, IClientSnapshotReloadPresentation, IClientSessionAuthentication,
    IClientSessionLogoutTransport, IClientRemoteSnapshotTransport, IDisposable
{
    internal readonly EventModuleTestPorts Ports = new();
    internal readonly ClientSynchronizationCoordinator Sync = new();
    internal readonly ClientLocalMutationCoordinator Mutations = new();
    internal readonly ClientSessionLifecycle Session;
    internal readonly ClientSessionConnection Connection;
    internal readonly EventModuleFailureView View;
    internal readonly ClientSnapshotReload Module;
    internal readonly TaskCompletionSource LoadQueued = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal readonly TaskCompletionSource Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal readonly TaskCompletionSource LogoutEntered = new(TaskCreationOptions.RunContinuationsAsynchronously), ReleaseLogout = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal readonly List<(string User, string? Auth, string Id)> Logouts = new();
    internal readonly List<string> Stages = new();
    internal Action<string>? Hook;
    internal string Mode = "", SnapshotId = "";
    internal int Logins, Downloads, Loads, Completed, Failures;
    internal bool HoldLogout;
    internal ClientSnapshotReloadCompleted? Receipt;
    internal ModActiveRaidRecoveryDto? ActiveRaidRecovery;
    internal ClientSnapshotReloadTestPorts(bool login = false)
    {
        ClientSessionWork.Reset(); Connection = new(Ports); Session = new(Ports, this, Connection); View = new(Ports);
        if (!login) Session.PublishLogin(new() { AuthenticatedUserId = "alice", AuthToken = "auth", SessionId = "borrowed" });
        Module = new(Session, Sync, Mutations, new(Session, Mutations, Sync, Ports, View), this,
            new(this), new(Ports), this, this);
        Module.Completed += receipt => { Receipt = receipt; Completed++; Hit("completed"); Done.TrySetResult(); };
    }
    internal Task Start() => Module.StartAsync(new("base", ClientSnapshotReloadPurpose.Raid));
    internal void Hit(string stage) { Stages.Add(stage); Hook?.Invoke(stage); }
    public void Enqueue(Action action, Action discarded) => Ports.Enqueue(action);
    public IClientSnapshotLoadOperation Bind() => new Load(this);
    private sealed class Load : IClientSnapshotLoadOperation
    {
        private readonly ClientSnapshotReloadTestPorts f; private object owner;
        internal Load(ClientSnapshotReloadTestPorts f) { this.f = f; owner = f.Ports.GameOwner; }
        public bool IsCurrent => ReferenceEquals(owner, f.Ports.GameOwner);
        public bool MutationStarted { get; private set; }
        public IClientSnapshotGameSession Game => f.Ports.BindCurrentGame();
        public Task<bool> LoadAsync(ClientRestoredSnapshot snapshot, Func<Action, bool> apply, CancellationToken token)
        {
            f.Loads++;
            if (f.Mode == "load-before") throw new IOException("before mutation");
            var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            f.Ports.Enqueue(() =>
            {
                f.Hit("load-queued"); f.LoadQueued.TrySetResult();
                if (f.Mode == "load-orphan") return;
                if (f.Mode == "load-discarded") { done.TrySetResult(false); return; }
                if (!apply(() => { MutationStarted = true; f.Ports.GameOwner = owner = new object(); f.Hit("scene"); })) { done.TrySetResult(false); return; }
                f.Ports.Enqueue(() =>
                {
                    if (!apply(() => { f.Ports.GameOwner = owner = new object(); f.Hit("loaded"); })) { done.TrySetResult(false); return; }
                    if (f.Mode == "load-after") done.TrySetException(new IOException("after mutation")); else done.TrySetResult(true);
                });
            });
            return done.Task.WaitAsync(token);
        }
        public void Dispose() { }
    }
    public Task<ClashOfRimClientNetworkResult<ModLoginResponseDto>> LoginAsync(ClashOfRimClientNetworkContext context, string purpose, string? manifest,
        CancellationToken token, Action<ModLoginResponseDto>? captureReceipt = null)
    {
        Logins++; Check(purpose == "restore-existing-snapshot" && context.UserId == "alice", "Login lost captured identity or purpose.");
        var receipt = new ModLoginResponseDto { Result = new() { Accepted = Mode != "login-rejected" }, SessionId = "received-session",
            AuthenticatedUserId = Mode == "login-other" ? " other " : "alice", AuthToken = Mode == "login-incomplete" ? "" : "received-token" };
        captureReceipt?.Invoke(receipt); Hit("login"); return Task.FromResult(ClashOfRimClientNetworkResult<ModLoginResponseDto>.Ok(receipt));
    }
    public Task<ClashOfRimClientNetworkResult<ModLogoutResponseDto>> LogoutAsync(ClashOfRimClientNetworkContext context, string? id, CancellationToken token, bool bindToSession = true) => throw new NotSupportedException();
    public async Task<ClashOfRimClientNetworkResult<ModLogoutResponseDto>> LogoutAsync(ClashOfRimClientNetworkContext context, string id)
    { Logouts.Add((context.UserId, context.AuthToken, id)); LogoutEntered.TrySetResult(); if (HoldLogout) await ReleaseLogout.Task; return ClashOfRimClientNetworkResult<ModLogoutResponseDto>.Ok(new() { Result = new() { Accepted = true } }); }
    public Task<RemoteSnapshotDownloadResult> DownloadAsync(ClashOfRimClientNetworkContext context, ClientSnapshotDownloadTarget target, CancellationToken token, Action<string>? payloadStarting = null)
    {
        Downloads++; Check(context.UserId == "alice" && target.UserId == "alice" && context.ColonyId == "colony", "Download changed captured owner.");
        var build = ClientSnapshotPackageBuilder.FromSaveBytes(Ports.SaveBytes, "test", context.UserId, context.ColonyId, new DateTime(2026, 10, 18));
        SnapshotId = build.Package!.SnapshotId; build.Package.NextLineageToken = "next";
        payloadStarting?.Invoke(SnapshotId); Hit("download");
        var metadata = Mode == "download-network" ? ClashOfRimClientNetworkResult<ModDownloadLatestSnapshotResponseDto>.Failed("HttpError", "503 unavailable")
            : ClashOfRimClientNetworkResult<ModDownloadLatestSnapshotResponseDto>.Ok(new() { Result = new() { Accepted = Mode != "download-rejected" }, Package = build.Package, SnapshotId = SnapshotId, ActiveRaidRecovery = ActiveRaidRecovery });
        return Task.FromResult(new RemoteSnapshotDownloadResult(metadata, ClashOfRimClientNetworkResult<byte[]>.Ok(build.Payload!)));
    }
    public void Progress(object owner, ClientSnapshotReloadStage stage, string snapshotId)
    { if (stage == ClientSnapshotReloadStage.Loaded) Hit("progress-loaded"); }
    public void Close(object owner) { }
    public string Text(string key) => key;
    public string LoginFailure(ClashOfRimClientNetworkResult<ModLoginResponseDto> result) => "login failure";
    public string DownloadFailure(ClientColonySnapshotFailure failure) => failure.Kind.ToString();
    public string Exception(Exception exception) => exception.Message;
    public void Failure(object owner, string message, ClientSnapshotReloadPurpose purpose) => Failures++;
    internal void Change(string change)
    {
        switch (change)
        {
            case "server": Ports.Server = "http://other"; break; case "user": Ports.User = "other"; break;
            case "colony": Ports.Colony = "other"; break; case "auth": Ports.Auth = "other"; break;
            case "snapshot": Ports.Snapshot = "other"; break; case "game": Ports.GameOwner = new object(); break;
            case "login": Session.PublishLogin(new() { AuthenticatedUserId = "alice", AuthToken = "new-token", SessionId = "new-session" }); break;
            case "same-login": Session.PublishLogin(new() { AuthToken = Ports.Auth, SessionId = Session.SessionId }); break;
            case "generation": ClientSessionWork.Reset(); Sync.Reset(); Mutations.Reset(); break;
            case "mutation": Mutations.Begin(LocalMutationKind.Bank, "other"); break;
        }
    }
    public void Dispose() { Sync.Reset(); ClientSessionWork.Reset(); }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
