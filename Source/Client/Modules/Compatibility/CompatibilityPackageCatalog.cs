using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility;
using System;
using System.Collections.Generic;
using System.Linq;

namespace AIRsLight.ClashOfRim.Modules.Compatibility;

internal sealed class CompatibilityPackageCatalog
{
    private readonly List<CompatibilityPackageRegistration> CompatibilityPackages = new();
    private readonly CompatibilityRegistrationScope Registrations;
    private HashSet<string>? serverDlcIds;

    internal CompatibilityPackageCatalog(CompatibilityRegistrationScope registrations) => Registrations = registrations;

    internal void RegisterCompatibilityPackage(
        string packageId,
        Func<bool> isLocallyActive,
        IReadOnlyCollection<string> capabilities)
    {
        if (string.IsNullOrWhiteSpace(packageId) || isLocallyActive is null || capabilities is null || capabilities.Count == 0)
        {
            return;
        }

        int priority = Registrations.ActiveRegistrationPriority();
        CompatibilityPackageRegistration? existing = CompatibilityPackages.FirstOrDefault(registration =>
            string.Equals(registration.PackageId, packageId, System.StringComparison.OrdinalIgnoreCase));
        if (!Registrations.TryReplaceDuplicate(CompatibilityPackages, existing,
                nameof(CompatibilityPackageRegistration), packageId, priority,
                "CompatibilityPackageDuplicateRejected", "CompatibilityPackageDuplicateReplaced",
                "Compatibility package '" + packageId + "'"))
        {
            return;
        }

        foreach (string capability in capabilities
            .Where(capability => !string.IsNullOrWhiteSpace(capability))
            .Select(capability => capability.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (CompatibilityPackages.Any(registration => registration.Capabilities.Contains(capability)))
            {
                Registrations.AddRegistrationDiagnostic(
                    "Info",
                    "CompatibilityCapabilityShared",
                    nameof(CompatibilityPackageRegistration),
                    capability,
                    "Compatibility capability '" + capability + "' is declared by multiple packages.");
            }
        }

        var registration = new CompatibilityPackageRegistration(packageId, isLocallyActive, capabilities);
        Registrations.Register(CompatibilityPackages, registration, nameof(CompatibilityPackageRegistration), packageId, priority);
    }

    internal void ApplyServerDlcBaseline(IEnumerable<string>? dlcIds)
    {
        serverDlcIds = dlcIds is null
            ? null
            : new HashSet<string>(dlcIds, System.StringComparer.OrdinalIgnoreCase);
    }

    internal bool HasCompatibilityCapability(string capability)
    {
        if (string.IsNullOrWhiteSpace(capability))
        {
            return false;
        }

        foreach (CompatibilityPackageRegistration package in CompatibilityPackages.ToList())
        {
            if (!Registrations.TryInvokeCompatibilityCallback(
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

    private bool ServerAllowsDlc(string packageId) => serverDlcIds is null || serverDlcIds.Contains(packageId);
}
