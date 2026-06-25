using AIRsLight.ClashOfRim.Save;
using AIRsLight.ClashOfRim.Network.Plugins;
using System.Reflection;
using System.Runtime.Loader;

namespace AIRsLight.ClashOfRim.Network;

public static class ServerPluginLoader
{
    public static ServerPluginRegistry Load(string contentRootPath)
    {
        IReadOnlyList<string> pluginDirectories = ResolvePluginDirectories(contentRootPath);
        if (pluginDirectories.Count == 0)
        {
            return new ServerPluginRegistry();
        }

        var descriptors = new List<ClashOfRimServerPluginDescriptor>();
        var endpointRegistrations = new List<ServerPluginEndpointRegistration>();
        var adminBaselineRequirementProviders = new List<IAdminBaselineRequirementProvider>();
        foreach (string pluginPath in pluginDirectories
            .SelectMany(directory => Directory.EnumerateFiles(directory, "*.dll", SearchOption.TopDirectoryOnly))
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            Assembly? assembly = TryLoadPluginAssembly(pluginPath);
            if (assembly is null)
            {
                continue;
            }

            bool pluginEntryFound = false;
            foreach (Type type in TryGetPluginTypes(assembly, pluginPath))
            {
                if (type.IsAbstract || type.GetConstructor(Type.EmptyTypes) is null)
                {
                    continue;
                }

                if (typeof(IClashOfRimServerPlugin).IsAssignableFrom(type)
                    && TryCreatePlugin(type, pluginPath) is IClashOfRimServerPlugin plugin)
                {
                    pluginEntryFound = true;
                    ClashOfRimServerPluginContext context = new(contentRootPath);
                    if (!TryConfigurePlugin(plugin, context, pluginPath))
                    {
                        continue;
                    }

                    foreach (ISaveIndexExtension extension in context.SaveIndexExtensions)
                    {
                        TryRegisterSaveIndexExtension(extension, pluginPath);
                    }

                    endpointRegistrations.AddRange(context.EndpointRegistrations);
                    adminBaselineRequirementProviders.AddRange(context.AdminBaselineRequirementProviders);
                    descriptors.Add(NormalizeDescriptor(plugin.Describe(), assembly, pluginPath, context));
                }
            }

            if (!pluginEntryFound)
            {
                int registered = 0;
                foreach (Type type in TryGetPluginTypes(assembly, pluginPath))
                {
                    if (type.IsAbstract
                        || type.GetConstructor(Type.EmptyTypes) is null
                        || !typeof(ISaveIndexExtension).IsAssignableFrom(type))
                    {
                        continue;
                    }

                    if (TryCreatePlugin(type, pluginPath) is ISaveIndexExtension extension
                        && TryRegisterSaveIndexExtension(extension, pluginPath))
                    {
                        registered++;
                    }
                }

                if (registered > 0)
                {
                    descriptors.Add(new ClashOfRimServerPluginDescriptor(
                        Id: Path.GetFileNameWithoutExtension(pluginPath),
                        Name: Path.GetFileNameWithoutExtension(pluginPath),
                        Version: assembly.GetName().Version?.ToString() ?? "unknown",
                        AssemblyName: assembly.GetName().Name ?? Path.GetFileNameWithoutExtension(pluginPath),
                        FileName: Path.GetFileName(pluginPath),
                        Capabilities: new[] { "save-index" }));
                }
            }
        }

        return new ServerPluginRegistry(
            descriptors,
            endpointRegistrations,
            adminBaselineRequirementProviders);
    }

    private static IReadOnlyList<string> ResolvePluginDirectories(string contentRootPath)
    {
        string primary = Path.Combine(contentRootPath, "Plugins");
        if (HasPluginAssemblies(primary))
        {
            return new[] { primary };
        }

        string? current = Path.GetFullPath(contentRootPath);
        while (!string.IsNullOrWhiteSpace(current))
        {
            string buildSibling = Path.Combine(current, "Build", "ServerPlugins");
            if (HasPluginAssemblies(buildSibling))
            {
                return new[] { buildSibling };
            }

            string directSibling = Path.Combine(current, "ServerPlugins");
            if (HasPluginAssemblies(directSibling))
            {
                return new[] { directSibling };
            }

            DirectoryInfo? parent = Directory.GetParent(current);
            current = parent?.FullName;
        }

        return Array.Empty<string>();
    }

    private static bool HasPluginAssemblies(string directory)
    {
        return Directory.Exists(directory)
            && Directory.EnumerateFiles(directory, "*.dll", SearchOption.TopDirectoryOnly).Any();
    }

