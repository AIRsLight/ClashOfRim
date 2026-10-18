using System;
using AIRsLight.ClashOfRim.MainMenu;
using Verse;

namespace AIRsLight.ClashOfRim;

// UI work must remain reachable both before entry and while a game is running.
internal static class ClientMainThreadDispatcher
{
    // This shared queue is drained by entry UI and Game updates. Account effects
    // belong to the authenticated session and must survive loading another Game.
    internal static void EnqueueSession(Action action, Action onDiscard)
        => ClashOfRimMainMenuPatches.EnqueueMainThreadAction(action, onDiscard);

    internal static void Enqueue(Action action, Action? onDiscard = null)
    {
        if (action is null) return;
        if (Current.ProgramState == ProgramState.Playing)
            ClashOfRimGameComponent.EnqueueMainThreadAction(action, onDiscard);
        else
            ClashOfRimMainMenuPatches.EnqueueMainThreadAction(action, onDiscard);
    }
}
