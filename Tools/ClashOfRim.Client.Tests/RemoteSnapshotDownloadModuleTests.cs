using System;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Maps;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using AIRsLight.ClashOfRim.Modules.Transport;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.RemoteMaps;

internal static class RemoteSnapshotDownloadModuleTests
{
    private static readonly byte[] Bytes = { 0, 127, 128, 255 };
    internal static void Run() => RunAsync().GetAwaiter().GetResult();

    private static async Task RunAsync()
    {
        var culture = CultureInfo.CurrentCulture;
        try
        {
            ClientSessionWork.Reset();
            foreach (string language in new[] { "en-US", "zh-CN", "fr-FR", "tr-TR" })
            foreach (bool facade in new[] { false, true })
            foreach (string? credential in new[] { null, "", "token-é" })
            foreach (string? scope in new[] { null, "Scout", "FriendlyObservation", "RaidObservation", "RaidPreparation", "RaidTarget", "RaidBattle" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(language);
                using var f = new Fixture(facade, credential, scope);
                var result = await f.Download();
                Check(f.Calls == 2 && Open(result).Payload.SequenceEqual(Bytes), "Download changed bytes or request count.");
                Check(Open(result).SnapshotId == "server-é", "Payload did not use the metadata snapshot ID.");
                f.Metadata.SnapshotId = null;
                result = await f.Download();
                Check(Open(result).SnapshotId == "package-é", "Missing top-level ID did not use package ID.");
            }
            foreach (bool facade in new[] { false, true })
            {
                using var f = new Fixture(facade, "token", "RaidObservation");
                foreach (string? id in new[] { "", " ", null })
                {
                    f.Metadata.SnapshotId = id; f.Metadata.Package!.SnapshotId = null!;
                    int before = f.Calls;
                    Failure(await f.Download(), ClientRemoteSnapshotFailureKind.MissingSnapshot);
                    Check(f.Calls == before + 1, "Missing snapshot still fetched a payload.");
                }
                f.Metadata.SnapshotId = ""; f.Metadata.Package!.SnapshotId = "package-é";
                Failure(await f.Download(), ClientRemoteSnapshotFailureKind.MissingSnapshot);
                f.Metadata.SnapshotId = "server-é";
                f.Metadata.Package = null;
                Failure(await f.Download(), ClientRemoteSnapshotFailureKind.MissingSnapshot);
                f.Metadata.Package = new() { SnapshotId = "package-é" };
                f.Metadata.Result = new() { Accepted = false, Message = "denied-é" };
                int count = f.Calls;
                Check(Failure(await f.Download(), ClientRemoteSnapshotFailureKind.Metadata).Message == "denied-é", "Lost protocol denial message.");
                Check(f.Calls == count + 1, "Rejected metadata fetched payload.");
                f.Metadata.Result = null;
                Failure(await f.Download(), ClientRemoteSnapshotFailureKind.Metadata);
                f.Metadata.Result = new() { Accepted = true };
                f.Malformed = true; count = f.Calls;
                Failure(await f.Download(), ClientRemoteSnapshotFailureKind.Metadata);
                Check(f.Calls == count + 1, "Malformed metadata fetched payload.");
                f.Malformed = false;
                foreach (bool payload in new[] { false, true })
                {
                    f.FailPayload = payload; f.FailMetadata = !payload; count = f.Calls;
                    Check(Failure(await f.Download(), payload ? ClientRemoteSnapshotFailureKind.Payload : ClientRemoteSnapshotFailureKind.Metadata).Code == "HttpError", "Lost HTTP failure.");
                    Check(f.Calls == count + (payload ? 2 : 1), "Download retried a failed request.");
                }
                f.FailMetadata = f.FailPayload = false; f.EmptyPayload = true;
                Check(Open(await f.Download()).Payload.Length == 0, "Download layer changed the empty-payload contract.");
                using var cancelled = new CancellationTokenSource(); cancelled.Cancel(); count = f.Calls;
                Failure(await f.Download(cancelled.Token), ClientRemoteSnapshotFailureKind.Metadata);
                Check(f.Calls == count, "Cancelled download reached HTTP.");
                using var unconfigured = new Fixture(facade, "token", null, configured: false);
                Check(Failure(await unconfigured.Download(), ClientRemoteSnapshotFailureKind.Metadata).Code == "NotConfigured"
                    && unconfigured.Calls == 0, "Unconfigured download reached HTTP.");
                await CheckInterrupted(facade, payload: false, resetSession: false);
                await CheckInterrupted(facade, payload: true, resetSession: false);
                await CheckInterrupted(facade, payload: false, resetSession: true);
                await CheckInterrupted(facade, payload: true, resetSession: true);
            }
            // Public request DTOs are mutable. Both requests must use one captured target and event scope.
            using (var f = new Fixture(true, "original", "RaidPreparation"))
            {
                f.OnMetadata = () => { f.Request.UserId = "other"; f.Request.ColonyId = "other";
                    f.Request.AuthorizationEventId = "other-event"; f.Request.AuthorizationScope = "Scout"; };
                Open(await f.Download());
            }
            await CheckGenericFacade();
            await CheckProgress();
            CheckMissingResponses();
        }
        finally { CultureInfo.CurrentCulture = culture; ClientSessionWork.Reset(); }
    }

    private static async Task CheckProgress()
    {
        foreach (string scenario in new[] { "success", "metadata-failure", "rejected", "missing-id", "payload-failure", "cancel", "throw" })
        {
            using var f = new Fixture(false, "token", null);
            using var cancel = new CancellationTokenSource();
            f.FailMetadata = scenario == "metadata-failure"; f.FailPayload = scenario == "payload-failure";
            if (scenario == "rejected") f.Metadata.Result!.Accepted = false;
            if (scenario == "missing-id") f.Metadata.SnapshotId = "";
            int calls = 0;
            try
            {
                var result = await f.DownloadProgress(id =>
                {
                    calls++;
                    Check(f.Calls == 1 && id == "server-é", "Progress was not between matching metadata and payload requests.");
                    if (scenario == "cancel") cancel.Cancel();
                    if (scenario == "throw") throw new InvalidOperationException("progress fault");
                }, cancel.Token);
                Check(scenario != "throw", "Progress exception was hidden.");
                if (scenario == "cancel") Failure(result, ClientRemoteSnapshotFailureKind.Payload);
            }
            catch (InvalidOperationException ex) when (scenario == "throw" && ex.Message == "progress fault") { }
            bool startsPayload = scenario is "success" or "payload-failure" or "cancel" or "throw";
            Check(calls == (startsPayload ? 1 : 0) && f.Calls == (scenario is "success" or "payload-failure" ? 2 : 1),
                "Download progress repeated, reported rejected metadata or initiated a cancelled request.");
        }
    }

    private static async Task CheckInterrupted(bool facade, bool payload, bool resetSession)
    {
        using var f = new Fixture(facade, "old-token", "RaidObservation") { PausePayload = payload, PauseMetadata = !payload };
        using var cancel = new CancellationTokenSource();
        RemoteSnapshotDownloadResult? result = null;
        Task pending = ClientSessionWork.Run(async () => result = await f.Download(cancel.Token));
        await f.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (resetSession) ClientSessionWork.Reset(); else cancel.Cancel();
        f.Release.TrySetResult();
        await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Failure(result!, payload ? ClientRemoteSnapshotFailureKind.Payload : ClientRemoteSnapshotFailureKind.Metadata);
        Check(f.Calls == (payload ? 2 : 1), "Cancellation/session replacement continued with another request.");
    }

    private static async Task CheckGenericFacade()
    {
        using var f = new Fixture(true, "generic-token", null) { ExpectedUser = "actor-é", ExpectedColony = "home", ExpectedEvent = null };
        Check((await f.Facade.DownloadLatestSnapshotAsync()).Success, "Own-colony metadata overload failed.");
        Check((await f.Facade.DownloadLatestSnapshotPayloadAsync("server-é")).Response!.SequenceEqual(Bytes), "Own-colony payload overload failed.");
        f.ExpectedUser = f.ExpectedColony = "";
        Check((await f.Facade.DownloadLatestSnapshotAsync(null!, null!)).Success, "Null target normalization changed.");
        f.ExpectedSnapshot = "";
        Check((await f.Facade.DownloadLatestSnapshotPayloadAsync(null!, null!, null!)).Success, "Null payload identity normalization changed.");
    }

    private static void CheckMissingResponses()
    {
        var missing = ClashOfRimClientNetworkResult<ModDownloadLatestSnapshotResponseDto>.Ok(null!);
        Failure(new(missing, null), ClientRemoteSnapshotFailureKind.Metadata);
        var valid = ClashOfRimClientNetworkResult<ModDownloadLatestSnapshotResponseDto>.Ok(new()
            { Result = new() { Accepted = true }, SnapshotId = "s", Package = new() });
        Check(Failure(new(valid, null), ClientRemoteSnapshotFailureKind.Payload).Code == "MissingPayload", "Missing payload code changed.");
        Check(Failure(new(valid, ClashOfRimClientNetworkResult<byte[]>.Ok(null!)), ClientRemoteSnapshotFailureKind.Payload).Code == "MissingPayload", "Null payload code changed.");
    }

    private static RemoteSnapshotOpenPayload Open(RemoteSnapshotDownloadResult result)
    {
        Check(ClientRemoteSnapshotDownload.TryGetOpenPayload(result, out var opened, out var failure) && failure is null && opened is not null,
            "Expected valid remote snapshot.");
        return opened!;
    }

    private static ClientRemoteSnapshotFailure Failure(RemoteSnapshotDownloadResult result, ClientRemoteSnapshotFailureKind kind)
    {
        Check(!ClientRemoteSnapshotDownload.TryGetOpenPayload(result, out var opened, out var failure) && opened is null && failure?.Kind == kind,
            "Unexpected remote snapshot failure category: " + failure);
        return failure!;
    }

    private sealed class Fixture : HttpMessageHandler, IClientProtocolEnvironment
    {
        internal readonly RemoteSnapshotDownloadRequest Request;
        internal readonly ClashOfRimModNetworkClient Facade;
        internal readonly ModDownloadLatestSnapshotResponseDto Metadata = new()
            { Result = new() { Accepted = true }, SnapshotId = "server-é", Package = new() { SnapshotId = "package-é" } };
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool FailMetadata, FailPayload, Malformed, EmptyPayload, PauseMetadata, PausePayload;
        internal int Calls;
        internal string ExpectedUser = "target-é", ExpectedColony = "remote", ExpectedSnapshot = "server-é";
        internal string? ExpectedEvent = "event-é";
        internal Action? OnMetadata;
        private readonly string? credential, scope;
        private readonly bool facade;
        private readonly HttpClient http;
        private readonly ClientRemoteSnapshotDownload module;
        internal Fixture(bool facade, string? credential, string? scope, bool configured = true)
        {
            this.facade = facade; this.credential = credential; this.scope = scope;
            Request = new() { UserId = ExpectedUser, ColonyId = ExpectedColony, AuthorizationEventId = ExpectedEvent, AuthorizationScope = scope };
            http = new HttpClient(this, disposeHandler: false);
            var owner = new ClashOfRimClientNetworkContext("http://owner/base", configured ? "actor-é" : "", "home", "base", null, null, credential);
            var wrong = new ClashOfRimClientNetworkContext("http://wrong", "wrong", "wrong", "wrong", null, null, "wrong");
            module = new(new ClientSnapshotDownloadProtocol(new ClientProtocolTransport(http, wrong, this), owner, () => "not configured"));
            Facade = new(http, owner);
        }
        internal Task<RemoteSnapshotDownloadResult> Download(CancellationToken token = default) => facade
            ? new RemoteSnapshotDownloadService(Facade).DownloadAsync(Request, token)
            : module.DownloadAsync(new(Request.UserId, Request.ColonyId, Request.AuthorizationEventId, Request.AuthorizationScope), token);

        internal Task<RemoteSnapshotDownloadResult> DownloadProgress(Action<string> progress, CancellationToken token) =>
            module.DownloadAsync(new(Request.UserId, Request.ColonyId, Request.AuthorizationEventId, Request.AuthorizationScope), token, progress);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            bool payload = request.RequestUri!.AbsolutePath.EndsWith(ProtocolContractManifest.Find(ProtocolMessageKind.DownloadLatestSnapshotPayload).Route.TrimStart('/'), StringComparison.Ordinal);
            var kind = payload ? ProtocolMessageKind.DownloadLatestSnapshotPayload : ProtocolMessageKind.DownloadLatestSnapshot;
            Check(request.Method == HttpMethod.Post && request.RequestUri.AbsoluteUri == "http://owner/base/" + ProtocolContractManifest.Find(kind).Route.TrimStart('/'), "Download borrowed another transport context or route.");
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token)); var root = body.RootElement;
            Check(root.GetProperty("userId").GetString() == ExpectedUser && root.GetProperty("colonyId").GetString() == ExpectedColony
                && root.GetProperty("authToken").GetString() == credential && root.GetProperty("authorizationEventId").GetString() == ExpectedEvent
                && root.GetProperty("authorizationScope").GetString() == scope, "Download changed target, principal or authorization scope.");
            bool hasHeader = request.Headers.TryGetValues(MultipartSnapshotTransport.AuthenticationHeaderName, out var header);
            Check(hasHeader == (payload && !string.IsNullOrWhiteSpace(credential)) && (!hasHeader || header!.Single() == credential), "Wrong download authentication header.");
            if (payload) Check(root.GetProperty("snapshotId").GetString() == ExpectedSnapshot, "Payload switched to a newer/unrelated snapshot.");
            else { ExpectedSnapshot = Metadata.SnapshotId ?? Metadata.Package?.SnapshotId ?? ""; OnMetadata?.Invoke(); }
            if (payload ? PausePayload : PauseMetadata)
            { Entered.TrySetResult(); await Release.Task.WaitAsync(TimeSpan.FromSeconds(5)); } // Deliberately allow a late response after cancellation.
            bool fail = payload ? FailPayload : FailMetadata;
            return new(fail ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
            {
                Content = fail ? new StringContent("unavailable-é") : payload
                    ? new ByteArrayContent(EmptyPayload ? Array.Empty<byte>() : Bytes)
                    : new StringContent(Malformed ? "{" : ClientProtocolJson.Serialize(Metadata), Encoding.UTF8, "application/json")
            };
        }
        public string? CurrentLanguage => null;
        public string DeserializationFailure(string type) => type;
        public Action? CaptureSessionUnavailable(AIRsLight.ClashOfRim.ClientNetwork.ClashOfRimClientNetworkContext context, string? sessionId) => null;
        public void MultipartDisposalFailed(string message) { }
        protected override void Dispose(bool disposing) { if (disposing) http.Dispose(); base.Dispose(disposing); }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
