using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Save;
using Microsoft.AspNetCore.Builder;

namespace AIRsLight.ClashOfRim.Network.Plugins;

public sealed class ClashOfRimServerPluginContext
{
    private readonly List<ISaveIndexExtension> saveIndexExtensions = new();
    private readonly List<ServerPluginEndpointRegistration> endpointRegistrations = new();
    private readonly List<IAdminBaselineRequirementProvider> adminBaselineRequirementProviders = new();
    private readonly List<IWorldConfigurationExtensionProvider> worldConfigurationExtensionProviders = new();
    private readonly List<IWorldObjectClassifier> worldObjectClassifiers = new();
    private readonly List<IRaidSettlementSnapshotEditorExtension> raidSettlementSnapshotEditorExtensions = new();
    private readonly List<AchievementDefinition> achievementDefinitions = new();
    private readonly List<ISnapshotAchievementMetricProvider> snapshotAchievementMetricProviders = new();
    private readonly List<ISnapshotPostUploadProcessor> snapshotPostUploadProcessors = new();
    private readonly List<IAuthoritativeEventAchievementMetricProvider> authoritativeEventAchievementMetricProviders = new();

    public ClashOfRimServerPluginContext(string contentRootPath)
    {
        ContentRootPath = contentRootPath;
    }

    public string ContentRootPath { get; }

    public IReadOnlyList<ISaveIndexExtension> SaveIndexExtensions => saveIndexExtensions;

    public IReadOnlyList<ServerPluginEndpointRegistration> EndpointRegistrations => endpointRegistrations;

    public IReadOnlyList<IAdminBaselineRequirementProvider> AdminBaselineRequirementProviders => adminBaselineRequirementProviders;

    public IReadOnlyList<IWorldConfigurationExtensionProvider> WorldConfigurationExtensionProviders =>
        worldConfigurationExtensionProviders;

    public IReadOnlyList<IWorldObjectClassifier> WorldObjectClassifiers => worldObjectClassifiers;

    public IReadOnlyList<IRaidSettlementSnapshotEditorExtension> RaidSettlementSnapshotEditorExtensions =>
        raidSettlementSnapshotEditorExtensions;

    public IReadOnlyList<AchievementDefinition> AchievementDefinitions => achievementDefinitions;

    public IReadOnlyList<ISnapshotAchievementMetricProvider> SnapshotAchievementMetricProviders =>
        snapshotAchievementMetricProviders;

    public IReadOnlyList<ISnapshotPostUploadProcessor> SnapshotPostUploadProcessors => snapshotPostUploadProcessors;

    public IReadOnlyList<IAuthoritativeEventAchievementMetricProvider> AuthoritativeEventAchievementMetricProviders =>
        authoritativeEventAchievementMetricProviders;

    public void RegisterSaveIndexExtension(ISaveIndexExtension extension)
    {
        ArgumentNullException.ThrowIfNull(extension);
        saveIndexExtensions.Add(extension);
    }

    public void RegisterEndpoints(string key, Action<WebApplication> mapEndpoints)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(mapEndpoints);
        endpointRegistrations.Add(new ServerPluginEndpointRegistration(key, mapEndpoints));
    }

    public void RegisterAdminBaselineRequirementProvider(IAdminBaselineRequirementProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        adminBaselineRequirementProviders.Add(provider);
    }

    public void RegisterWorldConfigurationExtensionProvider(IWorldConfigurationExtensionProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        worldConfigurationExtensionProviders.Add(provider);
    }

    public void RegisterWorldObjectClassifier(IWorldObjectClassifier classifier)
    {
        ArgumentNullException.ThrowIfNull(classifier);
        worldObjectClassifiers.Add(classifier);
    }

    public void RegisterRaidSettlementSnapshotEditorExtension(IRaidSettlementSnapshotEditorExtension extension)
    {
        ArgumentNullException.ThrowIfNull(extension);
        raidSettlementSnapshotEditorExtensions.Add(extension);
    }

    public void RegisterAchievementDefinition(AchievementDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        achievementDefinitions.Add(definition);
    }

    public void RegisterAchievementDefinitions(IEnumerable<AchievementDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        achievementDefinitions.AddRange(definitions.Where(definition => definition is not null));
    }

    public void RegisterSnapshotAchievementMetricProvider(ISnapshotAchievementMetricProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        snapshotAchievementMetricProviders.Add(provider);
    }

    public void RegisterSnapshotPostUploadProcessor(ISnapshotPostUploadProcessor processor)
    {
        ArgumentNullException.ThrowIfNull(processor);
        snapshotPostUploadProcessors.Add(processor);
    }

    public void RegisterAuthoritativeEventAchievementMetricProvider(IAuthoritativeEventAchievementMetricProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        authoritativeEventAchievementMetricProviders.Add(provider);
    }
}
