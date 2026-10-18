using AIRsLight.ClashOfRim.ThirdPartyCompatibility;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;

namespace AIRsLight.ClashOfRim.Modules.Compatibility;

internal sealed class SnapshotSanitizationCompatibility
{
    private readonly List<CompatibilitySnapshotSaveSanitizer> SnapshotSaveSanitizers = new();
    private readonly CompatibilityRegistrationScope Registrations;

    internal SnapshotSanitizationCompatibility(CompatibilityRegistrationScope registrations) => Registrations = registrations;

    internal bool HasSnapshotSaveSanitizers => SnapshotSaveSanitizers.Count > 0;

    internal void RegisterSnapshotSaveSanitizer(CompatibilitySnapshotSaveSanitizer sanitizer)
    {
        Registrations.RegisterUnique(SnapshotSaveSanitizers, sanitizer);
    }

    internal int SanitizeSnapshotSave(XDocument document)
    {
        int changes = 0;
        foreach (CompatibilitySnapshotSaveSanitizer sanitizer in SnapshotSaveSanitizers.ToList())
        {
            if (Registrations.TryInvokeCompatibilityCallback(
                    nameof(SanitizeSnapshotSave),
                    sanitizer,
                    () => sanitizer(document),
                    out int sanitizerChanges))
            {
                changes += Math.Max(0, sanitizerChanges);
            }
        }

        return changes;
    }
}
