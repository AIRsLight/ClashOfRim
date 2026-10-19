using System;
using System.Collections.Generic;
using System.Globalization;
using AIRsLight.ClashOfRim.Modules.Compatibility;

internal static class ClientModSettingsPolicyTests
{
    internal const string Manifest = "{\"manifestId\":\"base-İ-é\",\"mods\":[{\"packageId\":\"example.mod\",\"configs\":[{\"fileName\":\"ExampleMod\"},{\"fileName\":\"Optional\"}]}],\"modConfigRules\":[{\"packageId\":\"example.mod\",\"fileName\":\"Optional\",\"mode\":\"Ignore\"}]}";
    internal static void Run()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            foreach (string culture in new[] { "en-US", "zh-CN", "fr-FR", "tr-TR" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                Check(ClientModConfigManifest.Read(Manifest)!.Id == "base-İ-é", "Manifest scope identifier changed.");
                var ordinal = ClientModConfigManifest.Read("{\"mods\":[{\"packageId\":\"I.MOD\",\"configs\":[{\"fileName\":\"ITEM\"}]}]}")!;
                Check(ordinal.Enforces("i.mod", "item") && !ordinal.Declares("ı.mod", "ıtem"), "Config identity followed the current culture.");
                foreach (string broad in new[] { "", "Enforce", "Ignore", "Warn" })
                foreach (string exact in new[] { "", "Enforce", "Ignore", "Warn" })
                foreach (bool reversed in new[] { false, true })
                {
                    string packageRule = "{\"packageId\":\" EXAMPLE.MOD \",\"mode\":\"" + broad + "\"}";
                    string fileRule = "{\"packageId\":\"example.mod\",\"fileName\":\"examplemod\",\"mode\":\"" + exact + "\"}";
                    string json = "{\"mods\":[{\"packageId\":\"example.mod\",\"configs\":[{\"fileName\":\"ExampleMod\"}]}],\"modConfigRules\":["
                        + (reversed ? fileRule + "," + packageRule : packageRule + "," + fileRule) + "]}";
                    var parsed = ClientModConfigManifest.Read(json)!;
                    Check(parsed.Declares("EXAMPLE.MOD", "EXAMPLEMOD") && parsed.Enforces("EXAMPLE.MOD", "EXAMPLEMOD") == (exact is "" or "Enforce"), "Exact config precedence/culture changed.");
                    Check(!parsed.Enforces("other", "ExampleMod") && !parsed.Enforces("example.mod", "Other"), "Rule created undeclared config.");
                }
                var f = new ClientCompatibilityBaselineTests.Fixture();
                string? fallback = Manifest; int fallbackReads = 0;
                var diagnostics = new List<string>();
                var policy = new ClientModSettingsPolicy(f.Flow, () => { fallbackReads++; return fallback; }, diagnostics.Add);
                Check(policy.IsControlled("example.mod", "ExampleMod") && !policy.IsControlled("example.mod", "Optional"), "Active profile rules changed.");
                Check(ClientModConfigManifest.Read(Manifest)!.Declares("example.mod", "Optional"), "Ignored config was removed from profile membership.");
                f.Flow.Apply("{}"); int reads = fallbackReads;
                Check(!policy.IsControlled("example.mod", "ExampleMod") && reads == fallbackReads, "Nonempty baseline borrowed old profile.");
                f.Flow.Apply(Manifest); Check(policy.IsControlled("example.mod", "ExampleMod"), "Baseline replacement stayed cached.");
                policy.Invalidate(); Check(policy.IsControlled("example.mod", "ExampleMod"), "Explicit invalidation lost rules.");
                f.Flow.Apply("{"); Check(!policy.IsControlled("example.mod", "ExampleMod") && diagnostics.Count == 1, "Bad JSON escaped or used stale cache.");
                f.Flow.Apply(Manifest); Check(policy.IsControlled("example.mod", "ExampleMod"), "Bad JSON poisoned recovery.");
                f.Flow.Apply(null); fallback = null; Check(!policy.IsControlled("example.mod", "ExampleMod"), "Removed profile stayed cached.");
                foreach (string empty in new[] { "{}", "{\"mods\":null}", "{\"mods\":[null,{\"packageId\":\"example.mod\",\"configs\":null}]}" })
                    Check(!ClientModConfigManifest.Read(empty)!.Enforces("example.mod", "ExampleMod"), "Missing config collection crashed or became enforced.");
                Check(ClientModConfigManifest.Read(null) is null && ClientModConfigManifest.Read(" ") is null, "Empty manifest changed.");
            }
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }
    private static void Check(bool result, string message) { if (!result) throw new InvalidOperationException(message); }
}
