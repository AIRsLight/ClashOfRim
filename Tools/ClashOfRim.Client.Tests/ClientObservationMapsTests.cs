using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Maps;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using AIRsLight.ClashOfRim.Modules.Synchronization;
using AIRsLight.ClashOfRim.RemoteMaps;

internal static class ClientObservationMapsTests
{
    [ThreadStatic] private static bool onGameThread;
    internal static void Run() => RunAsync().GetAwaiter().GetResult();
    private static async Task RunAsync()
    {
        var culture = CultureInfo.CurrentCulture;
        try
        {
            foreach (string language in new[] { "en-US", "zh-CN", "fr-FR", "tr-TR" })
            foreach (string? mode in new[] { "Scout", "friendlyobservation", "raidobservation", "RaidBattle", "unknown", null })
            foreach (bool login in new[] { false, true })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(language);
                var f = new Fixture(login);
                Task work = f.Start(mode)!;
                f.Target.OwnerUserId = "edited"; f.Target.MapId = "other-map"; f.Target.RelatedEventId = "other-event";
                f.Target.PathTiles![0] = 999;
                Check(f.Sync.IsBusy, "Observation did not acquire manual admission.");
                if (login) (await f.Next())();
                (await f.Next())();
                Check(f.Sync.IsBusy && f.Opened is null && f.OpenQueued == 1, "Admission ended before the long event.");
                f.Ports.Snapshot = "newer"; // Unrelated snapshot progress must not invalidate an observation.
                (await f.Next())(); await work.WaitAsync(TimeSpan.FromSeconds(5));
                var opened = f.Opened!; string scope = ClientRemoteMapModes.Normalize(mode);
                Check(!f.Sync.IsBusy && f.LoginCalls == (login ? 1 : 0) && f.DownloadCalls == 1 && f.LoadedCount == 1 && f.Logouts.Count == 0,
                    "Observation completion leaked admission, retried or logged out an adopted/borrowed session.");
                Check(opened.Mode == scope && opened.Target.OwnerUserId == "bob" && opened.Target.MapId == "Map_7"
                    && opened.Target.PathTiles!.Single() == 7 && opened.RelatedEventId == "raid-event" && opened.SnapshotId == "remote-snapshot"
                    && opened.Payload.SequenceEqual(new byte[] { 1, 2, 3 }) && opened.CloseExistingObservationSessions
                    && opened.SessionId.StartsWith("observation:" + (login ? "canonical" : "alice") + ":bob:remote-snapshot:", StringComparison.Ordinal),
                    "Observation reused mutable UI state or lost open parameters.");
                Check(f.DownloadTarget!.AuthorizationEventId == (scope == ClientRemoteMapModes.RaidObservation ? "raid-event" : null)
                    && f.DownloadTarget.AuthorizationScope == scope && f.DownloadContext!.AuthToken == (login ? "new-auth" : "auth"),
                    "Observation used the wrong login or event authorization.");
                Check(ActiveRemoteMapSession.KindFromMode(mode) == ClientRemoteMapModes.Kind(mode)
                    && RemoteMapSessionPolicy.NormalizeMode(mode) == scope
                    && RemoteMapSessionPolicy.ObservationDownloadingStatusKey(scope) == ClientRemoteMapModes.DownloadingKey(scope)
                    && RemoteMapSessionPolicy.ObservationLoadedMessageKey(scope) == ClientRemoteMapModes.LoadedKey(scope), "Public mode APIs diverged.");
            }
            CheckAdmission();
            foreach (string stage in new[] { "login", "login-queued", "download", "generate-queued", "open-queued" })
            foreach (string change in new[] { "server", "user", "colony", "auth", "identity", "game", "generation" })
                await CheckObsolete(stage, change);
            await CheckReplacementAdmission();
            await CheckFailures();
            await LoginCallbackReplacement();
        }
        finally { CultureInfo.CurrentCulture = culture; ClientSessionWork.Reset(); }
    }

    private static void CheckAdmission()
    {
        foreach (string invalid in new[] { "configuration", "user", "colony", "event", "busy" })
        {
            var f = new Fixture(false); ClientSynchronizationCoordinator.Lease? held = null;
            if (invalid == "configuration") f.Ports.Server = "";
            if (invalid == "user") f.Target.OwnerUserId = " ";
            if (invalid == "colony") f.Target.OwnerColonyId = null;
            if (invalid == "event") f.Target.RelatedEventId = "";
            if (invalid == "busy") Check(f.Sync.TryBeginManual(out held), "Test admission failed.");
            Check(f.Start("RaidObservation") is null && f.DownloadCalls == 0 && f.LoginCalls == 0 && f.Errors.Count == 1
                && f.Closed == 0 && f.Sync.IsBusy == (held is not null), "Admission rejection changed active progress or performed work: " + invalid);
            held?.Dispose();
        }
    }

    private static async Task CheckObsolete(string stage, string change)
    {
        var f = new Fixture(stage.StartsWith("login", StringComparison.Ordinal));
        if (stage == "login") f.HoldLogin = NewSignal();
        if (stage == "download") f.HoldDownload = NewSignal();
        Task work = f.Start()!;
        Action? queued = null;
        if (stage == "login") await f.LoginEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        else if (stage == "download") await f.DownloadEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        else
        {
            queued = await f.Next();
            if (stage == "open-queued") { queued(); queued = await f.Next(); }
        }
        int notices = f.Notices;
        Change(f, change);
        f.HoldLogin?.TrySetResult(); f.HoldDownload?.TrySetResult(); queued?.Invoke();
        await work.WaitAsync(TimeSpan.FromSeconds(5));
        Check(f.Opened is null && f.Notices == notices && !f.Sync.IsBusy, "Obsolete work published or retained admission: " + stage + "/" + change);
        bool login = stage.StartsWith("login", StringComparison.Ordinal);
        Check(f.Logouts.Count == (login ? 1 : 0) && (!login || f.Logouts.Single() == ("canonical", "new-auth", "new-session")),
            "Unadopted login leaked or released another session: " + stage + "/" + change);
    }

    private static async Task LoginCallbackReplacement()
    {
        var f = new Fixture(true); bool replaced = false;
        f.Session.IdentityChanged += _ =>
        {
            if (replaced) return; replaced = true;
            f.Session.PublishLogin(new() { AuthenticatedUserId = "other", AuthToken = "replacement", SessionId = "replacement" });
        };
        await f.Ports.PumpUntil(f.Start("Observation")!);
        Check(f.DownloadCalls == 0 && f.OpenQueued == 0 && !f.Sync.IsBusy && f.Ports.Auth == "replacement",
            "Observation download adopted the identity installed by another login callback.");
    }
    private static void Change(Fixture f, string change)
    {
        switch (change)
        {
            case "server": f.Ports.Server = "http://other"; break;
            case "user": f.Ports.User = "other"; break;
            case "colony": f.Ports.Colony = "other"; break;
            case "auth": f.Ports.Auth = "other"; break;
            case "identity": f.Session.PublishLogin(new() { AuthenticatedUserId = "alice", AuthToken = "replacement", SessionId = "replacement" }); break;
            case "game": f.Ports.GameOwner = new(); break;
            default: ClientSessionWork.Reset(); f.Sync.Reset(); break;
        }
    }

    private static async Task CheckReplacementAdmission()
    {
        var f = new Fixture(false) { HoldDownload = NewSignal() };
        Task old = f.Start()!; await f.DownloadEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Change(f, "identity"); f.Sync.Reset();
        Check(f.Sync.TryBeginManual(out var replacement), "Replacement admission failed.");
        f.HoldDownload.SetResult(); await old;
        Check(replacement!.IsCurrent && f.Opened is null && f.Logouts.Count == 0, "Old finally cleared new work or logged out a borrowed session.");
        replacement.Dispose();
    }

    private static async Task CheckFailures()
    {
        foreach (string failure in new[] { "login-rejected", "login-token", "login-session", "login-late-cancel", "login-exception",
            "download-exception", "metadata", "payload", "open-failure", "open-exception", "queue-exception", "discard-open" })
        {
            bool login = failure.StartsWith("login", StringComparison.Ordinal);
            var f = new Fixture(login) { Failure = failure };
            if (failure == "login-rejected") f.Receipt.Result!.Accepted = false;
            if (failure == "login-token") f.Receipt.AuthToken = "";
            if (failure == "login-session") f.Receipt.SessionId = "";
            Task work = f.Start()!;
            if (failure == "login-late-cancel") { await work; Check(f.Logouts.Count == 1 && f.Notices == 1, "Canceled accepted login was not cleaned up silently."); continue; }
            (await f.Next())();
            if (failure is "open-failure" or "open-exception" or "discard-open") (await f.Next())();
            if (failure == "queue-exception") (await f.Next())(); // Failure notification returns to the game thread.
            await work.WaitAsync(TimeSpan.FromSeconds(5));
            Check(!f.Sync.IsBusy && f.LoadedCount == 0 && f.Errors.Count == (failure == "discard-open" ? 0 : 1), "Failure did not finish exactly once: " + failure);
        }
        // An accepted login may be captured before transport cancellation is reported.
        var captured = new Fixture(true) { Failure = "login-result-failed" };
        Task pending = captured.Start()!; (await captured.Next())(); await pending;
        Check(captured.Logouts.Count == 1 && captured.DownloadCalls == 0 && captured.Ports.Auth == "", "Failed login result adopted/leaked its captured credential.");
        var cleanup = new Fixture(true) { Failure = "login-result-failed", HoldLogout = NewSignal() };
        pending = cleanup.Start()!; (await cleanup.Next())(); await cleanup.LogoutEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(!cleanup.Sync.IsBusy, "Detached login cleanup retained admission.");
        Check(cleanup.Sync.TryBeginManual(out var next), "Detached login cleanup blocked another operation.");
        cleanup.HoldLogout.SetResult(); await pending;
        Check(next!.IsCurrent, "Cleanup released a later admission."); next.Dispose();
    }

    internal sealed class Fixture : IClientObservationTransport, IClientObservationRuntime, IClientObservationPresentation, IClientSessionLogoutTransport
    {
        internal readonly EventModuleTestPorts Ports = new();
        internal readonly ClientSynchronizationCoordinator Sync = new();
        internal readonly ClientSessionLifecycle Session;
        internal readonly ClientObservationMaps Maps;
        internal readonly ModWorldMapMarkerDto Target = new() { OwnerUserId = "bob", OwnerColonyId = "remote", MapId = "Map_7",
            WorldObjectId = "WorldObject_8", Tile = 7, TileLayerId = 2, RelatedEventId = "raid-event", PathTiles = new() { 7 } };
        internal readonly ModLoginResponseDto Receipt = new() { Result = new() { Accepted = true }, AuthenticatedUserId = " canonical ",
            AuthToken = "new-auth", SessionId = "new-session" };
        internal readonly TaskCompletionSource LoginEntered = NewSignal(), DownloadEntered = NewSignal(), LogoutEntered = NewSignal();
        internal TaskCompletionSource? HoldLogin, HoldDownload, HoldLogout;
        internal string? Failure;
        internal int LoginCalls, DownloadCalls, OpenQueued, LoadedCount, Notices, Closed;
        internal readonly List<string> Errors = new();
        internal readonly List<(string User, string Auth, string Session)> Logouts = new();
        internal RemoteSessionMapOpenRequest? Opened;
        internal ClientSnapshotDownloadTarget? DownloadTarget;
        internal ClashOfRimClientNetworkContext? DownloadContext;
        internal Fixture(bool login)
        {
            ClientSessionWork.Reset(); if (login) Ports.Auth = "";
            Session = new(Ports, this, new ClientSessionConnection(Ports));
            Maps = new(Session, Sync, Ports, this, this, this);
        }
        internal Task? Start(string? mode = "RaidObservation")
        { Task? work = null; Game(() => work = Maps.OpenAsync(Target, mode!)); return work; }
        internal async Task<Action> Next() { Action queued = await Ports.Next(); return () => Game(queued); }
        public async Task<ClashOfRimClientNetworkResult<ModLoginResponseDto>> LoginAsync(ClashOfRimClientNetworkContext context,
            CancellationToken token, Action<ModLoginResponseDto> captureReceipt)
        {
            LoginCalls++; LoginEntered.TrySetResult();
            if (HoldLogin is not null) await HoldLogin.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (Failure == "login-exception") throw new InvalidOperationException("login fault");
            captureReceipt(Receipt);
            if (Failure == "login-late-cancel") { ClientSessionWork.Reset(); Sync.Reset(); }
            return Failure == "login-result-failed" ? ClashOfRimClientNetworkResult<ModLoginResponseDto>.Failed("cancelled", "cancelled")
                : ClashOfRimClientNetworkResult<ModLoginResponseDto>.Ok(Receipt);
        }
        public async Task<RemoteSnapshotDownloadResult> DownloadAsync(ClashOfRimClientNetworkContext context,
            ClientSnapshotDownloadTarget target, CancellationToken token, Action<string>? payloadStarting = null)
        {
            DownloadCalls++; DownloadContext = context; DownloadTarget = target; DownloadEntered.TrySetResult();
            if (HoldDownload is not null) await HoldDownload.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (Failure == "download-exception") throw new InvalidOperationException("download fault");
            return new(ClashOfRimClientNetworkResult<ModDownloadLatestSnapshotResponseDto>.Ok(new()
                { Result = new() { Accepted = Failure != "metadata" }, SnapshotId = "remote-snapshot", Package = new() { SnapshotId = "remote-snapshot" } }),
                Failure == "payload" ? null : ClashOfRimClientNetworkResult<byte[]>.Ok(new byte[] { 1, 2, 3 }));
        }
        public void EnqueueOpen(Action action, Action onDiscard)
        {
            Check(onGameThread, "Long event enqueued outside the game thread."); OpenQueued++;
            if (Failure == "queue-exception") throw new InvalidOperationException("queue fault");
            Ports.Enqueue(Failure == "discard-open" ? onDiscard : action);
        }
        public string? Open(RemoteSessionMapOpenRequest request)
        {
            Check(onGameThread && Sync.IsBusy, "Map opened outside the game thread/admission.");
            if (Failure == "open-exception") throw new InvalidOperationException("open fault");
            if (Failure == "open-failure") return "map load failed";
            Opened = request; return null;
        }
        public async Task<ClashOfRimClientNetworkResult<ModLogoutResponseDto>> LogoutAsync(ClashOfRimClientNetworkContext context, string id)
        {
            Logouts.Add((context.UserId, context.AuthToken!, id)); LogoutEntered.TrySetResult();
            if (HoldLogout is not null) await HoldLogout.Task.WaitAsync(TimeSpan.FromSeconds(5));
            return ClashOfRimClientNetworkResult<ModLogoutResponseDto>.Ok(new() { Result = new() { Accepted = true } });
        }
        public void Reject(string key) => Error(key);
        public void Downloading(string mode, ModWorldMapMarkerDto target) => Notice();
        public void Generating(ModWorldMapMarkerDto target) => Notice();
        public void Loaded(string mode, ModWorldMapMarkerDto target) { Notice(); LoadedCount++; }
        public void LoginFailed(ClashOfRimClientNetworkResult<ModLoginResponseDto> login) => Error("login");
        public void DownloadFailed(ClientRemoteSnapshotFailure failure) => Error(failure.Kind.ToString());
        public void OpenFailed(string message) => Error(message);
        public void Exception(Exception exception, bool opening) => Error((opening ? "open:" : "download:") + exception.Message);
        public void CloseProgress() { Check(onGameThread, "Progress closed outside game thread."); Closed++; }
        private void Error(string message) { Notice(); Errors.Add(message); }
        private void Notice() { Check(onGameThread, "Observation UI published outside game thread."); Notices++; }
    }
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal static void Game(Action action) { bool previous = onGameThread; onGameThread = true; try { action(); } finally { onGameThread = previous; } }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
