using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace AIRsLight.ClashOfRim.ThirdPartyCompatibility;

internal sealed class ClientCompatibilityPackageApplyContext
{
    public ClientCompatibilityPackageApplyContext(Harmony harmony)
    {
        Harmony = harmony ?? throw new ArgumentNullException(nameof(harmony));
    }

    public Harmony Harmony { get; }
}

internal interface IClientCompatibilityPackageDescriptor
{
    string PackageId { get; }

    int Order { get; }

    void Apply(ClientCompatibilityPackageApplyContext context);
}

internal static class BuiltInClientCompatibility
{
    public static void Apply(Harmony harmony)
    {
        var context = new ClientCompatibilityPackageApplyContext(harmony);
        foreach (IClientCompatibilityPackageDescriptor descriptor in DiscoverDescriptors())
        {
            try
            {
                descriptor.Apply(context);
            }
            catch (Exception ex)
            {
                Verse.Log.Error(
                    "[ClashOfRim] Client compatibility package failed during Apply: "
                    + descriptor.PackageId
                    + " "
                    + ex.GetType().Name
                    + " "
                    + ex.Message);
            }
        }
    }

    private static IReadOnlyList<IClientCompatibilityPackageDescriptor> DiscoverDescriptors()
    {
        return TryGetDescriptorTypes()
            .Select(type => type.GetProperty(
                "Descriptor",
                BindingFlags.Public | BindingFlags.Static))
            .Where(property => property is not null
                && typeof(IClientCompatibilityPackageDescriptor).IsAssignableFrom(property.PropertyType))
            .Select(TryGetDescriptor)
            .Where(descriptor => descriptor is not null)
            .Select(descriptor => descriptor!)
            .OrderBy(descriptor => descriptor.Order)
            .ThenBy(descriptor => descriptor.PackageId, StringComparer.Ordinal)
            .ToList();
    }

    private static IReadOnlyList<Type> TryGetDescriptorTypes()
    {
        try
        {
            return typeof(BuiltInClientCompatibility).Assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            Verse.Log.Error("[ClashOfRim] Client compatibility descriptor type discovery partially failed: " + ex.Message);
            foreach (Exception loaderException in ex.LoaderExceptions.OfType<Exception>())
            {
                Verse.Log.Error(
                    "[ClashOfRim] Client compatibility descriptor type discovery detail: "
                    + loaderException.GetType().Name
                    + " "
                    + loaderException.Message);
            }

            return ex.Types.OfType<Type>().ToList();
        }
        catch (Exception ex)
        {
            Verse.Log.Error(
                "[ClashOfRim] Client compatibility descriptor type discovery failed: "
                + ex.GetType().Name
                + " "
                + ex.Message);
            return Array.Empty<Type>();
        }
    }

    private static IClientCompatibilityPackageDescriptor? TryGetDescriptor(PropertyInfo? property)
    {
        if (property is null)
        {
            return null;
        }

        try
        {
            return (IClientCompatibilityPackageDescriptor?)property.GetValue(null);
        }
        catch (Exception ex)
        {
            Verse.Log.Error(
                "[ClashOfRim] Client compatibility descriptor discovery failed: "
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
