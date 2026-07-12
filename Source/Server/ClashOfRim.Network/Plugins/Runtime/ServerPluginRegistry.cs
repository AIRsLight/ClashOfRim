using AIRsLight.ClashOfRim.Compatibility;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using Microsoft.AspNetCore.Builder;

namespace AIRsLight.ClashOfRim.Network.Plugins;

public sealed class ServerPluginRegistry
{
    private readonly IReadOnlyList<ClashOfRimServerPluginDescriptor> plugins;
    private readonly IReadOnlyList<ServerPluginEndpointRegistration> endpointRegistrations;
    private readonly IReadOnlyList<IAdminBaselineRequirementProvider> adminBaselineRequirementProviders;
    private readonly IReadOnlyList<ITradeThingMetadataMatcher> tradeThingMetadataMatchers;
    private readonly IReadOnlyList<IWorldConfigurationExtensionProvider> worldConfigurationExtensionProviders;
    private readonly IReadOnlyList<IWorldObjectClassifier> worldObjectClassifiers;
    private readonly IReadOnlyList<AchievementDefinition> achievementDefinitions;
    private readonly IReadOnlyList<ISnapshotAchievementMetricProvider> snapshotAchievementMetricProviders;
    private readonly IReadOnlyList<ISnapshotPostUploadProcessor> snapshotPostUploadProcessors;
    private readonly IReadOnlyList<IAuthoritativeEventAchievementMetricProvider> authoritativeEventAchievementMetricProviders;
    private readonly IReadOnlyList<IRaidSettlementSnapshotEditorExtension> raidSettlementSnapshotEditorExtensions;
    private readonly IReadOnlyList<string> ignoredRaidSettlementThingDefNames;
    private readonly IReadOnlyList<ServerPluginDiagnostic> diagnostics;
    private readonly WorldConfigurationExtensionService worldConfigurationExtensions;
    private readonly object activeSelectionLock = new();
    private CompatibilityManifest? cachedActiveSelectionManifest;
    private ActivePluginSelection? cachedActiveSelection;

    public ServerPluginRegistry(
        IReadOnlyList<ClashOfRimServerPluginDescriptor>? plugins = null,
        IReadOnlyList<ServerPluginEndpointRegistration>? endpointRegistrations = null,
        IReadOnlyList<IAdminBaselineRequirementProvider>? adminBaselineRequirementProviders = null)
    {
        this.plugins = plugins?.ToList() ?? new List<ClashOfRimServerPluginDescriptor>();
        this.endpointRegistrations = endpointRegistrations?.ToList() ?? new List<ServerPluginEndpointRegistration>();
        this.adminBaselineRequirementProviders = (adminBaselineRequirementProviders ?? Array.Empty<IAdminBaselineRequirementProvider>())
            .Concat(this.plugins.SelectMany(plugin => plugin.AdminBaselineRequirementProviders ?? Array.Empty<IAdminBaselineRequirementProvider>()))
            .ToList();
        tradeThingMetadataMatchers = this.plugins
            .SelectMany(plugin => plugin.TradeThingMetadataMatchers ?? Array.Empty<ITradeThingMetadataMatcher>())
            .ToList();
        worldConfigurationExtensionProviders = this.plugins
            .SelectMany(plugin => plugin.WorldConfigurationExtensionProviders ?? Array.Empty<IWorldConfigurationExtensionProvider>())
            .ToList();
        worldObjectClassifiers = this.plugins
            .SelectMany(plugin => plugin.WorldObjectClassifiers ?? Array.Empty<IWorldObjectClassifier>())
            .ToList();
        achievementDefinitions = this.plugins
            .SelectMany(plugin => plugin.AchievementDefinitions ?? Array.Empty<AchievementDefinition>())
            .ToList();
        snapshotAchievementMetricProviders = this.plugins
            .SelectMany(plugin => plugin.SnapshotAchievementMetricProviders ?? Array.Empty<ISnapshotAchievementMetricProvider>())
            .ToList();
        snapshotPostUploadProcessors = this.plugins
            .SelectMany(plugin => plugin.SnapshotPostUploadProcessors ?? Array.Empty<ISnapshotPostUploadProcessor>())
            .ToList();
        authoritativeEventAchievementMetricProviders = this.plugins
            .SelectMany(plugin => plugin.AuthoritativeEventAchievementMetricProviders ?? Array.Empty<IAuthoritativeEventAchievementMetricProvider>())
            .ToList();
        raidSettlementSnapshotEditorExtensions = this.plugins
            .SelectMany(plugin => plugin.RaidSettlementSnapshotEditorExtensions ?? Array.Empty<IRaidSettlementSnapshotEditorExtension>())
            .ToList();
        ignoredRaidSettlementThingDefNames = this.plugins
            .SelectMany(plugin => plugin.IgnoredRaidSettlementThingDefNames ?? Array.Empty<string>())
            .Where(defName => !string.IsNullOrWhiteSpace(defName))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        RegisterSaveIndexExtensions(this.plugins);
        diagnostics = BuildDiagnostics(this.plugins, this.endpointRegistrations);
        WriteDiagnostics(diagnostics);
        worldConfigurationExtensions = new WorldConfigurationExtensionService(worldConfigurationExtensionProviders);
    }

