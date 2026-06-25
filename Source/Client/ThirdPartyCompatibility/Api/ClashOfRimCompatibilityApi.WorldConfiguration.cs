using AIRsLight.ClashOfRim.ClientNetwork;
using System;
using System.Collections.Generic;
using System.Linq;

namespace AIRsLight.ClashOfRim.ThirdPartyCompatibility;

public static partial class ClashOfRimCompatibilityApi
{
    public static void RegisterAdminBaselineExtensionProvider(
        string providerId,
        string kind,
        AdminBaselineExtensionProvider provider)
    {
        if (provider is null || string.IsNullOrWhiteSpace(providerId) || string.IsNullOrWhiteSpace(kind))
        {
            return;
        }

        int priority = ActiveRegistrationPriority();
        string requestKey = AdminBaselineExtensionProviderRegistration.Key(providerId, kind);
        AdminBaselineExtensionProviderRegistration? existing = AdminBaselineExtensionProviders.FirstOrDefault(registration =>
            string.Equals(registration.ProviderId, providerId, System.StringComparison.OrdinalIgnoreCase)
            && string.Equals(registration.Kind, kind, System.StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            int existingPriority = RegistrationPriority(existing);
            if (priority <= existingPriority)
            {
                AddRegistrationDiagnostic(
                    "Warning",
                    "AdminBaselineExtensionDuplicateRejected",
                    nameof(AdminBaselineExtensionProviderRegistration),
                    requestKey,
                    "Admin baseline extension '" + providerId + "/" + kind + "' was rejected because an existing registration has priority " + existingPriority + ".");
                return;
            }

            AddRegistrationDiagnostic(
                "Warning",
                "AdminBaselineExtensionDuplicateReplaced",
                nameof(AdminBaselineExtensionProviderRegistration),
                requestKey,
                "Admin baseline extension '" + providerId + "/" + kind + "' replaced an existing registration with priority " + existingPriority + ".");
            AdminBaselineExtensionProviders.Remove(existing);
            RemoveRegistrationTracking(existing, nameof(AdminBaselineExtensionProviderRegistration), requestKey);
        }

        var registration = new AdminBaselineExtensionProviderRegistration(providerId, kind, provider);
        AdminBaselineExtensionProviders.Add(registration);
        RegistrationPriorities[registration] = priority;
        RegistrationOrders[registration] = unchecked(++registrationOrderSequence);
        SortRegistrationsByPriority(AdminBaselineExtensionProviders);
        CompatibilityRegistrationRecord record = AddRegistrationRecord(
            nameof(AdminBaselineExtensionProviderRegistration),
            registration.RequestKey,
            priority);
        TrackRegistration(() =>
        {
            AdminBaselineExtensionProviders.Remove(registration);
            RegistrationPriorities.Remove(registration);
            RegistrationOrders.Remove(registration);
            RegistrationRecords.Remove(record);
        });
    }

    public static void RegisterCompatibilityPackage(
        string packageId,
        Func<bool> isLocallyActive,
        IReadOnlyCollection<string> capabilities)
    {
        if (string.IsNullOrWhiteSpace(packageId) || isLocallyActive is null || capabilities is null || capabilities.Count == 0)
        {
            return;
        }

        int priority = ActiveRegistrationPriority();
        CompatibilityPackageRegistration? existing = CompatibilityPackages.FirstOrDefault(registration =>
            string.Equals(registration.PackageId, packageId, System.StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            int existingPriority = RegistrationPriority(existing);
            if (priority <= existingPriority)
            {
                AddRegistrationDiagnostic(
                    "Warning",
                    "CompatibilityPackageDuplicateRejected",
                    nameof(CompatibilityPackageRegistration),
                    packageId,
                    "Compatibility package '" + packageId + "' was rejected because an existing registration has priority " + existingPriority + ".");
                return;
            }

            AddRegistrationDiagnostic(
                "Warning",
                "CompatibilityPackageDuplicateReplaced",
                nameof(CompatibilityPackageRegistration),
                packageId,
                "Compatibility package '" + packageId + "' replaced an existing registration with priority " + existingPriority + ".");
            CompatibilityPackages.Remove(existing);
            RemoveRegistrationTracking(existing, nameof(CompatibilityPackageRegistration), packageId);
        }

        foreach (string capability in capabilities
            .Where(capability => !string.IsNullOrWhiteSpace(capability))
            .Select(capability => capability.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (CompatibilityPackages.Any(registration => registration.Capabilities.Contains(capability)))
            {
                AddRegistrationDiagnostic(
                    "Info",
                    "CompatibilityCapabilityShared",
                    nameof(CompatibilityPackageRegistration),
                    capability,
                    "Compatibility capability '" + capability + "' is declared by multiple packages.");
            }
        }

        var registration = new CompatibilityPackageRegistration(packageId, isLocallyActive, capabilities);
        CompatibilityPackages.Add(registration);
        RegistrationPriorities[registration] = priority;
        RegistrationOrders[registration] = unchecked(++registrationOrderSequence);
        SortRegistrationsByPriority(CompatibilityPackages);
        CompatibilityRegistrationRecord record = AddRegistrationRecord(
            nameof(CompatibilityPackageRegistration),
            packageId,
            priority);
        TrackRegistration(() =>
        {
            CompatibilityPackages.Remove(registration);
            RegistrationPriorities.Remove(registration);
            RegistrationOrders.Remove(registration);
            RegistrationRecords.Remove(record);
        });
    }

    public static void ApplyServerDlcBaseline(IEnumerable<string>? dlcIds)
    {
        serverDlcIds = dlcIds is null
            ? null
            : new HashSet<string>(dlcIds, System.StringComparer.OrdinalIgnoreCase);
    }

    public static bool HasCompatibilityCapability(string capability)
    {
        if (string.IsNullOrWhiteSpace(capability))
        {
            return false;
        }

        foreach (CompatibilityPackageRegistration package in CompatibilityPackages.ToList())
        {
            if (!TryInvokeCompatibilityCallback(
                    nameof(HasCompatibilityCapability),
                    package,
                    package.IsLocallyActive,
                    out bool locallyActive))
            {
                continue;
            }

            if (locallyActive
                && ServerAllowsDlc(package.PackageId)
                && package.Capabilities.Contains(capability))
            {
                return true;
            }
        }

        return false;
    }

    public static void RegisterWorldConfigurationExtensionHandler(
        string providerId,
        string kind,
        WorldConfigurationExtensionCollector? collector,
        WorldConfigurationExtensionApplier? applier)
    {
        if (string.IsNullOrWhiteSpace(providerId)
            || string.IsNullOrWhiteSpace(kind)
            || (collector is null && applier is null))
        {
            return;
        }

        int priority = ActiveRegistrationPriority();
        string requestKey = WorldConfigurationExtensionHandlerRegistration.Key(providerId, kind);
        WorldConfigurationExtensionHandlerRegistration? existing = WorldConfigurationExtensionHandlers.FirstOrDefault(registration =>
            string.Equals(registration.RequestKey, requestKey, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            int existingPriority = RegistrationPriority(existing);
            if (priority <= existingPriority)
            {
                AddRegistrationDiagnostic(
                    "Warning",
                    "WorldConfigurationExtensionDuplicateRejected",
                    nameof(WorldConfigurationExtensionHandlerRegistration),
                    requestKey,
                    "World configuration extension handler '" + requestKey + "' was rejected because an existing registration has priority " + existingPriority + ".");
                return;
            }

            AddRegistrationDiagnostic(
                "Warning",
                "WorldConfigurationExtensionDuplicateReplaced",
                nameof(WorldConfigurationExtensionHandlerRegistration),
                requestKey,
                "World configuration extension handler '" + requestKey + "' replaced an existing registration with priority " + existingPriority + ".");
            WorldConfigurationExtensionHandlers.Remove(existing);
            RemoveRegistrationTracking(existing, nameof(WorldConfigurationExtensionHandlerRegistration), requestKey);
        }

        var registration = new WorldConfigurationExtensionHandlerRegistration(providerId, kind, collector, applier);
        WorldConfigurationExtensionHandlers.Add(registration);
        RegistrationPriorities[registration] = priority;
        RegistrationOrders[registration] = unchecked(++registrationOrderSequence);
        SortRegistrationsByPriority(WorldConfigurationExtensionHandlers);
        CompatibilityRegistrationRecord record = AddRegistrationRecord(
            nameof(WorldConfigurationExtensionHandlerRegistration),
            requestKey,
            priority);
        TrackRegistration(() =>
        {
            WorldConfigurationExtensionHandlers.Remove(registration);
            RegistrationPriorities.Remove(registration);
            RegistrationOrders.Remove(registration);
            RegistrationRecords.Remove(record);
        });
    }

    public static bool HasWorldConfigurationExtensionCollector =>
        WorldConfigurationExtensionHandlers.Any(registration => registration.Collector is not null);

    public static void RegisterWorldConfigurationExtensionSummaryProvider(WorldConfigurationExtensionSummaryProvider provider)
    {
        RegisterUnique(WorldConfigurationExtensionSummaryProviders, provider);
    }

    public static void RegisterWorldGenerationFloatSettingProvider(string settingKey, WorldGenerationFloatSettingProvider provider)
    {
        if (string.IsNullOrWhiteSpace(settingKey) || provider is null)
        {
            return;
        }

        int priority = ActiveRegistrationPriority();
        string normalizedKey = settingKey.Trim();
        WorldGenerationFloatSettingProviderRegistration? existing = WorldGenerationFloatSettingProviders.FirstOrDefault(registration =>
            string.Equals(registration.SettingKey, normalizedKey, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            int existingPriority = RegistrationPriority(existing);
            if (priority <= existingPriority)
            {
                AddRegistrationDiagnostic(
                    "Warning",
                    "WorldGenerationFloatSettingDuplicateRejected",
                    nameof(WorldGenerationFloatSettingProviderRegistration),
                    normalizedKey,
                    "World generation float setting '" + normalizedKey + "' was rejected because an existing registration has priority " + existingPriority + ".");
                return;
            }

            AddRegistrationDiagnostic(
                "Warning",
                "WorldGenerationFloatSettingDuplicateReplaced",
                nameof(WorldGenerationFloatSettingProviderRegistration),
                normalizedKey,
                "World generation float setting '" + normalizedKey + "' replaced an existing registration with priority " + existingPriority + ".");
            WorldGenerationFloatSettingProviders.Remove(existing);
            RemoveRegistrationTracking(existing, nameof(WorldGenerationFloatSettingProviderRegistration), normalizedKey);
        }

        var registration = new WorldGenerationFloatSettingProviderRegistration(normalizedKey, provider);
        WorldGenerationFloatSettingProviders.Add(registration);
        RegistrationPriorities[registration] = priority;
        RegistrationOrders[registration] = unchecked(++registrationOrderSequence);
        SortRegistrationsByPriority(WorldGenerationFloatSettingProviders);
        CompatibilityRegistrationRecord record = AddRegistrationRecord(
            nameof(WorldGenerationFloatSettingProviderRegistration),
            normalizedKey,
            priority);
        TrackRegistration(() =>
        {
            WorldGenerationFloatSettingProviders.Remove(registration);
            RegistrationPriorities.Remove(registration);
            RegistrationOrders.Remove(registration);
            RegistrationRecords.Remove(record);
        });
    }

    public static float ResolveWorldGenerationFloatSetting(
        ModWorldConfigurationDto configuration,
        string settingKey,
        float defaultValue)
    {
        if (configuration is null || string.IsNullOrWhiteSpace(settingKey))
        {
            return defaultValue;
        }

        foreach (WorldGenerationFloatSettingProviderRegistration registration in WorldGenerationFloatSettingProviders.ToList())
        {
            if (!string.Equals(registration.SettingKey, settingKey, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            float value = defaultValue;
            if (!TryInvokeCompatibilityCallback(
                    nameof(ResolveWorldGenerationFloatSetting),
                    registration,
                    () => registration.Provider(configuration, out value),
                    out bool resolved))
            {
                continue;
            }

            if (resolved)
            {
                return value;
            }
        }

        return defaultValue;
    }

    public static IReadOnlyList<ModWorldConfigurationExtensionDto> CollectCurrentWorldConfigurationExtensions(
        string? userId,
        string? colonyId,
        string worldConfigurationId)
    {
        var extensions = new List<ModWorldConfigurationExtensionDto>();
        foreach (WorldConfigurationExtensionHandlerRegistration registration in WorldConfigurationExtensionHandlers.ToList())
        {
            if (registration.Collector is null)
            {
                continue;
            }

            if (!TryInvokeCompatibilityCallback(
                    nameof(CollectCurrentWorldConfigurationExtensions),
                    registration,
                    () => registration.Collector(userId, colonyId, worldConfigurationId),
                    out IReadOnlyList<ModWorldConfigurationExtensionDto>? result))
            {
                continue;
            }

            if (result is not null && result.Count > 0)
            {
                extensions.AddRange(result.Where(extension =>
                    extension is not null
                    && string.Equals(extension.ProviderId, registration.ProviderId, StringComparison.Ordinal)
                    && string.Equals(extension.Kind, registration.Kind, StringComparison.Ordinal)));
            }
        }

        return extensions
            .GroupBy(extension => extension.ProviderId + "\u001f" + extension.Kind, StringComparer.Ordinal)
            .Select(group => group.Last())
            .ToList();
    }

    public static int ApplyWorldConfigurationExtensions(
        ModWorldConfigurationDto configuration,
        string? localUserId,
        bool applyWorldState)
    {
        if (configuration is null)
        {
            return 0;
        }

        int applied = 0;
        IReadOnlyList<ModWorldConfigurationExtensionDto> incoming = configuration.Extensions ?? new List<ModWorldConfigurationExtensionDto>();
        foreach (WorldConfigurationExtensionHandlerRegistration registration in WorldConfigurationExtensionHandlers.ToList())
        {
            if (registration.Applier is null
                || !incoming.Any(extension =>
                    string.Equals(extension.ProviderId, registration.ProviderId, StringComparison.Ordinal)
                    && string.Equals(extension.Kind, registration.Kind, StringComparison.Ordinal)))
            {
                continue;
            }

            if (TryInvokeCompatibilityCallback(
                    nameof(ApplyWorldConfigurationExtensions),
                    registration,
                    () => registration.Applier(configuration, localUserId, applyWorldState),
                    out int registrationApplied))
            {
                applied += Math.Max(0, registrationApplied);
            }
        }

        return applied;
    }

    public static IReadOnlyList<WorldConfigurationExtensionSummaryItem> GetWorldConfigurationExtensionSummary(
        ModWorldConfigurationDto configuration)
    {
        if (configuration is null)
        {
            return Array.Empty<WorldConfigurationExtensionSummaryItem>();
        }

        var items = new List<WorldConfigurationExtensionSummaryItem>();
        foreach (WorldConfigurationExtensionSummaryProvider provider in WorldConfigurationExtensionSummaryProviders.ToList())
        {
            if (!TryInvokeCompatibilityCallback(
                    nameof(GetWorldConfigurationExtensionSummary),
                    provider,
                    () => provider(configuration),
                    out IReadOnlyList<WorldConfigurationExtensionSummaryItem>? result))
            {
                continue;
            }

            if (result is not null && result.Count > 0)
            {
                items.AddRange(result.Where(item => item is not null));
            }
        }

        return items
            .Where(item => !string.IsNullOrWhiteSpace(item.Key))
            .GroupBy(item => item.Key, StringComparer.Ordinal)
            .Select(group => group.Last())
            .ToList();
    }

    public static void AppendAdminBaselineExtensions(
        ModSubmitAdminBaselineRequestDto baseline,
        IEnumerable<ModAdminBaselineExtensionRequirementDto>? requirements)
    {
        if (baseline is null || AdminBaselineExtensionProviders.Count == 0)
        {
            return;
        }

        var requested = new HashSet<string>(
            (requirements ?? Enumerable.Empty<ModAdminBaselineExtensionRequirementDto>())
            .Where(requirement => !string.IsNullOrWhiteSpace(requirement.ProviderId)
                && !string.IsNullOrWhiteSpace(requirement.Kind))
            .Select(requirement => AdminBaselineExtensionProviderRegistration.Key(requirement.ProviderId, requirement.Kind)),
            System.StringComparer.OrdinalIgnoreCase);
        if (requested.Count == 0)
        {
            return;
        }

        foreach (AdminBaselineExtensionProviderRegistration registration in AdminBaselineExtensionProviders.ToList())
        {
            if (!requested.Contains(registration.RequestKey))
            {
                continue;
            }

            if (!TryInvokeCompatibilityCallback(
                    nameof(AppendAdminBaselineExtensions),
                    registration,
                    () => registration.Provider(),
                    out ModAdminBaselineExtensionDto? extension))
            {
                continue;
            }

            if (extension is null
                || string.IsNullOrWhiteSpace(extension.ProviderId)
                || string.IsNullOrWhiteSpace(extension.Kind))
            {
                continue;
            }

            baseline.BaselineExtensions.Add(extension);
        }
    }

}
