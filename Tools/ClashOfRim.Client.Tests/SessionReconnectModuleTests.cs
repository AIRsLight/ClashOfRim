using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Protocol;

internal static class SessionReconnectModuleTests
{
    private static readonly SessionReconnectLocalState Ready = new(true, true, false, false);
    internal static void Run() => RunAsync().GetAwaiter().GetResult();
    private static async Task RunAsync()
    {
        try
        {
            await AdmissionAndAdoption();
            await Failures();
            await MainThreadRechecks();
            foreach (string stage in new[] { "prepare", "login", "metadata" }) await ReplacementDuringRequest(stage);
            await ReplacementDuringCleanup();
            await DiscardedCompletionAndCleanupFailure();
            await DetachedLogoutWire();
        }
        finally { ClientSessionWork.Reset(); }
    }

    private static async Task AdmissionAndAdoption()
    {
        ClientSessionWork.Reset();
        var f = new Fixture();
        foreach (var test in new[]
        {
            (Ready with { IsPlaying = false }, SessionReconnectBlockReason.NotInPlayableSession),
            (Ready with { HasGame = false }, SessionReconnectBlockReason.NotInPlayableSession),
            (Ready with { SynchronizationBusy = true }, SessionReconnectBlockReason.SynchronizationBusy),
            (Ready with { AtomicMutationPending = true }, SessionReconnectBlockReason.AtomicMutationPending)
        }) Require(f.Service.TryBegin(test.Item1, out var denied) == test.Item2 && denied is null, "Invalid local admission.");
        f.Store.Colony = "";
        Require(f.Service.TryBegin(Ready, out _) == SessionReconnectBlockReason.MissingIdentity, "Missing colony admitted.");
        f.Store.Colony = "colony";
        var attempt = f.Begin();
        await attempt.PrepareAsync();
        Require(f.Service.IsRunning && f.Service.TryBegin(Ready, out _) == SessionReconnectBlockReason.SynchronizationBusy
            && f.Store.Token == "" && f.Lifecycle.SessionId is null, "Prepared login was published or released before the game accepted it.");
        var result = attempt.Complete(true, true, false);
        Require(result.Outcome == SessionReconnectOutcome.Succeeded && result.Snapshot?.ActiveRaidRecovery?.EventId == "active-raid"
            && f.Store.Token == "candidate-token" && f.Lifecycle.SessionId == "candidate-session"
            && f.Transport.MetadataContext?.AuthToken == "candidate-token" && f.Store.Writes == 1,
            "Reconnect did not adopt the authenticated login and exact recovery metadata.");
        Require(attempt.Complete(true, true, false).Outcome == SessionReconnectOutcome.Obsolete, "Reconnect published twice.");
        await attempt.DisposeAsync();
        await attempt.DisposeAsync();
        Require(f.Logout.Calls.Count == 0 && !f.Service.IsRunning && f.Transport.Calls.SequenceEqual(new[] { "prepare", "login", "metadata" }),
            "Adopted session was logged out or reconnect order changed.");
    }

