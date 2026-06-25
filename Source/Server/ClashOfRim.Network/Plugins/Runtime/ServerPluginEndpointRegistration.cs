using Microsoft.AspNetCore.Builder;

namespace AIRsLight.ClashOfRim.Network.Plugins;

public sealed record ServerPluginEndpointRegistration(
    string Key,
    Action<WebApplication> MapEndpoints);
