namespace AIRsLight.ClashOfRim.Network.Modules.Admin;

// Owns the runtime value; callers receive an immutable configuration snapshot.
internal sealed class ServerConfigurationState(ClashOfRimServerConfiguration initial)
{
    private ClashOfRimServerConfiguration current = initial;
    internal ClashOfRimServerConfiguration Current => Volatile.Read(ref current);
    internal ClashOfRimServerConfiguration Replace(ClashOfRimServerConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        Volatile.Write(ref current, configuration);
        return configuration;
    }
}
