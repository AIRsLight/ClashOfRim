using Microsoft.AspNetCore.Http;

namespace AIRsLight.ClashOfRim.Network;

public static partial class ClashOfRimNetworkServer
{
    private static IResult ListServerPlugins(ClashOfRimNetworkState state)
    {
        return Results.Ok(new
        {
            plugins = state.Plugins.Plugins.Select(plugin => new
            {
                plugin.Id,
                plugin.Name,
                plugin.Version,
                plugin.AssemblyName,
                plugin.FileName,
                plugin.Capabilities
            })
        });
    }
}