    private static ClashOfRimServerPluginDescriptor NormalizeDescriptor(
        ClashOfRimServerPluginDescriptor descriptor,
        Assembly assembly,
        string pluginPath,
        ClashOfRimServerPluginContext context)
    {
        IReadOnlyList<string> capabilities = descriptor.Capabilities.Count > 0
            ? descriptor.Capabilities
            : InferCapabilities(context);
        return descriptor with
        {
            AssemblyName = string.IsNullOrWhiteSpace(descriptor.AssemblyName)
                ? assembly.GetName().Name ?? string.Empty
                : descriptor.AssemblyName,
            FileName = string.IsNullOrWhiteSpace(descriptor.FileName)
                ? Path.GetFileName(pluginPath)
                : descriptor.FileName,
            Capabilities = capabilities,
            SaveIndexExtensions = (descriptor.SaveIndexExtensions ?? Array.Empty<ISaveIndexExtension>())
                .Concat(context.SaveIndexExtensions)
                .ToList(),
            RaidSettlementSnapshotEditorExtensions =
                (descriptor.RaidSettlementSnapshotEditorExtensions ?? Array.Empty<IRaidSettlementSnapshotEditorExtension>())
                .Concat(context.RaidSettlementSnapshotEditorExtensions)
                .ToList(),
            WorldConfigurationExtensionProviders = (descriptor.WorldConfigurationExtensionProviders ?? Array.Empty<IWorldConfigurationExtensionProvider>())
                .Concat(context.WorldConfigurationExtensionProviders)
                .ToList(),
            WorldObjectClassifiers = (descriptor.WorldObjectClassifiers ?? Array.Empty<IWorldObjectClassifier>())
                .Concat(context.WorldObjectClassifiers)
                .ToList(),
            AchievementDefinitions = (descriptor.AchievementDefinitions ?? Array.Empty<AchievementDefinition>())
                .Concat(context.AchievementDefinitions)
                .ToList(),
            SnapshotAchievementMetricProviders =
                (descriptor.SnapshotAchievementMetricProviders ?? Array.Empty<ISnapshotAchievementMetricProvider>())
                .Concat(context.SnapshotAchievementMetricProviders)
                .ToList(),
            AuthoritativeEventAchievementMetricProviders =
                (descriptor.AuthoritativeEventAchievementMetricProviders ?? Array.Empty<IAuthoritativeEventAchievementMetricProvider>())
                .Concat(context.AuthoritativeEventAchievementMetricProviders)
                .ToList()
        };
    }

    private static Assembly? TryLoadPluginAssembly(string pluginPath)
    {
        try
        {
            return AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(pluginPath));
        }
        catch (Exception ex)
        {
            LogPluginError("PluginAssemblyLoadFailed", pluginPath, ex);
            return null;
        }
    }

    private static IReadOnlyList<Type> TryGetPluginTypes(Assembly assembly, string pluginPath)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            Console.Error.WriteLine(
                "[ClashOfRim][ServerPlugin][Error] PluginTypeLoadPartialFailure: "
                + Path.GetFileName(pluginPath)
                + " "
                + ex.Message);
            foreach (Exception loaderException in ex.LoaderExceptions.OfType<Exception>())
            {
                Console.Error.WriteLine(
                    "[ClashOfRim][ServerPlugin][Error] PluginTypeLoadPartialFailure.Detail: "
                    + loaderException.GetType().Name
                    + " "
                    + loaderException.Message);
            }

            return ex.Types.OfType<Type>().ToList();
        }
        catch (Exception ex)
        {
            LogPluginError("PluginTypeLoadFailed", pluginPath, ex);
            return Array.Empty<Type>();
        }
    }

    private static object? TryCreatePlugin(Type type, string pluginPath)
    {
        try
        {
            return Activator.CreateInstance(type);
        }
        catch (Exception ex)
        {
            LogPluginError("PluginCreateFailed", pluginPath + "::" + type.FullName, ex);
            return null;
        }
    }

    private static bool TryConfigurePlugin(
        IClashOfRimServerPlugin plugin,
        ClashOfRimServerPluginContext context,
        string pluginPath)
    {
        try
        {
            plugin.Configure(context);
            return true;
        }
        catch (Exception ex)
        {
            LogPluginError("PluginConfigureFailed", pluginPath + "::" + plugin.GetType().FullName, ex);
            return false;
        }
    }

    private static bool TryRegisterSaveIndexExtension(ISaveIndexExtension extension, string pluginPath)
    {
        try
        {
            SaveIndexExtensionRegistry.Register(extension);
            return true;
        }
        catch (Exception ex)
        {
            LogPluginError("PluginSaveIndexExtensionRegisterFailed", pluginPath + "::" + extension.GetType().FullName, ex);
            return false;
        }
    }

    private static void LogPluginError(string code, string subject, Exception ex)
    {
        Console.Error.WriteLine(
            "[ClashOfRim][ServerPlugin][Error] "
            + code
            + ": "
            + subject
            + " "
            + ex.GetType().Name
            + " "
            + ex.Message);
    }

    private static IReadOnlyList<string> InferCapabilities(ClashOfRimServerPluginContext context)
    {
        var capabilities = new List<string>();
        if (context.SaveIndexExtensions.Count > 0)
        {
            capabilities.Add("save-index");
        }

        if (context.EndpointRegistrations.Count > 0)
        {
            capabilities.Add("endpoints");
        }

        if (context.AdminBaselineRequirementProviders.Count > 0)
        {
            capabilities.Add("admin-baseline-requirements");
        }

        if (context.WorldConfigurationExtensionProviders.Count > 0)
        {
            capabilities.Add("world-configuration-extensions");
        }

        if (context.WorldObjectClassifiers.Count > 0)
        {
            capabilities.Add("world-object-classifiers");
        }

        if (context.AchievementDefinitions.Count > 0)
        {
            capabilities.Add("achievement-definitions");
        }

        if (context.SnapshotAchievementMetricProviders.Count > 0)
        {
            capabilities.Add("snapshot-achievement-metrics");
        }

        if (context.AuthoritativeEventAchievementMetricProviders.Count > 0)
        {
            capabilities.Add("authoritative-event-achievement-metrics");
        }

        if (context.RaidSettlementSnapshotEditorExtensions.Count > 0)
        {
            capabilities.Add("raid-settlement-snapshot-editor");
        }

        return capabilities;
    }
}
