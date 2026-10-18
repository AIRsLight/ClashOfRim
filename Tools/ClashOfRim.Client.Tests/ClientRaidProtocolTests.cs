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
using AIRsLight.ClashOfRim.Modules.Raids;
using AIRsLight.ClashOfRim.Modules.Transport;
using AIRsLight.ClashOfRim.Protocol;

internal static class ClientRaidProtocolTests
{
    internal static void Run() => RunAsync().GetAwaiter().GetResult();
    private static async Task RunAsync()
    {
        var culture = CultureInfo.CurrentCulture;
        try
        {
            ClientSessionWork.Reset();
            foreach (string language in new[] { "en-US", "zh-CN", "fr-FR", "tr-TR" })
            foreach (bool facade in new[] { false, true })
            foreach (string? token in new[] { null, "", "raid-token" })
            foreach (string opponent in new[] { "Player", "AutomaticFaction" })
            foreach (bool optional in new[] { false, true })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(language);
                using var f = new Fixture(facade, token) { Opponent = opponent, Optional = optional };
                foreach (bool create in new[] { false, true })
                foreach (bool accepted in new[] { false, true })
                { f.Create = create; f.Accepted = accepted; Check(await f.Send(), "Raid request failed."); }
            }
            foreach (bool facade in new[] { false, true })
            foreach (bool create in new[] { false, true })
            {
                using var f = new Fixture(facade, "auth") { Create = create };
                f.Status = HttpStatusCode.ServiceUnavailable;
                Check(!await f.Send() && f.Calls == 1, "Failed raid command retried automatically.");
                string original = f.Json!; f.Status = HttpStatusCode.OK;
                Check(await f.Send() && f.Calls == 2 && original == f.Json, "Raid retry changed intent or snapshot.");
                f.Malformed = true; Check(!await f.Send(), "Invalid raid JSON accepted."); f.Malformed = false;
                using var cancellation = new CancellationTokenSource(); cancellation.Cancel(); int before = f.Calls;
                Check(!await f.Send(cancellation.Token) && before == f.Calls, "Canceled raid reached HTTP.");
                using var missing = new Fixture(facade, "auth", configured: false) { Create = create };
                Check(!await missing.Send() && missing.Calls == 0 && missing.Snapshot.SnapshotUploadKind == "original", "Unconfigured request changed snapshot/contacted HTTP.");
            }
        }
        finally { CultureInfo.CurrentCulture = culture; ClientSessionWork.Reset(); }
    }
    private sealed class Fixture : HttpMessageHandler, IClientProtocolEnvironment
    {
        internal bool Create, Optional, Accepted = true, Malformed;
        internal string Opponent = "Player";
        internal int Calls;
        internal string? Json;
        internal HttpStatusCode Status = HttpStatusCode.OK;
        internal readonly ModSnapshotPackageMetadataDto Snapshot = new() { SnapshotId = "confirmed-é", LineageToken = "lineage", SnapshotUploadKind = "original" };
        private readonly byte[] bytes = { 0, 127, 128, 255 };
        private readonly ModThingReferenceDto[] cargo = { new() { GlobalKey = "cargo", DefName = "Rifle", StackCount = 2,
            Metadata = new() { ["complex"] = "state-é" }, ThingPackage = new() { Scribe = new() { XmlGzipBase64 = "packed", UncompressedBytes = 99 } } } };
        private readonly string? auth;
        private readonly HttpClient http;
        private readonly ClientRaidProtocol protocol;
        private readonly ClashOfRimModNetworkClient? facade;
        internal Fixture(bool legacy, string? auth, bool configured = true)
        {
            this.auth = auth; http = new(this, disposeHandler: false);
            var context = new ClashOfRimClientNetworkContext("http://owner/base", configured ? "actor-é" : "", "colony", "base", null, null, auth);
            var wrong = new ClashOfRimClientNetworkContext("http://wrong", "wrong", "wrong", "wrong", null, null, "wrong");
            protocol = new(new ClientProtocolTransport(http, wrong, this), context, () => "not configured");
            if (legacy) facade = new(http, context);
        }
        internal async Task<bool> Send(CancellationToken token = default)
        {
            int? tile = Optional ? null : 37;
            if (!Create)
            {
                var result = await (facade?.PrepareRaidAsync("key-é", "target", "target-colony", "world", "map", tile, !Optional, Opponent, token)
                    ?? protocol.PrepareRaidAsync("key-é", "target", "target-colony", "world", "map", tile, !Optional, Opponent, token));
                if (result.Success) Check(result.Response?.RaidPreparationId == "prepared" && result.Response.RaidEventId == "event"
                    && result.Response.GuardDeployment?.Points == 100 && result.Response.RaidMaxDurationMinutes == 0.25
                    && result.Response.Result?.Accepted == Accepted, "Preparation response lost its identity, guards or durations.");
                return result.Success;
            }
            var keys = new[] { "pawn-é", "pawn-二" };
            var created = await (facade?.CreateRaidWithSnapshotAsync("key-é", Optional ? null : "prepared", "target", "target-colony",
                Optional ? null : "defender-snapshot", "world", "map", tile, !Optional, Optional, 1200, Optional ? null : "deadline",
                keys, cargo, Snapshot, bytes, Optional ? null : "guard", Opponent, token)
                ?? protocol.CreateRaidWithSnapshotAsync("key-é", Optional ? null : "prepared", "target", "target-colony",
                    Optional ? null : "defender-snapshot", "world", "map", tile, !Optional, Optional, 1200, Optional ? null : "deadline",
                    keys, cargo, Snapshot, bytes, Optional ? null : "guard", Opponent, token));
            if (created.Success) Check(created.Response?.Result?.Accepted == Accepted && created.Response.AppliedSnapshotId == "confirmed"
                && created.Response.NextLineageToken == "next" && created.Response.RaidDeadlineUtc == "battle-deadline", "Creation response lost its receipt or battle deadline.");
            return created.Success;
        }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            var kind = Create ? ProtocolMessageKind.CreateRaidWithSnapshot : ProtocolMessageKind.PrepareRaid;
            Check(request.Method == HttpMethod.Post && request.RequestUri!.AbsoluteUri == "http://owner/base/" + ProtocolContractManifest.Find(kind).Route.TrimStart('/'), "Wrong raid route or context.");
            bool header = request.Headers.TryGetValues(MultipartSnapshotTransport.AuthenticationHeaderName, out var credentials);
            Check(header == (Create && !string.IsNullOrWhiteSpace(auth)) && (!header || credentials!.Single() == auth), "Wrong raid credentials.");
            if (Create)
            {
                var parts = ((MultipartFormDataContent)request.Content!).ToDictionary(p => p.Headers.ContentDisposition!.Name!.Trim('"'));
                Check(parts.Count == 2 && (await parts["payload"].ReadAsByteArrayAsync(token)).SequenceEqual(bytes), "Raid snapshot bytes changed.");
                Json = await parts["request"].ReadAsStringAsync(token);
            }
            else Json = await request.Content!.ReadAsStringAsync(token);
            using var document = JsonDocument.Parse(Json); var root = document.RootElement;
            var raid = Create ? root.GetProperty("raid") : root;
            var attacker = raid.GetProperty("attacker"); var defender = raid.GetProperty("defender");
            Check(raid.GetProperty("idempotencyKey").GetString() == "key-é" && attacker.GetProperty("userId").GetString() == "actor-é"
                && attacker.GetProperty("colonyId").GetString() == "colony" && attacker.GetProperty("snapshotId").GetString() == "base"
                && defender.GetProperty("userId").GetString() == "target" && defender.GetProperty("colonyId").GetString() == "target-colony"
                && defender.GetProperty("snapshotId").GetString() == (Create && !Optional ? "defender-snapshot" : null)
                && raid.GetProperty("targetMapId").GetString() == "map" && raid.GetProperty("targetWorldObjectId").GetString() == "world"
                && raid.GetProperty("isHostile").GetBoolean() == !Optional && raid.GetProperty("opponentKind").GetString() == Opponent
                && (Optional ? raid.GetProperty("targetTile").ValueKind == JsonValueKind.Null : raid.GetProperty("targetTile").GetInt32() == 37), "Raid intent changed.");
            if (Create)
            {
                var item = raid.GetProperty("carriedThings")[0];
                Check(root.GetProperty("confirmedSnapshot").GetProperty("snapshotUploadKind").GetString() == ModSnapshotUploadKinds.RaidCreationConfirmation
                    && root.GetProperty("confirmedSnapshot").GetProperty("lineageToken").GetString() == "lineage"
                    && raid.GetProperty("defenderOnline").GetBoolean() == Optional && raid.GetProperty("defenderWealth").GetInt32() == 1200
                    && raid.GetProperty("defenderSnapshotId").GetString() == (Optional ? "" : "defender-snapshot")
                    && raid.GetProperty("raidPreparationId").GetString() == (Optional ? null : "prepared")
                    && raid.GetProperty("guardDeploymentId").GetString() == (Optional ? null : "guard")
                    && raid.GetProperty("defenderRaidCooldownUntilUtc").GetString() == (Optional ? null : "deadline")
                    && raid.GetProperty("pawnGlobalKeys").EnumerateArray().Select(p => p.GetString()).SequenceEqual(new[] { "pawn-é", "pawn-二" })
                    && item.GetProperty("metadata").GetProperty("complex").GetString() == "state-é"
                    && item.GetProperty("thingPackage").GetProperty("scribe").GetProperty("uncompressedBytes").GetInt32() == 99,
                    "Creation lost preparation, guard, snapshot or complex cargo state.");
            }
            string response = "{\"result\":{\"accepted\":" + (Accepted ? "true" : "false") + "}," + (Create
                ? "\"eventId\":\"event\",\"appliedSnapshotId\":\"confirmed\",\"nextLineageToken\":\"next\",\"raidDeadlineUtc\":\"battle-deadline\"}"
                : "\"raidEventId\":\"event\",\"raidPreparationId\":\"prepared\",\"raidMaxDurationMinutes\":0.25,\"guardDeployment\":{\"points\":100}}" );
            return new(Status) { Content = new StringContent(Malformed ? "{" : response, Encoding.UTF8, "application/json") };
        }
        public string? CurrentLanguage => null;
        public string DeserializationFailure(string type) => type;
        public void SessionExpired(string message, string? token) { }
        public void MultipartDisposalFailed(string message) { }
        protected override void Dispose(bool disposing) { if (disposing) http.Dispose(); base.Dispose(disposing); }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
