using System;
using AIRsLight.ClashOfRim.Modules.World;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim;

internal sealed class ClientWorldNameRuntime : IClientWorldFeatureNameGenerator
{
    public string? Generate(string defName, string seed, Func<string, bool> available)
        => Generate(DefDatabase<FeatureDef>.GetNamedSilentFail(defName)?.nameMaker, seed, available);
    internal static string? Generate(RulePackDef? maker, string seed, Func<string, bool>? available = null)
    {
        if (maker is null) return null;
        Rand.PushState(GenText.StableStringHash(seed));
        try { return NameGenerator.GenerateName(maker, candidate => available is null || available(candidate), appendNumberIfNameUsed: false, rootKeyword: "r_name"); }
        catch (Exception ex) { Log.Warning("[ClashOfRim] Failed to localize world name: " + ex.Message); return null; }
        finally { Rand.PopState(); }
    }
    internal static string? ReadCurrentGameLanguage()
    {
        try { return (LanguageDatabase.activeLanguage?.folderName ?? Prefs.LangFolderName)?.Trim(); }
        catch { return null; }
    }
}
