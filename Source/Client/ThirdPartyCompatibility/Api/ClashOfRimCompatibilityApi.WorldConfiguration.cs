using AIRsLight.ClashOfRim.ClientNetwork;
using System;
using System.Collections.Generic;

namespace AIRsLight.ClashOfRim.ThirdPartyCompatibility;

public static partial class ClashOfRimCompatibilityApi
{
    public static bool HasWorldConfigurationExtensionCollector => WorldConfiguration.HasWorldConfigurationExtensionCollector;

    public static void RegisterAdminBaselineExtensionProvider(
        string providerId,
        string kind,
        AdminBaselineExtensionProvider provider) =>
        AdminBaseline.RegisterAdminBaselineExtensionProvider(providerId, kind, provider);

    public static void RegisterCompatibilityPackage(
        string packageId,
        Func<bool> isLocallyActive,
        IReadOnlyCollection<string> capabilities) =>
        CompatibilityPackages.RegisterCompatibilityPackage(packageId, isLocallyActive, capabilities);

    public static void ApplyServerDlcBaseline(IEnumerable<string>? dlcIds) =>
        CompatibilityPackages.ApplyServerDlcBaseline(dlcIds);

    public static bool HasCompatibilityCapability(string capability) =>
        CompatibilityPackages.HasCompatibilityCapability(capability);

    public static void RegisterWorldConfigurationExtensionHandler(
        string providerId,
        string kind,
        WorldConfigurationExtensionCollector? collector,
        WorldConfigurationExtensionApplier? applier) =>
        WorldConfiguration.RegisterWorldConfigurationExtensionHandler(providerId, kind, collector, applier);

    public static void RegisterWorldConfigurationExtensionSummaryProvider(WorldConfigurationExtensionSummaryProvider provider) =>
        WorldConfiguration.RegisterWorldConfigurationExtensionSummaryProvider(provider);

    public static void RegisterWorldGenerationFloatSettingProvider(string settingKey, WorldGenerationFloatSettingProvider provider) =>
        WorldGenerationSettings.RegisterWorldGenerationFloatSettingProvider(settingKey, provider);

    public static float ResolveWorldGenerationFloatSetting(
        ModWorldConfigurationDto configuration,
        string settingKey,
        float defaultValue) =>
        WorldGenerationSettings.ResolveWorldGenerationFloatSetting(configuration, settingKey, defaultValue);

    public static IReadOnlyList<ModWorldConfigurationExtensionDto> CollectCurrentWorldConfigurationExtensions(
        string? userId,
        string? colonyId,
        string worldConfigurationId) =>
        WorldConfiguration.CollectCurrentWorldConfigurationExtensions(userId, colonyId, worldConfigurationId);

    public static int ApplyWorldConfigurationExtensions(
        ModWorldConfigurationDto configuration,
        string? localUserId,
        bool applyWorldState) =>
        WorldConfiguration.ApplyWorldConfigurationExtensions(configuration, localUserId, applyWorldState);

    public static IReadOnlyList<WorldConfigurationExtensionSummaryItem> GetWorldConfigurationExtensionSummary(
        ModWorldConfigurationDto configuration) =>
        WorldConfiguration.GetWorldConfigurationExtensionSummary(configuration);

    public static void AppendAdminBaselineExtensions(
        ModSubmitAdminBaselineRequestDto baseline,
        IEnumerable<ModAdminBaselineExtensionRequirementDto>? requirements) =>
        AdminBaseline.AppendAdminBaselineExtensions(baseline, requirements);
}
