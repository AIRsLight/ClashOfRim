using HarmonyLib;
using RimWorld;

namespace AIRsLight.ClashOfRim.DlcCompatibility;

[HarmonyPatch(typeof(IdeoDevelopmentUtility), nameof(IdeoDevelopmentUtility.ApplyChangesToIdeo))]
internal static class IdeoReformSyncPatches
{
    public static void Postfix(Ideo ideo) => _ = ClientIdeologyReforms.Current?.NotifyAsync(ideo);
}
