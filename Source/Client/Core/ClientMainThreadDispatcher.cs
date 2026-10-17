using System;
using AIRsLight.ClashOfRim.MainMenu;
using Verse;

namespace AIRsLight.ClashOfRim;

// UI work must remain reachable both before entry and while a game is running.
internal static class ClientMainThreadDispatcher
{
    internal static void Enqueue(Action action, Action? onDiscard = null)
    {
        if (action is null) return;
        if (Current.ProgramState == ProgramState.Playing)
            ClashOfRimGameComponent.EnqueueMainThreadAction(action, onDiscard);
        else
            ClashOfRimMainMenuPatches.EnqueueMainThreadAction(action, onDiscard);
    }
}
