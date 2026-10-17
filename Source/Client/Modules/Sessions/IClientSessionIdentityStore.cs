using AIRsLight.ClashOfRim.ClientNetwork;

namespace AIRsLight.ClashOfRim.Modules.Sessions;

internal interface IClientSessionIdentityStore
{
    ClashOfRimClientNetworkContext ReadContext();
    // Null names preserve the existing account/display name. The token is always replaced.
    void Write(string? userId, string? displayName, string authToken);
}