    public IReadOnlyList<ClashOfRimServerPluginDescriptor> Plugins => plugins;

    public IReadOnlyList<ServerPluginDiagnostic> Diagnostics => diagnostics;

    public IReadOnlyList<IAdminBaselineRequirementProvider> AdminBaselineRequirementProviders => adminBaselineRequirementProviders;

    public IReadOnlyList<ITradeThingMetadataMatcher> TradeThingMetadataMatchers => tradeThingMetadataMatchers;

    public IReadOnlyList<IWorldConfigurationExtensionProvider> WorldConfigurationExtensionProviders =>
        worldConfigurationExtensionProviders;

    public IReadOnlyList<IWorldObjectClassifier> WorldObjectClassifiers => worldObjectClassifiers;

    public IReadOnlyList<AchievementDefinition> AchievementDefinitions => achievementDefinitions;

    public IReadOnlyList<ISnapshotAchievementMetricProvider> SnapshotAchievementMetricProviders =>
        snapshotAchievementMetricProviders;

    public IReadOnlyList<ISnapshotPostUploadProcessor> SnapshotPostUploadProcessors => snapshotPostUploadProcessors;

    public IReadOnlyList<IAuthoritativeEventAchievementMetricProvider> AuthoritativeEventAchievementMetricProviders =>
        authoritativeEventAchievementMetricProviders;

    public IReadOnlyList<IRaidSettlementSnapshotEditorExtension> RaidSettlementSnapshotEditorExtensions =>
        raidSettlementSnapshotEditorExtensions;

    public IReadOnlyList<string> IgnoredRaidSettlementThingDefNames => ignoredRaidSettlementThingDefNames;

    public WorldConfigurationExtensionService WorldConfigurationExtensions => worldConfigurationExtensions;

    public IReadOnlyList<ClashOfRimServerPluginDescriptor> ActivePlugins(CompatibilityManifest? manifest)
    {
        return ActiveSelection(manifest).Plugins;
    }

    public IReadOnlyList<ITradeThingMetadataMatcher> ActiveTradeThingMetadataMatchers(CompatibilityManifest? manifest)
    {
        return ActiveSelection(manifest).TradeThingMetadataMatchers;
    }

    public WorldConfigurationExtensionService ActiveWorldConfigurationExtensions(CompatibilityManifest? manifest)
    {
        return ActiveSelection(manifest).WorldConfigurationExtensions;
    }

    public IReadOnlyList<IWorldObjectClassifier> ActiveWorldObjectClassifiers(CompatibilityManifest? manifest)
    {
        return ActiveSelection(manifest).WorldObjectClassifiers;
    }

    public IReadOnlyList<ISaveIndexExtension> ActiveSaveIndexExtensions(CompatibilityManifest? manifest)
    {
        return ActiveSelection(manifest).SaveIndexExtensions;
    }

    public IReadOnlyList<AchievementDefinition> ActiveAchievementDefinitions(CompatibilityManifest? manifest)
    {
        return ActiveSelection(manifest).AchievementDefinitions;
    }

    public IReadOnlyList<ISnapshotAchievementMetricProvider> ActiveSnapshotAchievementMetricProviders(CompatibilityManifest? manifest)
    {
        return ActiveSelection(manifest).SnapshotAchievementMetricProviders;
    }

    public IReadOnlyList<ISnapshotPostUploadProcessor> ActiveSnapshotPostUploadProcessors(CompatibilityManifest? manifest)
    {
        return ActiveSelection(manifest).SnapshotPostUploadProcessors;
    }

