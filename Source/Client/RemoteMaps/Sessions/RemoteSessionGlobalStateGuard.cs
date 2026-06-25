using RimWorld.Planet;
using System;
using System.Collections.Generic;
using Verse;

namespace AIRsLight.ClashOfRim.RemoteMaps;

public static class RemoteSessionGlobalStateGuard
{
    private static readonly List<Action> removalGlobalEffectsSuppressedHandlers = new();

    [ThreadStatic]
    private static int suppressRemoteMapRemovalGlobalEffectsDepth;

    public static bool SuppressRemoteMapRemovalGlobalEffects => suppressRemoteMapRemovalGlobalEffectsDepth > 0;

    public static IDisposable BeginSuppressRemoteMapRemovalGlobalEffects()
    {
        suppressRemoteMapRemovalGlobalEffectsDepth++;
        return new SuppressionScope();
    }

    public static void RegisterRemoteMapRemovalGlobalEffectsSuppressedHandler(Action handler)
    {
        if (handler is null)
        {
            return;
        }

        lock (removalGlobalEffectsSuppressedHandlers)
        {
            if (!removalGlobalEffectsSuppressedHandlers.Contains(handler))
            {
                removalGlobalEffectsSuppressedHandlers.Add(handler);
            }
        }
    }

    public static bool IsRemoteMap(Map? map)
    {
        return map?.Parent is RemoteSessionMapParent;
    }

    public static bool IsRemoteThing(Thing? thing)
    {
        return IsRemoteMap(thing?.MapHeld);
    }

    public static bool IsRemotePawn(Pawn? pawn)
    {
        return IsRemoteThing(pawn);
    }

    public static bool IsRemoteMapParent(MapParent? mapParent)
    {
        return mapParent is RemoteSessionMapParent;
    }

    private sealed class SuppressionScope : IDisposable
    {
        private bool disposed;

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            if (suppressRemoteMapRemovalGlobalEffectsDepth > 0)
            {
                suppressRemoteMapRemovalGlobalEffectsDepth--;
                if (suppressRemoteMapRemovalGlobalEffectsDepth == 0)
                {
                    NotifyRemoteMapRemovalGlobalEffectsSuppressed();
                }
            }
        }
    }

    private static void NotifyRemoteMapRemovalGlobalEffectsSuppressed()
    {
        Action[] handlers;
        lock (removalGlobalEffectsSuppressedHandlers)
        {
            handlers = removalGlobalEffectsSuppressedHandlers.ToArray();
        }

        foreach (Action handler in handlers)
        {
            try
            {
                handler();
            }
            catch (Exception ex)
            {
                Log.Warning("[ClashOfRim][RemoteSession] Remote map removal suppression handler failed: " + ex);
            }
        }
    }
}