    private static async Task Failures()
    {
        foreach (var test in new[]
        {
            ("prepare-network", SessionReconnectOutcome.NetworkFailed, 0),
            ("prepare-throw", SessionReconnectOutcome.NetworkFailed, 0),
            ("prepare-rejected", SessionReconnectOutcome.Rejected, 0),
            ("prepare-new-colony", SessionReconnectOutcome.SnapshotChanged, 0),
            ("prepare-colony", SessionReconnectOutcome.SnapshotChanged, 0),
            ("prepare-snapshot", SessionReconnectOutcome.SnapshotChanged, 0),
            ("login-network", SessionReconnectOutcome.NetworkFailed, 0),
            ("login-throw", SessionReconnectOutcome.NetworkFailed, 0),
            ("login-rejected", SessionReconnectOutcome.Rejected, 0),
            ("login-no-token", SessionReconnectOutcome.Rejected, 0),
            ("login-no-session", SessionReconnectOutcome.Rejected, 0),
            ("login-identity", SessionReconnectOutcome.IdentityChanged, 1),
            ("metadata-network", SessionReconnectOutcome.SnapshotChanged, 1),
            ("metadata-rejected", SessionReconnectOutcome.SnapshotChanged, 1),
            ("metadata-snapshot", SessionReconnectOutcome.SnapshotChanged, 1),
            ("metadata-throw", SessionReconnectOutcome.NetworkFailed, 1)
        })
        {
            ClientSessionWork.Reset();
            var f = new Fixture(test.Item1);
            var attempt = f.Begin();
            await attempt.PrepareAsync();
            Require(attempt.Complete(true, true, false).Outcome == test.Item2 && f.Store.Writes == 0,
                "Wrong reconnect failure result: " + test.Item1);
            await attempt.DisposeAsync();
            await attempt.DisposeAsync();
            Require(f.Logout.Calls.Count == test.Item3 && !f.Service.IsRunning, "Wrong cleanup count: " + test.Item1);
            if (test.Item3 == 1)
            {
                var released = f.Logout.Calls.Single();
                Require(released.Context.UserId == (test.Item1 == "login-identity" ? "different-user" : "user")
                    && released.Context.AuthToken == "candidate-token" && released.Session == "candidate-session",
                    "Cleanup used the pre-login identity instead of the returned identity.");
            }
            if (test.Item1.StartsWith("prepare-")) Require(!f.Transport.Calls.Contains("login"), "Preparation failure still logged in.");
            if (test.Item1.StartsWith("login-")) Require(!f.Transport.Calls.Contains("metadata"), "Rejected identity downloaded a snapshot.");
        }
    }

    private static async Task MainThreadRechecks()
    {
        foreach (string change in new[] { "not-playing", "other-game", "atomic", "missing", "user", "colony", "server", "snapshot", "identity-owner" })
        {
            ClientSessionWork.Reset();
            var f = new Fixture();
            var attempt = f.Begin();
            await attempt.PrepareAsync();
            switch (change)
            {
                case "missing": f.Store.Colony = ""; break;
                case "user": f.Store.User = "other-user"; break;
                case "colony": f.Store.Colony = "other-colony"; break;
                case "server": f.Store.Url = "http://localhost:54321"; break;
                case "snapshot": f.Store.Snapshot = "new-snapshot"; break;
                case "identity-owner": f.Lifecycle.PublishLogin(Login("new-session", "new-token")); break;
            }
            var result = attempt.Complete(change != "not-playing", change != "other-game", change == "atomic");
            var expected = change switch
            {
                "user" or "colony" or "server" => SessionReconnectOutcome.IdentityChanged,
                "snapshot" => SessionReconnectOutcome.SnapshotChanged,
                "identity-owner" => SessionReconnectOutcome.Obsolete,
                _ => SessionReconnectOutcome.LocalStateBlocked
            };
            Require(result.Outcome == expected, "Main thread accepted changed state: " + change);
            await attempt.DisposeAsync();
            Require(f.Logout.Calls.Count == 1 && f.Store.Token == (change == "identity-owner" ? "new-token" : ""),
                "Declined completion leaked a temporary session or overwrote replacement credentials.");
        }
    }

    private static async Task ReplacementDuringRequest(string stage)
    {
        ClientSessionWork.Reset();
        var f = new Fixture();
        f.Transport.PauseAt = stage;
        var old = f.Begin();
        Task preparing = ClientSessionWork.Run(old.PrepareAsync);
        await f.Transport.Paused.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await f.Lifecycle.End(sendLogout: false);
        var newer = f.Begin();
        f.Transport.Continue.SetResult(true); // Simulate a response already accepted when cancellation arrived.
        await preparing.WaitAsync(TimeSpan.FromSeconds(5));
        Require(old.Owner.Token.IsCancellationRequested && old.Complete(true, true, false).Outcome == SessionReconnectOutcome.Obsolete,
            "Expired request became adoptable.");
        await old.DisposeAsync();
        Require(f.Service.IsRunning && f.Logout.Calls.Count == (stage == "prepare" ? 0 : 1)
            && f.Store.Token == "", "Old finalizer cleared the new attempt or leaked its late login.");
        await newer.DisposeAsync();
        Require(!f.Service.IsRunning, "New attempt could not release admission.");
    }

