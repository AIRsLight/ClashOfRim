using System;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Events;
using AIRsLight.ClashOfRim.Modules.Support;
using AIRsLight.ClashOfRim.Modules.Transport;
using AIRsLight.ClashOfRim.Protocol;

internal static class SupportClosureTransportTests
{
    public static void Run() => Verify().GetAwaiter().GetResult();
    private static async Task Verify()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            ClientSessionWork.Reset();
            foreach (string culture in new[] { "en-US", "zh-CN", "tr-TR", "fr-FR" })
            foreach (bool facade in new[] { false, true })
            foreach (string? auth in new[] { null, "", "support-token" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                using var f = new Fixture(facade, auth);
                foreach (bool accepted in new[] { false, true })
                {
                    f.Accepted = accepted;
                    foreach (bool permanent in new[] { false, true })
                    { f.Permanent = permanent; Check(await f.Create(), "Support creation receipt changed."); }
                    foreach (bool dead in new[] { false, true })
                    { f.Dead = dead; Check(await f.Finish(), "Support closure receipt changed."); }
                    Check(await f.Reject(), "Support rejection receipt changed.");
                }
            }
            foreach (bool facade in new[] { false, true })
            {
                using var missingBase = new Fixture(facade, "auth", snapshot: null);
                Check(await missingBase.Create() && await missingBase.Finish(), "Missing base normalization changed.");
                using var unconfigured = new Fixture(facade, "auth", configured: false);
                Check(!await unconfigured.Create() && !await unconfigured.Finish() && unconfigured.Calls == 0
                    && unconfigured.Snapshot.SnapshotUploadKind == "original", "Unconfigured request contacted HTTP or mutated metadata.");
            }
        }
        finally { CultureInfo.CurrentCulture = previous; ClientSessionWork.Reset(); }
    }
    private sealed class Fixture : HttpMessageHandler
    {
        private static readonly byte[] Payload = { 0, 255, 128, 42 };
        private readonly HttpClient http;
        private readonly ClashOfRimClientNetworkContext context;
        private readonly ClientSupportProtocol protocol;
        private readonly ClientEventDecisionProtocol decisions;
        private readonly ClashOfRimModNetworkClient? facade;
        private readonly ModCrossMapPawnReferenceDto reference = new() { GlobalId = "pawn-é", SourceSnapshotId = "origin", Name = "支援者" };
        private readonly ModPawnExchangePackageDto package = new() { Scribe = new() { Xml = "<pawn><state>复杂状态-é</state></pawn>", XmlSha256 = "scribe-hash" } };
        internal readonly ModSnapshotPackageMetadataDto Snapshot = new() { SnapshotId = "confirmed-é", PreviousSnapshotId = "base-é", LineageToken = "lineage", SnapshotUploadKind = "original" };
        private ProtocolMessageKind route;
        internal int Calls;
        internal bool Accepted = true, Permanent, Dead;
        internal Fixture(bool legacy, string? auth, bool configured = true, string? snapshot = "base-é")
        {
            context = new("http://owner/captured", configured ? "recipient" : "", "colony", snapshot, null, null, auth);
            http = new HttpClient(this, disposeHandler: false);
            var transport = new ClientProtocolTransport(http, new("http://wrong", "wrong", "wrong", "wrong", null, null, "wrong"), new Environment());
            protocol = new(transport, context, () => "not configured");
            decisions = new(transport, () => "not configured");
            if (legacy) facade = new(http, context);
        }
        internal async Task<bool> Create()
        {
            route = ProtocolMessageKind.CreateSupportPawnWithSnapshot;
            var target = new ModEventTargetContextDto { WorldObjectId = "WorldObject1", MapUniqueId = "Map2", Tile = 0, LandingMode = "MapEdge" };
            var result = await (facade?.CreateSupportPawnWithSnapshotAsync("send-é", "target", "target-colony", "target-base", "pawn-é", "支援者", reference,
                package, target, 0, "Caravan1", Permanent, Permanent ? null : 3, long.MaxValue - 1, true, Snapshot, Payload)
                ?? protocol.CreateSupportPawnWithSnapshotAsync("send-é", "target", "target-colony", "target-base", "pawn-é", "支援者", reference,
                    package, target, 0, "Caravan1", Permanent, Permanent ? null : 3, long.MaxValue - 1, true, Snapshot, Payload));
            if (!context.IsConfigured) { Check(result.ErrorCode == "NotConfigured", "Wrong local rejection."); return false; }
            var response = result.Response;
            return result.Success && response is not null && response.Result?.Accepted == Accepted && response.EventId == "support"
                && response.AppliedSnapshotId == "confirmed-é" && response.NextLineageToken == "next" && response.DeliverySemantics == 2;
        }
        internal async Task<bool> Finish()
        {
            route = ProtocolMessageKind.FinishSupportPawn;
            var result = await (facade?.FinishSupportPawnAsync("finish-é", "support", "Expired", "pawn-é", "支援者", Dead, Dead ? null : package)
                ?? protocol.FinishSupportPawnAsync("finish-é", "support", "Expired", "pawn-é", "支援者", Dead, Dead ? null : package));
            if (!context.IsConfigured) { Check(result.ErrorCode == "NotConfigured", "Wrong local rejection."); return false; }
            return result.Success && result.Response?.Result?.Accepted == Accepted && result.Response.ReturnEventId == "return" && result.Response.Created;
        }
        internal async Task<bool> Reject()
        {
            route = ProtocolMessageKind.RejectSupportPawn;
            var result = await (facade?.RejectSupportPawnAsync("support", "拒绝") ?? decisions.RejectSupportAsync(context, "support", "拒绝", default));
            return result.Success && result.Response?.Result?.Accepted == Accepted && result.Response.ReturnEventId == "return" && result.Response.ReturnEventCreated;
        }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Check(request.Method == HttpMethod.Post && request.RequestUri!.AbsoluteUri == "http://owner/captured/" + ProtocolContractManifest.Find(route).Route.TrimStart('/'), "Support protocol borrowed another server or route.");
            string body;
            if (route == ProtocolMessageKind.CreateSupportPawnWithSnapshot)
            {
                Check(!request.Headers.TryGetValues(MultipartSnapshotTransport.AuthenticationHeaderName, out var credentials)
                    ? string.IsNullOrWhiteSpace(context.AuthToken) : credentials.Single() == context.AuthToken, "Multipart principal changed.");
                var parts = ((MultipartFormDataContent)request.Content!).ToDictionary(p => p.Headers.ContentDisposition!.Name!.Trim('"'));
                Check(parts.Count == 2 && (await parts["payload"].ReadAsByteArrayAsync(cancellationToken)).SequenceEqual(Payload), "Snapshot bytes changed.");
                body = await parts["request"].ReadAsStringAsync(cancellationToken);
            }
            else body = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var json = JsonDocument.Parse(body);
            var data = json.RootElement;
            if (route == ProtocolMessageKind.CreateSupportPawnWithSnapshot)
            {
                Strings(data.GetProperty("confirmedSnapshot"), ("snapshotId", "confirmed-é"), ("previousSnapshotId", "base-é"), ("lineageToken", "lineage"),
                    ("snapshotUploadKind", ModSnapshotUploadKinds.SupportPawnCreationConfirmation));
                data = data.GetProperty("supportPawn");
                Strings(data.GetProperty("actor"), ("userId", context.UserId), ("colonyId", context.ColonyId), ("snapshotId", context.CurrentSnapshotId));
                Strings(data.GetProperty("target"), ("userId", "target"), ("colonyId", "target-colony"), ("snapshotId", "target-base"));
                Strings(data, ("idempotencyKey", "send-é"), ("sourceSnapshotId", context.CurrentSnapshotId ?? ""), ("pawnGlobalKey", "pawn-é"), ("pawnName", "支援者"), ("sourceCaravanLoadId", "Caravan1"));
                Strings(data.GetProperty("pawnReference"), ("globalId", "pawn-é"), ("sourceSnapshotId", "origin"), ("name", "支援者"));
                Strings(data.GetProperty("targetContext"), ("worldObjectId", "WorldObject1"), ("mapUniqueId", "Map2"), ("landingMode", "MapEdge"));
                Check(data.GetProperty("sourceTile").GetInt32() == 0 && data.GetProperty("targetContext").GetProperty("tile").GetInt32() == 0
                    && data.GetProperty("temporaryControl").GetBoolean() && data.GetProperty("expectedReturnAtUtc").ValueKind == JsonValueKind.Null
                    && data.GetProperty("permanentSupport").GetBoolean() == Permanent && data.GetProperty("autoReturnOnSettlement").GetBoolean()
                    && data.GetProperty("expiresAtGameTicks").GetInt64() == long.MaxValue - 1
                    && (Permanent ? data.GetProperty("supportDurationDays").ValueKind == JsonValueKind.Null : data.GetProperty("supportDurationDays").GetInt32() == 3), "Support lifecycle terms changed.");
            }
            else
            {
                Strings(data, ("eventId", "support"), ("userId", context.UserId), ("colonyId", context.ColonyId), ("currentSnapshotId", context.CurrentSnapshotId ?? ""), ("authToken", context.AuthToken));
                if (route == ProtocolMessageKind.RejectSupportPawn) Strings(data, ("reason", "拒绝"));
                else
                {
                    Strings(data, ("idempotencyKey", "finish-é"), ("finishReason", "Expired"), ("pawnGlobalKey", "pawn-é"), ("pawnName", "支援者"));
                    Check(data.GetProperty("pawnDead").GetBoolean() == Dead, "Support death flag changed.");
                    if (Dead) Check(data.GetProperty("pawnPackage").ValueKind == JsonValueKind.Null, "Dead pawn acquired a package.");
                }
            }
            if (route == ProtocolMessageKind.CreateSupportPawnWithSnapshot || route == ProtocolMessageKind.FinishSupportPawn && !Dead)
                Strings(data.GetProperty("pawnPackage").GetProperty("scribe"), ("xml", package.Scribe!.Xml), ("xmlSha256", package.Scribe.XmlSha256));
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { result = new { accepted = Accepted },
                eventId = "support", returnEventId = "return", created = true, returnEventCreated = true, deliverySemantics = 2, appliedSnapshotId = "confirmed-é", nextLineageToken = "next" })) };
        }
        protected override void Dispose(bool disposing) { if (disposing) http.Dispose(); base.Dispose(disposing); }
    }
    private sealed class Environment : IClientProtocolEnvironment
    {
        public string? CurrentLanguage => null;
        public string DeserializationFailure(string type) => type;
        public void SessionExpired(string message, string? token) { }
        public void MultipartDisposalFailed(string message) { }
    }
    private static void Strings(JsonElement data, params (string Field, string? Expected)[] fields)
    { foreach (var (field, expected) in fields) Check(data.GetProperty(field).GetString() == expected, "Support field mismatch: " + field); }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
