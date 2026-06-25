using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Xml;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.Profile;

namespace AIRsLight.ClashOfRim.ClientSnapshots;

public static class ServerSessionGameDataLoader
{
    private const string VirtualSavePrefix = "ClashOfRim_server_memory_";
    private static readonly object PendingLoadsLock = new();
    private static readonly Dictionary<string, PendingServerSessionSave> PendingLoads = new(StringComparer.Ordinal);
    private static int suppressPlayUiRootOnGui;
    private static string suppressedPlayUiRootLoad = string.Empty;

    public static void LoadGame(byte[] saveBytes, string saveName, Action? onLoaded = null)
    {
        byte[] payload = saveBytes ?? throw new ArgumentNullException(nameof(saveBytes));
        string displayName = string.IsNullOrWhiteSpace(saveName) ? "server-buffer" : saveName.Trim();
        string virtualSaveName = VirtualSavePrefix + Guid.NewGuid().ToString("N");
        RegisterPendingLoad(virtualSaveName, payload, displayName, onLoaded);
        SuppressPlayUiRootOnGui(displayName);

        ClashLog.Message("[ClashOfRim] Prepared server snapshot memory load: snapshot=" + displayName + ", virtualSave=" + virtualSaveName);
        LongEventHandler.QueueLongEvent(
            () =>
            {
                ClashLog.Message("[ClashOfRim] Entering Play scene for server snapshot memory load: " + displayName);
                PrepareForServerMemoryLoad();
                Current.Game = new Game
                {
                    InitData = new GameInitData
                    {
                        gameToLoad = virtualSaveName
                    }
                };
            },
            "Play",
            "LoadingLongEvent",
            false,
            null,
            true,
            false);
    }

    private static void PrepareForServerMemoryLoad()
    {
        if (Scribe.mode != LoadSaveMode.Inactive
            || Scribe.loader.curParent is not null
            || Scribe.loader.curPathRelToParent is not null)
        {
            Log.Warning("[ClashOfRim] Clearing active Scribe state before server snapshot memory load.");
        }

        Scribe.loader.ForceStop();

        Game? previousGame = Current.Game;
        if (previousGame is not null)
        {
            try
            {
                previousGame.Dispose();
            }
            catch (Exception ex)
            {
                Log.Warning("[ClashOfRim] Failed to dispose previous game before server snapshot memory load: " + ex);
            }
        }

        try
        {
            MemoryUtility.ClearAllMapsAndWorld();
        }
        catch (Exception ex)
        {
            Log.Warning("[ClashOfRim] Failed to clear previous world before server snapshot memory load: " + ex);
        }

        Game.ClearCaches();
        Current.Game = null;
    }

    internal static bool TryConsumePendingLoad(
        string? virtualSaveName,
        out byte[] saveBytes,
        out string displayName,
        out Action? onLoaded)
    {
        saveBytes = Array.Empty<byte>();
        displayName = string.Empty;
        onLoaded = null;
        if (virtualSaveName is null
            || string.IsNullOrWhiteSpace(virtualSaveName)
            || !virtualSaveName.StartsWith(VirtualSavePrefix, StringComparison.Ordinal))
        {
            return false;
        }

        lock (PendingLoadsLock)
        {
            if (!PendingLoads.TryGetValue(virtualSaveName, out PendingServerSessionSave? pending))
            {
                Log.Error("[ClashOfRim] Missing pending server snapshot memory load for virtual save: " + virtualSaveName);
                ClearPlayUiRootOnGuiSuppression("missing pending load");
                return false;
            }

            PendingLoads.Remove(virtualSaveName);
            saveBytes = pending.SaveBytes;
            displayName = pending.DisplayName;
            onLoaded = pending.OnLoaded;
            return true;
        }
    }

    internal static bool ShouldSuppressPlayUiRootOnGui => Volatile.Read(ref suppressPlayUiRootOnGui) != 0;

    internal static string SuppressedPlayUiRootLoad => suppressedPlayUiRootLoad;

    internal static void ClearPlayUiRootOnGuiSuppression(string reason)
    {
        if (Interlocked.Exchange(ref suppressPlayUiRootOnGui, 0) != 0)
        {
            ClashLog.Message("[ClashOfRim] Released Play UI root during server snapshot memory load: "
                + suppressedPlayUiRootLoad
                + ", reason="
                + reason);
        }

        suppressedPlayUiRootLoad = string.Empty;
    }

    private static void SuppressPlayUiRootOnGui(string displayName)
    {
        suppressedPlayUiRootLoad = displayName;
        Interlocked.Exchange(ref suppressPlayUiRootOnGui, 1);
    }

