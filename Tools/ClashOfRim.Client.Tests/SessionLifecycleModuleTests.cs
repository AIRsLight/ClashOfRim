using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Sessions;

internal static class SessionLifecycleModuleTests
{
    internal static void Run() => RunAsync().GetAwaiter().GetResult();

    private static async Task RunAsync()
    {
        try
        {
            PublishIdentity();
            ExpiryOwnership();
            await IdentityChangeStopsStream();
            await ConnectionAndExit();
            await StaleLogin();
            await LogoutFailures();
        }
        finally { ClientSessionWork.Reset(); }
    }

    private static void PublishIdentity()
    {
        ClientSessionWork.Reset();
        var fixture = new Fixture();
        var changes = new List<ModLoginResponseDto?>();
        fixture.Session.IdentityChanged += changes.Add;
        var response = Login("session", "token", "  canonical-user  ", "  显示名称  ");
        fixture.Session.PublishLogin(response);
        Require(fixture.Store.UserId == "canonical-user" && fixture.Store.DisplayName == "显示名称"
            && fixture.Store.AuthToken == "token" && fixture.Session.SessionId == "session" && changes[0] == response,
            "Login did not publish the server identity and normalized names.");
        fixture.Session.PublishLogin(Login("second", null, "  ", null));
        Require(fixture.Store.UserId == "canonical-user" && fixture.Store.DisplayName == "显示名称" && fixture.Store.AuthToken == "",
            "Missing legacy names overwrote identity or retained an old credential.");
        fixture.Session.ClearIdentity();
        Require(fixture.Session.SessionId is null && !fixture.Session.IsUnavailable && changes.Count == 3 && changes[2] is null,
            "Explicit identity cleanup retained session state.");
    }

    private static void ExpiryOwnership()
    {
        ClientSessionWork.Reset();
        var f = new Fixture();
        f.Session.PublishLogin(Login("old-session", "old-token"));
        Require(f.Session.BeginUnavailable("OLD-token", null) is null && f.Session.BeginUnavailable(null, "OLD-session") is null
            && !f.Session.IsUnavailable, "Stale or case-insensitive expiry matched the current session.");
        var old = f.Session.BeginUnavailable("old-token", "old-session")!;
        Require(old is not null && f.Session.IsUnavailable && f.Session.BeginUnavailable(null, null) is null,
            "Expiry did not acquire one transition.");
        f.Session.PublishLogin(Login("new-session", "new-token"));
        int writes = f.Store.Writes;
        Require(!f.Session.CompleteUnavailable(old!) && f.Store.Writes == writes
            && f.Store.AuthToken == "new-token" && f.Session.SessionId == "new-session" && !f.Session.IsUnavailable,
            "Queued old expiry cleared a replacement in the same generation.");
        var current = f.Session.BeginUnavailable(null, "new-session")!;
        Require(f.Session.CompleteUnavailable(current) && f.Session.IsUnavailable && f.Session.SessionId is null
            && f.Store.AuthToken == "" && !f.Session.CompleteUnavailable(current), "Expiry did not commit exactly once.");
        Require(f.Session.BeginUnavailable(null, null) is null, "Completed expiry prompted repeatedly before reconnect.");

        f.Session.PublishLogin(Login("cross-generation", "token"));
        var stale = f.Session.BeginUnavailable("token", null)!;
        ClientSessionWork.Reset();
        Require(!f.Session.CompleteUnavailable(stale) && f.Store.AuthToken == "token", "New generation accepted an old queued transition.");
        f.Session.ClearIdentity();
        // A credential loaded from settings can expire before any stream/session was opened.
        f.Store.AuthToken = "persisted-token";
        var persisted = f.Session.BeginUnavailable("persisted-token", null)!;
        Require(f.Session.CompleteUnavailable(persisted) && f.Store.AuthToken == "", "Persisted credentials could not expire without a stream.");

        f.Session.PublishLogin(Login("duplicate", "duplicate-token"));
        int acquired = 0;
        Parallel.For(0, 32, _ => { if (f.Session.BeginUnavailable("duplicate-token", "duplicate") is not null) Interlocked.Increment(ref acquired); });
        Require(acquired == 1, "Concurrent expiry acquired more than one transition.");
    }

