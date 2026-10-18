using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility;
using System;
using System.Collections.Generic;
using System.Linq;

namespace AIRsLight.ClashOfRim.Modules.Compatibility;

internal sealed class WorldGenerationSettingCompatibility
{
    private readonly List<WorldGenerationFloatSettingProviderRegistration> WorldGenerationFloatSettingProviders = new();
    private readonly CompatibilityRegistrationScope Registrations;

    internal WorldGenerationSettingCompatibility(CompatibilityRegistrationScope registrations) => Registrations = registrations;

    internal void RegisterWorldGenerationFloatSettingProvider(string settingKey, WorldGenerationFloatSettingProvider provider)
    {
        if (string.IsNullOrWhiteSpace(settingKey) || provider is null)
        {
            return;
        }

        int priority = Registrations.ActiveRegistrationPriority();
        string normalizedKey = settingKey.Trim();
        WorldGenerationFloatSettingProviderRegistration? existing = WorldGenerationFloatSettingProviders.FirstOrDefault(registration =>
            string.Equals(registration.SettingKey, normalizedKey, StringComparison.OrdinalIgnoreCase));
        if (!Registrations.TryReplaceDuplicate(WorldGenerationFloatSettingProviders, existing,
                nameof(WorldGenerationFloatSettingProviderRegistration), normalizedKey, priority,
                "WorldGenerationFloatSettingDuplicateRejected", "WorldGenerationFloatSettingDuplicateReplaced",
                "World generation float setting '" + normalizedKey + "'"))
        {
            return;
        }

        var registration = new WorldGenerationFloatSettingProviderRegistration(normalizedKey, provider);
        Registrations.Register(WorldGenerationFloatSettingProviders, registration, nameof(WorldGenerationFloatSettingProviderRegistration), normalizedKey, priority);
    }

    internal float ResolveWorldGenerationFloatSetting(
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
            if (!Registrations.TryInvokeCompatibilityCallback(
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
}
