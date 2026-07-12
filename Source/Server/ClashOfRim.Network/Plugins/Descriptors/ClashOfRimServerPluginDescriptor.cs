using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Protocol;

namespace AIRsLight.ClashOfRim.Network.Plugins;

public sealed record ClashOfRimServerPluginDescriptor(
    string Id,
    string Name,
    string Version,
    string AssemblyName,
    string FileName,
    IReadOnlyList<string> Capabilities,
    IReadOnlyList<ITradeThingMetadataMatcher>? TradeThingMetadataMatchers = null,
    IReadOnlyList<IAdminBaselineRequirementProvider>? AdminBaselineRequirementProviders = null,
    IReadOnlyList<IWorldConfigurationExtensionProvider>? WorldConfigurationExtensionProviders = null,
    IReadOnlyList<IWorldObjectClassifier>? WorldObjectClassifiers = null,
    IReadOnlyList<AchievementDefinition>? AchievementDefinitions = null,
    IReadOnlyList<ISnapshotAchievementMetricProvider>? SnapshotAchievementMetricProviders = null,
    IReadOnlyList<IAuthoritativeEventAchievementMetricProvider>? AuthoritativeEventAchievementMetricProviders = null,
    IReadOnlyList<AIRsLight.ClashOfRim.Save.ISaveIndexExtension>? SaveIndexExtensions = null,
    IReadOnlyList<AIRsLight.ClashOfRim.Save.IRaidSettlementSnapshotEditorExtension>? RaidSettlementSnapshotEditorExtensions = null,
    IReadOnlyList<string>? IgnoredRaidSettlementThingDefNames = null,
    IReadOnlyList<string>? RequiredPackageIds = null,
    int Order = 0)
{
    public IReadOnlyList<ISnapshotPostUploadProcessor>? SnapshotPostUploadProcessors { get; init; }
}
