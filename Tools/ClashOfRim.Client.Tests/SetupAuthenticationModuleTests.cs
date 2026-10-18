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
using AIRsLight.ClashOfRim.Modules.Transport;
using AIRsLight.ClashOfRim.Protocol;

internal static class SetupAuthenticationModuleTests
{
    private static readonly ClashOfRimClientNetworkContext Context = new("http://localhost/base", "alias", "colony", "snapshot", "steam-ticket", "password", null);
    internal static void Run() => RunAsync().GetAwaiter().GetResult();
    private static async Task RunAsync()
    {
        try
        {
            await BorrowingAndValidation();
            await OperationOutcomes();
            await LateReceipt();
            await ConcurrentOwnership();
            await ProtocolAndCleanupDeadline();
            await LoginRetryAndReceipt();
        }
        finally { ClientSessionWork.Reset(); }
    }

    private static async Task BorrowingAndValidation()
    {
        using var fixture = new Fixture();
        var borrowed = new ClashOfRimClientNetworkContext(Context.ServerBaseUrl, "existing", "colony", null, null, null, "borrowed");
        var result = await fixture.Lease.RunAsync(borrowed, "borrow", (context, _, token) =>
        {
            Require(ReferenceEquals(context, borrowed) && !token.IsCancellationRequested, "Borrowed identity was replaced.");
            return Task.FromResult(ClashOfRimClientNetworkResult<string>.Ok("done"));
        }, default);
        Require(result.Success && fixture.Authentication.Logins == 0 && fixture.Authentication.Closed.Count == 0, "Borrowed credentials created/released a session.");
        int business = 0;
        var invalid = new ClashOfRimClientNetworkContext("", "user", "colony", null, null, null, "token");
        result = await fixture.Lease.RunAsync(invalid, "invalid", (_, _, _) => { business++; return Task.FromResult(ClashOfRimClientNetworkResult<string>.Ok("unexpected")); }, default);
        Require(result.ErrorCode == "NotConfigured" && business == 0, "An unconfigured borrowed identity reached business work.");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        result = await fixture.Lease.RunAsync<string>(Context, "canceled", (_, _, _) => throw new Exception("Canceled work ran."), canceled.Token);
        Require(!result.Success && fixture.Authentication.Logins == 0, "Pre-canceled work started login.");
    }

    private static async Task OperationOutcomes()
    {
        foreach (string outcome in new[] { "success", "rejected", "exception", "caller-cancel", "session-reset", "cleanup-rejected" })
        {
            ClientSessionWork.Reset();
            using var fixture = new Fixture();
            using var cancellation = new CancellationTokenSource();
            if (outcome == "cleanup-rejected") fixture.Authentication.CloseResult = ClashOfRimClientNetworkResult<ModLogoutResponseDto>.Failed("HttpError", "cleanup failed");
            var expected = new ApplicationException("business failed");
            bool exceptionObserved = false;
            try
            {
                var result = await fixture.Lease.RunAsync(Context, "setup", (context, _, token) =>
                {
                    Require(context.UserId == "canonical" && context.ColonyId == "colony" && context.AuthToken == "temporary-token"
                        && context.CurrentSnapshotId == "snapshot" && context.SteamAuthTicket == "steam-ticket" && context.OfflinePassword == "password",
                        "Authenticated identity or request context was not retained.");
                    if (outcome == "exception") throw expected;
                    if (outcome == "caller-cancel") cancellation.Cancel();
                    if (outcome == "session-reset") ClientSessionWork.Reset();
                    return Task.FromResult(outcome == "rejected" ? ClashOfRimClientNetworkResult<string>.Failed("Rejected", "business rejected")
                        : ClashOfRimClientNetworkResult<string>.Ok("done"));
                }, cancellation.Token);
                Require(result.Success == (outcome is "success" or "cleanup-rejected"), "Business outcome changed: " + outcome);
            }
            catch (ApplicationException ex) when (outcome == "exception" && ReferenceEquals(ex, expected)) { exceptionObserved = true; }
            Require(exceptionObserved == (outcome == "exception"), "Cleanup hid the business exception.");
            Require(fixture.Authentication.Closed.Count == 1 && fixture.Authentication.Closed[0] == ("canonical", "temporary-session", "temporary-token"),
                "Setup cleanup did not release exactly its own session: " + outcome);
            Require(Context.AuthToken is null && fixture.Environment.CleanupFailures == (outcome == "cleanup-rejected" ? 1 : 0),
                "Temporary credential escaped or cleanup failure was hidden.");
        }
    }

