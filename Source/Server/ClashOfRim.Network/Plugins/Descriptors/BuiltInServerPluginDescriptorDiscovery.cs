using System.Reflection;

namespace AIRsLight.ClashOfRim.Network.Plugins;

internal static class BuiltInServerPluginDescriptorDiscovery
{
    public static IReadOnlyList<ClashOfRimServerPluginDescriptor> Discover(Type markerType, string pluginNamespace)
    {
        if (markerType is null || string.IsNullOrWhiteSpace(pluginNamespace))
        {
            return Array.Empty<ClashOfRimServerPluginDescriptor>();
        }

        return TryGetTypes(markerType)
            .Where(type => type.Namespace == pluginNamespace)
            .Select(type => type.GetProperty(
                "Descriptor",
                BindingFlags.Public | BindingFlags.Static))
            .Where(property => property is not null
                && property.PropertyType == typeof(ClashOfRimServerPluginDescriptor))
            .Select(TryReadDescriptor)
            .Where(descriptor => descriptor is not null)
            .Select(descriptor => descriptor!)
            .OrderBy(descriptor => descriptor.Order)
            .ThenBy(descriptor => descriptor.Id)
            .ToList();
    }

    private static IReadOnlyList<Type> TryGetTypes(Type markerType)
    {
        try
        {
            return markerType.Assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            Console.Error.WriteLine(
                "[ClashOfRim][ServerPlugin][Error] BuiltInPluginTypeDiscoveryPartialFailure: "
                + markerType.Assembly.GetName().Name
                + " "
                + ex.Message);
            foreach (Exception loaderException in ex.LoaderExceptions.OfType<Exception>())
            {
                Console.Error.WriteLine(
                    "[ClashOfRim][ServerPlugin][Error] BuiltInPluginTypeDiscoveryPartialFailure.Detail: "
                    + loaderException.GetType().Name
                    + " "
                    + loaderException.Message);
            }

            return ex.Types.OfType<Type>().ToList();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                "[ClashOfRim][ServerPlugin][Error] BuiltInPluginTypeDiscoveryFailed: "
                + markerType.Assembly.GetName().Name
                + " "
                + ex.GetType().Name
                + " "
                + ex.Message);
            return Array.Empty<Type>();
        }
    }

    private static ClashOfRimServerPluginDescriptor? TryReadDescriptor(PropertyInfo? property)
    {
        if (property is null)
        {
            return null;
        }

        try
        {
            return (ClashOfRimServerPluginDescriptor?)property.GetValue(null);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                "[ClashOfRim][ServerPlugin][Error] BuiltInPluginDescriptorDiscoveryFailed: "
                + property.DeclaringType?.FullName
                + "."
                + property.Name
                + " "
                + ex.GetType().Name
                + " "
                + ex.Message);
            return null;
        }
    }
}
