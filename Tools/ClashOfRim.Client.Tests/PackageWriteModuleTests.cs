using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Packages;
using AIRsLight.ClashOfRim.Modules.Transport;
using AIRsLight.ClashOfRim.Pawns;
using AIRsLight.ClashOfRim.Protocol;
using HarmonyLib;
using Verse;

internal static class PackageWriteModuleTests
{
    private static readonly ClashOfRimClientNetworkContext Context = new("http://owner/captured", "用户-é", "colony", "base", null, null, "auth");
    private static readonly byte[] Preview = { 0, 127, 128, 255 };
    internal static void Run() => RunAsync().GetAwaiter().GetResult();
    private static async Task RunAsync()
    {
        var harmony = new Harmony("ClashOfRim.Tests.PackageWriteModule");
        foreach (Type[] parameters in new[] { new[] { typeof(string) }, new[] { typeof(string), typeof(NamedArgument[]) } })
            harmony.Patch(AccessTools.Method(typeof(ClashOfRimText), "Key", parameters), prefix: new HarmonyMethod(typeof(PackageWriteModuleTests), nameof(Translate)));
        var original = CultureInfo.CurrentCulture;
        try
        {
            ClientSessionWork.Reset();
            foreach (string culture in new[] { "en-US", "zh-CN", "tr-TR", "fr-FR" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                foreach (bool facade in new[] { false, true }) await Contracts(facade);
                await ResumePackages();
            }
            foreach (bool pawn in new[] { false, true })
            foreach (string failure in new[] { "transport", "rejected", "missing" })
            {
                var writer = new Writer { Failure = failure };
                var items = new[] { Item("first") };
                Exception? error = null;
                try { await Upload(writer, items, pawn); } catch (Exception ex) { error = ex; }
                Check(error is not null && ClientNetworkFailurePolicy.CanRetry(error) == (failure == "transport")
                    && items[0].PawnPackageId is null && items[0].ThingPackageId is null, "Package failure classification or partial state changed.");
                if (failure == "transport") Check(error is ClientProtocolRequestException { Code: "HttpError", ServerMessage: "503 unavailable" }, "Transport details were discarded.");
            }
        }
        finally { CultureInfo.CurrentCulture = original; harmony.UnpatchAll(harmony.Id); ClientSessionWork.Reset(); }
    }
    private static async Task Contracts(bool facade)
    {
        int calls = 0;
        var kinds = new[] { ProtocolMessageKind.StorePawnPackage, ProtocolMessageKind.StorePawnPackagePreview, ProtocolMessageKind.StoreThingPackage };
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            var kind = kinds[calls++];
            Check(request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/captured/" + ProtocolContractManifest.Find(kind).Route.TrimStart('/'), "Package write lost route or captured server.");
            string body;
            if (kind == ProtocolMessageKind.StorePawnPackagePreview)
            {
                var parts = ((MultipartFormDataContent)request.Content!).ToDictionary(p => p.Headers.ContentDisposition!.Name!.Trim('"'));
                Check(request.Headers.GetValues(MultipartSnapshotTransport.AuthenticationHeaderName).Single() == "auth", "Preview borrowed another credential.");
                Check(parts.Keys.OrderBy(k => k).SequenceEqual(new[] { "payload", "request" })
                    && (await parts["payload"].ReadAsByteArrayAsync(token)).SequenceEqual(Preview), "Preview bytes or multipart names changed.");
                body = await parts["request"].ReadAsStringAsync(token);
            }
            else body = await request.Content!.ReadAsStringAsync(token);
            using var json = JsonDocument.Parse(body); var root = json.RootElement; var owner = root.GetProperty("owner");
            Check(owner.GetProperty("userId").GetString() == Context.UserId && owner.GetProperty("colonyId").GetString() == "colony"
                && owner.GetProperty("snapshotId").GetString() == "base", "Package owner changed.");
            if (kind == ProtocolMessageKind.StorePawnPackagePreview) Check(root.GetProperty("pawnPackageId").GetString() == "pawn-id", "Preview lost package identity.");
            else
            {
                Check(root.GetProperty("idempotencyKey").GetString() == "key-é", "Package lost opaque command key.");
                var package = root.GetProperty(kind == ProtocolMessageKind.StorePawnPackage ? "pawnPackage" : "thingPackage");
                Check(package.GetProperty("scribe").GetProperty(kind == ProtocolMessageKind.StorePawnPackage ? "xml" : "xmlGzipBase64").GetString() == "complex-state-é", "Complex package payload changed.");
                Check(!package.TryGetProperty("PendingPreviewPng", out _) && !package.TryGetProperty("pendingPreviewPng", out _), "Client preview entered authoritative JSON.");
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"result\":{\"accepted\":true},\"pawnPackageId\":\"pawn-id\",\"thingPackageId\":\"thing-id\"}", Encoding.UTF8, "application/json") };
        }));
        IClientPackageWriter writer = facade ? new ClashOfRimModNetworkClient(http, Context) : PackageWriter(http, Context);
        var pawn = await writer.StorePawnPackageAsync("key-é", new() { Scribe = new() { Xml = "complex-state-é" }, PendingPreviewPng = Preview });
        var preview = await writer.StorePawnPackagePreviewAsync("pawn-id", Preview);
        var thing = await writer.StoreThingPackageAsync("key-é", new() { Scribe = new() { XmlGzipBase64 = "complex-state-é" } });
        Check(pawn.Response?.PawnPackageId == "pawn-id" && preview.Response?.Result?.Accepted == true && thing.Response?.ThingPackageId == "thing-id" && calls == 3, "Package response mapping changed.");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Check(!(await writer.StorePawnPackageAsync("key", new(), cancelled.Token)).Success
            && !(await writer.StorePawnPackagePreviewAsync("id", Preview, cancelled.Token)).Success
            && !(await writer.StoreThingPackageAsync("key", new(), cancelled.Token)).Success && calls == 3, "Cancelled package reached HTTP.");
        var unconfigured = new ClashOfRimClientNetworkContext("http://owner", "", "colony", "base", null, null, "auth");
        writer = facade ? new ClashOfRimModNetworkClient(http, unconfigured) : PackageWriter(http, unconfigured);
        Check((await writer.StorePawnPackageAsync("key", new())).ErrorCode == "NotConfigured"
            && (await writer.StorePawnPackagePreviewAsync("id", Preview)).ErrorCode == "NotConfigured"
            && (await writer.StoreThingPackageAsync("key", new())).ErrorCode == "NotConfigured" && calls == 3, "Unconfigured package reached HTTP.");
    }
    private static async Task ResumePackages()
    {
        var writer = new Writer { FailSecond = true };
        var source = new[] { Item("first"), Item("second") };
        var items = ClientPackageCopies.CopyThings(source);
        foreach (bool pawn in new[] { true, false })
        {
            try { await Upload(writer, items, pawn); throw new Exception("Partial upload did not fail."); }
            catch (ClientProtocolRequestException) { }
            Check(pawn ? items[0].PawnPackageId == "pawn-first" && items[1].PawnPackageId is null
                : items[0].ThingPackageId == "thing-first" && items[1].ThingPackageId is null, "Completed packages were not retained.");
            writer.FailSecond = false; await Upload(writer, items, pawn); writer.FailSecond = true;
            var calls = pawn ? writer.Pawns : writer.Things;
            string type = pawn ? "pawn" : "thing";
            Check(calls.SequenceEqual(new[] { "intent:" + type + "-package:0:first", "intent:" + type + "-package:1:second", "intent:" + type + "-package:1:second" }), "Package retry changed keys or repeated completed upload.");
        }
        Check(writer.Previews == 2 && source.All(i => i.PawnPackage is not null && i.ThingPackage is not null && i.PawnPackageId is null && i.ThingPackageId is null)
            && items.All(i => i.PawnPackage is null && i.ThingPackage is null), "Upload mutated the source or lost a preview.");
    }
    private static Task Upload(Writer writer, IReadOnlyList<ModThingReferenceDto> things, bool pawn) => pawn
        ? PawnPackageTransferService.StoreThingPawnPackagesAsync(writer, things, "intent", writer.Token)
        : PawnPackageTransferService.StoreThingStatePackagesAsync(writer, things, "intent", writer.Token);
    private static ModThingReferenceDto Item(string id) => new() { GlobalKey = id, PawnPackage = new() { PendingPreviewPng = Preview }, ThingPackage = new() { GlobalKey = id } };
    private static ClientPackageWriter PackageWriter(HttpClient http, ClashOfRimClientNetworkContext owner) => new(new ClientProtocolTransport(http,
        new("http://wrong", "wrong", "wrong", null, null, null, "wrong"), new Environment()), owner, () => "not configured");
    private static bool Translate(string key, ref string __result) { __result = key; return false; }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class Writer : IClientPackageWriter
    {
        internal readonly CancellationToken Token = new CancellationTokenSource().Token;
        internal readonly List<string> Pawns = new(), Things = new();
        internal bool FailSecond;
        internal string Failure = "";
        internal int Previews;
        public Task<ClashOfRimClientNetworkResult<ModStorePawnPackageResponseDto>> StorePawnPackageAsync(string key, ModPawnExchangePackageDto package, CancellationToken cancellationToken = default)
        {
            Check(cancellationToken == Token, "Pawn upload dropped cancellation."); Pawns.Add(key);
            return Task.FromResult(Failed(key) ? ClashOfRimClientNetworkResult<ModStorePawnPackageResponseDto>.Failed("HttpError", "503 unavailable")
                : ClashOfRimClientNetworkResult<ModStorePawnPackageResponseDto>.Ok(new() { Result = new() { Accepted = Failure != "rejected" }, PawnPackageId = Failure == "missing" ? "" : "pawn-" + key.Split(':').Last() }));
        }
        public Task<ClashOfRimClientNetworkResult<ModStoreThingPackageResponseDto>> StoreThingPackageAsync(string key, ModThingStatePackageDto package, CancellationToken cancellationToken = default)
        {
            Check(cancellationToken == Token, "Thing upload dropped cancellation."); Things.Add(key);
            return Task.FromResult(Failed(key) ? ClashOfRimClientNetworkResult<ModStoreThingPackageResponseDto>.Failed("HttpError", "503 unavailable")
                : ClashOfRimClientNetworkResult<ModStoreThingPackageResponseDto>.Ok(new() { Result = new() { Accepted = Failure != "rejected" }, ThingPackageId = Failure == "missing" ? "" : "thing-" + key.Split(':').Last() }));
        }
        public Task<ClashOfRimClientNetworkResult<ModStorePawnPackagePreviewResponseDto>> StorePawnPackagePreviewAsync(string id, byte[] preview, CancellationToken cancellationToken = default)
        {
            Check(cancellationToken == Token && preview.SequenceEqual(Preview) && !ReferenceEquals(preview, Preview), "Preview copy or cancellation lost."); Previews++;
            return Task.FromResult(ClashOfRimClientNetworkResult<ModStorePawnPackagePreviewResponseDto>.Ok(new() { Result = new() { Accepted = true } }));
        }
        private bool Failed(string key) => Failure == "transport" || (FailSecond && key.EndsWith(":second", StringComparison.Ordinal));
    }
    private sealed class Handler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send;
        internal Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) => this.send = send;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }
    private sealed class Environment : IClientProtocolEnvironment
    {
        public string? CurrentLanguage => null;
        public string DeserializationFailure(string type) => type;
        public Action? CaptureSessionUnavailable(AIRsLight.ClashOfRim.ClientNetwork.ClashOfRimClientNetworkContext context, string? sessionId) => null;
        public void MultipartDisposalFailed(string message) { }
    }
}
