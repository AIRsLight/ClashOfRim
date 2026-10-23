using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Compatibility;
using AIRsLight.ClashOfRim.CompatibilityClient;
using AIRsLight.ClashOfRim.Protocol;
using HarmonyLib;

internal static class CompatibilitySummaryContractTests
{
    private static CompatibilityManifest current = new();
    internal static void Run()
    {
        var harmony = new Harmony("ClashOfRim.Tests.CompatibilitySummaryContract");
        var culture = CultureInfo.CurrentCulture;
        harmony.Patch(AccessTools.Method(typeof(ClientCompatibilityManifestBuilder), "Build"),
            prefix: new HarmonyMethod(typeof(CompatibilitySummaryContractTests), nameof(BuildFixture)));
        try
        {
            var failures = new List<string>();
            foreach (string mode in new[] { "saved-config", "unsaved-config", "optional-missing", "extra-translation" })
            {
                var baseline = Manifest();
                var client = baseline;
                if (mode == "unsaved-config") baseline = client = baseline with { Mods = new[] { baseline.Mods[0] with { Configs = new[] { baseline.Mods[0].Configs[0] with { HasSavedFile = false } } } } };
                if (mode == "optional-missing") baseline = baseline with { Mods = baseline.Mods.Concat(new[] { new ModManifestEntry { LoadOrder = 1, PackageId = "optional", Role = ModCompatibilityRole.Optional } }).ToArray() };
                if (mode == "extra-translation") client = client with { Mods = client.Mods.Concat(new[] { new ModManifestEntry { LoadOrder = 1, PackageId = "translation", Role = ModCompatibilityRole.OptionalPureTranslation,
                    Files = new[] { new ControlledFileEntry { RelativePath = "Languages/ChineseSimplified/Test.xml", Kind = ModFileKind.Language } } } }).ToArray() };
                current = client;
                var summary = JsonSerializer.Deserialize<CompatibilityManifestSummary>(ClientCompatibilityManifestBuilder.BuildSummaryJson(),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true, Converters = { new JsonStringEnumConverter() } })!;
                var full = CompatibilityManifestComparer.Compare(baseline, client);
                var brief = CompatibilityManifestSummaryComparer.Compare(baseline, summary, CompatibilityComparisonOptions.Default);
                bool valid = full.Accepted && (mode == "extra-translation" ? brief.Accepted || brief.RequiresFullManifest : brief.Accepted);
                if (!valid)
                {
                    string failure = mode + ": full=" + full.Accepted + ", summary=" + brief.Accepted + ", details=" + brief.RequiresFullManifest
                        + ", requested=" + string.Join(',', brief.RequestedPackageIds) + ", issues=" + string.Join(',', brief.Issues.Select(i => i.Code));
                    Console.WriteLine("REPRO: " + failure); failures.Add(failure);
                }
            }
            if (failures.Count != 0) throw new InvalidOperationException("Compatibility summary diverged from full manifest: " + string.Join("; ", failures));
            foreach (string name in new[] { "en-US", "zh-CN", "tr-TR", "ar-SA" })
            { CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name); Matrix(); ValidationOrder(); }
            HashAndWire();
            RetryWire().GetAwaiter().GetResult();
        }
        finally { harmony.UnpatchAll(harmony.Id); current = new(); CultureInfo.CurrentCulture = culture; ClientSessionWork.Reset(); }
    }
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, Converters = { new JsonStringEnumConverter() } };
    private static void ValidationOrder()
    {
        var first = Manifest().Mods[0];
        var server = Manifest() with { DefSummaries = new[] { new DefSummary { Name = "ThingDef", Count = 1, Hash = 1 } }, Mods = new[] { first,
            first with { PackageId = "second", LoadOrder = 1 }, new ModManifestEntry { PackageId = "third", LoadOrder = 2 } } };
        foreach (string phase in new[] { "missing", "extra", "order", "dlc", "files", "config", "complete" })
        foreach (bool selected in new[] { false, true })
        {
            var client = server with { ManifestId = "client",
                DefSummaries = phase is "config" or "complete" ? server.DefSummaries : new[] { server.DefSummaries[0] with { Hash = 2 } }, Mods = new[] {
                first with { Configs = new[] { first.Configs[0] with { Sha256 = phase == "complete" ? first.Configs[0].Sha256 : "config-changed" } } },
                server.Mods[1] with { Files = new[] { first.Files[0] with { Sha256 = phase is "complete" or "config" ? first.Files[0].Sha256 : "file-changed" } } },
                server.Mods[2] } };
            if (phase == "missing") client = client with { Mods = client.Mods.Take(2).ToArray() };
            if (phase == "extra") client = client with { Mods = client.Mods.Append(new ModManifestEntry { PackageId = "extra", LoadOrder = 3,
                Files = new[] { new ControlledFileEntry { RelativePath = "Defs/Extra.xml", Kind = ModFileKind.Def } } }).ToArray() };
            if (phase == "order") client = client with { Mods = new[] { client.Mods[0] with { LoadOrder = 1 }, client.Mods[1] with { LoadOrder = 0 }, client.Mods[2] } };
            if (phase == "dlc") client = client with { DlcIds = new[] { "different" } };
            var expected = phase == "files" ? CompatibilityIssueCategory.Hash : phase == "config" ? CompatibilityIssueCategory.Config : CompatibilityIssueCategory.Manifest;
            var full = CompatibilityManifestComparer.Compare(server, client);
            Check(full.Accepted == (phase == "complete") && full.Issues.All(i => CompatibilityIssueClassifier.CategoryFor(i.Code) == expected),
                "Full validation did not stop at the first failing stage: " + phase);
            current = client;
            var summary = CompatibilityManifestSummaryBuilder.Build(client) with { SupportsSelectedDetails = selected };
            var brief = CompatibilityManifestSummaryComparer.Compare(server, summary, CompatibilityComparisonOptions.Default);
            CompatibilityComparisonResult completed;
            if (brief.RequiresFullManifest)
            {
                var details = JsonSerializer.Deserialize<CompatibilityManifest>(ClientCompatibilityManifestBuilder.BuildJsonForPackages(brief.RequestedPackageIds), Json)!;
                completed = CompatibilityManifestDetailsComparer.Compare(server, details, summary, CompatibilityComparisonOptions.Default);
                if (phase is "files" or "config") Check((details.Mods.Count < client.Mods.Count) == selected, "Priority test missed the requested detail shape.");
            }
            else completed = new(brief.Issues);
            Check(completed.Accepted == full.Accepted && completed.Issues.Select(i => i.Code).SequenceEqual(full.Issues.Select(i => i.Code)),
                "Summary/detail exchange changed stage priority: " + phase);
        }
    }
    private static void Matrix()
    {
        foreach (string mode in new[] { "same", "optional", "optional-subset", "translation", "translation-role", "translation-denied", "fake-translation", "order", "required",
            "dlc-order", "dlc-spacing", "def-missing", "def-count", "def-hash", "file-hash", "file-missing", "config", "config-ignore", "config-warn", "saved-state", "display" })
        {
            var server = Manifest() with { DlcIds = new[] { "Core", "DLC" }, DefSummaries = new[] { new DefSummary { Name = "ThingDef", Count = 42, Hash = 123 } },
                Mods = new[] { Manifest().Mods[0], new ModManifestEntry { PackageId = "second", LoadOrder = 1 } } };
            var client = server; var options = CompatibilityComparisonOptions.Default;
            var first = server.Mods[0];
            switch (mode)
            {
                case "optional": server = server with { Mods = new[] { first with { Role = ModCompatibilityRole.Optional }, server.Mods[1] } }; client = server with { Mods = new[] { server.Mods[1] } }; break;
                case "optional-subset":
                    server = server with { Mods = server.Mods.Concat(new[] { new ModManifestEntry { PackageId = "optional", LoadOrder = 2, Role = ModCompatibilityRole.Optional } }).ToArray() };
                    client = server with { Mods = new[] { first with { Configs = new[] { first.Configs[0] with { Sha256 = "changed" } } }, server.Mods[1] } };
                    options = new() { ModConfigRules = new[] { new ModConfigComparisonRule { PackageId = first.PackageId, Mode = ModConfigComparisonMode.Warn } } }; break;
                case "translation": case "translation-role": case "translation-denied": case "fake-translation":
                    client = server with { Mods = new[] { first, new ModManifestEntry { PackageId = "extra", LoadOrder = 1,
                        Role = mode == "translation-role" ? ModCompatibilityRole.Required : ModCompatibilityRole.OptionalPureTranslation,
                        Files = new[] { new ControlledFileEntry { RelativePath = mode == "fake-translation" ? "Defs/Unsafe.xml" : "Languages/Test.xml",
                            Kind = mode == "fake-translation" ? ModFileKind.Def : ModFileKind.Language } } }, server.Mods[1] with { LoadOrder = 2 } } };
                    if (mode == "translation-denied") options = new() { AllowExtraPureTranslationMods = false }; break;
                case "order": client = server with { Mods = new[] { first with { LoadOrder = 1 }, server.Mods[1] with { LoadOrder = 0 } } }; break;
                case "required": client = server with { Mods = new[] { first } }; break;
                case "dlc-order": client = server with { DlcIds = server.DlcIds.Reverse().ToArray() }; break;
                case "dlc-spacing": client = server with { DlcIds = new[] { " core ", " dlc " } }; break;
                case "def-missing": client = server with { DefSummaries = Array.Empty<DefSummary>() }; break;
                case "def-count": client = server with { DefSummaries = new[] { server.DefSummaries[0] with { Count = 43 } } }; break;
                case "def-hash": client = server with { DefSummaries = new[] { server.DefSummaries[0] with { Hash = 124 } } }; break;
                case "file-hash": first = first with { Files = new[] { first.Files[0] with { Sha256 = "changed" } } }; break;
                case "file-missing": first = first with { Files = Array.Empty<ControlledFileEntry>() }; break;
                case "config": case "config-ignore": case "config-warn":
                    first = first with { Configs = new[] { first.Configs[0] with { Sha256 = "changed" } } };
                    if (mode != "config") options = new() { ModConfigRules = new[] { new ModConfigComparisonRule { PackageId = first.PackageId,
                        Mode = mode == "config-ignore" ? ModConfigComparisonMode.Ignore : ModConfigComparisonMode.Warn } } }; break;
                case "saved-state": first = first with { Configs = new[] { first.Configs[0] with { HasSavedFile = false } } }; break;
                case "display": first = first with { Name = "other display" }; break;
            }
            if (mode is "file-hash" or "file-missing" or "config" or "config-ignore" or "config-warn" or "saved-state" or "display")
                client = server with { Mods = new[] { first, server.Mods[1] } };
            current = client;
            var summary = JsonSerializer.Deserialize<CompatibilityManifestSummary>(ClientCompatibilityManifestBuilder.BuildSummaryJson(), Json)!;
            var full = CompatibilityManifestComparer.Compare(server, client, options);
            bool expected = mode is "same" or "optional" or "optional-subset" or "translation" or "translation-role" or "dlc-spacing" or "config-ignore" or "config-warn" or "display";
            Check(full.Accepted == expected, "Full comparison expectation failed: " + mode);
            var brief = CompatibilityManifestSummaryComparer.Compare(server, summary, options);
            Check(!brief.Accepted || full.Accepted, "Summary accepted incompatible data: " + mode);
            Check(!full.Accepted || !brief.Issues.Any(i => i.Severity == CompatibilityIssueSeverity.Error), "Summary rejected compatible data: " + mode);
            if (brief.RequiresFullManifest)
            {
                var details = JsonSerializer.Deserialize<CompatibilityManifest>(ClientCompatibilityManifestBuilder.BuildJsonForPackages(brief.RequestedPackageIds), Json)!;
                var completed = CompatibilityManifestDetailsComparer.Compare(server, details, summary, options);
                Check(completed.Accepted == full.Accepted, "Requested details diverged from full comparison: " + mode);
                Check(completed.Issues.Select(i => i.Code).OrderBy(code => code).SequenceEqual(full.Issues.Select(i => i.Code).OrderBy(code => code)), "Requested details lost or duplicated diagnostics: " + mode);
                if (mode is "translation" or "translation-role" or "fake-translation") Check(brief.RequestedPackageIds.SequenceEqual(new[] { "*" }), "Extra mod was trusted without file evidence.");
            }
            else if (!brief.Accepted)
                Check(brief.Issues.Select(i => i.Code).SequenceEqual(full.Issues.Select(i => i.Code)), "Header/presence diagnostics diverged: " + mode);
        }
    }
    private static void HashAndWire()
    {
        var mod = Manifest().Mods[0];
        string golden = CompatibilityManifestSummaryBuilder.ComputeModHash(mod);
        Check(golden == "a5fdf9dd6fa14105d8a9c341e49b025e81d713d7a1ec4ab63d3d36b09655d637", "Shared digest changed the original client layout.");
        Check(CompatibilityManifestSummaryBuilder.ComputeModHash(mod with { Configs = new[] { mod.Configs[0] with { HasSavedFile = false } } }) != golden, "Saved and missing config states collide.");
        var signed = mod with { LoadOrder = -1, Files = new[] { mod.Files[0] with { Size = -42 } } };
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture; string expected = CompatibilityManifestSummaryBuilder.ComputeModHash(signed);
        var custom = (CultureInfo)CultureInfo.InvariantCulture.Clone(); custom.NumberFormat.NegativeSign = "negative"; CultureInfo.CurrentCulture = custom;
        Check(CompatibilityManifestSummaryBuilder.ComputeModHash(signed) == expected, "Digest uses current number formatting.");
        current = Manifest() with { Mods = new[] { signed } };
        var wire = JsonSerializer.Deserialize<CompatibilityManifest>(ClientCompatibilityManifestBuilder.BuildJson(), Json)!;
        Check(wire.Mods[0].LoadOrder == -1 && wire.Mods[0].Files[0].Size == -42, "Manifest JSON uses localized numbers.");
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
    }
    private static async Task RetryWire()
    {
        var oldId = ClashOfRimModNetworkClient.CompatibilityManifestIdProvider;
        var oldSummary = ClashOfRimModNetworkClient.CompatibilityManifestSummaryJsonProvider;
        var oldFull = ClashOfRimModNetworkClient.CompatibilityManifestJsonProvider;
        var oldSelected = ClashOfRimModNetworkClient.CompatibilityManifestJsonForPackagesProvider;
        try
        {
            foreach (bool world in new[] { false, true })
            {
                var baseline = Manifest() with { Mods = new[] { Manifest().Mods[0], new ModManifestEntry { PackageId = "stable", LoadOrder = 1 } } };
                current = baseline with { Mods = new[] { baseline.Mods[0] with { Configs = new[] { baseline.Mods[0].Configs[0] with { Sha256 = "changed" } } }, baseline.Mods[1] } };
                string captured = ClientCompatibilityManifestBuilder.BuildSummaryJson(); int calls = 0;
                ClashOfRimModNetworkClient.CompatibilityManifestIdProvider = () => "client-id";
                ClashOfRimModNetworkClient.CompatibilityManifestSummaryJsonProvider = () => captured;
                ClashOfRimModNetworkClient.CompatibilityManifestJsonProvider = ClientCompatibilityManifestBuilder.BuildJson;
                ClashOfRimModNetworkClient.CompatibilityManifestJsonForPackagesProvider = ClientCompatibilityManifestBuilder.BuildJsonForPackages;
                using var http = new HttpClient(new Handler(async request =>
                {
                    calls++; using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync()); var root = body.RootElement;
                    Check(root.GetProperty("compatibilityManifestSummaryJson").GetString() == captured, "Retry lost or refreshed its captured summary.");
                    if (calls == 1)
                    {
                        ClashOfRimModNetworkClient.CompatibilityManifestSummaryJsonProvider = () => "changed-after-send";
                        return Reply("{\"result\":{\"accepted\":false},\"requiresFullCompatibilityManifest\":true,\"requestedCompatibilityPackageIds\":[\"required-é\"]}");
                    }
                    var details = JsonSerializer.Deserialize<CompatibilityManifest>(root.GetProperty("compatibilityManifestJson").GetString()!, Json)!;
                    var summary = JsonSerializer.Deserialize<CompatibilityManifestSummary>(captured, Json)!;
                    Check(details.Mods.Count == 1 && CompatibilityManifestDetailsComparer.IsSelection(details, summary), "Client did not send the selected mod details.");
                    var options = new CompatibilityComparisonOptions { ModConfigRules = new[] { new ModConfigComparisonRule { PackageId = "required-é", Mode = ModConfigComparisonMode.Ignore } } };
                    Check(CompatibilityManifestDetailsComparer.Compare(baseline, details, summary, options).Accepted, "Actual retry wire was not a complete selectable exchange.");
                    return Reply("{\"result\":{\"accepted\":true}}");
                }));
                var client = new ClashOfRimModNetworkClient(http, new("http://localhost", "user", "colony", "snapshot", null, "pw", null));
                bool accepted = world ? (await client.PrepareWorldSessionAsync()).Response?.Result?.Accepted == true : (await client.LoginAsync("test")).Response?.Result?.Accepted == true;
                Check(accepted && calls == 2, "Login/world setup retry changed its round count or outcome.");
            }
        }
        finally
        {
            ClashOfRimModNetworkClient.CompatibilityManifestIdProvider = oldId;
            ClashOfRimModNetworkClient.CompatibilityManifestSummaryJsonProvider = oldSummary;
            ClashOfRimModNetworkClient.CompatibilityManifestJsonProvider = oldFull;
            ClashOfRimModNetworkClient.CompatibilityManifestJsonForPackagesProvider = oldSelected;
        }
    }
    private static HttpResponseMessage Reply(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json) };
    private sealed class Handler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> send;
        internal Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) => this.send = send;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request);
    }
    private static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    private static bool BuildFixture(ref CompatibilityManifest __result) { __result = current; return false; }
    internal static CompatibilityManifest Manifest() => new()
    {
        ManifestId = "manifest", ProtocolVersion = ProtocolApiVersion.Current, RimWorldVersion = "1.7", GameLanguage = "English",
        Mods = new[] { new ModManifestEntry { PackageId = "required-é", Name = "测试", LoadOrder = 0,
            Files = new[] { new ControlledFileEntry { RelativePath = "Defs/Test.xml", Kind = ModFileKind.Def, Size = 42, Sha256 = "file" } },
            Configs = new[] { new ModConfigDigest { FileName = "Settings.xml", HasSavedFile = true, Sha256 = "config" } } } }
    };
}