    private static async Task IdentityChangeStopsStream()
    {
        foreach (string change in new[] { "login", "clear", "end" })
        {
            ClientSessionWork.Reset();
            var f = new Fixture();
            f.Session.PublishLogin(Login("old", "old-token"));
            Task work = f.Connection.Start(f.Store.ReadContext(), f.Session.SessionId, 0, 0);
            var token = await f.Stream.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (change == "login") f.Session.PublishLogin(Login("new", "new-token"));
            else if (change == "clear") f.Session.ClearIdentity();
            else await f.Session.End(sendLogout: false);
            Require(token.IsCancellationRequested && !f.Connection.IsRunning, "Identity transition retained the old stream: " + change);
            f.Stream.Completion.SetResult(ClashOfRimClientNetworkResult<ModSessionStreamClosedDto>.Ok(new(true)));
            await work.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static async Task ConnectionAndExit()
    {
        ClientSessionWork.Reset();
        var f = new Fixture();
        f.Session.PublishLogin(Login("old-session", "old-token"));
        Task stream = f.Connection.Start(f.Store.ReadContext(), f.Session.SessionId, 0, 0);
        CancellationToken streamToken = await f.Stream.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var pending = f.Session.BeginUnavailable("old-token", null)!;
        Require(streamToken.IsCancellationRequested && !f.Connection.IsRunning, "Unavailable identity kept its stream alive.");
        f.Stream.Completion.SetResult(ClashOfRimClientNetworkResult<ModSessionStreamClosedDto>.Ok(new(true)));
        await stream.WaitAsync(TimeSpan.FromSeconds(5));

        bool ended = false;
        var owner = ClientSessionWork.Token;
        f.Session.Ended += () =>
        {
            Require(owner.IsCancellationRequested && f.Session.SessionId is null && !f.Connection.IsRunning,
                "Domain exit ran before work/connection/identity were detached.");
            f.Store.Snapshot = "";
            ended = true;
        };
        f.Store.BeforeWrite = () => Require(ended && f.Store.Snapshot == "", "Exit persisted before domain cleanup.");
        Task exit = f.Session.End(sendLogout: true);
        f.Store.BeforeWrite = null;
        var request = await f.Logout.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Require(request.Context.AuthToken == "old-token" && request.Context.CurrentSnapshotId == "snapshot"
            && request.SessionId == "old-session" && f.Store.AuthToken == "" && !f.Session.IsUnavailable,
            "Logout did not retain the exited credentials/snapshot or exit retained authentication.");
        Require(!f.Session.CompleteUnavailable(pending), "Exit retained an expiry continuation.");
        f.Session.PublishLogin(Login("replacement", "replacement-token"));
        f.Logout.Completion.SetResult(ClashOfRimClientNetworkResult<ModLogoutResponseDto>.Ok(new() { Result = new() { Accepted = true } }));
        await exit.WaitAsync(TimeSpan.FromSeconds(5));
        Require(f.Session.SessionId == "replacement" && f.Store.AuthToken == "replacement-token", "Old logout completion overwrote the next login.");

        var noLogout = new Fixture();
        noLogout.Session.PublishLogin(Login("main-menu", "token"));
        await noLogout.Session.End(sendLogout: false);
        Require(!noLogout.Logout.Started.Task.IsCompleted && noLogout.Store.AuthToken == "", "Main-menu reset sent an unintended logout.");
        await noLogout.Session.End(sendLogout: true);
        Require(!noLogout.Logout.Started.Task.IsCompleted, "Repeated exit logged out an absent session.");
    }

    private static async Task StaleLogin()
    {
        ClientSessionWork.Reset();
        var f = new Fixture();
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task old = ClientSessionWork.Run(async () =>
        {
            ready.SetResult(true);
            await release.Task;
            f.Session.PublishLogin(Login("old", "old-token"));
        });
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await f.Session.End(sendLogout: false);
        f.Session.PublishLogin(Login("new", "new-token"));
        release.SetResult(true);
        await old.WaitAsync(TimeSpan.FromSeconds(5));
        Require(f.Session.SessionId == "new" && f.Store.AuthToken == "new-token", "Expired work published a login.");
    }

    private static async Task LogoutFailures()
    {
        foreach (bool throws in new[] { false, true })
        {
            ClientSessionWork.Reset();
            var f = new Fixture();
            string? diagnostic = null;
            f.Session.LogoutFailed += value => diagnostic = value;
            f.Session.PublishLogin(Login("session", "token"));
            Task exit = f.Session.End(sendLogout: true);
            await f.Logout.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (throws) f.Logout.Completion.SetException(new InvalidOperationException("network failure"));
            else f.Logout.Completion.SetResult(ClashOfRimClientNetworkResult<ModLogoutResponseDto>.Ok(new() { Result = new() { Accepted = false, Message = "denied" } }));
            await exit.WaitAsync(TimeSpan.FromSeconds(5));
            Require(diagnostic?.Contains(throws ? "network failure" : "denied") == true
                && f.Store.AuthToken == "" && f.Session.SessionId is null, "Logout failure lost diagnostics or restored exited identity.");
        }
    }

    private sealed class Fixture
    {
        internal readonly IdentityStore Store = new();
        internal readonly LogoutTransport Logout = new();
        internal readonly StreamTransport Stream = new();
        internal readonly ClientSessionConnection Connection;
        internal readonly ClientSessionLifecycle Session;
        internal Fixture() { Connection = new(Stream); Session = new(Store, Logout, Connection); }
    }

    private sealed class IdentityStore : IClientSessionIdentityStore
    {
        internal string UserId = "user", DisplayName = "name", AuthToken = "", Snapshot = "snapshot";
        internal int Writes;
        internal Action? BeforeWrite;
        public ClashOfRimClientNetworkContext ReadContext() => new("http://localhost:12345", UserId, "colony", Snapshot, null, "password", AuthToken);
        public void Write(string? userId, string? displayName, string authToken)
        {
            BeforeWrite?.Invoke();
            if (userId is not null) UserId = userId;
            if (displayName is not null) DisplayName = displayName;
            AuthToken = authToken;
            Writes++;
        }
    }

    private sealed class LogoutTransport : IClientSessionLogoutTransport
    {
        internal readonly TaskCompletionSource<(ClashOfRimClientNetworkContext Context, string SessionId)> Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<ClashOfRimClientNetworkResult<ModLogoutResponseDto>> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<ClashOfRimClientNetworkResult<ModLogoutResponseDto>> LogoutAsync(ClashOfRimClientNetworkContext context, string sessionId)
        { Started.SetResult((context, sessionId)); return Completion.Task; }
    }

    private sealed class StreamTransport : IClientSessionStreamTransport
    {
        internal readonly TaskCompletionSource<CancellationToken> Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<ClashOfRimClientNetworkResult<ModSessionStreamClosedDto>> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<ClashOfRimClientNetworkResult<ModSessionStreamClosedDto>> StreamAsync(ClashOfRimClientNetworkContext context, string sessionId,
            long notificationVersion, long worldVersion, Action<ModSessionStreamEvent> onEvent, CancellationToken cancellationToken)
        { Started.SetResult(cancellationToken); return Completion.Task; }
    }

    private static ModLoginResponseDto Login(string session, string? token, string? user = null, string? displayName = null) =>
        new() { SessionId = session, AuthToken = token, AuthenticatedUserId = user, DisplayName = displayName, Result = new() { Accepted = true } };
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