    private static async Task ReplacementDuringCleanup()
    {
        ClientSessionWork.Reset();
        var f = new Fixture("metadata-snapshot");
        var old = f.Begin();
        await old.PrepareAsync();
        f.Logout.Pause = true;
        Task cleanup = old.DisposeAsync().AsTask();
        await f.Logout.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Require(f.Service.TryBegin(Ready, out _) == SessionReconnectBlockReason.SynchronizationBusy, "Retry raced unfinished temporary-session cleanup.");
        f.Lifecycle.PublishLogin(Login("other", "other-token"));
        var newer = f.Begin();
        f.Logout.Continue.SetResult(true);
        await cleanup.WaitAsync(TimeSpan.FromSeconds(5));
        Require(f.Service.IsRunning && f.Store.Token == "other-token", "Cleanup cleared a replacement attempt or identity.");
        await newer.DisposeAsync();
    }

    private static async Task DiscardedCompletionAndCleanupFailure()
    {
        foreach (bool fails in new[] { false, true })
        {
            ClientSessionWork.Reset();
            var f = new Fixture();
            var attempt = f.Begin();
            await attempt.PrepareAsync();
            int diagnostics = 0;
            f.Lifecycle.LogoutFailed += _ => diagnostics++;
            f.Logout.Fails = fails;
            // The engine discarded the queued completion: never call Complete.
            await attempt.DisposeAsync();
            Require(attempt.Complete(true, true, false).Outcome == SessionReconnectOutcome.Obsolete
                && f.Store.Writes == 0 && f.Logout.Calls.Count == 1 && diagnostics == (fails ? 1 : 0),
                "Discarded engine action leaked or adopted its login.");
        }
    }

    private static async Task DetachedLogoutWire()
    {
        ClientSessionWork.Reset();
        using var handler = new LogoutHandler();
        using var http = new HttpClient(handler);
        var context = new ClashOfRimClientNetworkContext("http://localhost:12345", "returned-user", "colony", "snapshot", null, null, "token");
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool success = false;
        Task work = ClientSessionWork.Run(async () =>
        {
            ready.SetResult(true);
            await release.Task;
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var result = await new ClashOfRimModNetworkClient(http, context).LogoutDetachedAsync("returned-session", cleanup.Token);
            success = result.Success && result.Response?.Result?.Accepted == true;
        });
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        ClientSessionWork.Reset();
        release.SetResult(true);
        await work.WaitAsync(TimeSpan.FromSeconds(5));
        Require(success && handler.Calls == 1, "Detached cleanup still inherited the cancelled session generation.");
    }

