using HarmonyLib;
using RimWorld;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using AIRsLight.ClashOfRim.CompatibilityClient;
using AIRsLight.ClashOfRim.RemoteMaps;
using UnityEngine;
using Verse;
using RimWorld.Planet;

namespace AIRsLight.ClashOfRim.MainMenu;

[HarmonyPatch(typeof(OptionListingUtility), nameof(OptionListingUtility.DrawOptionListing))]
public static class ClashOfRimMainMenuPatches
{
    private static readonly ConcurrentQueue<Action> MainThreadActions = new();

    public static void EnqueueMainThreadAction(Action action)
    {
        if (action is not null)
        {
            MainThreadActions.Enqueue(action);
        }
    }

    [HarmonyPrefix]
    public static void Prefix(Rect rect, List<ListableOption> optList)
    {
        RunQueuedMainThreadActions();

        if (Current.ProgramState != ProgramState.Entry || optList is null)
        {
            RewritePlayingMenuForMultiplayer(optList);
            return;
        }

        if (!IsEntryMainMenuOptionList(optList))
        {
            return;
        }

        if (CompatibilityConfigOverlayPath.ServerProfileActive)
        {
            RedirectSinglePlayerEntryOptionsForServerProfile(optList);
        }

        string label = ClashOfRimText.Key("ClashOfRim.ServerEntry.JoinTitle");
        if (optList.Any(option => string.Equals(option.label, label, System.StringComparison.Ordinal)))
        {
            return;
        }

        int insertIndex = optList.FindIndex(option => LabelEquals(option, "NewColony"));
        if (insertIndex < 0)
        {
            insertIndex = Math.Min(1, optList.Count);
        }

        optList.Insert(insertIndex, new ListableOption(label, () =>
        {
            OpenServerEntryDialog();
        }));
    }

    private static void RedirectSinglePlayerEntryOptionsForServerProfile(List<ListableOption> optList)
    {
        ReplaceOption(optList, "NewColony", "NewColony".Translate().ToString(), RequestStandaloneRestart);
        ReplaceOption(optList, "LoadGame", "LoadGame".Translate().ToString(), RequestStandaloneRestart);
    }

