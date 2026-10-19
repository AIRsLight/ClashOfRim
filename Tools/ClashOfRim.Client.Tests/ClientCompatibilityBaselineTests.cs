using System;
using System.Collections.Generic;
using System.Linq;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Compatibility;

internal static class ClientCompatibilityBaselineTests
{
    internal static void Run()
    {
        try
        {
            foreach (string? input in new[] { null, "", " ", "{}", "{\"dlcIds\":null}", "{\"dlcIds\":[]}", "{", "{\"dlcIds\":[\"Biotech\",\"İ\"]}" })
            {
                var f = new Fixture(); f.Flow.Apply(input);
                Check(f.Flow.Manifest == (input ?? "") && f.Invalidations == 1 && f.Calls.Count == 1, "Manifest/default projection changed.");
                Check(f.Diagnostics == (input == "{" ? 1 : 0), "Parse failure policy changed.");
                if (input?.Contains("Biotech") == true) Check(f.Calls.Single()!.SequenceEqual(new[] { "Biotech", "İ" }), "DLC IDs changed.");
            }
            foreach (string phase in new[] { "diagnostic", "dlc" })
            {
                foreach (bool replace in new[] { false, true })
                {
                    var f = new Fixture(); bool current = true;
                    f.Hook = stage => { if (stage == phase) { f.Hook = null; if (replace) f.Flow.Apply("{\"dlcIds\":[\"new\"]}"); else current = false; } };
                    f.Flow.Apply(phase == "diagnostic" ? "{" : "{\"dlcIds\":[\"old\"]}", () => current);
                    Check(f.Invalidations == (replace ? 1 : 0), "Obsolete publication invalidated the new baseline.");
                    if (replace) Check(f.Flow.Manifest.Contains("new") && f.Calls.Last()!.Single() == "new", "Reentrant baseline was overwritten.");
                }
                var failure = new Fixture(); failure.Hook = stage => { if (stage == phase) throw new InvalidOperationException("injected"); };
                try { failure.Flow.Apply(phase == "diagnostic" ? "{" : "{}"); throw new Exception("Expected callback failure."); }
                catch (InvalidOperationException ex) when (ex.Message == "injected") { }
                failure.Hook = null; failure.Flow.Apply("{\"dlcIds\":[]}"); Check(failure.Invalidations == 1, "Failure poisoned next publication.");
            }
            var skipped = new Fixture(); skipped.Flow.Apply("ignored", () => false);
            Check(skipped.Flow.Manifest == "" && skipped.Calls.Count == 0 && skipped.Diagnostics == 0, "Expired receipt published.");
            Console.WriteLine("  compatibility baseline: 15 scenarios checked");
        }
        finally { ClientSessionWork.Reset(); }
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    internal sealed class Fixture : IClientCompatibilityBaselineRuntime
    {
        internal readonly ClientCompatibilityBaseline Flow;
        internal readonly List<string[]?> Calls = new();
        internal int Invalidations, Diagnostics;
        internal Action<string>? Hook;
        internal Fixture() { ClientSessionWork.Reset(); Flow = new(this); }
        public void ApplyDlcIds(IReadOnlyList<string>? ids) { Calls.Add(ids?.ToArray()); Hook?.Invoke("dlc"); }
        public void InvalidateManifestCache() => Invalidations++;
        public void Diagnose(string message) { Diagnostics++; Hook?.Invoke("diagnostic"); }
    }
}
