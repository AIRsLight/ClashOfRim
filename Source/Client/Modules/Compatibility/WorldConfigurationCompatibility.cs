using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility;
using System;
using System.Collections.Generic;
using System.Linq;

namespace AIRsLight.ClashOfRim.Modules.Compatibility;

internal sealed class WorldConfigurationCompatibility
{
    private readonly List<WorldConfigurationExtensionHandlerRegistration> WorldConfigurationExtensionHandlers = new();
    private readonly List<WorldConfigurationExtensionSummaryProvider> WorldConfigurationExtensionSummaryProviders = new();
    private readonly CompatibilityRegistrationScope Registrations;

    internal WorldConfigurationCompatibility(CompatibilityRegistrationScope registrations) => Registrations = registrations;

    internal bool HasWorldConfigurationExtensionCollector =>
        WorldConfigurationExtensionHandlers.Any(registration => registration.Collector is not null);

    internal void RegisterWorldConfigurationExtensionHandler(
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

        int priority = Registrations.ActiveRegistrationPriority();
        string requestKey = WorldConfigurationExtensionHandlerRegistration.Key(providerId, kind);
        WorldConfigurationExtensionHandlerRegistration? existing = WorldConfigurationExtensionHandlers.FirstOrDefault(registration =>
            string.Equals(registration.RequestKey, requestKey, StringComparison.OrdinalIgnoreCase));
        if (!Registrations.TryReplaceDuplicate(WorldConfigurationExtensionHandlers, existing,
                nameof(WorldConfigurationExtensionHandlerRegistration), requestKey, priority,
                "WorldConfigurationExtensionDuplicateRejected", "WorldConfigurationExtensionDuplicateReplaced",
                "World configuration extension handler '" + requestKey + "'"))
        {
            return;
        }

        var registration = new WorldConfigurationExtensionHandlerRegistration(providerId, kind, collector, applier);
        Registrations.Register(WorldConfigurationExtensionHandlers, registration, nameof(WorldConfigurationExtensionHandlerRegistration), requestKey, priority);
    }

    internal void RegisterWorldConfigurationExtensionSummaryProvider(WorldConfigurationExtensionSummaryProvider provider)
    {
        Registrations.RegisterUnique(WorldConfigurationExtensionSummaryProviders, provider);
    }

    internal IReadOnlyList<ModWorldConfigurationExtensionDto> CollectCurrentWorldConfigurationExtensions(
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

            if (!Registrations.TryInvokeCompatibilityCallback(
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

    internal int ApplyWorldConfigurationExtensions(
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

            if (Registrations.TryInvokeCompatibilityCallback(
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

    internal IReadOnlyList<WorldConfigurationExtensionSummaryItem> GetWorldConfigurationExtensionSummary(
        ModWorldConfigurationDto configuration)
    {
        if (configuration is null)
        {
            return Array.Empty<WorldConfigurationExtensionSummaryItem>();
        }

        var items = new List<WorldConfigurationExtensionSummaryItem>();
        foreach (WorldConfigurationExtensionSummaryProvider provider in WorldConfigurationExtensionSummaryProviders.ToList())
        {
            if (!Registrations.TryInvokeCompatibilityCallback(
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
}
