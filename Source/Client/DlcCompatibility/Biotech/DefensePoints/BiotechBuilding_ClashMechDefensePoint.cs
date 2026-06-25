using System.Collections.Generic;
using AIRsLight.ClashOfRim.Raids;

namespace AIRsLight.ClashOfRim.DlcCompatibility;

public sealed class Building_ClashMechDefensePoint : Building_ClashDefensePoint
{
    protected override IReadOnlyList<DefensePointAiMode> AvailableModes => DefensePointUtility.NonEquipmentModes;
}