    private static async Task LateReceipt()
    {
        foreach (bool reset in new[] { false, true })
        {
            ClientSessionWork.Reset();
            using var fixture = new Fixture();
            using var cancellation = new CancellationTokenSource();
            var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.Authentication.Open = async (purpose, token, capture) =>
            {
                received.SetResult();
                await release.Task;
                capture!(Receipt("owned-late", "late-token"));
                return ClashOfRimClientNetworkResult<ModLoginResponseDto>.Failed("TaskCanceledException", "owner ended");
            };
            int business = 0;
            var work = fixture.Lease.RunAsync(Context, "late", (_, _, _) => { business++; return Task.FromResult(ClashOfRimClientNetworkResult<string>.Ok("unexpected")); }, cancellation.Token);
            await received.Task;
            if (reset) ClientSessionWork.Reset(); else cancellation.Cancel();
            release.SetResult();
            var result = await work;
            Require(!result.Success && business == 0 && fixture.Authentication.Closed.Single() == ("canonical", "owned-late", "late-token"),
                "A known late receipt leaked its session or ran obsolete business work.");
        }
    }

    private static async Task ConcurrentOwnership()
    {
        ClientSessionWork.Reset();
        using var fixture = new Fixture();
        fixture.Authentication.Open = (purpose, token, capture) =>
        {
            var receipt = Receipt(purpose + "-session", purpose + "-token");
            capture!(receipt);
            return Task.FromResult(ClashOfRimClientNetworkResult<ModLoginResponseDto>.Ok(receipt));
        };
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<ClashOfRimClientNetworkResult<string>> Start(string purpose, Task wait) => fixture.Lease.RunAsync(Context, purpose, async (context, _, _) =>
        { await wait; return ClashOfRimClientNetworkResult<string>.Ok(context.AuthToken!); }, default);
        var a = Start("a", first.Task); var b = Start("b", second.Task);
        first.SetResult();
        var resultA = await a;
        Require(!b.IsCompleted && resultA.Response == "a-token" && fixture.Authentication.Closed.Single().Session == "a-session",
            "Finishing one lease affected the other lease's credentials.");
        second.SetResult();
        var resultB = await b;
        Require(resultB.Response == "b-token" && fixture.Authentication.Closed.Count == 2 && fixture.Authentication.Closed[1].Session == "b-session",
            "Concurrent leases shared temporary session state.");
    }