    public IReadOnlyList<IAuthoritativeEventAchievementMetricProvider> ActiveAuthoritativeEventAchievementMetricProviders(
        CompatibilityManifest? manifest)
    {
        return ActiveSelection(manifest).AuthoritativeEventAchievementMetricProviders;
    }

    public IReadOnlyList<string> ActiveIgnoredRaidSettlementThingDefNames(CompatibilityManifest? manifest)
    {
        return ActiveSelection(manifest).IgnoredRaidSettlementThingDefNames;
    }

    public IReadOnlyList<IRaidSettlementSnapshotEditorExtension> ActiveRaidSettlementSnapshotEditorExtensions(CompatibilityManifest? manifest)
    {
        return ActiveSelection(manifest).RaidSettlementSnapshotEditorExtensions;
    }

    private ActivePluginSelection ActiveSelection(CompatibilityManifest? manifest)
    {
        lock (activeSelectionLock)
        {
            if (cachedActiveSelection is not null
                && ReferenceEquals(cachedActiveSelectionManifest, manifest))
            {
                return cachedActiveSelection;
            }

            IReadOnlyList<ClashOfRimServerPluginDescriptor> activePlugins;
            if (manifest is null)
            {
                activePlugins = plugins
                    .Where(plugin => plugin.RequiredPackageIds is null || plugin.RequiredPackageIds.Count == 0)
                    .ToList();
            }
            else
            {
                HashSet<string> packageIds = BuildManifestPackageIds(manifest);
                activePlugins = plugins
                    .Where(plugin => IsPluginActive(plugin, packageIds))
                    .ToList();
            }

            cachedActiveSelectionManifest = manifest;
            cachedActiveSelection = BuildActiveSelection(activePlugins);
            return cachedActiveSelection;
        }
    }

    private static ActivePluginSelection BuildActiveSelection(IReadOnlyList<ClashOfRimServerPluginDescriptor> activePlugins)
    {
        IReadOnlyList<IWorldConfigurationExtensionProvider> worldProviders = activePlugins
            .SelectMany(plugin => plugin.WorldConfigurationExtensionProviders ?? Array.Empty<IWorldConfigurationExtensionProvider>())
            .ToList();
        return new ActivePluginSelection(
            activePlugins,
            activePlugins
                .SelectMany(plugin => plugin.TradeThingMetadataMatchers ?? Array.Empty<ITradeThingMetadataMatcher>())
                .ToList(),
            worldProviders,
            new WorldConfigurationExtensionService(worldProviders),
            activePlugins
                .SelectMany(plugin => plugin.WorldObjectClassifiers ?? Array.Empty<IWorldObjectClassifier>())
                .ToList(),
            activePlugins
                .SelectMany(plugin => plugin.AchievementDefinitions ?? Array.Empty<AchievementDefinition>())
                .ToList(),
            activePlugins
                .SelectMany(plugin => plugin.SnapshotAchievementMetricProviders ?? Array.Empty<ISnapshotAchievementMetricProvider>())
                .ToList(),
            activePlugins
                .SelectMany(plugin => plugin.SnapshotPostUploadProcessors ?? Array.Empty<ISnapshotPostUploadProcessor>())
                .ToList(),
            activePlugins
                .SelectMany(plugin => plugin.AuthoritativeEventAchievementMetricProviders ?? Array.Empty<IAuthoritativeEventAchievementMetricProvider>())
                .ToList(),
            activePlugins
                .SelectMany(plugin => plugin.SaveIndexExtensions ?? Array.Empty<ISaveIndexExtension>())
                .ToList(),
            activePlugins
                .SelectMany(plugin => plugin.RaidSettlementSnapshotEditorExtensions ?? Array.Empty<IRaidSettlementSnapshotEditorExtension>())
                .ToList(),
            activePlugins
                .SelectMany(plugin => plugin.IgnoredRaidSettlementThingDefNames ?? Array.Empty<string>())
                .Where(defName => !string.IsNullOrWhiteSpace(defName))
                .Distinct(StringComparer.Ordinal)
                .ToList());
    }

    public ServerPluginRegistry WithBuiltInPlugins(IReadOnlyList<ClashOfRimServerPluginDescriptor> builtInPlugins)
    {
        if (builtInPlugins is null || builtInPlugins.Count == 0)
        {
            return this;
        }

        return new ServerPluginRegistry(
            builtInPlugins.Concat(plugins).ToList(),
            endpointRegistrations,
            adminBaselineRequirementProviders);
    }

