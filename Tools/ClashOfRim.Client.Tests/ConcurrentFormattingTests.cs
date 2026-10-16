using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using HarmonyLib;
using Verse;

internal static class ConcurrentFormattingTests
{
    public static void Run()
    {
        var original = LanguageDatabase.activeLanguage;
        var harmony = new Harmony("ClashOfRim.Tests.ConcurrentFormatting");
        var patch = typeof(AIRsLight.ClashOfRim.ClashOfRimMod).Assembly.GetType("AIRsLight.ClashOfRim.CoreCompatibility.ConcurrentMessageFormattingPatch", true)!;
        harmony.CreateClassProcessor(patch).Patch();
        var language = new LoadedLanguage("English");
        AccessTools.Field(typeof(LoadedLanguage), "workerInt").SetValue(language, new LanguageWorker_English());
        LanguageDatabase.activeLanguage = language;
        try
        {
            Parallel.For(0, 8, worker =>
            {
                for (int i = 0; i < 1000; i++)
                {
                    string group = "Group" + worker, count = (worker * 1000 + i).ToString();
                    string expected = group + ": " + count;
                    string actual = (i % 2 == 0
                        ? "{GROUP}: {COUNT}".Formatted(group.Named("GROUP"), count.Named("COUNT"))
                        : GrammarResolverSimple.Formatted("{GROUP}: {COUNT}", new List<string> { "GROUP", "COUNT" }, new List<object> { group, count })).ToString();
                    if (actual != expected) throw new Exception($"Concurrent formatter mixed arguments: {expected} => {actual}");
                }
            });
        }
        finally { harmony.UnpatchAll(harmony.Id); LanguageDatabase.activeLanguage = original; }
    }
}
