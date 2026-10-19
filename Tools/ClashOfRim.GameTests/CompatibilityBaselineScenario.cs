using AIRsLight.ClashOfRim.Compatibility;
using AIRsLight.ClashOfRim.Network;
using RimTest.Protocol;
using RimTest.Testing;

namespace ClashOfRim.GameTests;

internal sealed class CompatibilityBaselineScenario(TestRun run, ScenarioPolling polling, ServerEntryScenario entry,
    CompatibilityBaselineRegistry baseline, Func<string> address, Func<string, bool> online)
{
    internal async Task RejectIncompatible()
    {
        var original = baseline.Current ?? throw new InvalidOperationException("Missing first-owner baseline.");
        await entry.LogoutWithSnapshot("B");
        try
        {
            foreach (string kind in new[] { "missing-mod", "mod-order", "config", "file-hash" })
            {
                var mods = original.Mods.ToArray();
                int index = Array.FindIndex(mods, m => m.Role == ModCompatibilityRole.Required && m.Files.Count > 0 && !m.PackageId.StartsWith("ludeon.", StringComparison.Ordinal));
                Check(index >= 0, "No required file-controlled mod for compatibility probe.");
                switch (kind)
                {
                    case "missing-mod": mods = mods.Append(new ModManifestEntry { PackageId = "rimtest.missing.required", Name = "RimTest missing required mod", LoadOrder = mods.Length }).ToArray(); break;
                    case "mod-order": mods = mods.Select(m => m with { LoadOrder = mods.Length - m.LoadOrder }).Reverse().ToArray(); break;
                    case "config":
                        const string xml = "<settings><server>中文 İ 1,25</server></settings>";
                        mods[index] = mods[index] with { Configs = [new() { FileName = "RimTestServerOnly", HasSavedFile = true, CanonicalXml = xml, Sha256 = ModConfigXmlCanonicalizer.Sha256(xml) }] }; break;
                    default: mods[index] = mods[index] with { Files = mods[index].Files.Select((f, i) => i == 0 ? f with { Sha256 = new string('0', 64) } : f).ToArray() }; break;
                }
                // Explicit server fixture; admission and rejection below use the real wire/client entry flow.
                baseline.ReplaceBaseline(original with { ManifestId = "rimtest-" + kind, Mods = mods }, "rimtest-A", DateTimeOffset.UtcNow);
                await run.RecordAsync(new { serverFixture = "compatibility-baseline", kind });
                Check((await run.InvokeClientAsync("B", "StartMainMenuServerEntryFlow", new { serverBaseUrl = address(), userId = "rimtest-B", password = "local-test-password", createAccountIfMissing = false })).Bool("returnValue"), "Entry did not start.");
                var mismatch = await polling.Wait("B", "clash.compatibility", null, s => s.Bool("open"));
                Check(!mismatch.Bool("canContinue"), "Blocking mismatch offered Join Anyway: " + kind);
                Check(mismatch.Int(kind == "config" ? "configDifferences" : kind == "file-hash" ? "fileDifferences" : "manifestDifferences") > 0, "Mismatch was assigned to the wrong category: " + kind);
                if (kind == "config") Check(mismatch.Bool("canApplyConfig"), "Configuration mismatch offered no repair.");
                if (kind == "file-hash") Check(!mismatch.Bool("canApplyManifest"), "File mismatch incorrectly offered mod-list repair.");
                Check(!(await run.CallAsync("B", "clash.state")).Bool("isInActiveMultiplayerSession") && !online("rimtest-B"), "Rejected client became online: " + kind);
                await run.CallAsync("B", "clash.compatibility.close");
                await polling.Idle("B");
            }
        }
        finally
        {
            if ((await run.CallAsync("B", "clash.compatibility")).Bool("open")) await run.CallAsync("B", "clash.compatibility.close");
            baseline.ReplaceBaseline(original, "rimtest-A", DateTimeOffset.UtcNow);
            await entry.LoginExisting(true);
        }
    }
}
