using System.Runtime.CompilerServices;
using AIRsLight.ClashOfRim.Visuals;
using HarmonyLib;
using UnityEngine;
using Verse;

internal static class ClientCommandIconTestAssets
{
    static ClientCommandIconTestAssets()
    {
        // readonly command icons live for the process. Initialize them before an
        // unrelated fixture's null texture stub can permanently populate them.
        var harmony = new Harmony("ClashOfRim.Tests.CommandIconAssets");
        harmony.Patch(AccessTools.Method(typeof(ContentFinder<Texture2D>), nameof(ContentFinder<Texture2D>.Get)),
            prefix: new HarmonyMethod(typeof(ClientCommandIconTestAssets), nameof(Texture)));
        try { RuntimeHelpers.RunClassConstructor(typeof(ClashCommandIcons).TypeHandle); }
        finally { harmony.UnpatchAll(harmony.Id); }
    }

    internal static void Initialize() { }
    private static bool Texture(ref Texture2D __result)
    { __result = (Texture2D)RuntimeHelpers.GetUninitializedObject(typeof(Texture2D)); return false; }
}
