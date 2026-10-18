using System;
using AIRsLight.ClashOfRim.Modules.Sessions;

namespace AIRsLight.ClashOfRim.Modules.Snapshots;

// Game domains add the loaded Game to the common session request boundary.
internal sealed class ClientGameRequestTracker : ClientRequestTracker<IClientSnapshotGameSession>
{
    internal ClientGameRequestTracker(ClientSessionLifecycle session, IClientSnapshotGame game, Action reset)
        : base(session, game.BindCurrentGame, static bound => bound.IsCurrent, reset) { }
}
