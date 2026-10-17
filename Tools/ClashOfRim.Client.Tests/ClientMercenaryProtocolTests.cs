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
using AIRsLight.ClashOfRim.Modules.Mercenaries;
using AIRsLight.ClashOfRim.Modules.Transport;
using AIRsLight.ClashOfRim.Protocol;

internal static class ClientMercenaryProtocolTests
{
    private static readonly ProtocolMessageKind[] Routes = { ProtocolMessageKind.QuoteMercenary, ProtocolMessageKind.QuoteMercenaryGuard,
        ProtocolMessageKind.HireMercenary, ProtocolMessageKind.HireMercenaryWithSnapshot, ProtocolMessageKind.HireMercenaryGuardWithSnapshot,
        ProtocolMessageKind.ReportMercenaryIncident };
    private static readonly byte[] Payload = { 0, 128, 255, 42 };
    internal static void Run() => RunAsync().GetAwaiter().GetResult();
    private static async Task RunAsync()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            ClientSessionWork.Reset();
            foreach (string culture in new[] { "en-US", "zh-CN", "tr-TR", "fr-FR" })
            foreach (bool facade in new[] { false, true })
            foreach (string? auth in new[] { null, "", "owner-token" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                using var f = new Fixture(facade, auth);
                foreach (var route in Routes)
                {
                    f.Route = route;
                    foreach (bool accepted in new[] { false, true }) { f.Accepted = accepted; Check((await f.Send()).Success, "Mercenary protocol failed."); }
                    f.HttpStatus = HttpStatusCode.ServiceUnavailable; int before = f.Calls;
                    Check((await f.Send()).ErrorCode == "HttpError" && f.Calls == before + 1, "Protocol retried a request internally.");
                    string original = f.LastJson!; f.HttpStatus = HttpStatusCode.OK;
                    Check((await f.Send()).Success && f.Calls == before + 2 && f.LastJson == original, "Retry changed the original intent.");
                    f.Malformed = true; Check(!(await f.Send()).Success, "Malformed JSON was accepted."); f.Malformed = false;
                    using var cancelled = new CancellationTokenSource(); cancelled.Cancel(); before = f.Calls;
                    Check(!(await f.Send(cancelled.Token)).Success && f.Calls == before, "Cancelled command reached HTTP.");
                }
                f.Route = ProtocolMessageKind.ReportMercenaryIncident;
                foreach (string incident in new[] { "Death", "ShuttleDestroyed", "HostileBehavior", "HarmfulSurgery", "OvertimeService", "Completed", "unknown" })
                { f.Incident = incident; Check((await f.Send()).Success, "Incident kind was lost on wire."); }
                foreach (string tier in new[] { "sKiLlEd", "Master", "unknown", "" })
                foreach (var route in new[] { ProtocolMessageKind.QuoteMercenaryGuard, ProtocolMessageKind.HireMercenaryGuardWithSnapshot })
                { f.Tier = tier; f.Route = route; Check((await f.Send()).Success, "Guard tier changed on wire."); }
            }
            foreach (bool facade in new[] { false, true })
            {
                using var f = new Fixture(facade, "auth", configured: false);
                foreach (var route in Routes) { f.Route = route; Check((await f.Send()).ErrorCode == "NotConfigured", "Unconfigured request was not rejected locally."); }
                Check(f.Calls == 0 && f.Snapshot.SnapshotUploadKind == "original", "Unconfigured protocol contacted HTTP or changed metadata.");
                using var noBase = new Fixture(facade, "auth", snapshot: null);
                foreach (var route in Routes) { noBase.Route = route; Check((await noBase.Send()).Success, "Missing base normalization changed."); }
            }
        }
        finally { CultureInfo.CurrentCulture = previous; ClientSessionWork.Reset(); }
    }

    private sealed class Fixture : HttpMessageHandler
    {
        private readonly HttpClient http;
        private readonly ClashOfRimClientNetworkContext context;
        private readonly ClientMercenaryProtocol protocol;
        private readonly ClashOfRimModNetworkClient? facade;
        internal ProtocolMessageKind Route;
        internal bool Accepted = true, Malformed;
        internal int Calls;
        internal string? LastJson;
        internal string Tier = "sKiLlEd", Incident = "Death";
        internal HttpStatusCode HttpStatus = HttpStatusCode.OK;
        internal readonly ModSnapshotPackageMetadataDto Snapshot = new() { SnapshotId = "paid-é", SnapshotUploadKind = "original" };
        internal Fixture(bool legacy, string? token, bool configured = true, string? snapshot = "base-é")
        {
            context = new("http://owner/captured", configured ? "user-é" : "", "colony", snapshot, null, null, token);
            http = new HttpClient(this, disposeHandler: false);
            protocol = new(new ClientProtocolTransport(http, new("http://wrong", "wrong", "wrong", "wrong", null, null, "wrong"), new Environment()), context, () => "not configured");
            if (legacy) facade = new(http, context);
        }
        internal async Task<ClashOfRimClientNetworkResult<object>> Send(CancellationToken token = default)
        {
            const long ticks = 1234567890123;
            var contract = new ModMercenaryContractDto { ContractId = "contract-é", SkillDefName = "Mining", SkillLevel = 14, DurationDays = 9,
                PriceSilver = 345, HarmfulSurgeryFineSilver = 1234, DeathFineSilver = 2345 };
            var guard = new ModMercenaryGuardContractDto { ContractId = "contract-é", Tier = Tier, PriceSilver = 345, PointRatio = .75f };
            return Route switch
            {
                ProtocolMessageKind.QuoteMercenary => Verify(await (facade?.QuoteMercenaryAsync(ticks, "Mining", 14, 9, token) ?? protocol.QuoteMercenaryAsync(ticks, "Mining", 14, 9, token))),
                ProtocolMessageKind.QuoteMercenaryGuard => Verify(await (facade?.QuoteMercenaryGuardAsync(ticks, Tier, token) ?? protocol.QuoteMercenaryGuardAsync(ticks, Tier, token))),
                ProtocolMessageKind.HireMercenary => Verify(await (facade?.HireMercenaryAsync("key-é", ticks, "Mining", 14, 9, token) ?? protocol.HireMercenaryAsync("key-é", ticks, "Mining", 14, 9, token))),
                ProtocolMessageKind.HireMercenaryWithSnapshot => Verify(await (facade?.HireMercenaryWithSnapshotAsync("key-é", ticks, contract, Snapshot, Payload, token) ?? protocol.HireMercenaryWithSnapshotAsync("key-é", ticks, contract, Snapshot, Payload, token))),
                ProtocolMessageKind.HireMercenaryGuardWithSnapshot => Verify(await (facade?.HireMercenaryGuardWithSnapshotAsync("key-é", ticks, guard, Snapshot, Payload, token) ?? protocol.HireMercenaryGuardWithSnapshotAsync("key-é", ticks, guard, Snapshot, Payload, token))),
                _ => Verify(await (facade?.ReportMercenaryIncidentAsync("key-é", ticks, "contract-é", Incident, token) ?? protocol.ReportMercenaryIncidentAsync("key-é", ticks, "contract-é", Incident, token)))
            };
        }
        private ClashOfRimClientNetworkResult<object> Verify<T>(ClashOfRimClientNetworkResult<T> result)
        {
            if (!result.Success) return ClashOfRimClientNetworkResult<object>.Failed(result.ErrorCode ?? "", result.Message ?? "");
            var body = JsonSerializer.SerializeToElement(result.Response, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Check(body.GetProperty("result").GetProperty("accepted").GetBoolean() == Accepted
                && body.GetProperty("result").GetProperty("errorCode").GetInt32() == 2
                && body.GetProperty("result").GetProperty("message").GetString() == "message-é"
                && body.GetProperty("bankStatus").GetProperty("colonyWealth").GetInt32() == 1200, "Response lost protocol or bank fields.");
            if (Route is ProtocolMessageKind.HireMercenary or ProtocolMessageKind.HireMercenaryWithSnapshot or ProtocolMessageKind.HireMercenaryGuardWithSnapshot)
                Check(body.GetProperty("contract").GetProperty("contractId").GetString() == "contract-é"
                    && body.GetProperty("appliedSnapshotId").GetString() == "paid-é" && body.GetProperty("nextLineageToken").GetString() == "lineage", "Hire receipt changed.");
            if (Route == ProtocolMessageKind.QuoteMercenary)
                Check(body.GetProperty("deathFineSilver").GetInt32() == 2345 && body.GetProperty("harmfulSurgeryFineSilver").GetInt32() == 1234
                    && body.GetProperty("skillLevel").GetInt32() == 14 && body.GetProperty("durationDays").GetInt32() == 9, "Quote terms changed.");
            if (Route is ProtocolMessageKind.QuoteMercenary or ProtocolMessageKind.QuoteMercenaryGuard)
                Check(body.GetProperty("priceSilver").GetInt32() == 345, "Quote price changed.");
            if (Route == ProtocolMessageKind.QuoteMercenaryGuard) Check(body.GetProperty("pointRatio").GetSingle() == .75f, "Guard ratio changed.");
            if (Route == ProtocolMessageKind.ReportMercenaryIncident) Check(body.GetProperty("debt").GetProperty("amountSilver").GetInt32() == 1234, "Incident fine changed.");
            return ClashOfRimClientNetworkResult<object>.Ok(result.Response!);
        }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            Check(request.Method == HttpMethod.Post && request.RequestUri!.AbsoluteUri == "http://owner/captured/" + ProtocolContractManifest.Find(Route).Route.TrimStart('/'), "Protocol borrowed another server or route.");
            bool snapshot = Route is ProtocolMessageKind.HireMercenaryWithSnapshot or ProtocolMessageKind.HireMercenaryGuardWithSnapshot;
            string json;
            if (snapshot)
            {
                Check(!request.Headers.TryGetValues(MultipartSnapshotTransport.AuthenticationHeaderName, out var credentials)
                    ? string.IsNullOrWhiteSpace(context.AuthToken) : credentials.Single() == context.AuthToken, "Multipart principal changed.");
                var parts = ((MultipartFormDataContent)request.Content!).ToDictionary(p => p.Headers.ContentDisposition!.Name!.Trim('"'));
                Check(parts.Count == 2 && (await parts["payload"].ReadAsByteArrayAsync(token)).SequenceEqual(Payload), "Snapshot bytes changed.");
                json = await parts["request"].ReadAsStringAsync(token);
            }
            else json = await request.Content!.ReadAsStringAsync(token);
            LastJson = json; using var document = JsonDocument.Parse(json); var root = document.RootElement;
            Check(root.GetProperty("userId").GetString() == context.UserId && root.GetProperty("colonyId").GetString() == context.ColonyId
                && root.GetProperty("authToken").GetString() == context.AuthToken && root.GetProperty("currentSnapshotId").GetString() == (context.CurrentSnapshotId ?? "")
                && root.GetProperty("currentGameTicks").GetInt64() == 1234567890123, "Captured body identity or time changed.");
            if (Route is not (ProtocolMessageKind.QuoteMercenary or ProtocolMessageKind.QuoteMercenaryGuard)) Check(root.GetProperty("idempotencyKey").GetString() == "key-é", "Mutation key changed.");
            if (Route is ProtocolMessageKind.QuoteMercenary or ProtocolMessageKind.HireMercenary or ProtocolMessageKind.HireMercenaryWithSnapshot)
                Check(root.GetProperty("skillDefName").GetString() == "Mining" && root.GetProperty("skillLevel").GetInt32() == 14
                    && root.GetProperty("durationDays").GetInt32() == 9, "Mercenary terms changed.");
            if (Route is ProtocolMessageKind.QuoteMercenaryGuard or ProtocolMessageKind.HireMercenaryGuardWithSnapshot) Check(root.GetProperty("tier").GetString() == Tier, "Protocol normalized a server-owned guard tier.");
            if (snapshot)
            {
                Check(root.GetProperty("requestedContractId").GetString() == "contract-é" && root.GetProperty("expectedPriceSilver").GetInt32() == 345, "Snapshot hire intent changed.");
                var metadata = root.GetProperty("confirmedSnapshot");
                Check(metadata.GetProperty("snapshotId").GetString() == "paid-é" && metadata.GetProperty("snapshotUploadKind").GetString()
                    == (Route == ProtocolMessageKind.HireMercenaryWithSnapshot ? ModSnapshotUploadKinds.MercenaryHireConfirmation : ModSnapshotUploadKinds.MercenaryGuardHireConfirmation), "Wrong snapshot purpose.");
                if (Route == ProtocolMessageKind.HireMercenaryWithSnapshot)
                    Check(root.GetProperty("expectedHarmfulSurgeryFineSilver").GetInt32() == 1234 && root.GetProperty("expectedDeathFineSilver").GetInt32() == 2345, "Fine quote changed.");
                else Check(root.GetProperty("expectedPointRatio").GetSingle() == .75f, "Guard quote changed.");
            }
            if (Route == ProtocolMessageKind.ReportMercenaryIncident) Check(root.GetProperty("contractId").GetString() == "contract-é" && root.GetProperty("incidentKind").GetString() == Incident, "Incident identity changed.");
            string reply = JsonSerializer.Serialize(new { result = new { accepted = Accepted, errorCode = 2, message = "message-é" },
                bankStatus = new { colonyWealth = 1200 }, priceSilver = 345, skillLevel = 14, durationDays = 9, harmfulSurgeryFineSilver = 1234, deathFineSilver = 2345,
                tier = Tier, pointRatio = .75f, contract = new { contractId = "contract-é", skillDefName = "Mining", skillLevel = 14, durationDays = 9, priceSilver = 345, tier = Tier, pointRatio = .75f },
                debt = new { debtId = "fine-é", amountSilver = 1234 }, appliedSnapshotId = "paid-é", nextLineageToken = "lineage" });
            return new(HttpStatus) { Content = new StringContent(Malformed ? "{" : reply, Encoding.UTF8, "application/json") };
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
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
