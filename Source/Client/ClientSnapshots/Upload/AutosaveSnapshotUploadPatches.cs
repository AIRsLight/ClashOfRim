using HarmonyLib;
using RimWorld;

namespace AIRsLight.ClashOfRim.ClientSnapshots;

[HarmonyPatch(typeof(Autosaver), nameof(Autosaver.DoAutosave))]
public static class AutosaveSnapshotUploadPatch
{
    [HarmonyPrefix]
    public static bool Prefix() => ClientSnapshotSavingCallbacks.Current?.TryAutosave() != true;
}
