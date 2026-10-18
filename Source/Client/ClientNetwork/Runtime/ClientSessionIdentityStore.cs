using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.World;

namespace AIRsLight.ClashOfRim.ClientNetwork;

internal sealed class ClientSessionIdentityStore : IClientSessionIdentityStore, IClientSessionExitStore
{
    private readonly ClashOfRimSettings settings;
    internal ClientSessionIdentityStore(ClashOfRimSettings settings) => this.settings = settings;
    public ClashOfRimClientNetworkContext ReadContext() => ClashOfRimClientNetworkContext.FromSettings(settings);
    public void ClearWorld() => settings.CurrentWorldConfigurationId = string.Empty;
    public void ClearSnapshot()
    {
        settings.CurrentSnapshotId = string.Empty;
        settings.CurrentLineageToken = string.Empty;
    }

    public void Write(string? userId, string? displayName, string authToken)
    {
        if (userId is not null) settings.UserId = userId;
        if (displayName is not null) settings.DisplayName = displayName;
        settings.AuthToken = authToken;
        settings.Write();
    }
}
