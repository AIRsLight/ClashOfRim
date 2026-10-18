using AIRsLight.ClashOfRim.Compatibility;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Network.Modules.Accounts;
using AIRsLight.ClashOfRim.Network.Modules.Compatibility;
using static RecoveryTestSupport;

internal static class CompatibilityHandshakeExchangeTests
{
    internal static void Run()
    {
        SnapshotEndpointTestClient.LoadLocalization();
        var baseline = EntryTestSupport.Manifest() with { Mods = [new() { PackageId = "required" },
            new() { PackageId = "optional-1", LoadOrder = 1, Role = ModCompatibilityRole.Optional },
            new() { PackageId = "optional-2", LoadOrder = 2, Role = ModCompatibilityRole.Optional }] };
        var client = baseline with { Mods = [baseline.Mods[0], new() { PackageId = "translation", LoadOrder = 1,
            Role = ModCompatibilityRole.OptionalPureTranslation, Files = [new() { RelativePath = "Languages/Chinese/Test.xml", Kind = ModFileKind.Language }] }] };
        var failures = new List<string>();
        CheckExchange("fewer-mods-full-manifest", baseline, client, true);
        var required = baseline with { Mods = [baseline.Mods[0], baseline.Mods[1] with { Role = ModCompatibilityRole.Required }] };
        CheckExchange("unmarked-incomplete-manifest", required, required with { Mods = [required.Mods[0]] }, false);
        if (failures.Count != 0) throw new InvalidOperationException(string.Join("; ", failures));
        SelectedDetails(); LegacyNegotiation();

        void CheckExchange(string name, CompatibilityManifest server, CompatibilityManifest actual, bool expected)
        {
            var registry = new CompatibilityBaselineRegistry(); registry.EnsureBaseline(server, "admin", DateTimeOffset.UtcNow);
            var service = new CompatibilityHandshakeService(registry, new(), () => CompatibilityComparisonOptions.Default);
            var result = service.Evaluate(AuthenticationValidationResult.Accept("player"), CompatibilityHandshakeInput.Prepare(
                CompatibilityManifestJsonWriter.Write(actual), null, CompatibilityManifestJsonWriter.WriteSummary(CompatibilityManifestSummaryBuilder.Build(actual))), DateTimeOffset.UtcNow, false);
            if (result.Result.Accepted != expected)
            { string failure = name + ": expected=" + expected + ", actual=" + result.Result.Accepted; Console.WriteLine("REPRO: " + failure); failures.Add(failure); }
        }
    }
    private static void SelectedDetails()
    {
        foreach (string mode in new[] { "valid", "absent-summary", "malformed-summary", "summary-error", "manifest-id", "detail-hash", "missing-details",
            "changed-baseline", "changed-policy", "unknown-extra", "first-baseline", "empty-first-baseline", "complete-ignores-malformed-summary", "empty-full" })
        {
            var at = DateTimeOffset.UtcNow; var slot = new Slot(); var baseline = new CompatibilityBaselineRegistry(slot);
            var original = EntryTestSupport.Manifest() with { Mods = [
                new() { PackageId = "a", Configs = [new() { FileName = "A.xml", Sha256 = "server" }] },
                new() { PackageId = "b", LoadOrder = 1, Configs = [new() { FileName = "B.xml", Sha256 = "server" }] },
                new() { PackageId = "c", LoadOrder = 2 }] };
            if (mode is not ("first-baseline" or "empty-first-baseline")) baseline.EnsureBaseline(original, "admin", at);
            var client = original with { ManifestId = "client", Mods = [original.Mods[0] with { Configs = [new() { FileName = "A.xml", Sha256 = "client" }] }, original.Mods[1], original.Mods[2]] };
            var options = new CompatibilityComparisonOptions { ModConfigRules = [new() { PackageId = "a", Mode = ModConfigComparisonMode.Ignore }] };
            var service = new CompatibilityHandshakeService(baseline, new(), () => options);
            var summary = CompatibilityManifestSummaryBuilder.Build(client);
            var details = client with { Mods = [client.Mods[0]] };
            if (mode == "manifest-id") details = details with { ManifestId = "other" };
            if (mode == "detail-hash") details = details with { Mods = [details.Mods[0] with { Name = "modified after advertisement" }] };
            if (mode == "summary-error") summary = summary with { Mods = [summary.Mods[0], summary.Mods[2]] };
            if (mode == "missing-details") summary = summary with { Mods = [summary.Mods[0], summary.Mods[1] with { Hash = "different" }, summary.Mods[2]] };
            if (mode == "changed-baseline") baseline.ReplaceBaseline(original with { Mods = [original.Mods[0], original.Mods[1] with { Name = "new baseline" }, original.Mods[2]] }, "admin", at);
            if (mode == "changed-policy") options = new();
            if (mode == "unknown-extra") summary = summary with { Mods = summary.Mods.Append(new ModManifestSummaryEntry { PackageId = "claimed-translation", Role = ModCompatibilityRole.OptionalPureTranslation }).ToArray() };
            if (mode is "empty-first-baseline" or "empty-full") details = details with { Mods = [] };
            if (mode == "complete-ignores-malformed-summary") details = client;
            string? json = mode is "malformed-summary" or "complete-ignores-malformed-summary" ? "{" : mode is "absent-summary" or "empty-full" ? null : CompatibilityManifestJsonWriter.WriteSummary(summary);
            string? before = slot.Read(); int writes = slot.Writes;
            var result = service.Evaluate(AuthenticationValidationResult.Accept("player"), CompatibilityHandshakeInput.Prepare(
                CompatibilityManifestJsonWriter.Write(details), null, json), at, true);
            bool expected = mode is "valid" or "complete-ignores-malformed-summary";
            Require(result.Result.Accepted == expected, "Selective detail admission diverged: " + mode);
            Require(slot.Writes == writes && slot.Read() == before, "Detail packet unexpectedly changed the baseline: " + mode);
            if (mode is "first-baseline" or "empty-first-baseline") Require(result.RequiresFullCompatibilityManifest
                && result.RequestedCompatibilityPackageIds.SequenceEqual(new[] { "*" }) && baseline.Current is null, "Incomplete bootstrap did not require the full manifest.");
        }
    }
    private static void Require(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    private static void LegacyNegotiation()
    {
        var manifest = EntryTestSupport.Manifest() with { Mods = [new() { PackageId = "a", Configs = [new() { FileName = "A.xml", Sha256 = "server" }] }, new() { PackageId = "b", LoadOrder = 1 }] };
        var client = manifest with { Mods = [manifest.Mods[0] with { Configs = [new() { FileName = "A.xml", Sha256 = "client" }] }, manifest.Mods[1]] };
        var summary = CompatibilityManifestSummaryBuilder.Build(client) with { SupportsSelectedDetails = false };
        string json = CompatibilityManifestJsonWriter.WriteSummary(summary);
        Require(!json.Contains("supportsSelectedDetails", StringComparison.Ordinal), "Legacy wire unexpectedly declares subset capability.");
        var baseline = new CompatibilityBaselineRegistry(); baseline.EnsureBaseline(manifest, "admin", DateTimeOffset.UtcNow);
        var options = new CompatibilityComparisonOptions { ModConfigRules = [new() { PackageId = "a", Mode = ModConfigComparisonMode.Ignore }] };
        var service = new CompatibilityHandshakeService(baseline, new(), () => options);
        var first = service.Evaluate(AuthenticationValidationResult.Accept("player"), CompatibilityHandshakeInput.Prepare(null, null, json), DateTimeOffset.UtcNow, false);
        Require(first.RequiresFullCompatibilityManifest && first.RequestedCompatibilityPackageIds.SequenceEqual(new[] { "*" }), "Legacy client was asked for an unverifiable subset.");
        var second = service.Evaluate(AuthenticationValidationResult.Accept("player"), CompatibilityHandshakeInput.Prepare(CompatibilityManifestJsonWriter.Write(client), null, null), DateTimeOffset.UtcNow, false);
        Require(second.Result.Accepted, "Legacy client's complete follow-up was rejected.");
    }
}