    public void MapEndpoints(WebApplication app)
    {
        foreach (ServerPluginEndpointRegistration registration in endpointRegistrations)
        {
            try
            {
                registration.MapEndpoints(app);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    "[ClashOfRim][ServerPlugin][Error] PluginEndpointMapFailed: "
                    + registration.Key
                    + " "
                    + ex.GetType().Name
                    + " "
                    + ex.Message);
            }
        }
    }

    private static IReadOnlyList<ServerPluginDiagnostic> BuildDiagnostics(
        IReadOnlyList<ClashOfRimServerPluginDescriptor> plugins,
        IReadOnlyList<ServerPluginEndpointRegistration> endpointRegistrations)
    {
        var result = new List<ServerPluginDiagnostic>();

        result.AddRange(
            plugins
                .Where(plugin => string.IsNullOrWhiteSpace(plugin.Id))
                .Select(plugin => new ServerPluginDiagnostic(
                    "Warning",
                    "PluginIdMissing",
                    plugin.Name,
                    $"Server plugin '{plugin.Name}' from '{plugin.FileName}' does not declare a plugin id.")));

        result.AddRange(
            plugins
                .Where(plugin => !string.IsNullOrWhiteSpace(plugin.Id))
                .GroupBy(plugin => plugin.Id, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1)
                .Select(group => new ServerPluginDiagnostic(
                    "Warning",
                    "PluginIdDuplicate",
                    group.Key,
                    $"Server plugin id '{group.Key}' is declared by {FormatPluginNames(group)}.")));

        result.AddRange(
            endpointRegistrations
                .Where(registration => !string.IsNullOrWhiteSpace(registration.Key))
                .GroupBy(registration => registration.Key, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1)
                .Select(group => new ServerPluginDiagnostic(
                    "Warning",
                    "PluginEndpointDuplicate",
                    group.Key,
                    $"Server plugin endpoint key '{group.Key}' is registered {group.Count()} times.")));

        result.AddRange(
            plugins
                .SelectMany(plugin => (plugin.Capabilities ?? Array.Empty<string>())
                    .Where(capability => !string.IsNullOrWhiteSpace(capability))
                    .Select(capability => new { Plugin = plugin, Capability = capability }))
                .GroupBy(item => item.Capability, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Select(item => item.Plugin.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
                .Select(group => new ServerPluginDiagnostic(
                    "Info",
                    "PluginCapabilityShared",
                    group.Key,
                    $"Server plugin capability '{group.Key}' is shared by {FormatPluginNames(group.Select(item => item.Plugin))}.")));

        return result;
    }

    private static void RegisterSaveIndexExtensions(IReadOnlyList<ClashOfRimServerPluginDescriptor> plugins)
    {
        foreach (ISaveIndexExtension extension in plugins
            .SelectMany(plugin => plugin.SaveIndexExtensions ?? Array.Empty<ISaveIndexExtension>()))
        {
            try
            {
                SaveIndexExtensionRegistry.Register(extension);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    "[ClashOfRim][ServerPlugin][Error] PluginSaveIndexExtensionRegisterFailed: "
                    + extension.GetType().FullName
                    + " "
                    + ex.GetType().Name
                    + " "
                    + ex.Message);
            }
        }
    }

    private static bool IsPluginActive(
        ClashOfRimServerPluginDescriptor plugin,
        IReadOnlySet<string> packageIds)
    {
        return plugin.RequiredPackageIds is null
            || plugin.RequiredPackageIds.Count == 0
            || plugin.RequiredPackageIds
                .Where(packageId => !string.IsNullOrWhiteSpace(packageId))
                .All(packageId => packageIds.Contains(NormalizePackageId(packageId)));
    }

    private static HashSet<string> BuildManifestPackageIds(CompatibilityManifest manifest)
    {
        var packageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string dlcId in manifest.DlcIds ?? Array.Empty<string>())
        {
            AddPackageId(packageIds, dlcId);
        }

        foreach (ModManifestEntry mod in manifest.Mods ?? Array.Empty<ModManifestEntry>())
        {
            AddPackageId(packageIds, mod.PackageId);
        }

        return packageIds;
    }

    private static void AddPackageId(HashSet<string> packageIds, string? packageId)
    {
        string normalized = NormalizePackageId(packageId);
        if (!string.IsNullOrWhiteSpace(normalized))
        {
            packageIds.Add(normalized);
        }
    }

    private static string NormalizePackageId(string? packageId)
    {
        return (packageId ?? string.Empty).Trim().ToLowerInvariant();
    }

    private static string FormatPluginNames(IEnumerable<ClashOfRimServerPluginDescriptor> plugins)
    {
        return string.Join(
            ", ",
            plugins.Select(plugin =>
            {
                if (!string.IsNullOrWhiteSpace(plugin.Id))
                {
                    return $"{plugin.Name} ({plugin.Id})";
                }

                return plugin.Name;
            }));
    }

    private sealed class ActivePluginSelection
    {
        public ActivePluginSelection(
            IReadOnlyList<ClashOfRimServerPluginDescriptor> plugins,
            IReadOnlyList<ITradeThingMetadataMatcher> tradeThingMetadataMatchers,
            IReadOnlyList<IWorldConfigurationExtensionProvider> worldConfigurationExtensionProviders,
            WorldConfigurationExtensionService worldConfigurationExtensions,
            IReadOnlyList<IWorldObjectClassifier> worldObjectClassifiers,
            IReadOnlyList<AchievementDefinition> achievementDefinitions,
            IReadOnlyList<ISnapshotAchievementMetricProvider> snapshotAchievementMetricProviders,
            IReadOnlyList<ISnapshotPostUploadProcessor> snapshotPostUploadProcessors,
            IReadOnlyList<IAuthoritativeEventAchievementMetricProvider> authoritativeEventAchievementMetricProviders,
            IReadOnlyList<ISaveIndexExtension> saveIndexExtensions,
            IReadOnlyList<IRaidSettlementSnapshotEditorExtension> raidSettlementSnapshotEditorExtensions,
            IReadOnlyList<string> ignoredRaidSettlementThingDefNames)
        {
            Plugins = plugins;
            TradeThingMetadataMatchers = tradeThingMetadataMatchers;
            WorldConfigurationExtensionProviders = worldConfigurationExtensionProviders;
            WorldConfigurationExtensions = worldConfigurationExtensions;
            WorldObjectClassifiers = worldObjectClassifiers;
            AchievementDefinitions = achievementDefinitions;
            SnapshotAchievementMetricProviders = snapshotAchievementMetricProviders;
            SnapshotPostUploadProcessors = snapshotPostUploadProcessors;
            AuthoritativeEventAchievementMetricProviders = authoritativeEventAchievementMetricProviders;
            SaveIndexExtensions = saveIndexExtensions;
            RaidSettlementSnapshotEditorExtensions = raidSettlementSnapshotEditorExtensions;
            IgnoredRaidSettlementThingDefNames = ignoredRaidSettlementThingDefNames;
        }

        public IReadOnlyList<ClashOfRimServerPluginDescriptor> Plugins { get; }

        public IReadOnlyList<ITradeThingMetadataMatcher> TradeThingMetadataMatchers { get; }

        public IReadOnlyList<IWorldConfigurationExtensionProvider> WorldConfigurationExtensionProviders { get; }

        public WorldConfigurationExtensionService WorldConfigurationExtensions { get; }

        public IReadOnlyList<IWorldObjectClassifier> WorldObjectClassifiers { get; }

        public IReadOnlyList<AchievementDefinition> AchievementDefinitions { get; }

        public IReadOnlyList<ISnapshotAchievementMetricProvider> SnapshotAchievementMetricProviders { get; }

        public IReadOnlyList<ISnapshotPostUploadProcessor> SnapshotPostUploadProcessors { get; }

        public IReadOnlyList<IAuthoritativeEventAchievementMetricProvider> AuthoritativeEventAchievementMetricProviders { get; }

        public IReadOnlyList<ISaveIndexExtension> SaveIndexExtensions { get; }

        public IReadOnlyList<IRaidSettlementSnapshotEditorExtension> RaidSettlementSnapshotEditorExtensions { get; }

        public IReadOnlyList<string> IgnoredRaidSettlementThingDefNames { get; }
    }

    private static void WriteDiagnostics(IReadOnlyList<ServerPluginDiagnostic> diagnostics)
    {
        foreach (ServerPluginDiagnostic diagnostic in diagnostics.Where(diagnostic => diagnostic.Severity != "Info"))
        {
            Console.Error.WriteLine($"[ClashOfRim][ServerPlugin][{diagnostic.Severity}] {diagnostic.Code}: {diagnostic.Message}");
        }
    }
}

public sealed record ServerPluginDiagnostic(
    string Severity,
    string Code,
    string Subject,
    string Message);