    private static async Task ProtocolAndCleanupDeadline()
    {
        ClientSessionWork.Reset();
        var environment = new Environment();
        bool logoutReachedDeadline = false;
        int logouts = 0;
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            if (request.RequestUri!.AbsolutePath.EndsWith("/login", StringComparison.Ordinal))
                return Json("{\"result\":{\"accepted\":true},\"authenticatedUserId\":\"canonical\",\"sessionId\":\"owned\",\"authToken\":\"secret\"}");
            logouts++;
            Require(body.RootElement.GetProperty("userId").GetString() == "canonical" && body.RootElement.GetProperty("colonyId").GetString() == "colony"
                && body.RootElement.GetProperty("sessionId").GetString() == "owned", "Logout used the caller alias or another session.");
            Require(!token.IsCancellationRequested, "Cleanup inherited obsolete session cancellation.");
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) { logoutReachedDeadline = true; throw; }
            throw new Exception("Unreachable");
        }));
        var transport = new ClientProtocolTransport(http, Context, environment);
        var protocol = new ClientSessionProtocol(transport, environment, environment);
        var lease = new ClientSetupAuthenticationLease(protocol, transport, environment, TimeSpan.FromMilliseconds(30));
        var result = await lease.RunAsync(Context, "bounded", (_, _, _) =>
        {
            ClientSessionWork.Reset();
            return Task.FromResult(ClashOfRimClientNetworkResult<string>.Ok("stale"));
        }, default).WaitAsync(TimeSpan.FromSeconds(2));
        Require(!result.Success && logoutReachedDeadline && logouts == 1 && environment.CleanupFailures == 1,
            "Bounded cleanup did not observe/report timeout after session reset.");
    }

    private static async Task LoginRetryAndReceipt()
    {
        foreach (string mode in new[] { "retry", "explicit", "missing-manifest", "accepted-retry-flag", "capture-cancel" })
        {
            ClientSessionWork.Reset();
            using var cancellation = new CancellationTokenSource();
            var environment = new Environment { Manifest = mode == "missing-manifest" ? null : "full-manifest" };
            int calls = 0, acceptedReceipts = 0;
            using var http = new HttpClient(new Handler(async (request, token) =>
            {
                calls++;
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                var root = body.RootElement;
                Require(root.GetProperty("userId").GetString() == "alias" && root.GetProperty("colonyId").GetString() == "colony"
                    && root.GetProperty("currentSnapshotId").GetString() == "snapshot" && root.GetProperty("steamAuthTicket").GetString() == "steam-ticket"
                    && root.GetProperty("password").GetString() == "password" && root.GetProperty("compatibilityDigest").GetString() == "purpose",
                    "Login identity, credentials or purpose changed.");
                if (calls == 1)
                {
                    Require(root.GetProperty("compatibilityManifestId").GetString() == "manifest-id", "Manifest ID missing.");
                    if (mode == "explicit") Require(root.GetProperty("compatibilityManifestJson").GetString() == "explicit", "Explicit manifest was replaced.");
                    else Require(root.GetProperty("compatibilityManifestSummaryJson").GetString() == "summary", "Initial manifest summary missing.");
                }
                else Require(root.GetProperty("compatibilityManifestJson").GetString() == "full-manifest"
                    && root.GetProperty("compatibilityManifestSummaryJson").GetString() == "summary", "Retry lost the captured summary or requested manifest.");
                if (mode is "accepted-retry-flag" or "capture-cancel" || calls == 2)
                    return Json("{\"result\":{\"accepted\":true},\"requiresFullCompatibilityManifest\":true,\"sessionId\":\"owned\",\"authToken\":\"token\"}");
                return Json("{\"result\":{\"accepted\":false},\"requiresFullCompatibilityManifest\":true,\"requestedCompatibilityPackageIds\":[\"a\",\"b\"]}");
            }));
            var protocol = new ClientSessionProtocol(new ClientProtocolTransport(http, Context, environment), environment, environment);
            var result = await protocol.LoginAsync(Context, "purpose", mode == "explicit" ? "explicit" : null, cancellation.Token, receipt =>
            {
                if (receipt.Result?.Accepted != true) return;
                acceptedReceipts++;
                if (mode == "capture-cancel") cancellation.Cancel();
            });
            Require(calls == (mode == "retry" ? 2 : 1), "Login retried beyond its single requested manifest exchange.");
            Require(acceptedReceipts == (mode is "retry" or "accepted-retry-flag" or "capture-cancel" ? 1 : 0), "Credential receipt ownership changed.");
            if (mode is "retry" or "missing-manifest") Require(environment.Requested!.SequenceEqual(new[] { "a", "b" }), "Requested package IDs were lost.");
            if (mode == "capture-cancel") Require(!result.Success && cancellation.IsCancellationRequested, "Receipt capture made obsolete login usable.");
            else if (mode == "missing-manifest") Require(result.ErrorCode == "CompatibilityManifestBuildFailed", "Missing manifest failure changed.");
            else Require(result.Success, "Login transport failed: " + result.Message);
        }
    }

    private static ModLoginResponseDto Receipt(string session = "temporary-session", string token = "temporary-token") =>
        new() { Result = new ModProtocolResponseDto { Accepted = true }, AuthenticatedUserId = "canonical", SessionId = session, AuthToken = token };
    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    private sealed class Handler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send;
        internal Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) => this.send = send;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }
    private sealed class Fixture : IDisposable
    {
        internal readonly Environment Environment = new();
        internal readonly Authentication Authentication = new();
        private readonly HttpClient http = new(new Handler((_, _) => throw new Exception("Unexpected HTTP request.")));
        internal ClientSetupAuthenticationLease Lease { get; }
        internal Fixture() => Lease = new(Authentication, new ClientProtocolTransport(http, Context, Environment), Environment);
        public void Dispose() => http.Dispose();
    }
    private sealed class Authentication : IClientSessionAuthentication
    {
        internal int Logins;
        internal readonly List<(string User, string Session, string Token)> Closed = new();
        internal Func<string, CancellationToken, Action<ModLoginResponseDto>?, Task<ClashOfRimClientNetworkResult<ModLoginResponseDto>>>? Open;
        internal ClashOfRimClientNetworkResult<ModLogoutResponseDto> CloseResult = ClashOfRimClientNetworkResult<ModLogoutResponseDto>.Ok(new() { Result = new ModProtocolResponseDto { Accepted = true } });
        public Task<ClashOfRimClientNetworkResult<ModLoginResponseDto>> LoginAsync(ClashOfRimClientNetworkContext context, string purpose, string? manifest,
            CancellationToken token, Action<ModLoginResponseDto>? captureReceipt = null)
        {
            Logins++;
            if (Open is not null) return Open(purpose, token, captureReceipt);
            var receipt = Receipt(); captureReceipt!(receipt);
            return Task.FromResult(ClashOfRimClientNetworkResult<ModLoginResponseDto>.Ok(receipt));
        }
        public Task<ClashOfRimClientNetworkResult<ModLogoutResponseDto>> LogoutAsync(ClashOfRimClientNetworkContext context, string? sessionId,
            CancellationToken token, bool bindToSession = true)
        {
            Require(!bindToSession && !token.IsCancellationRequested && token.CanBeCanceled, "Cleanup cancellation/ownership policy changed.");
            Closed.Add((context.UserId, sessionId!, context.AuthToken!));
            return Task.FromResult(CloseResult);
        }
    }
    private sealed class Environment : IClientProtocolEnvironment, IClientCompatibilityManifestSource, IClientAuthenticationDiagnostics
    {
        internal string? Manifest = "full-manifest";
        internal IReadOnlyCollection<string>? Requested;
        internal int CleanupFailures;
        public string? CurrentLanguage => null;
        public string? Id => "manifest-id";
        public string? SummaryJson => "summary";
        public string NotConfiguredMessage => "not configured";
        public string ManifestBuildFailedMessage => "manifest failed";
        public string DeserializationFailure(string type) => "invalid " + type;
        public string? BuildJson(IReadOnlyCollection<string>? packageIds) { Requested = packageIds; return Manifest; }
        public Action? CaptureSessionUnavailable(AIRsLight.ClashOfRim.ClientNetwork.ClashOfRimClientNetworkContext context, string? sessionId) => () => throw new Exception("Unexpected expiry.");
        public void MultipartDisposalFailed(string message) => throw new Exception(message);
        public void CleanupFailed(string? code, string? message) => CleanupFailures++;
    }
}
