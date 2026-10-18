using System;

namespace AIRsLight.ClashOfRim.Modules.Maps;

// One close order for observations and battles. Engine objects and registration belong to the bound operation.
internal static class ClientRemoteMapClosure
{
    internal static bool Close(IClientRemoteMapCloseOperation operation, string reason, bool clearActiveSession)
    {
        using var admission = operation.TryEnter();
        if (admission is null) return false;
        bool Apply(Action action)
        {
            if (!operation.IsCurrent) return false;
            action();
            return true;
        }

        if (operation.HasMap)
        {
            if (!Apply(operation.EndHiddenTraps)
                || !Apply(() => operation.ClosePocketMaps(reason))
                || !Apply(operation.PrepareCurrentMap)
                || !Apply(() => operation.DiscardMapThings(reason))) return false;
            try { if (!Apply(operation.RemoveMap)) return false; }
            catch (Exception exception) when (operation.IsRaidBattle)
            {
                if (!Apply(() => operation.RemovalFailed(exception, reason))) return false;
                // A failed unload must not leave a live map with its parent/session already removed.
                if (operation.HasMap) return false;
            }
        }

        return Apply(() => operation.DiscardReferencePawns(reason))
            && Apply(operation.RemoveParent)
            && Apply(() => operation.Complete(clearActiveSession, reason));
    }
}

internal interface IClientRemoteMapCloseOperation
{
    IDisposable? TryEnter();
    bool IsCurrent { get; }
    bool HasMap { get; }
    bool IsRaidBattle { get; }
    void EndHiddenTraps();
    void ClosePocketMaps(string reason);
    void PrepareCurrentMap();
    void DiscardMapThings(string reason);
    void RemoveMap();
    void RemovalFailed(Exception exception, string reason);
    void DiscardReferencePawns(string reason);
    void RemoveParent();
    void Complete(bool clearActiveSession, string reason);
}
