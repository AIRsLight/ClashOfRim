using AIRsLight.ClashOfRim.Modules.Sessions;

namespace AIRsLight.ClashOfRim.ClientNetwork;

// Informational world status shared by engine adapters and the public API.
// Availability gates commands separately; a disconnected remote map stays multiplayer.
internal static class ClientMultiplayerWorldStatus
{
    internal static bool IsActive(ClientSessionLifecycle? session)
    {
        try
        {
            return session is not null && (session.HasSnapshotSession(session.ReadContext())
                || ClashOfRimGameComponent.HasActiveRemoteMapSession
                || ClashOfRimGameComponent.HasActiveRaidBattleSession);
        }
        catch { return false; }
    }
}
