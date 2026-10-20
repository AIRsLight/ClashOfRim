using System.Collections.Generic;
using HarmonyLib;
using Verse;
using Verse.Grammar;

namespace AIRsLight.ClashOfRim.DlcCompatibility;

internal static class OdysseyFactionNameRulePatch
{
    private static readonly AccessTools.FieldRef<RulePackDef, RulePack> Pack =
        AccessTools.FieldRefAccess<RulePackDef, RulePack>("rulePack");
    private static readonly AccessTools.FieldRef<RulePack, List<string>> Rules =
        AccessTools.FieldRefAccess<RulePack, List<string>>("rulesStrings");

    internal static void Apply(Harmony harmony)
    {
        var prefix = new HarmonyMethod(typeof(OdysseyFactionNameRulePatch), nameof(Prefix));
        harmony.Patch(AccessTools.PropertyGetter(typeof(RulePackDef), nameof(RulePackDef.RulesPlusIncludes)), prefix: prefix);
        harmony.Patch(AccessTools.PropertyGetter(typeof(RulePackDef), nameof(RulePackDef.RulesImmediate)), prefix: prefix);
    }

    private static void Prefix(RulePackDef __instance)
    {
        if (__instance.defName != "NamerFactionTradersGuild" || Pack(__instance) is not { } pack) return;
        // Odyssey 1.7 Russian translation omits the root keyword on this one
        // alternative. Repair before native parsing; leave other rules and the
        // untranslated fallback intact. Already corrected translations are no-ops.
        var rules = Rules(pack);
        for (int i = 0; i < rules.Count; i++)
            if (rules[i] == "[tradeAdj_fem] [tradeNoun_fem]")
                rules[i] = "r_name->" + rules[i];
    }
}
