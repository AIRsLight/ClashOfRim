using System;
using System.Globalization;
using System.Xml.Linq;
using AIRsLight.ClashOfRim;
using HarmonyLib;
using Verse;

internal static class ScribeCultureTests
{
    public static void Run()
    {
        var original = CultureInfo.CurrentCulture;
        var ui = CultureInfo.CurrentUICulture;
        var harmony = new Harmony("ClashOfRim.Tests.ScribeCulture");
        var patch = typeof(ClashOfRimMod).Assembly.GetType("AIRsLight.ClashOfRim.CoreCompatibility.InvariantGameDataCulturePatch", true)!;
        harmony.CreateClassProcessor(patch).Patch();
        try
        {
            foreach (string name in new[] { "en-US", "fr-FR", "de-DE", "tr-TR", "zh-CN" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
                var rule = new Verse.Grammar.Rule_String("test(p=0.15,priority=1.25)->中文 Français");
                if (rule.BaseSelectionWeight != .15f || rule.Priority != 1.25f || rule.Generate() != "中文 Français")
                    throw new Exception("Grammar numeric parameters or translated text changed.");
                var root = XElement.Parse(Scribe.saver.DebugOutputFor(new Probe()));
                foreach (var (field, expected) in new[] { ("coverage", .3f), ("pollution", .25f), ("threat", 1.37f) })
                {
                    float actual = float.Parse(root.Element(field)!.Value, CultureInfo.InvariantCulture);
                    if (actual != expected) throw new Exception($"{name}: {field} became {actual}");
                }
                if (CultureInfo.CurrentCulture.Name != name || CultureInfo.CurrentUICulture != ui)
                    throw new Exception("Serialization leaked culture changes.");
                // Exceptional exit must restore the caller too.
                try { Scribe.saver.DebugOutputFor(new Probe { Fail = true }); }
                catch (InvalidOperationException) { }
                finally { Scribe.ForceStop(); }
                if (CultureInfo.CurrentCulture.Name != name) throw new Exception("Failure leaked invariant culture.");
            }
        }
        finally
        {
            harmony.UnpatchAll(harmony.Id);
            CultureInfo.CurrentCulture = original;
            Scribe.ForceStop();
        }
    }

    private sealed class Probe : IExposable
    {
        public bool Fail;
        public void ExposeData()
        {
            if (Fail) throw new InvalidOperationException("Expected serialization failure");
            float coverage = .3f, pollution = .25f, threat = 1.37f;
            Scribe_Values.Look(ref coverage, "coverage");
            Scribe_Values.Look(ref pollution, "pollution");
            Scribe_Values.Look(ref threat, "threat");
        }
    }
}
