using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Compatibility;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility;

internal static class WorldCompatibilityTests
{
    internal static void Run()
    {
        OdysseyFactionNameRuleTests.Run();
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            foreach (string culture in new[] { "en-US", "zh-CN", "tr-TR", "ja-JP" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                PriorityAndRecordIdentity();
                WorldExtensionsAndSummary();
                FloatSettings();
                RequestedBaselineExtensions();
                PackageCatalogAndDlcPolicy();
                PublicFacadeLifecycle();
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
            ClashOfRimCompatibilityApi.ApplyServerDlcBaseline(null);
            ClashOfRimCompatibilityApi.BeginRegistrationCycle("client-tests");
        }
    }

    private static void PriorityAndRecordIdentity()
    {
        foreach (string capability in new[] { "setting", "world", "admin", "package" })
        {
            var f = new Fixture(); var low = f.Scope.BeginRegistrationCycle("low");
            void Register(string key)
            {
                switch (capability)
                {
                    case "setting": f.Settings.RegisterWorldGenerationFloatSettingProvider(key, (ModWorldConfigurationDto _, out float value) => { value = 0.3f; return true; }); break;
                    case "world": f.World.RegisterWorldConfigurationExtensionHandler(key, "kind", (_, _, _) => Array.Empty<ModWorldConfigurationExtensionDto>(), null); break;
                    case "admin": f.Admin.RegisterAdminBaselineExtensionProvider(key, "kind", () => null); break;
                    case "package": f.Packages.RegisterCompatibilityPackage(key, () => true, new[] { "capability" }); break;
                }
            }
            Register("ID");
            var high = f.Scope.CreateRegistrationToken("high", 10);
            f.Scope.UseRegistrationToken(high, () => { Register("id"); Register("Id"); });
            Check(f.Scope.Records.Count == 1 && f.Scope.Records.Single().Owner == "high",
                capability + " case-insensitive replacement left stale records: " + string.Join(", ", f.Scope.Records.Select(r => r.Owner + "/" + r.Key)));
            Check(f.Scope.Diagnostics.Count == 2 && f.Scope.Diagnostics[0].Code.EndsWith("DuplicateReplaced", StringComparison.Ordinal)
                && f.Scope.Diagnostics[1].Code.EndsWith("DuplicateRejected", StringComparison.Ordinal), "Priority replacement/rejection diagnostics changed.");
            f.Scope.RevokeRegistrationToken(low);
            Check(f.Scope.Records.Count == 1 && f.Scope.Records.Single().Owner == "high", "Revoking the old owner removed the winning registration.");
            f.Scope.RevokeRegistrationToken(high);
            Check(f.Scope.Records.Count == 0, "Revocation left a replaced registration record.");
        }

        var collision = new Fixture(); var first = collision.Scope.BeginRegistrationCycle("first");
        collision.Admin.RegisterAdminBaselineExtensionProvider("provider", "kind", () => null);
        var distinct = collision.Scope.CreateRegistrationToken("distinct");
        collision.Scope.UseRegistrationToken(distinct, () => collision.Admin.RegisterAdminBaselineExtensionProvider(" provider ", "kind", () => null));
        var winner = collision.Scope.CreateRegistrationToken("winner", 10);
        collision.Scope.UseRegistrationToken(winner, () => collision.Admin.RegisterAdminBaselineExtensionProvider("provider", "kind", () => null));
        collision.Scope.RevokeRegistrationToken(first);
        Check(collision.Scope.Records.Select(r => r.Owner).SequenceEqual(new[] { "distinct", "winner" }),
            "Replacing one registration erased another registration with the same normalized diagnostic key.");
    }

    private static void WorldExtensionsAndSummary()
    {
        var f = new Fixture(); f.Scope.BeginRegistrationCycle("world");
        var first = Extension("Provider", "Kind", "first"); var last = Extension("Provider", "Kind", "last");
        var configuration = new ModWorldConfigurationDto { Extensions = new() { last, Extension("negative", "kind", ""), Extension("broken", "kind", "") } };
        var trace = new List<string>();
        f.World.RegisterWorldConfigurationExtensionHandler(" Provider ", " Kind ", (user, colony, world) =>
        {
            Check(user == "u" && colony == "c" && world == "w", "Collector lost identity arguments.");
            return new[] { first, null!, Extension("provider", "Kind", "wrong case"), Extension("other", "Kind", "wrong owner"), last };
        }, (actual, user, applyState) =>
        {
            Check(ReferenceEquals(actual, configuration) && user == "u" && !applyState, "Applier lost configuration or flags.");
            trace.Add("apply"); return 3;
        });
        f.World.RegisterWorldConfigurationExtensionHandler("negative", "kind", null, (_, _, _) => -9);
        f.World.RegisterWorldConfigurationExtensionHandler("broken", "kind", (_, _, _) => throw new Exception("collect"), (_, _, _) => throw new Exception("apply"));
        f.World.RegisterWorldConfigurationExtensionHandler("absent", "kind", null, (_, _, _) => { trace.Add("unexpected"); return 8; });
        Check(f.World.HasWorldConfigurationExtensionCollector
            && ReferenceEquals(f.World.CollectCurrentWorldConfigurationExtensions("u", "c", "w").Single(), last)
            && f.World.ApplyWorldConfigurationExtensions(configuration, "u", false) == 3 && trace.SequenceEqual(new[] { "apply" }),
            "World extension filtering, last-wins collection, apply gating or nonnegative counting changed.");
        configuration.Extensions = new() { Extension("provider", "Kind", "case mismatch") };
        Check(f.World.ApplyWorldConfigurationExtensions(configuration, "u", true) == 0, "World payload matching became case-insensitive.");
        var summaryFirst = new WorldConfigurationExtensionSummaryItem("key", "旧", "1");
        var summaryLast = new WorldConfigurationExtensionSummaryItem("key", "文化 İ", "0.25");
        var differentCase = new WorldConfigurationExtensionSummaryItem("KEY", "Other", "2");
        f.World.RegisterWorldConfigurationExtensionSummaryProvider(_ => new[] { summaryFirst, differentCase, null!, new WorldConfigurationExtensionSummaryItem(" ", "ignored", "") });
        f.World.RegisterWorldConfigurationExtensionSummaryProvider(_ => throw new Exception("summary"));
        f.World.RegisterWorldConfigurationExtensionSummaryProvider(_ => new[] { summaryLast });
        Check(f.World.GetWorldConfigurationExtensionSummary(configuration).SequenceEqual(new[] { summaryLast, differentCase })
            && f.World.GetWorldConfigurationExtensionSummary(null!).Count == 0 && f.Scope.Diagnostics.Count == 3,
            "Summary filtering, ordinal last-wins grouping or callback failure isolation changed.");
        f.Scope.BeginRegistrationCycle("next");
        Check(!f.World.HasWorldConfigurationExtensionCollector && f.World.CollectCurrentWorldConfigurationExtensions(null, null, "").Count == 0
            && f.World.ApplyWorldConfigurationExtensions(configuration, null, true) == 0 && f.World.GetWorldConfigurationExtensionSummary(configuration).Count == 0,
            "New cycle retained world collectors, appliers or summary providers.");
    }

    private static void FloatSettings()
    {
        var f = new Fixture(); f.Scope.BeginRegistrationCycle("settings"); var configuration = new ModWorldConfigurationDto();
        f.Settings.RegisterWorldGenerationFloatSettingProvider(" Coverage ", (ModWorldConfigurationDto actual, out float value) =>
        {
            Check(ReferenceEquals(actual, configuration), "Float setting provider lost configuration.");
            value = 0.3f; return true;
        });
        f.Settings.RegisterWorldGenerationFloatSettingProvider("unset", (ModWorldConfigurationDto _, out float value) => { value = 100; return false; });
        f.Settings.RegisterWorldGenerationFloatSettingProvider("broken", (ModWorldConfigurationDto _, out float value) => throw new Exception("setting"));
        f.Settings.RegisterWorldGenerationFloatSettingProvider("raw", (ModWorldConfigurationDto _, out float value) => { value = float.NaN; return true; });
        Check(f.Settings.ResolveWorldGenerationFloatSetting(configuration, "COVERAGE", 1) == 0.3f
            && f.Settings.ResolveWorldGenerationFloatSetting(configuration, " Coverage ", 1) == 1
            && f.Settings.ResolveWorldGenerationFloatSetting(configuration, "unset", 0.5f) == 0.5f
            && f.Settings.ResolveWorldGenerationFloatSetting(configuration, "broken", 0.5f) == 0.5f
            && f.Settings.ResolveWorldGenerationFloatSetting(null!, "Coverage", 0.5f) == 0.5f
            && float.IsNaN(f.Settings.ResolveWorldGenerationFloatSetting(configuration, "raw", 1)) && f.Scope.Diagnostics.Count == 1,
            "Float settings changed key normalization, fallback or raw provider-value semantics.");
    }

    private static void RequestedBaselineExtensions()
    {
        var f = new Fixture(); f.Scope.BeginRegistrationCycle("baseline"); int called = 0;
        var baseline = new ModSubmitAdminBaselineRequestDto();
        var existing = new ModAdminBaselineExtensionDto { ProviderId = "existing", Kind = "keep" }; baseline.BaselineExtensions.Add(existing);
        var supplied = new ModAdminBaselineExtensionDto { ProviderId = "supplied", Kind = "payload" };
        f.Admin.RegisterAdminBaselineExtensionProvider("provider", "kind", () => { called++; return supplied; });
        f.Admin.RegisterAdminBaselineExtensionProvider("null", "kind", () => null);
        f.Admin.RegisterAdminBaselineExtensionProvider("blank", "kind", () => new ModAdminBaselineExtensionDto { ProviderId = " ", Kind = "kind" });
        f.Admin.RegisterAdminBaselineExtensionProvider("broken", "kind", () => throw new Exception("baseline"));
        f.Admin.RegisterAdminBaselineExtensionProvider("unused", "kind", () => { called++; return supplied; });
        var requirements = new[] { Requirement(" PROVIDER ", " KIND "), Requirement("provider", "kind"), Requirement("null", "kind"),
            Requirement("blank", "kind"), Requirement("broken", "kind"), Requirement("", "kind") };
        f.Admin.AppendAdminBaselineExtensions(baseline, requirements);
        Check(baseline.BaselineExtensions.SequenceEqual(new[] { existing, supplied }) && called == 1 && f.Scope.Diagnostics.Count == 1,
            "Requested baseline providers lost request deduplication, existing output, nonblank-response semantics or failure isolation.");
        f.Admin.AppendAdminBaselineExtensions(baseline, null);
        Check(called == 1 && baseline.BaselineExtensions.Count == 2, "Missing requirements invoked baseline providers.");
    }

    private static void PackageCatalogAndDlcPolicy()
    {
        var f = new Fixture(); f.Scope.BeginRegistrationCycle("packages"); bool active = true;
        var capabilities = new List<string> { "world", " raw " };
        f.Packages.RegisterCompatibilityPackage("DLC", () => active, capabilities);
        capabilities.Clear();
        Check(f.Packages.HasCompatibilityCapability("WORLD") && f.Packages.HasCompatibilityCapability(" raw ") && !f.Packages.HasCompatibilityCapability("raw"),
            "Package capabilities lost copy ownership, case comparison or original whitespace semantics.");
        active = false; Check(!f.Packages.HasCompatibilityCapability("world"), "Local activation was cached."); active = true;
        var baseline = new List<string> { "dlc" }; f.Packages.ApplyServerDlcBaseline(baseline); baseline.Clear();
        Check(f.Packages.HasCompatibilityCapability("world"), "DLC policy lost case-insensitive copy ownership.");
        f.Packages.ApplyServerDlcBaseline(Array.Empty<string>());
        Check(!f.Packages.HasCompatibilityCapability("world"), "Empty server DLC baseline did not deny capabilities.");
        f.Scope.BeginRegistrationCycle("reloaded"); f.Packages.RegisterCompatibilityPackage("DLC", () => true, new[] { "world" });
        Check(!f.Packages.HasCompatibilityCapability("world"), "Registration cycle reset the independent server DLC policy.");
        f.Packages.ApplyServerDlcBaseline(null); Check(f.Packages.HasCompatibilityCapability("world"), "Null DLC baseline did not restore unrestricted capability queries.");
        f.Packages.RegisterCompatibilityPackage("other", () => throw new Exception("active"), new[] { "WORLD", "extra" });
        f.Packages.RegisterCompatibilityPackage("fallback", () => true, new[] { "extra" });
        Check(f.Packages.HasCompatibilityCapability("extra") && f.Scope.Diagnostics.Count(x => x.Code == "CompatibilityCapabilityShared") == 2
            && f.Scope.Diagnostics.Count(x => x.Code == "CompatibilityCallbackException") == 1,
            "Shared capability diagnostics or activation failure fallback changed.");
        var independent = new Fixture();
        Check(!independent.Packages.HasCompatibilityCapability("world"), "An independent catalog inherited package state.");
    }

    private static void PublicFacadeLifecycle()
    {
        for (int cycle = 0; cycle < 2; cycle++)
        {
            var owner = ClashOfRimCompatibilityApi.BeginRegistrationCycle("world-facade");
            ClashOfRimCompatibilityApi.ApplyServerDlcBaseline(new[] { "package" });
            var extension = Extension("provider", "kind", "data"); var configuration = new ModWorldConfigurationDto { Extensions = new() { extension } };
            ClashOfRimCompatibilityApi.RegisterWorldConfigurationExtensionHandler("provider", "kind", (_, _, _) => new[] { extension }, (_, _, _) => 2);
            ClashOfRimCompatibilityApi.RegisterWorldConfigurationExtensionSummaryProvider(_ => new[] { new WorldConfigurationExtensionSummaryItem("key", "文化", "0.3") });
            ClashOfRimCompatibilityApi.RegisterWorldGenerationFloatSettingProvider("coverage", (ModWorldConfigurationDto _, out float value) => { value = 0.3f; return true; });
            ClashOfRimCompatibilityApi.RegisterAdminBaselineExtensionProvider("provider", "kind", () => new ModAdminBaselineExtensionDto { ProviderId = "provider", Kind = "kind" });
            ClashOfRimCompatibilityApi.RegisterCompatibilityPackage("package", () => true, new[] { "world" });
            var baseline = new ModSubmitAdminBaselineRequestDto();
            ClashOfRimCompatibilityApi.AppendAdminBaselineExtensions(baseline, new[] { Requirement("provider", "kind") });
            Check(ClashOfRimCompatibilityApi.HasWorldConfigurationExtensionCollector
                && ReferenceEquals(ClashOfRimCompatibilityApi.CollectCurrentWorldConfigurationExtensions(null, null, "world").Single(), extension)
                && ClashOfRimCompatibilityApi.ApplyWorldConfigurationExtensions(configuration, null, true) == 2
                && ClashOfRimCompatibilityApi.GetWorldConfigurationExtensionSummary(configuration).Single().Value == "0.3"
                && ClashOfRimCompatibilityApi.ResolveWorldGenerationFloatSetting(configuration, "COVERAGE", 1) == 0.3f
                && baseline.BaselineExtensions.Count == 1 && ClashOfRimCompatibilityApi.HasCompatibilityCapability("world"),
                "World API did not forward all five capability collections and DLC state.");
            if (cycle == 0) Check(ClashOfRimCompatibilityApi.RevokeRegistrationToken(owner), "World owner was not revoked.");
            else ClashOfRimCompatibilityApi.BeginRegistrationCycle("next");
            baseline.BaselineExtensions.Clear(); ClashOfRimCompatibilityApi.AppendAdminBaselineExtensions(baseline, new[] { Requirement("provider", "kind") });
            Check(!ClashOfRimCompatibilityApi.HasWorldConfigurationExtensionCollector
                && ClashOfRimCompatibilityApi.CollectCurrentWorldConfigurationExtensions(null, null, "world").Count == 0
                && ClashOfRimCompatibilityApi.ApplyWorldConfigurationExtensions(configuration, null, true) == 0
                && ClashOfRimCompatibilityApi.GetWorldConfigurationExtensionSummary(configuration).Count == 0
                && ClashOfRimCompatibilityApi.ResolveWorldGenerationFloatSetting(configuration, "coverage", 1) == 1
                && baseline.BaselineExtensions.Count == 0 && !ClashOfRimCompatibilityApi.HasCompatibilityCapability("world"),
                "Revocation/new cycle retained world or package capability data.");
        }
    }

    private sealed class Fixture
    {
        internal readonly CompatibilityRegistrationScope Scope = new(_ => { });
        internal readonly AdminBaselineCompatibility Admin;
        internal readonly WorldConfigurationCompatibility World;
        internal readonly WorldGenerationSettingCompatibility Settings;
        internal readonly CompatibilityPackageCatalog Packages;
        internal Fixture() { Admin = new(Scope); World = new(Scope); Settings = new(Scope); Packages = new(Scope); }
    }
    private static ModWorldConfigurationExtensionDto Extension(string provider, string kind, string data) => new() { ProviderId = provider, Kind = kind, PayloadJson = data };
    private static ModAdminBaselineExtensionRequirementDto Requirement(string provider, string kind) => new() { ProviderId = provider, Kind = kind };
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
}
