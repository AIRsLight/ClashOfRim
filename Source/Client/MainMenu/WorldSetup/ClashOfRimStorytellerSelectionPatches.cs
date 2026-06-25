using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim.MainMenu;

[HarmonyPatch(typeof(WindowStack), nameof(WindowStack.Add))]
public static class ClashOfRimStorytellerSelectionPatches
{
    private static readonly Dictionary<int, string> InGamePageInitialSignatures = new();

    [HarmonyPrefix]
    public static bool Prefix(Window window)
    {
        if (window is not Page_SelectStorytellerInGame)
        {
            return true;
        }

        ClashOfRimMod mod = LoadedModManager.GetMod<ClashOfRimMod>();
        if (!mod.IsInActiveMultiplayerSession)
        {
            return true;
        }

        if (mod.IsAdministrator)
        {
            InGamePageInitialSignatures[window.GetHashCode()] = mod.BuildCurrentStorytellerDifficultySignature();
            return true;
        }

        Messages.Message(
            ClashOfRimText.Key("ClashOfRim.Storyteller.SettingsLocked"),
            MessageTypeDefOf.RejectInput,
            historical: false);
        return false;
    }

    [HarmonyPostfix]
    public static void Postfix(Window window)
    {
        if (window is not Page_SelectStoryteller storytellerPage)
        {
            return;
        }

        ClashOfRimMod mod = LoadedModManager.GetMod<ClashOfRimMod>();
        mod.LockMultiplayerCommitmentModeIfNeeded();
        mod.TryApplyServerStorytellerBaselineAndContinue(storytellerPage);
    }

    internal static string? TakeInitialSignature(Page_SelectStorytellerInGame page)
    {
        int key = page.GetHashCode();
        if (!InGamePageInitialSignatures.TryGetValue(key, out string? signature))
        {
            return null;
        }

        InGamePageInitialSignatures.Remove(key);
        return signature;
    }
}

[HarmonyPatch(typeof(Window), nameof(Window.PostClose))]
public static class ClashOfRimStorytellerInGamePostClosePatch
{
    [HarmonyPostfix]
    public static void Postfix(Window __instance)
    {
        if (__instance is not Page_SelectStorytellerInGame page)
        {
            return;
        }

        ClashOfRimMod mod = LoadedModManager.GetMod<ClashOfRimMod>();
        mod.SubmitRuntimeStorytellerSettingsIfChanged(
            ClashOfRimStorytellerSelectionPatches.TakeInitialSignature(page));
    }
}

[HarmonyPatch(typeof(Page_SelectStoryteller), nameof(Page_SelectStoryteller.DoWindowContents))]
public static class ClashOfRimStorytellerCommitmentModePatch
{
    [HarmonyPostfix]
    public static void Postfix()
    {
        LoadedModManager.GetMod<ClashOfRimMod>().LockMultiplayerCommitmentModeIfNeeded();
    }
}
