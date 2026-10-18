using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility;
using System;
using System.Collections.Generic;
using System.Linq;

namespace AIRsLight.ClashOfRim.Modules.Compatibility;

internal sealed class AdminBaselineCompatibility
{
    private readonly List<AdminBaselineExtensionProviderRegistration> AdminBaselineExtensionProviders = new();
    private readonly CompatibilityRegistrationScope Registrations;

    internal AdminBaselineCompatibility(CompatibilityRegistrationScope registrations) => Registrations = registrations;

    internal void RegisterAdminBaselineExtensionProvider(
        string providerId,
        string kind,
        AdminBaselineExtensionProvider provider)
    {
        if (provider is null || string.IsNullOrWhiteSpace(providerId) || string.IsNullOrWhiteSpace(kind))
        {
            return;
        }

        int priority = Registrations.ActiveRegistrationPriority();
        string requestKey = AdminBaselineExtensionProviderRegistration.Key(providerId, kind);
        AdminBaselineExtensionProviderRegistration? existing = AdminBaselineExtensionProviders.FirstOrDefault(registration =>
            string.Equals(registration.ProviderId, providerId, System.StringComparison.OrdinalIgnoreCase)
            && string.Equals(registration.Kind, kind, System.StringComparison.OrdinalIgnoreCase));
        if (!Registrations.TryReplaceDuplicate(AdminBaselineExtensionProviders, existing,
                nameof(AdminBaselineExtensionProviderRegistration), requestKey, priority,
                "AdminBaselineExtensionDuplicateRejected", "AdminBaselineExtensionDuplicateReplaced",
                "Admin baseline extension '" + providerId + "/" + kind + "'"))
        {
            return;
        }

        var registration = new AdminBaselineExtensionProviderRegistration(providerId, kind, provider);
        Registrations.Register(AdminBaselineExtensionProviders, registration, nameof(AdminBaselineExtensionProviderRegistration), registration.RequestKey, priority);
    }

    internal void AppendAdminBaselineExtensions(
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

            if (!Registrations.TryInvokeCompatibilityCallback(
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
