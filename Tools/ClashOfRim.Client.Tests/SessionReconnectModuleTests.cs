using static SessionReconnectTestTransport;
using AIRsLight.ClashOfRim.Modules.Synchronization;
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
    private static readonly SessionReconnectLocalState Ready = new(true, true, false);
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
            await LoginCallbackReplacement();
            await FrozenLoginReceipt();
            foreach (bool afterWrite in new[] { false, true }) await StoreFailure(afterWrite);
            await CapturedCancellation();
        }
        finally { ClientSessionWork.Reset(); }
    }

    private static async Task StoreFailure(bool afterWrite)
    {
        ClientSessionWork.Reset(); var f = new Fixture(); var attempt = f.Begin(); await attempt.PrepareAsync();
        f.Store.Failure = afterWrite ? "after" : "before";
        bool failed = false;
        try { attempt.Complete(true, true, false); }
        catch (InvalidOperationException ex) when (ex.Message == "store failure") { failed = true; }
        await attempt.DisposeAsync();
        Require(failed && attempt.Published is not null && !f.Synchronization.IsBusy
            && f.Logout.Calls.Count == (afterWrite ? 0 : 1) && f.Store.Token == (afterWrite ? "candidate-token" : ""),
            "Reconnect did not reuse login ownership around identity-store failure.");
    }

    private static async Task CapturedCancellation()
    {
        ClientSessionWork.Reset(); var f = new Fixture("login-canceled-after-receipt"); var attempt = f.Begin();
        await attempt.PrepareAsync(); var result = attempt.Complete(true, true, false); await attempt.DisposeAsync();
        Require(result.Outcome == SessionReconnectOutcome.NetworkFailed && f.Logout.Calls.Single().Session == "candidate-session"
            && f.Store.Token == "" && !f.Synchronization.IsBusy, "Canceled accepted login leaked its temporary credential.");
    }

    private static async Task AdmissionAndAdoption()
    {
        ClientSessionWork.Reset();
        var f = new Fixture();
        foreach (var test in new[]
        {
            (Ready with { IsPlaying = false }, SessionReconnectBlockReason.NotInPlayableSession),
            (Ready with { HasGame = false }, SessionReconnectBlockReason.NotInPlayableSession),
            (Ready with { AtomicMutationPending = true }, SessionReconnectBlockReason.AtomicMutationPending)
        }) Require(f.Service.TryBegin(test.Item1, out var denied) == test.Item2 && denied is null, "Invalid local admission.");
        Require(f.Synchronization.TryBeginManual(out var manual), "Manual admission failed.");
        Require(f.Service.TryBegin(Ready, out _) == SessionReconnectBlockReason.SynchronizationBusy, "Reconnect ignored manual admission.");
        manual!.Dispose();
        Require(f.Synchronization.TryAcquireSnapshot(out var snapshot), "Snapshot admission failed.");
        Require(f.Service.TryBegin(Ready, out _) == SessionReconnectBlockReason.SynchronizationBusy, "Reconnect ignored snapshot admission.");
        snapshot!.Dispose();
        f.Store.Colony = "";
        Require(f.Service.TryBegin(Ready, out _) == SessionReconnectBlockReason.MissingIdentity, "Missing colony admitted.");
        f.Store.Colony = "colony";
        var attempt = f.Begin();
        await attempt.PrepareAsync();
        Require(f.Service.IsRunning && f.Service.TryBegin(Ready, out _) == SessionReconnectBlockReason.SynchronizationBusy
            && f.Synchronization.ManualInProgress && !f.Synchronization.TryAcquireSnapshot(out _)
            && f.Store.Token == "" && f.Lifecycle.SessionId is null, "Prepared login was published or released before the game accepted it.");
        var result = attempt.Complete(true, true, false);
        Require(result.Outcome == SessionReconnectOutcome.Succeeded && result.Publication is not null && f.Lifecycle.Owns(result.Publication) && result.Snapshot?.ActiveRaidRecovery?.EventId == "active-raid"
            && f.Store.Token == "candidate-token" && f.Lifecycle.SessionId == "candidate-session"
            && f.Transport.MetadataContext?.AuthToken == "candidate-token" && f.Store.Writes == 1,
            "Reconnect did not adopt the authenticated login and exact recovery metadata.");
        Require(!f.Synchronization.IsBusy, "Completed reconnect retained manual admission.");
        Require(attempt.Complete(true, true, false).Outcome == SessionReconnectOutcome.Obsolete, "Reconnect published twice.");
        await attempt.DisposeAsync();
        await attempt.DisposeAsync();
        Require(f.Logout.Calls.Count == 0 && !f.Service.IsRunning && f.Transport.Calls.SequenceEqual(new[] { "prepare", "login", "metadata" }),
            "Adopted session was logged out or reconnect order changed.");
    }
    private static async Task LoginCallbackReplacement()
    {
        ClientSessionWork.Reset(); var f = new Fixture(); var attempt = f.Begin();
        await attempt.PrepareAsync(); bool changed = false;
        f.Lifecycle.IdentityChanged += _ =>
        {
            if (changed) return; changed = true;
            f.Lifecycle.PublishLogin(Login("replacement-session", "replacement-token"));
        };
        var result = attempt.Complete(true, true, false); await attempt.DisposeAsync();
        Require(result.Outcome == SessionReconnectOutcome.Obsolete && f.Store.Token == "replacement-token" && !f.Synchronization.IsBusy,
            "Reconnect reported success for a different reentrant login identity.");
    }
    private static async Task FrozenLoginReceipt()
    {
        ClientSessionWork.Reset(); var f = new Fixture(); f.Transport.PauseAt = "metadata";
        var attempt = f.Begin(); var pending = attempt.PrepareAsync();
        await f.Transport.Paused.Task.WaitAsync(TimeSpan.FromSeconds(5));
        f.Transport.LastLogin!.AuthToken = "reused-response-token";
        f.Transport.LastLogin.AuthenticatedUserId = "reused-response-user";
        f.Transport.LastLogin.SessionId = "reused-response-session";
        f.Transport.Continue.SetResult(true); await pending;
        var result = attempt.Complete(true, true, false); await attempt.DisposeAsync();
        Require(result.Outcome == SessionReconnectOutcome.Succeeded && f.Store.Token == "candidate-token"
            && f.Store.User == "user" && f.Lifecycle.SessionId == "candidate-session", "Metadata wait reused mutable transport login data.");
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
            Require(f.Logout.Calls.Count == test.Item3 && !f.Service.IsRunning && !f.Synchronization.IsBusy, "Wrong cleanup count: " + test.Item1);
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
        Require(f.Service.IsRunning && f.Synchronization.ManualInProgress && f.Logout.Calls.Count == (stage == "prepare" ? 0 : 1)
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
        internal readonly ClientSynchronizationCoordinator Synchronization = new();
        internal readonly SessionLogoutTestTransport Logout = new();
        internal readonly SessionReconnectTestTransport Transport;
        internal readonly ClientSessionLifecycle Lifecycle;
        internal readonly ClientSessionReconnect Service;
        internal Fixture(string scenario = "success")
        {
            Transport = new(scenario);
            Lifecycle = new(Store, Logout, new ClientSessionConnection(new Stream()));
            Lifecycle.Ended += Synchronization.Reset;
            Service = new(Transport, Lifecycle, Synchronization);
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
        internal string? Failure;
        public ClashOfRimClientNetworkContext ReadContext() => new(Url, User, Colony, Snapshot, null, "password", Token);
        public void Write(string? userId, string? displayName, string authToken)
        {
            if (Failure == "before") throw new InvalidOperationException("store failure");
            User = userId ?? User; Token = authToken; Writes++;
            if (Failure == "after") throw new InvalidOperationException("store failure");
        }
    }
    private sealed class Stream : IClientSessionStreamTransport
    {
        public Task<ClashOfRimClientNetworkResult<ModSessionStreamClosedDto>> StreamAsync(ClashOfRimClientNetworkContext context, string sessionId,
            long notificationVersion, long worldVersion, Action<ModSessionStreamEvent> onEvent, CancellationToken cancellationToken) =>
            Task.FromResult(ClashOfRimClientNetworkResult<ModSessionStreamClosedDto>.Ok(new(true)));
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
