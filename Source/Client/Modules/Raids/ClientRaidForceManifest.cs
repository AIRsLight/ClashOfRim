using System.Collections.Generic;
using AIRsLight.ClashOfRim.ClientNetwork;

namespace AIRsLight.ClashOfRim.Modules.Raids;

internal sealed record ClientRaidForceManifest(string SourceLoadId, IReadOnlyList<string> PawnGlobalKeys,
    IReadOnlyList<ModThingReferenceDto> CarriedThings);
