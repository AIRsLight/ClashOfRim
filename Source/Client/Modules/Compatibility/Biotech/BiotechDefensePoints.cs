using System;
using System.Collections.Generic;

namespace AIRsLight.ClashOfRim.Modules.Compatibility.Biotech;

internal sealed class BiotechDefensePoints
{
    private readonly Func<bool> enabled;

    internal BiotechDefensePoints(Func<bool> enabled) => this.enabled = enabled;

    private const string MechDefensePointDefName = "ClashOfRim_MechDefensePoint";

    internal IEnumerable<string> DefNames()
    {
        if (enabled())
        {
            yield return MechDefensePointDefName;
        }
    }
}
