using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Gifts;
using AIRsLight.ClashOfRim.Modules.Packages;
using AIRsLight.ClashOfRim.Modules.Transport;
using AIRsLight.ClashOfRim.Pawns;
using AIRsLight.ClashOfRim.Protocol;
using HarmonyLib;
using Verse;

internal static class PackageReadModuleTests
{
    private static readonly ClashOfRimClientNetworkContext Context = new("http://localhost/captured", "用户-é", "colony", "base", null, null, "auth");
    private const string PawnJson = "{\"result\":{\"accepted\":true},\"pawnPackage\":{\"packageVersion\":1,\"reference\":{\"globalId\":\"pawn-é\"},\"scribe\":{\"xml\":\"<pawn><value>0.25</value></pawn>\"},\"extensions\":[{\"providerId\":\"custom\",\"kind\":\"state\",\"payloadJson\":\"{\\\"value\\\":0.25}\"}]}}";
    private const string ThingJson = "{\"result\":{\"accepted\":true},\"thingPackage\":{\"globalKey\":\"thing-é\",\"stackCount\":3,\"scribe\":{\"xmlGzipBase64\":\"encoded-state\",\"xmlSha256\":\"hash\",\"uncompressedBytes\":123}}}";

    internal static void Run() => RunAsync().GetAwaiter().GetResult();
    private static async Task RunAsync()
    {
        var harmony = new Harmony("ClashOfRim.Tests.PackageReadModule");
        harmony.Patch(typeof(ClashOfRimText).GetMethod("Key", BindingFlags.Static | BindingFlags.NonPublic, null,
            new[] { typeof(string), typeof(NamedArgument[]) }, null), prefix: new HarmonyMethod(typeof(PackageReadModuleTests), nameof(Translate)));
        try
        {
            await Contracts();
            await Hydration();
            await Failures();
            await Cancellation();
        }
        finally { harmony.UnpatchAll(harmony.Id); ClientSessionWork.Reset(); }
    }

