using System.Collections.Generic;
using AIRsLight.ClashOfRim.ClientNetwork;
using RimWorld.Planet;
using Verse;

namespace AIRsLight.ClashOfRim;

public sealed partial class ClashOfRimMod
{
    internal void OpenCaravanSupportPawnMenu(Caravan caravan, IReadOnlyList<ModWorldMapMarkerDto> targets, IReadOnlyList<Pawn> pawns) =>
        supportActions.OpenMenu(caravan, targets, pawns);

}
