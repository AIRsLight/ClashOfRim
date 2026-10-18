using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim;
using AIRsLight.ClashOfRim.ClientNetwork;
using Verse;

internal static class MainThreadDispatchTests
{
    private static int inGameCompletion;
    private static readonly MethodInfo PumpGame = typeof(ClashOfRimGameComponent).GetMethod("RunMainThreadActions", BindingFlags.Static | BindingFlags.NonPublic)!;
    private static readonly MethodInfo PumpEntry = typeof(ClashOfRimMod).Assembly.GetType("AIRsLight.ClashOfRim.MainMenu.ClashOfRimMainMenuPatches", true)!
        .GetMethod("RunQueuedMainThreadActions", BindingFlags.Static | BindingFlags.NonPublic)!;

    // Isolated RimTest probe: leave an entry callback pending after the entry UI has gone away.
    // Only the real game's update pump may complete it; the test never invokes either pump.
    public static object QueueEntryContinuationInGame()
    {
        if (Current.ProgramState != ProgramState.Playing || Current.Game is null)
            throw new InvalidOperationException("The entry handoff probe requires an active game.");
        Volatile.Write(ref inGameCompletion, 0);
        PumpEntry.DeclaringType!.GetMethod("EnqueueMainThreadAction", BindingFlags.Static | BindingFlags.Public)!
            .Invoke(null, new object[] { (Action)(() => Volatile.Write(ref inGameCompletion, 1)),
                (Action)(() => Volatile.Write(ref inGameCompletion, -1)) });
        return new { queued = true };
    }

    public static object ReadEntryContinuationInGame() => new { completed = Volatile.Read(ref inGameCompletion) == 1,
        discarded = Volatile.Read(ref inGameCompletion) == -1 };

    public static void Run()
    {
        var originalGame = Current.Game;
        var originalState = Current.ProgramState;
        try
        {
            ClientSessionWork.Reset();
            var gameA = (Game)RuntimeHelpers.GetUninitializedObject(typeof(Game));
            var gameB = (Game)RuntimeHelpers.GetUninitializedObject(typeof(Game));
            Current.Game = gameA;
            Current.ProgramState = ProgramState.Entry;
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int ran = 0;
            Enqueue(() => { ran++; completion.TrySetResult(); }, () => completion.TrySetCanceled());
            // Entering the game removes the entry update pump before its queued callback runs.
            Current.ProgramState = ProgramState.Playing;
            PumpGame.Invoke(null, null);
            Require(completion.Task.IsCompletedSuccessfully && ran == 1, "Entry continuation was stranded after entering the game");
            PumpEntry.Invoke(null, null);
            PumpGame.Invoke(null, null);
            Require(ran == 1, "Entry continuation ran more than once");

            Current.ProgramState = ProgramState.Entry;
            var discarded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Enqueue(() => ran++, () => discarded.TrySetCanceled());
            ClientSessionWork.Reset();
            Current.Game = gameB;
            Current.ProgramState = ProgramState.Playing;
            PumpGame.Invoke(null, null);
            Require(discarded.Task.IsCanceled && ran == 1, "Previous session entry callback reached the new game or stranded its waiter");

            int gameDiscarded = 0;
            Enqueue(() => ran++, () => gameDiscarded++);
            Current.Game = gameA;
            PumpGame.Invoke(null, null);
            Require(gameDiscarded == 1 && ran == 1, "Game-bound callback escaped its owning game");

            // A surviving entry callback may enqueue game work; both are consumed exactly once.
            Current.ProgramState = ProgramState.Entry;
            Enqueue(() => { ran++; Enqueue(() => ran++); });
            Current.ProgramState = ProgramState.Playing;
            PumpGame.Invoke(null, null);
            Require(ran == 3, "Nested entry-to-game work did not complete");
            PumpGame.Invoke(null, null);
            Require(ran == 3, "Nested entry-to-game work repeated");
        }
        finally
        {
            ClientSessionWork.Reset();
            PumpEntry.Invoke(null, null);
            PumpGame.Invoke(null, null);
            Current.Game = originalGame;
            Current.ProgramState = originalState;
        }
    }

    private static void Enqueue(Action action, Action? discard = null) => ClientMainThreadDispatcher.Enqueue(action, discard);
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
}
