using AIRsLight.ClashOfRim.ThirdPartyCompatibility;
using System.Collections.Generic;
using System.Linq;

namespace AIRsLight.ClashOfRim.Modules.Compatibility;

internal sealed class ColonySiteCompatibility
{
    private readonly List<PlayerColonySiteRegistrationSuppressor> PlayerColonySiteRegistrationSuppressors = new();
    private readonly CompatibilityRegistrationScope Registrations;

    internal ColonySiteCompatibility(CompatibilityRegistrationScope registrations) => Registrations = registrations;

    internal void RegisterPlayerColonySiteRegistrationSuppressor(PlayerColonySiteRegistrationSuppressor suppressor)
    {
        Registrations.RegisterUnique(PlayerColonySiteRegistrationSuppressors, suppressor);
    }

    internal bool IsPlayerColonySiteRegistrationSuppressedByCompatibility()
    {
        foreach (PlayerColonySiteRegistrationSuppressor suppressor in PlayerColonySiteRegistrationSuppressors.ToList())
        {
            if (!Registrations.TryInvokeCompatibilityCallback(
                    nameof(IsPlayerColonySiteRegistrationSuppressedByCompatibility),
                    suppressor,
                    () => suppressor(),
                    out bool suppressed))
            {
                continue;
            }

            if (suppressed)
            {
                return true;
            }
        }

        return false;
    }
}