    private static async Task Contracts()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            foreach (string culture in new[] { "en-US", "zh-CN", "tr-TR", "fr-FR" })
            foreach (bool facade in new[] { false, true })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                int calls = 0;
                using var http = new HttpClient(new Handler(async (request, _) =>
                {
                    bool pawn = calls++ == 0;
                    var kind = pawn ? ProtocolMessageKind.GetPawnPackage : ProtocolMessageKind.GetThingPackage;
                    Require(request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/captured/" + ProtocolContractManifest.Find(kind).Route.TrimStart('/'), "Package route or captured server changed.");
                    using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                    var json = body.RootElement;
                    var identity = json.GetProperty("requester");
                    Require(identity.GetProperty("userId").GetString() == Context.UserId
                        && identity.GetProperty("colonyId").GetString() == "colony"
                        && identity.GetProperty("snapshotId").GetString() == "base"
                        && json.GetProperty(pawn ? "pawnPackageId" : "thingPackageId").GetString() == "包-é", "Package request lost identity or opaque id.");
                    return Response(pawn ? PawnJson : ThingJson);
                }));
                IClientPackageReader reader = facade ? new ClashOfRimModNetworkClient(http, Context) : Reader(http);
                var pawn = await reader.GetPawnPackageAsync("包-é");
                var thing = await reader.GetThingPackageAsync("包-é");
                Require(pawn.Success && pawn.Response!.PawnPackage!.Reference!.GlobalId == "pawn-é"
                    && pawn.Response.PawnPackage.Extensions.Single().PayloadJson == "{\"value\":0.25}"
                    && pawn.Response.PawnPackage.Scribe!.Xml == "<pawn><value>0.25</value></pawn>"
                    && thing.Success && thing.Response!.ThingPackage!.Scribe!.UncompressedBytes == 123
                    && calls == 2, "Package response payload changed across client entry or culture.");
            }
        }
        finally { CultureInfo.CurrentCulture = original; }

        using var unexpected = new HttpClient(new Handler((_, _) => throw new Exception("Unconfigured reader sent HTTP.")));
        var missing = new ClashOfRimClientNetworkContext("http://localhost", "", "colony", null, null, null, null);
        var empty = new ClientPackageReader(new ClientProtocolTransport(unexpected, Context, new Environment()), missing, () => "not configured");
        Require((await empty.GetPawnPackageAsync("pawn")).ErrorCode == "NotConfigured"
            && (await empty.GetThingPackageAsync("thing")).Message == "not configured", "Unconfigured package reader changed its contract.");
    }

    private static async Task Hydration()
    {
        var calls = new List<string>();
        var handler = new Handler((request, _) =>
        {
            bool pawn = IsPawn(request);
            calls.Add(pawn ? "pawn" : "thing");
            return Task.FromResult(Response(pawn ? PawnJson : ThingJson));
        });
        using var http = new HttpClient(handler, disposeHandler: false);
        var reader = Reader(http);
        var presentPawn = new ModPawnExchangePackageDto();
        var presentThing = new ModThingStatePackageDto();
        var things = new[]
        {
            new ModThingReferenceDto { PawnPackageId = "pawn", ThingPackageId = "thing" },
            new ModThingReferenceDto { PawnPackageId = "already", PawnPackage = presentPawn, ThingPackageId = "already", ThingPackage = presentThing },
            new ModThingReferenceDto()
        };
        await PawnPackageHydration.HydrateThingPawnPackagesAsync(reader, things);
        await PawnPackageHydration.HydrateThingStatePackagesAsync(reader, things);
        Require(calls.SequenceEqual(new[] { "pawn", "thing" }) && things[0].PawnPackage!.Reference!.GlobalId == "pawn-é"
            && things[0].ThingPackage!.Scribe!.XmlGzipBase64 == "encoded-state"
            && ReferenceEquals(things[1].PawnPackage, presentPawn) && ReferenceEquals(things[1].ThingPackage, presentThing), "Thing hydration redownloaded or replaced existing state.");

        calls.Clear();
        var item = Item("pawn", "thing");
        var existing = new GiftItemReference("existing", "Human", 1, "source", pawnPackageId: "already", pawnPackage: presentPawn,
            thingPackageId: "already", thingPackage: presentThing);
        int clients = 0;
        var hydration = new ClientGiftPackageHydration(new Environment(), () => { clients++; return new HttpClient(handler, disposeHandler: false); });
        var plan = Plan(item, existing, Item(null, null));
        Require((await hydration.HydrateAsync(plan, Context)).Success && calls.SequenceEqual(new[] { "pawn", "thing" })
            && item.PawnPackage!.Extensions.Single().ProviderId == "custom" && item.ThingPackage!.StackCount == 3
            && ReferenceEquals(existing.PawnPackage, presentPawn) && ReferenceEquals(existing.ThingPackage, presentThing), "Gift hydration lost the shared algorithm or phase order.");
        Require((await hydration.HydrateAsync(plan, Context)).Success && clients == 1 && calls.Count == 2, "Hydrated gift allocated HTTP or downloaded again.");
    }

    private static async Task Failures()
    {
        foreach (bool pawn in new[] { true, false })
        foreach (string failure in new[] { "http", "rejected", "missing", "malformed" })
        {
            int calls = 0;
            var handler = new Handler((_, _) =>
            {
                calls++;
                return Task.FromResult(failure switch
                {
                    "http" => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("unavailable") },
                    "rejected" => Response("{\"result\":{\"accepted\":false,\"errorCode\":5,\"message\":\"missing\"}}"),
                    "missing" => Response("{\"result\":{\"accepted\":true}}"),
                    _ => Response("not-json")
                });
            });
            var item = Item(pawn ? "failed" : null, "failed");
            var later = Item(pawn ? "later" : null, "later");
            var hydration = new ClientGiftPackageHydration(new Environment(), () => new HttpClient(handler, disposeHandler: false));
            var result = await hydration.HydrateAsync(Plan(item, later), Context);
            string key = failure == "rejected" ? "DownloadRejected" : failure == "missing" ? "DownloadMissingPackage" : "DownloadFailed";
            Require(!result.Success && result.Message == "ClashOfRim.PawnPackage." + key && calls == 1
                && item.PawnPackage is null && item.ThingPackage is null && later.PawnPackage is null && later.ThingPackage is null,
                "Failed hydration advanced to later items or lost its failure classification: " + failure);

            // Trade/shop callers retain their throwing contract; gift callers return a failure result.
            using var http = new HttpClient(handler, disposeHandler: false);
            var things = new[] { new ModThingReferenceDto { PawnPackageId = "failed", ThingPackageId = "failed" } };
            try
            {
                if (pawn) await PawnPackageHydration.HydrateThingPawnPackagesAsync(Reader(http), things);
                else await PawnPackageHydration.HydrateThingStatePackagesAsync(Reader(http), things);
                throw new Exception("Thing hydration silently accepted a failed package.");
            }
            catch (InvalidOperationException ex) { Require(ex.Message == result.Message, "Gift and thing package failures diverged."); }
        }
    }

    private static async Task Cancellation()
    {
        foreach (bool sessionExit in new[] { false, true })
        foreach (bool pawnCompleted in new[] { false, true })
        {
            ClientSessionWork.Reset();
            using var cancellation = new CancellationTokenSource();
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int calls = 0;
            var handler = new Handler(async (request, token) =>
            {
                calls++;
                if (pawnCompleted && IsPawn(request)) return Response(PawnJson);
                started.SetResult();
                await Task.Delay(Timeout.Infinite, token);
                throw new Exception("Canceled package request completed.");
            });
            var item = Item("pawn", "thing");
            var hydration = new ClientGiftPackageHydration(new Environment(), () => new HttpClient(handler, disposeHandler: false));
            var work = hydration.HydrateAsync(Plan(item, Item("later", "later")), Context, cancellation.Token);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (sessionExit) ClientSessionWork.Reset(); else cancellation.Cancel();
            var result = await work.WaitAsync(TimeSpan.FromSeconds(5));
            Require(!result.Success && calls == (pawnCompleted ? 3 : 1) && item.ThingPackage is null,
                "Cancellation did not stop the active hydration phase.");
        }
    }

    private static ClientPackageReader Reader(HttpClient http) => new(new ClientProtocolTransport(http,
        new ClashOfRimClientNetworkContext("http://wrong-host", "wrong", "wrong", null, null, null, null), new Environment()), Context, () => "not configured");
    private static GiftItemReference Item(string? pawn, string? thing) => new("key", "Human", 1, "source", pawnPackageId: pawn, thingPackageId: thing);
    private static GiftLandingPlan Plan(params GiftItemReference[] items) => new("event", null, "map", null, "DropPod", items, true);
    private static bool IsPawn(HttpRequestMessage request) => request.RequestUri!.AbsolutePath.EndsWith(ProtocolContractManifest.Find(ProtocolMessageKind.GetPawnPackage).Route, StringComparison.Ordinal);
    private static HttpResponseMessage Response(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static bool Translate(string key, ref string __result) { __result = key; return false; }
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
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
