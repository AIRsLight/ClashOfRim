using System.Collections.Generic;

namespace AIRsLight.ClashOfRim.DlcCompatibility;

internal static partial class BiotechCompatibility
{
    private const string MechDefensePointDefName = "ClashOfRim_MechDefensePoint";

    internal static IEnumerable<string> BiotechDefensePointDefNames()
    {
        if (HasBiotechPawnExchange || HasBiotechTradeMetadata || HasBiotechWorldPollution)
        {
            yield return MechDefensePointDefName;
        }
    }
}
