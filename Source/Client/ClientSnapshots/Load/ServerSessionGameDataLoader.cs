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
    private static string suppressedPlayUiRootOwner = string.Empty;

    public static void LoadGame(byte[] saveBytes, string saveName, Action? onLoaded = null)
        => LoadOwned(saveBytes, saveName, action => { action(); return true; }, null, null, onLoaded, null, null);

    internal static PendingServerSessionSave LoadOwned(byte[] saveBytes, string saveName, Func<Action, bool> apply,
        Action<Game?>? bindGame, Action? mutationStarted, Action? onLoaded, Action<Exception>? onFailed, Action? onDiscarded)
    {
        byte[] payload = saveBytes ?? throw new ArgumentNullException(nameof(saveBytes));
        string displayName = string.IsNullOrWhiteSpace(saveName) ? "server-buffer" : saveName.Trim();
        string virtualSaveName = VirtualSavePrefix + Guid.NewGuid().ToString("N");
        var pending = new PendingServerSessionSave(virtualSaveName, payload, displayName, apply, bindGame, mutationStarted, onLoaded, onFailed, onDiscarded);
        lock (PendingLoadsLock) PendingLoads.Add(virtualSaveName, pending);
        SuppressPlayUiRootOnGui(displayName, virtualSaveName);
        try
        {
            LongEventHandler.QueueLongEvent(pending.PrepareScene, "Play", "LoadingLongEvent", false, null, true, false);
            return pending;
        }
        catch { pending.Dispose(); throw; }
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

    internal static bool TryLoadPending(string? virtualSaveName)
    {
        if (virtualSaveName is null || !virtualSaveName.StartsWith(VirtualSavePrefix, StringComparison.Ordinal)) return false;
        PendingServerSessionSave? pending;
        lock (PendingLoadsLock) PendingLoads.Remove(virtualSaveName, out pending);
        // A cancelled virtual load must never fall through to filesystem loading.
        if (pending is not null) pending.Load();
        return true;
    }

    internal static bool ShouldSuppressPlayUiRootOnGui => Volatile.Read(ref suppressPlayUiRootOnGui) != 0;

    internal static string SuppressedPlayUiRootLoad => suppressedPlayUiRootLoad;

    private static void ClearPlayUiRootOnGuiSuppression(string owner, string reason)
    {
        lock (PendingLoadsLock)
        {
            if (suppressedPlayUiRootOwner != owner) return;
            if (Interlocked.Exchange(ref suppressPlayUiRootOnGui, 0) != 0)
            {
                ClashLog.Message("[ClashOfRim] Released Play UI root during server snapshot memory load: "
                    + suppressedPlayUiRootLoad
                    + ", reason="
                    + reason);
            }

            suppressedPlayUiRootLoad = string.Empty;
            suppressedPlayUiRootOwner = string.Empty;
        }
    }

    private static void SuppressPlayUiRootOnGui(string displayName, string owner)
    {
        lock (PendingLoadsLock)
        {
            suppressedPlayUiRootOwner = owner;
            suppressedPlayUiRootLoad = displayName;
            Interlocked.Exchange(ref suppressPlayUiRootOnGui, 1);
        }
    }

    internal static void LoadGameFromServerSaveNow(byte[] saveBytes, string saveName, Action<Game?>? bindGame = null,
        Func<Action, bool>? apply = null)
    {
        void Publish(Action action)
        {
            if (apply is null) { action(); return; }
            if (!apply(action)) throw new OperationCanceledException("The server save load owner was replaced.");
        }
        string mods = LoadedModManager.RunningMods
            .Select(mod => mod.PackageIdPlayerFacing + (!mod.ModMetaData.VersionCompatible ? " (incompatible version)" : string.Empty))
            .ToLineList("  - ", false);
        ClashLog.Message("Loading ClashOfRim server game from memory buffer " + saveName + " with mods:\n" + mods);
        DeepProfiler.Start("Loading ClashOfRim server game from memory buffer " + saveName);
        Publish(() => AssignGame(new Game(), bindGame));
        DeepProfiler.Start("InitLoading (read memory buffer)");
        InitLoadingFromBytes(saveBytes, saveName);
        DeepProfiler.End();
        try
        {
            ScribeMetaHeaderUtility.LoadGameDataHeader(ScribeMetaHeaderUtility.ScribeHeaderMode.Map, true);
            if (!Scribe.EnterNode("game"))
            {
                throw new InvalidDataException("Could not find game XML node.");
            }

            Game loading = null!;
            Publish(() => { loading = new Game(); AssignGame(loading, bindGame); });
            // Scribe finalization waits for parallel tile validation, which reads
            // session-owned reservations. Keep only Game publication under the gate.
            loading.LoadGame();
        }
        catch (Exception)
        {
            Scribe.ForceStop();
            throw;
        }

        Publish(() => PermadeathModeUtility.CheckUpdatePermadeathModeUniqueNameOnGameLoad(saveName));
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

    private static void AssignGame(Game game, Action<Game?>? bindGame)
    {
        Current.Game = game;
        bindGame?.Invoke(game);
    }

    internal sealed class PendingServerSessionSave : IDisposable
    {
        private readonly byte[] bytes;
        private readonly string name;
        private readonly Func<Action, bool> apply;
        private readonly Action<Game?>? bind;
        private readonly Action? mutationStarted, loaded, discarded;
        private readonly Action<Exception>? failed;
        private int finished;
        internal string VirtualName { get; }
        internal PendingServerSessionSave(string id, byte[] bytes, string name, Func<Action, bool> apply,
            Action<Game?>? bind, Action? mutationStarted, Action? loaded, Action<Exception>? failed, Action? discarded)
        { VirtualName = id; this.bytes = bytes; this.name = name; this.apply = apply; this.bind = bind;
            this.mutationStarted = mutationStarted; this.loaded = loaded; this.failed = failed; this.discarded = discarded; }
        private bool Run(Action action)
        {
            if (Volatile.Read(ref finished) != 0) return false;
            if (apply(action)) return true;
            Dispose(); return false;
        }
        internal void PrepareScene()
        {
            try
            {
                Run(() =>
                {
                    mutationStarted?.Invoke();
                    PrepareForServerMemoryLoad();
                    bind?.Invoke(Current.Game);
                    AssignGame(new Game { InitData = new GameInitData { gameToLoad = VirtualName } }, bind);
                });
            }
            catch (Exception ex) { Fail(ex); }
        }
        internal void Load()
        {
            try
            {
                if (!Run(static () => { })) return;
                LoadGameFromServerSaveNow(bytes, name, bind, Run);
                if (!Run(static () => { })) return;
                ClearPlayUiRootOnGuiSuppression(VirtualName, "server save loaded");
                LongEventHandler.ExecuteWhenFinished(() =>
                {
                    try { Run(() => { Release(); loaded?.Invoke(); Interlocked.Exchange(ref finished, 1); }); }
                    catch (Exception ex) { Fail(ex); }
                });
            }
            catch (Exception ex) { Fail(ex); }
        }
        private void Fail(Exception ex)
        {
            if (Interlocked.Exchange(ref finished, 1) != 0) return;
            Release();
            if (failed is not null) failed(ex);
            else System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex).Throw();
        }
        private void Release()
        {
            lock (PendingLoadsLock) PendingLoads.Remove(VirtualName);
            ClearPlayUiRootOnGuiSuppression(VirtualName, "load finished or discarded");
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref finished, 1) != 0) return;
            Release(); discarded?.Invoke();
        }
    }
}

[HarmonyPatch(typeof(SavedGameLoaderNow), nameof(SavedGameLoaderNow.LoadGameFromSaveFileNow))]
internal static class ServerSessionSavedGameLoaderPatch
{
    [HarmonyPrefix]
    public static bool Prefix(string fileName) => !ServerSessionGameDataLoader.TryLoadPending(fileName);

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
