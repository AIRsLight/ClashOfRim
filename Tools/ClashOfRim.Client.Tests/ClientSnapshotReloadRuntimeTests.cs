using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.ClientSnapshots;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using HarmonyLib;
using Verse;

internal static class ClientSnapshotReloadRuntimeTests
{
    private static Game? game;
    private static readonly Queue<Action> scenes = new(), completions = new();
    private static int preparations, loads;
    private static bool failLoad, parallelLoad, parallelReadCompleted;
    private static readonly ClientRestoredSnapshot Snapshot = new("snapshot", "", "server-save", new byte[] { 1, 2, 3 }, null);
    internal static void Run() => RunAsync().GetAwaiter().GetResult();
    private static async Task RunAsync()
    {
        var harmony = new Harmony("ClashOfRim.Tests.SnapshotReloadRuntime");
        try
        {
            Patch(harmony, AccessTools.PropertyGetter(typeof(Current), nameof(Current.Game)), nameof(GetGame));
            Patch(harmony, AccessTools.PropertySetter(typeof(Current), nameof(Current.Game)), nameof(SetGame));
            Patch(harmony, AccessTools.Constructor(typeof(Game), Type.EmptyTypes), nameof(Skip));
            Patch(harmony, AccessTools.Method(typeof(ClashLog), nameof(ClashLog.Message)), nameof(Skip));
            Patch(harmony, AccessTools.Method(typeof(LongEventHandler), "QueueLongEvent", new[] { typeof(Action), typeof(string), typeof(string), typeof(bool), typeof(Action<Exception>), typeof(bool), typeof(bool) }), nameof(QueueScene));
            Patch(harmony, AccessTools.Method(typeof(LongEventHandler), nameof(LongEventHandler.ExecuteWhenFinished)), nameof(QueueCompletion));
            Patch(harmony, AccessTools.Method(typeof(ServerSessionGameDataLoader), "PrepareForServerMemoryLoad"), nameof(Prepare));
            Patch(harmony, AccessTools.Method(typeof(ServerSessionGameDataLoader), "LoadGameFromServerSaveNow"), nameof(Load));
            foreach (string scenario in new[] { "success", "parallel-load", "main-menu", "source-replaced", "scene-replaced", "completion-replaced", "permission", "cancel", "failure" })
            {
                Reset(); if (scenario == "main-menu") game = null; using var cancel = new CancellationTokenSource();
                using var operation = new ClientSnapshotReloadRuntime().Bind(); bool permission = true;
                bool Apply(Action action)
                {
                    bool applied = false;
                    ClientSessionWork.TryApply(() =>
                    { if (permission && operation.IsCurrent && !cancel.IsCancellationRequested) { action(); applied = true; } });
                    return applied;
                }
                Task<bool> task = operation.LoadAsync(Snapshot, Apply, cancel.Token);
                Check(!task.IsCompleted && ServerSessionGameDataLoader.ShouldSuppressPlayUiRootOnGui, "Load did not retain its pending transition.");
                var initial = game;
                if (scenario == "source-replaced") game = NewGame();
                if (scenario == "permission") permission = false;
                if (scenario == "cancel") { cancel.Cancel(); try { await task; throw new Exception("Cancellation completed a load."); } catch (OperationCanceledException) { } }
                scenes.Dequeue()();
                if (scenario is "source-replaced" or "permission" or "cancel")
                {
                    if (scenario != "cancel") Check(!await task, "Rejected queued transition loaded a game.");
                    Check(preparations == 0 && loads == 0 && !operation.MutationStarted && !ServerSessionGameDataLoader.ShouldSuppressPlayUiRootOnGui,
                        "Obsolete queued load mutated engine state.");
                    continue;
                }
                string id = game!.InitData.gameToLoad;
                Check(!ReferenceEquals(initial, game) && operation.IsCurrent && operation.MutationStarted, "Scene Game was not adopted by the real runtime.");
                if (scenario == "scene-replaced") game = NewGame();
                failLoad = scenario == "failure";
                parallelLoad = scenario == "parallel-load";
                Check(!ServerSessionSavedGameLoaderPatch.Prefix(id), "Virtual load fell through to the filesystem.");
                Check(!parallelLoad || parallelReadCompleted, "Native save loading holds the publication gate while waiting for tile readers.");
                if (scenario == "scene-replaced") Check(!await task && loads == 0, "Replaced scene Game was overwritten.");
                else if (scenario == "failure")
                {
                    try { await task; throw new Exception("Deserialization failure was hidden."); } catch (IOException) { }
                    Check(operation.IsCurrent && operation.MutationStarted, "Failure lost ownership of the partially loaded game.");
                }
                else
                {
                    Check(loads == 1 && !task.IsCompleted && operation.IsCurrent, "Completion was reported before long events finished.");
                    if (scenario == "completion-replaced") game = NewGame();
                    completions.Dequeue()();
                    Check(await task == (scenario is "success" or "parallel-load" or "main-menu"), "Late completion adopted the wrong game.");
                    if (scenario is "success" or "parallel-load" or "main-menu")
                    {
                        var component = new ClashOfRimGameComponent(game!);
                        AccessTools.Field(typeof(Game), "components").SetValue(game, new List<GameComponent> { component });
                        ClashOfRimGameComponent.SetSnapshotLineage("old", "old-token");
                        var store = new EventModuleTestPorts();
                        new ClientSnapshotPublication(store).PublishRestoredGame(operation.Game, "snapshot", "");
                        var lineage = ClashOfRimGameComponent.CopySnapshotLineage();
                        Check(lineage.SnapshotId == "snapshot" && lineage.Token == "", "Restored empty lineage retained the prior game's token.");
                    }
                }
                Check(!ServerSessionGameDataLoader.ShouldSuppressPlayUiRootOnGui && !ServerSessionSavedGameLoaderPatch.Prefix(id), "Finished virtual load leaked UI suppression or fell through on duplicate dispatch.");
            }
            Reset();
            int oldDiscards = 0, newDiscards = 0;
            using var old = ServerSessionGameDataLoader.LoadOwned(Snapshot.SaveBytes, "old", _ => false, null, null, null, null, () => oldDiscards++);
            using var next = ServerSessionGameDataLoader.LoadOwned(Snapshot.SaveBytes, "new", _ => false, null, null, null, null, () => newDiscards++);
            old.Dispose(); old.Dispose();
            Check(oldDiscards == 1 && ServerSessionGameDataLoader.ShouldSuppressPlayUiRootOnGui && ServerSessionGameDataLoader.SuppressedPlayUiRootLoad == "new",
                "Old load disposal cleared the new load's UI suppression.");
            next.Dispose(); Check(newDiscards == 1 && !ServerSessionGameDataLoader.ShouldSuppressPlayUiRootOnGui, "Load disposal was not idempotent.");
            Check(ServerSessionSavedGameLoaderPatch.Prefix("ordinary-save"), "Ordinary saves were intercepted.");
        }
        finally { harmony.UnpatchAll(harmony.Id); scenes.Clear(); completions.Clear(); game = null; ClientSessionWork.Reset(); }
    }
    private static void Reset() { scenes.Clear(); completions.Clear(); game = NewGame(); preparations = loads = 0; failLoad = parallelLoad = parallelReadCompleted = false; }
    private static Game NewGame() => (Game)RuntimeHelpers.GetUninitializedObject(typeof(Game));
    private static bool GetGame(ref Game? __result) { __result = game; return false; }
    private static bool SetGame(Game? value) { game = value; return false; }
    private static bool Skip() => false;
    private static bool QueueScene(Action __0) { scenes.Enqueue(__0); return false; }
    private static bool QueueCompletion(Action __0) { completions.Enqueue(__0); return false; }
    private static bool Prepare() { preparations++; game = null; return false; }
    private static bool Load(byte[] saveBytes, string saveName, Action<Game?>? bindGame)
    {
        Check(saveName == Snapshot.SaveName && saveBytes.SequenceEqual(Snapshot.SaveBytes), "Loader changed prepared bytes/name.");
        loads++; game = NewGame(); bindGame?.Invoke(game);
        if (parallelLoad)
        {
            var reader = Task.Factory.StartNew(() =>
            { using var publication = ClientSessionWork.EnterPublication(); },
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            parallelReadCompleted = reader.Wait(TimeSpan.FromSeconds(2));
            Check(parallelReadCompleted, "Native save loading holds the publication gate while waiting for tile readers.");
        }
        if (failLoad) throw new IOException("deserialize");
        return false;
    }
    private static void Patch(Harmony harmony, MethodBase method, string prefix) => harmony.Patch(method, prefix: new HarmonyMethod(typeof(ClientSnapshotReloadRuntimeTests), prefix));
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
