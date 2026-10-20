using System;
using System.Collections.Generic;
using System.Linq;
using AIRsLight.ClashOfRim.DlcCompatibility;
using HarmonyLib;
using Verse;
using Verse.Grammar;

internal static class OdysseyFactionNameRuleTests
{
    private static readonly List<string> errors = new();
    private static bool Error(string text) { errors.Add(text); return false; }

    internal static void Run()
    {
        var harmony = new Harmony("ClashOfRim.Tests.OdysseyFactionNameRules");
        try
        {
            harmony.Patch(AccessTools.Method(typeof(Log), nameof(Log.Error)), prefix: new HarmonyMethod(typeof(OdysseyFactionNameRuleTests), nameof(Error)));
            OdysseyFactionNameRulePatch.Apply(harmony);
            foreach (bool immediate in new[] { false, true })
            {
                errors.Clear();
                var def = Pack("NamerFactionTradersGuild", "[tradeAdj_fem] [tradeNoun_fem]");
                var rules = immediate ? def.RulesImmediate : def.RulesPlusIncludes;
                Check(errors.Count == 0 && rules.All(r => r.keyword is not null)
                    && rules.Count(r => r.keyword == "r_name") == 2, "Known Russian rule still failed native parsing.");
                var again = immediate ? def.RulesImmediate : def.RulesPlusIncludes;
                Check(ReferenceEquals(rules, again) && rules.Count == 4, "Repair changed the native cache or duplicated rules.");
                Check(def.UntranslatedRulesPlusIncludes.Single().Generate() == "English fallback", "Repair changed untranslated rules.");
            }
            errors.Clear();
            var corrected = Pack("NamerFactionTradersGuild", "r_name->[tradeAdj_fem] [tradeNoun_fem]");
            Check(corrected.RulesPlusIncludes.Count == 4 && errors.Count == 0, "Already fixed translation changed.");
            foreach (var def in new[] { Pack("Unrelated", "[tradeAdj_fem] [tradeNoun_fem]"), Pack("NamerFactionTradersGuild", "[another_bad_rule]") })
            {
                errors.Clear();
                _ = def.RulesPlusIncludes;
                Check(errors.Count == 1 && errors[0].StartsWith("Bad string pass", StringComparison.Ordinal),
                    "Unrelated grammar errors were hidden.");
            }
        }
        finally { harmony.UnpatchAll(harmony.Id); errors.Clear(); }
    }

    private static RulePackDef Pack(string name, string alternative)
    {
        var pack = new RulePack();
        var field = AccessTools.Field(typeof(RulePack), "rulesStrings");
        field.SetValue(pack, new List<string> { "r_name->English fallback" });
        pack.PostLoad();
        field.SetValue(pack, new List<string>
        { "r_name->masculine name", alternative, "tradeAdj_fem->торговая", "tradeNoun_fem->гильдия" });
        var def = new RulePackDef { defName = name };
        AccessTools.Field(typeof(RulePackDef), "rulePack").SetValue(def, pack);
        return def;
    }
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
}
