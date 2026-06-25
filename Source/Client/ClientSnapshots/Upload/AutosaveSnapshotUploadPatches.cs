using HarmonyLib;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim.ClientSnapshots;

[HarmonyPatch(typeof(Autosaver), nameof(Autosaver.DoAutosave))]
public static class AutosaveSnapshotUploadPatch
{
    [HarmonyPrefix]
    public static bool Prefix()
    {
        ClashOfRimMod? mod = LoadedModManager.GetMod<ClashOfRimMod>();
        if (mod is null || !mod.ShouldInterceptVanillaAutosave)
        {
            return true;
        }

        mod.StartAutosaveSnapshotUpload();
        return false;
    }
}
