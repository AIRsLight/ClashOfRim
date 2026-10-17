using AIRsLight.ClashOfRim.Modules.Sessions;

namespace AIRsLight.ClashOfRim.ClientNetwork;

internal sealed class ClientSessionIdentityStore : IClientSessionIdentityStore
{
    private readonly ClashOfRimSettings settings;
    internal ClientSessionIdentityStore(ClashOfRimSettings settings) => this.settings = settings;
    public ClashOfRimClientNetworkContext ReadContext() => ClashOfRimClientNetworkContext.FromSettings(settings);

    public void Write(string? userId, string? displayName, string authToken)
    {
        if (userId is not null) settings.UserId = userId;
        if (displayName is not null) settings.DisplayName = displayName;
        settings.AuthToken = authToken;
        settings.Write();
    }
}