    private static void RequestStandaloneRestart()
    {
        Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
            ClashOfRimText.Key("ClashOfRim.Compatibility.ServerProfileSinglePlayerRestart"),
            GenCommandLine.Restart,
            destructive: false,
            title: ClashOfRimText.Key("ClashOfRim.Compatibility.ServerProfileActiveTitle")));
    }

    private static void OpenServerEntryDialog()
    {
        ClashOfRimMod? mod = ClashOfRimMod.Instance ?? LoadedModManager.GetMod<ClashOfRimMod>();
        if (mod is null)
        {
            string message = ClashOfRimText.Key("ClashOfRim.ServerEntry.ModUnavailable");
            Log.Warning("[ClashOfRim] Main menu server entry requested before mod instance was available.");
            Find.WindowStack.Add(new Dialog_MessageBox(message));
            return;
        }

        if (Find.WindowStack.WindowOfType<ClashOfRimServerEntryDialog>() is null)
        {
            Find.WindowStack.Add(new ClashOfRimServerEntryDialog(mod));
        }
    }

    private static void RewritePlayingMenuForMultiplayer(List<ListableOption>? optList)
    {
        if (Current.ProgramState != ProgramState.Playing || optList is null)
        {
            return;
        }

        ClashOfRimMod? mod = ClashOfRimMod.Instance ?? LoadedModManager.GetMod<ClashOfRimMod>();
        if (mod is null)
        {
            return;
        }

        if (!IsPlayingGameMenuOptionList(optList))
        {
            return;
        }

        ActiveRemoteMapSession? activeRemoteMap = ClashOfRimGameComponent.ActiveRemoteMapSession;
        if (activeRemoteMap?.IsActive == true)
        {
            optList.RemoveAll(option => LabelEquals(option, "LoadGame"));
            if (!activeRemoteMap.IsRaidBattle)
            {
                InsertRemoteMapSessionOptionBeforeSave(optList, mod, activeRemoteMap);
            }
        }

        if (!mod.CanUseVanillaMenuSnapshotUpload)
        {
            return;
        }

        optList.RemoveAll(option => LabelEquals(option, "LoadGame"));
        ReplaceOption(optList, "Save", ClashOfRimText.Key("ClashOfRim.Menu.UploadSnapshot"), () =>
            StartMenuUpload(mod, onSuccess: null));
        ReplaceOption(optList, "SaveAndQuitToMainMenu", ClashOfRimText.Key("ClashOfRim.Menu.UploadSnapshotAndQuitToMainMenu"), () =>
            StartMenuUpload(mod, GenScene.GoToMainMenu));
        ReplaceOption(optList, "SaveAndQuitToOS", ClashOfRimText.Key("ClashOfRim.Menu.UploadSnapshotAndQuitToOS"), () =>
            StartMenuUpload(mod, Root.Shutdown));
    }

    private static void InsertRemoteMapSessionOptionBeforeSave(
        List<ListableOption> optList,
        ClashOfRimMod mod,
        ActiveRemoteMapSession session)
    {
        string label = RemoteMapSessionController.PrimaryActionLabel(session);
        if (optList.Any(option => string.Equals(option.label, label, System.StringComparison.Ordinal)))
        {
            return;
        }

        var option = new ListableOption(label, () => StartRemoteMapSessionMenuAction(mod, session));
        int saveIndex = optList.FindIndex(option => LabelEquals(option, "Save"));
        if (saveIndex >= 0)
        {
            option.minHeight = optList[saveIndex].minHeight;
            optList.Insert(saveIndex, option);
            return;
        }

        optList.Insert(0, option);
    }

    private static bool IsEntryMainMenuOptionList(List<ListableOption> optList)
    {
        return optList.Any(option =>
            LabelEquals(option, "NewColony")
            || LabelEquals(option, "LoadGame"));
    }

    private static bool IsPlayingGameMenuOptionList(List<ListableOption> optList)
    {
        return optList.Any(option =>
            LabelEquals(option, "Save")
            || LabelEquals(option, "SaveAndQuitToMainMenu")
            || LabelEquals(option, "SaveAndQuitToOS"));
    }

    private static void StartRemoteMapSessionMenuAction(ClashOfRimMod mod, ActiveRemoteMapSession session)
    {
        Find.MainTabsRoot?.SetCurrentTab(null);
        RemoteMapSessionController.TryRunPrimaryAction(
            mod,
            session,
            "ManualFinish",
            requireConfirmation: true);
    }

    private static bool LabelEquals(ListableOption option, string translationKey)
    {
        return string.Equals(option.label, translationKey.Translate().ToString(), System.StringComparison.Ordinal);
    }

    private static void ReplaceOption(
        List<ListableOption> optList,
        string vanillaTranslationKey,
        string replacementLabel,
        Action action)
    {
        int index = optList.FindIndex(option => LabelEquals(option, vanillaTranslationKey));
        if (index < 0)
        {
            return;
        }

        optList[index] = new ListableOption(replacementLabel, action)
        {
            minHeight = optList[index].minHeight
        };
    }

    private static void StartMenuUpload(ClashOfRimMod mod, Action? onSuccess)
    {
        Find.MainTabsRoot?.SetCurrentTab(null);
        mod.StartVanillaMenuSnapshotUpload(onSuccess);
    }

    internal static void RunQueuedMainThreadActions()
    {
        while (MainThreadActions.TryDequeue(out Action action))
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Log.Warning("[ClashOfRim] Main menu action failed: " + ex);
            }
        }
    }
}

[HarmonyPatch(typeof(UIRoot_Entry), nameof(UIRoot_Entry.UIRootUpdate))]
public static class ClashOfRimEntryUiMainThreadActionPatch
{
    [HarmonyPostfix]
    public static void Postfix()
    {
        ClashOfRimMainMenuPatches.RunQueuedMainThreadActions();
    }
}

[HarmonyPatch(typeof(GenScene), nameof(GenScene.GoToMainMenu))]
public static class ClashOfRimGoToMainMenuDisconnectPatch
{
    [HarmonyPrefix]
    public static void Prefix()
    {
        LoadedModManager.GetMod<ClashOfRimMod>()?.DisconnectMultiplayerSessionForExit();
    }
}

[HarmonyPatch(typeof(Root), nameof(Root.Shutdown))]
public static class ClashOfRimShutdownDisconnectPatch
{
    [HarmonyPrefix]
    public static void Prefix()
    {
        LoadedModManager.GetMod<ClashOfRimMod>()?.DisconnectMultiplayerSessionForExit();
    }
}
