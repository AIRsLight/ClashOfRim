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

[HarmonyPatch(typeof(MainMenuDrawer), nameof(MainMenuDrawer.DoMainMenuControls))]
internal static class ClashOfRimMainMenuDrawContextPatch
{
    [HarmonyPrefix]
    private static void Prefix(Rect rect, bool anyMapFiles)
    {
        MainMenuDrawContext.Begin(rect.height, anyMapFiles);
    }

    [HarmonyFinalizer]
    private static Exception? Finalizer(Exception? __exception)
    {
        MainMenuDrawContext.End();
        return __exception;
    }
}

internal static class MainMenuDrawContext
{
    [ThreadStatic]
    private static bool active;

    [ThreadStatic]
    private static bool primaryOptionsClaimed;

    [ThreadStatic]
    private static bool anyMapFiles;

    [ThreadStatic]
    private static float expectedPrimaryHeight;

    public static bool AnyMapFiles => active && anyMapFiles;

    public static void Begin(float primaryHeight, bool hasMapFiles)
    {
        active = true;
        primaryOptionsClaimed = false;
        anyMapFiles = hasMapFiles;
        expectedPrimaryHeight = primaryHeight;
    }

    public static bool TryClaimPrimaryOptions(Rect rect)
    {
        const float vanillaPrimaryWidth = 170f;
        if (!active
            || primaryOptionsClaimed
            || !Mathf.Approximately(rect.x, 0f)
            || !Mathf.Approximately(rect.y, 0f)
            || !Mathf.Approximately(rect.width, vanillaPrimaryWidth)
            || !Mathf.Approximately(rect.height, expectedPrimaryHeight))
        {
            return false;
        }

        primaryOptionsClaimed = true;
        return true;
    }

    public static void End()
    {
        active = false;
        primaryOptionsClaimed = false;
        anyMapFiles = false;
        expectedPrimaryHeight = 0f;
    }
}

internal enum ClashOfRimMenuOptionKind
{
    ServerEntry,
    RemoteMapAction
}

internal sealed class ClashOfRimMenuOption : ListableOption
{
    public ClashOfRimMenuOption(ClashOfRimMenuOptionKind kind, string label, Action action)
        : base(label, action)
    {
        Kind = kind;
    }

    public ClashOfRimMenuOptionKind Kind { get; }
}

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
    [HarmonyPriority(Priority.First)]
    public static void Prefix(Rect rect, List<ListableOption> optList)
    {
        RunQueuedMainThreadActions();

        if (optList is null || !MainMenuDrawContext.TryClaimPrimaryOptions(rect))
        {
            return;
        }

        if (Current.ProgramState == ProgramState.Entry)
        {
            RewriteEntryMenu(optList);
            return;
        }

        if (Current.ProgramState == ProgramState.Playing)
        {
            RewritePlayingMenuForMultiplayer(optList);
        }
    }

    private static void RewriteEntryMenu(List<ListableOption> optList)
    {
        const int newColonyIndex = 1;
        int loadGameIndex = 2 + (Prefs.DevMode ? 1 : 0);
        if (CompatibilityConfigOverlayPath.ServerProfileActive)
        {
            ReplaceAt(optList, newColonyIndex, "NewColony".Translate().ToString(), RequestStandaloneRestart);
            if (MainMenuDrawContext.AnyMapFiles)
            {
                ReplaceAt(optList, loadGameIndex, "LoadGame".Translate().ToString(), RequestStandaloneRestart);
            }
        }

        string label = ClashOfRimText.Key("ClashOfRim.ServerEntry.JoinTitle");
        if (optList.OfType<ClashOfRimMenuOption>().Any(option => option.Kind == ClashOfRimMenuOptionKind.ServerEntry))
        {
            return;
        }

        optList.Insert(Math.Min(1, optList.Count), new ClashOfRimMenuOption(
            ClashOfRimMenuOptionKind.ServerEntry,
            label,
            OpenServerEntryDialog));
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

    private static void RewritePlayingMenuForMultiplayer(List<ListableOption> optList)
    {
        ClashOfRimMod? mod = ClashOfRimMod.Instance ?? LoadedModManager.GetMod<ClashOfRimMod>();
        if (mod is null)
        {
            return;
        }

        ActiveRemoteMapSession? activeRemoteMap = ClashOfRimGameComponent.ActiveRemoteMapSession;
        bool normalSavePresent = !GameDataSaveLoader.SavingIsTemporarilyDisabled
            && Current.Game?.Info?.permadeathMode != true;
        bool loadPresent = MainMenuDrawContext.AnyMapFiles
            && Current.Game?.Info?.permadeathMode != true;
        int loadIndex = normalSavePresent ? 1 : 0;
        bool remoteActionInserted = false;
        if (activeRemoteMap?.IsActive == true)
        {
            RemoveAtIfPresent(optList, loadPresent ? loadIndex : -1);
            loadPresent = false;
            if (!activeRemoteMap.IsRaidBattle)
            {
                InsertRemoteMapSessionOption(optList, mod, activeRemoteMap);
                remoteActionInserted = true;
            }
        }

        if (!mod.CanUseVanillaMenuSnapshotUpload)
        {
            return;
        }

        RemoveAtIfPresent(optList, loadPresent ? loadIndex : -1);
        if (normalSavePresent)
        {
            ReplaceAt(optList, remoteActionInserted ? 1 : 0, ClashOfRimText.Key("ClashOfRim.Menu.UploadSnapshot"), () =>
                StartMenuUpload(mod, onSuccess: null));
            return;
        }

        if (Current.Game?.Info?.permadeathMode == true && !GameDataSaveLoader.SavingIsTemporarilyDisabled)
        {
            int saveAndQuitIndex = remoteActionInserted ? 3 : 2;
            ReplaceAt(optList, saveAndQuitIndex, ClashOfRimText.Key("ClashOfRim.Menu.UploadSnapshotAndQuitToMainMenu"), () =>
                StartMenuUpload(mod, GenScene.GoToMainMenu));
            ReplaceAt(optList, saveAndQuitIndex + 1, ClashOfRimText.Key("ClashOfRim.Menu.UploadSnapshotAndQuitToOS"), () =>
                StartMenuUpload(mod, Root.Shutdown));
        }
    }

    private static void InsertRemoteMapSessionOption(
        List<ListableOption> optList,
        ClashOfRimMod mod,
        ActiveRemoteMapSession session)
    {
        string label = RemoteMapSessionController.PrimaryActionLabel(session);
        if (optList.OfType<ClashOfRimMenuOption>().Any(option => option.Kind == ClashOfRimMenuOptionKind.RemoteMapAction))
        {
            return;
        }

        optList.Insert(0, new ClashOfRimMenuOption(
            ClashOfRimMenuOptionKind.RemoteMapAction,
            label,
            () => StartRemoteMapSessionMenuAction(mod, session)));
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

    private static void RemoveAtIfPresent(List<ListableOption> optList, int index)
    {
        if (index >= 0 && index < optList.Count)
        {
            optList.RemoveAt(index);
        }
    }

    private static void ReplaceAt(
        List<ListableOption> optList,
        int index,
        string replacementLabel,
        Action action)
    {
        if (index < 0 || index >= optList.Count)
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