    internal static void LoadGameFromServerSaveNow(byte[] saveBytes, string saveName)
    {
        string mods = LoadedModManager.RunningMods
            .Select(mod => mod.PackageIdPlayerFacing + (!mod.ModMetaData.VersionCompatible ? " (incompatible version)" : string.Empty))
            .ToLineList("  - ", false);
        ClashLog.Message("Loading ClashOfRim server game from memory buffer " + saveName + " with mods:\n" + mods);
        DeepProfiler.Start("Loading ClashOfRim server game from memory buffer " + saveName);
        Current.Game = new Game();
        DeepProfiler.Start("InitLoading (read memory buffer)");
        InitLoadingFromBytes(saveBytes, saveName);
        DeepProfiler.End();
        try
        {
            ScribeMetaHeaderUtility.LoadGameDataHeader(ScribeMetaHeaderUtility.ScribeHeaderMode.Map, true);
            if (!Scribe.EnterNode("game"))
            {
                Log.Error("Could not find game XML node.");
                Scribe.ForceStop();
                return;
            }

            Current.Game = new Game();
            Current.Game.LoadGame();
        }
        catch (Exception)
        {
            Scribe.ForceStop();
            throw;
        }

        PermadeathModeUtility.CheckUpdatePermadeathModeUniqueNameOnGameLoad(saveName);
        DeepProfiler.End();
    }

    private static void InitLoadingFromBytes(byte[] saveBytes, string sourceName)
    {
        if (Scribe.mode != LoadSaveMode.Inactive)
        {
            Log.Error("Called ClashOfRim memory InitLoading() but current mode is " + Scribe.mode);
            Scribe.ForceStop();
        }

        if (Scribe.loader.curParent is not null)
        {
            Log.Error("Current parent is not null in ClashOfRim memory InitLoading");
            Scribe.loader.curParent = null;
        }

        if (Scribe.loader.curPathRelToParent is not null)
        {
            Log.Error("Current path relative to parent is not null in ClashOfRim memory InitLoading");
            Scribe.loader.curPathRelToParent = null;
        }

        try
        {
            var document = new XmlDocument();
            using var source = new MemoryStream(saveBytes);
            using XmlReader reader = XmlReader.Create(source, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit
            });
            document.Load(reader);
            Scribe.loader.curXmlParent = document.DocumentElement;
            Scribe.mode = LoadSaveMode.LoadingVars;
        }
        catch (Exception ex)
        {
            Log.Error("Exception while init loading ClashOfRim server save buffer: " + sourceName + "\n" + ex);
            Scribe.loader.ForceStop();
            throw;
        }
    }

    private static void RegisterPendingLoad(string virtualSaveName, byte[] saveBytes, string displayName, Action? onLoaded)
    {
        lock (PendingLoadsLock)
        {
            PendingLoads[virtualSaveName] = new PendingServerSessionSave(saveBytes, displayName, onLoaded);
        }
    }

    private sealed class PendingServerSessionSave
    {
        public PendingServerSessionSave(byte[] saveBytes, string displayName, Action? onLoaded)
        {
            SaveBytes = saveBytes;
            DisplayName = displayName;
            OnLoaded = onLoaded;
        }

        public byte[] SaveBytes { get; }
        public string DisplayName { get; }
        public Action? OnLoaded { get; }
    }
}

[HarmonyPatch(typeof(SavedGameLoaderNow), nameof(SavedGameLoaderNow.LoadGameFromSaveFileNow))]
internal static class ServerSessionSavedGameLoaderPatch
{
    [HarmonyPrefix]
    public static bool Prefix(string fileName)
    {
        if (!ServerSessionGameDataLoader.TryConsumePendingLoad(
                fileName,
                out byte[] saveBytes,
                out string displayName,
                out Action? onLoaded))
        {
            return true;
        }

        ClashLog.Message("[ClashOfRim] Loading server snapshot through vanilla saved-game path: " + displayName);
        try
        {
            ServerSessionGameDataLoader.LoadGameFromServerSaveNow(saveBytes, displayName);
            if (onLoaded is not null)
            {
                LongEventHandler.ExecuteWhenFinished(onLoaded);
            }
        }
        finally
        {
            ServerSessionGameDataLoader.ClearPlayUiRootOnGuiSuppression("server save loaded");
        }

        return false;
    }
}

[HarmonyPatch(typeof(UIRoot_Play), nameof(UIRoot_Play.UIRootOnGUI))]
internal static class ServerSessionPlayUiRootPatch
{
    [HarmonyPrefix]
    public static bool Prefix()
    {
        return !ServerSessionGameDataLoader.ShouldSuppressPlayUiRootOnGui;
    }
}