    private sealed class LogoutHandler : HttpMessageHandler
    {
        internal int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Require(!cancellationToken.IsCancellationRequested && cancellationToken.CanBeCanceled, "Cleanup lost its independent timeout.");
            Require(request.RequestUri!.AbsolutePath == ProtocolContractManifest.Find(ProtocolMessageKind.Logout).Route, "Cleanup changed the logout route.");
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var json = body.RootElement;
            Require(json.GetProperty("userId").GetString() == "returned-user" && json.GetProperty("colonyId").GetString() == "colony"
                && json.GetProperty("sessionId").GetString() == "returned-session", "Cleanup changed its captured connection credential.");
            Calls++;
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"result\":{\"accepted\":true},\"disconnected\":true}") };
        }
    }

    private sealed class Fixture
    {
        internal readonly Store Store = new();
        internal readonly Logout Logout = new();
        internal readonly Transport Transport;
        internal readonly ClientSessionLifecycle Lifecycle;
        internal readonly ClientSessionReconnect Service;
        internal Fixture(string scenario = "success")
        {
            Transport = new(scenario);
            Lifecycle = new(Store, Logout, new ClientSessionConnection(new Stream()));
            Service = new(Transport, Lifecycle);
        }
        internal ClientSessionReconnect.Attempt Begin()
        {
            Require(Service.TryBegin(Ready, out var attempt) == SessionReconnectBlockReason.None, "Reconnect did not acquire admission.");
            return attempt!;
        }
    }
    private sealed class Store : IClientSessionIdentityStore
    {
        internal string Url = "http://localhost:12345", User = "user", Colony = "colony", Snapshot = "snapshot", Token = "";
        internal int Writes;
        public ClashOfRimClientNetworkContext ReadContext() => new(Url, User, Colony, Snapshot, null, "password", Token);
        public void Write(string? userId, string? displayName, string authToken) { User = userId ?? User; Token = authToken; Writes++; }
    }
    private sealed class Logout : IClientSessionLogoutTransport
    {
        internal readonly List<(ClashOfRimClientNetworkContext Context, string Session)> Calls = new();
        internal bool Pause, Fails;
        internal readonly TaskCompletionSource<bool> Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<bool> Continue = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<ClashOfRimClientNetworkResult<ModLogoutResponseDto>> LogoutAsync(ClashOfRimClientNetworkContext context, string sessionId)
        {
            Calls.Add((context, sessionId)); Started.TrySetResult(true);
            if (Pause) await Continue.Task;
            if (Fails) throw new InvalidOperationException("cleanup failure");
            return ClashOfRimClientNetworkResult<ModLogoutResponseDto>.Ok(new() { Result = Accepted() });
        }
    }
    private sealed class Stream : IClientSessionStreamTransport
    {
        public Task<ClashOfRimClientNetworkResult<ModSessionStreamClosedDto>> StreamAsync(ClashOfRimClientNetworkContext context, string sessionId,
            long notificationVersion, long worldVersion, Action<ModSessionStreamEvent> onEvent, CancellationToken cancellationToken) =>
            Task.FromResult(ClashOfRimClientNetworkResult<ModSessionStreamClosedDto>.Ok(new(true)));
    }
    private sealed class Transport : IClientSessionReconnectTransport
    {
        private readonly string scenario;
        internal readonly List<string> Calls = new();
        internal string? PauseAt;
        internal ClashOfRimClientNetworkContext? MetadataContext;
        internal readonly TaskCompletionSource<bool> Paused = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<bool> Continue = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Transport(string scenario) => this.scenario = scenario;
        private async Task Before(string stage)
        {
            Calls.Add(stage);
            if (PauseAt == stage) { Paused.SetResult(true); await Continue.Task; }
            if (scenario == stage + "-throw") throw new InvalidOperationException("transport failure");
        }
        public async Task<ClashOfRimClientNetworkResult<ModPrepareWorldSessionResponseDto>> PrepareAsync(ClashOfRimClientNetworkContext context, CancellationToken token)
        {
            await Before("prepare");
            if (scenario == "prepare-network") return ClashOfRimClientNetworkResult<ModPrepareWorldSessionResponseDto>.Failed("network", "offline");
            return ClashOfRimClientNetworkResult<ModPrepareWorldSessionResponseDto>.Ok(new()
            {
                Result = Accepted(scenario != "prepare-rejected"), HasExistingColony = scenario != "prepare-new-colony",
                AssignedColonyId = scenario == "prepare-colony" ? "COLONY" : "colony", LatestSnapshotId = scenario == "prepare-snapshot" ? "other" : "snapshot"
            });
        }
        public async Task<ClashOfRimClientNetworkResult<ModLoginResponseDto>> LoginAsync(ClashOfRimClientNetworkContext context, CancellationToken token)
        {
            await Before("login");
            if (scenario == "login-network") return ClashOfRimClientNetworkResult<ModLoginResponseDto>.Failed("network", "offline");
            var response = Login("candidate-session", "candidate-token");
            response.AuthenticatedUserId = scenario == "login-identity" ? " different-user " : " user ";
            if (scenario == "login-rejected") response.Result = Accepted(false);
            if (scenario == "login-no-token") response.AuthToken = null;
            if (scenario == "login-no-session") response.SessionId = null;
            return ClashOfRimClientNetworkResult<ModLoginResponseDto>.Ok(response);
        }
        public async Task<ClashOfRimClientNetworkResult<ModDownloadLatestSnapshotResponseDto>> ReadSnapshotAsync(ClashOfRimClientNetworkContext context, CancellationToken token)
        {
            MetadataContext = context;
            await Before("metadata");
            if (scenario == "metadata-network") return ClashOfRimClientNetworkResult<ModDownloadLatestSnapshotResponseDto>.Failed("network", "offline");
            return ClashOfRimClientNetworkResult<ModDownloadLatestSnapshotResponseDto>.Ok(new()
            {
                Result = Accepted(scenario != "metadata-rejected"), SnapshotId = scenario == "metadata-snapshot" ? "other" : "snapshot",
                ActiveRaidRecovery = new() { EventId = "active-raid" }
            });
        }
    }
    private static ModProtocolResponseDto Accepted(bool value = true) => new() { Accepted = value, Message = value ? "ok" : "rejected" };
    private static ModLoginResponseDto Login(string session, string token) => new() { Result = Accepted(), SessionId = session, AuthToken = token };
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
