using HarmonyLib;
using LudeonTK;
using RimWorld;
using UnityEngine;
using Verse;

namespace AIRsLight.ClashOfRim.Admin;

[HarmonyPatch(typeof(Prefs), "set_DevMode")]
public static class ClashOfRimPrefsDevModePatch
{
    public static bool Prefix(ref bool value)
    {
        if (!value || DeveloperToolAccessPolicy.CanUseDeveloperTools())
        {
            return true;
        }

        DeveloperToolAccessPolicy.NotifyBlocked();
        value = false;
        return false;
    }
}

[HarmonyPatch(typeof(DebugWindowsOpener), nameof(DebugWindowsOpener.DevToolStarterOnGUI))]
public static class ClashOfRimDebugWindowsOpenerPatch
{
    public static bool Prefix()
    {
        if (!Prefs.DevMode || DeveloperToolAccessPolicy.CanUseDeveloperTools())
        {
            return true;
        }

        Prefs.DevMode = false;
        DeveloperToolAccessPolicy.NotifyBlocked();
        return false;
    }
}

internal static class DeveloperToolAccessPolicy
{
    private static float nextNotificationAt;

    public static bool CanUseDeveloperTools()
    {
        ClashOfRimMod? mod = LoadedModManager.GetMod<ClashOfRimMod>();
        return mod is null || mod.CanUseDeveloperTools;
    }

    public static void NotifyBlocked()
    {
        if (Time.realtimeSinceStartup < nextNotificationAt)
        {
            return;
        }

        nextNotificationAt = Time.realtimeSinceStartup + 3f;
        Messages.Message(ClashOfRimText.Key("ClashOfRim.Admin.DevToolsBlocked"), MessageTypeDefOf.RejectInput, historical: false);
    }

    public static void EnforceCurrentState()
    {
        if (CanUseDeveloperTools())
        {
            return;
        }

        DebugSettings.godMode = false;
        DebugSettings.devPalette = false;
        if (Prefs.DevMode)
        {
            Prefs.DevMode = false;
        }

        WindowStack? stack = Find.WindowStack;
        if (stack is null)
        {
            return;
        }

        stack.TryRemove(typeof(Dialog_Debug), true);
        stack.TryRemove(typeof(Dialog_DevPalette), true);
        stack.TryRemove(typeof(EditWindow_TweakValues), true);
        stack.TryRemove(typeof(EditWindow_DebugInspector), true);
    }
}
