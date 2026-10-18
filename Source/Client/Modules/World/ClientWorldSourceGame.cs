using AIRsLight.ClashOfRim.ClientNetwork;

namespace AIRsLight.ClashOfRim.Modules.World;

internal interface IClientWorldSourceGame
{
    object? Current { get; }
}

// This guard intentionally outlives multiplayer identity changes: the old scene
// can keep updating after logout while a different account is being prepared.
internal sealed class ClientWorldSourceGame
{
    private readonly IClientWorldSourceGame game;
    private object? blocked;
    internal ClientWorldSourceGame(IClientWorldSourceGame game) => this.game = game;
    internal void BlockCurrent() { using var p = ClientSessionWork.EnterPublication(); blocked = game.Current; }
    internal bool BlocksCurrent { get { using var p = ClientSessionWork.EnterPublication(); return blocked is not null && ReferenceEquals(blocked, game.Current); } }
    internal bool Initialized(object initialized)
    {
        using var p = ClientSessionWork.EnterPublication();
        if (!ReferenceEquals(initialized, game.Current) || ReferenceEquals(initialized, blocked)) return false;
        blocked = null;
        return true;
    }
    internal void Loaded() { using var p = ClientSessionWork.EnterPublication(); blocked = null; }
}
