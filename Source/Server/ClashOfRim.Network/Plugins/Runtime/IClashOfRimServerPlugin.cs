namespace AIRsLight.ClashOfRim.Network.Plugins;

public interface IClashOfRimServerPlugin
{
    ClashOfRimServerPluginDescriptor Describe();

    void Configure(ClashOfRimServerPluginContext context);
}
