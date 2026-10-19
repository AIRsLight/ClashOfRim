using HarmonyLib;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim.MainMenu;

[HarmonyPatch(typeof(WindowStack), nameof(WindowStack.Add))]
public static class ClashOfRimStorytellerSelectionPatches
{
    [HarmonyPrefix]
    public static bool Prefix(Window window)
        => ClientStorytellerEditor.Current?.Opening(window) ?? true;

    [HarmonyPostfix]
    public static void Postfix(Window window)
    {
        if (window is not Page_SelectStoryteller storytellerPage)
        {
            return;
        }

        var configuration = ClientStorytellerPage.Configuration;
        configuration?.LockCommitment();
        configuration?.ContinueSetup(new ClientStorytellerPage(storytellerPage));
    }

}

[HarmonyPatch(typeof(Window), nameof(Window.PostClose))]
public static class ClashOfRimStorytellerInGamePostClosePatch
{
    [HarmonyPostfix]
    public static void Postfix(Window __instance)
        => _ = ClientStorytellerEditor.Current?.ClosedAsync(__instance);
}

[HarmonyPatch(typeof(Page_SelectStoryteller), nameof(Page_SelectStoryteller.DoWindowContents))]
public static class ClashOfRimStorytellerCommitmentModePatch
{
    [HarmonyPostfix]
    public static void Postfix()
    {
        ClientStorytellerPage.Configuration?.LockCommitment();
    }
}
