using System;
using System.Collections.Generic;
using System.Linq;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Support;
using AIRsLight.ClashOfRim.WorldObjects;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace AIRsLight.ClashOfRim.Support;

internal sealed class ClientSupportActions
{
    private readonly ClientSupportSending sending;
    private readonly IClientSupportPresentation presentation;
    internal ClientSupportActions(ClientSupportSending sending, IClientSupportPresentation presentation)
    { this.sending = sending; this.presentation = presentation; }

    internal void OpenMenu(Caravan caravan, IReadOnlyList<ModWorldMapMarkerDto> targets, IReadOnlyList<Pawn> pawns)
    {
        if (targets.Count == 0) { presentation.Status(presentation.Text("StatusNoTargets")); return; }
        var owners = pawns.Where(pawn => pawn is not null && !pawn.Dead && caravan.IsOwner(pawn)).ToArray();
        if (owners.Length == 0) { presentation.Status(presentation.Text("StatusNoPawns")); return; }
        var scope = sending.CaptureScope();
        var options = new List<FloatMenuOption>();
        foreach (var marker in targets)
        foreach (var pawn in owners)
        {
            var target = new ClientSupportTarget(marker.OwnerUserId, marker.OwnerColonyId ?? "", marker.MapId ?? "", marker.WorldObjectId ?? "",
                marker.Tile, marker.TileLayerId, ClientWorldTargetPresentation.Label(marker), string.Equals(marker.Kind, "ActiveRaidTarget", StringComparison.Ordinal));
            var source = new ClientSupportSource(caravan, pawn);
            string label = pawn.LabelShort;
            Action<int?, bool> confirm = (days, permanent) => sending.InScope(scope,
                () => _ = sending.SendAsync(source, target, new(days, permanent)));
            Action duration = () => sending.InScope(scope,
                () => Find.WindowStack.Add(new SupportDurationDialogWindow(label, target.Label, confirm)));
            options.Add(new FloatMenuOption(presentation.Text("CaravanPawnOption", ("PAWN", label), ("TARGET", target.Label)), () => sending.InScope(scope, () =>
                Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                    presentation.Text("ConfirmSend", ("PAWN", label), ("TARGET", target.Label)), duration)))));
        }
        Find.WindowStack.Add(new FloatMenu(options));
    }
}
