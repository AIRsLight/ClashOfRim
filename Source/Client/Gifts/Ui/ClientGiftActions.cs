using System;
using System.Collections.Generic;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Gifts;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using AIRsLight.ClashOfRim.Trades;
using AIRsLight.ClashOfRim.WorldObjects;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace AIRsLight.ClashOfRim.Gifts;

internal sealed class ClientGiftActions
{
    private readonly ClientGiftOperations operations;
    private readonly ClientSessionLifecycle session;
    private readonly IClientSnapshotGame game;
    private readonly IClientGiftPresentation presentation;
    private readonly Func<ClientGiftCapabilities> capabilities;
    private readonly Func<IReadOnlyList<ModWorldMapMarkerDto>> markers;
    internal ClientGiftActions(ClientGiftOperations operations, ClientSessionLifecycle session, IClientSnapshotGame game,
        IClientGiftPresentation presentation, Func<ClientGiftCapabilities> capabilities, Func<IReadOnlyList<ModWorldMapMarkerDto>> markers)
    { this.operations = operations; this.session = session; this.game = game; this.presentation = presentation; this.capabilities = capabilities; this.markers = markers; }

    internal ClashOfRimClientNetworkContext Context => session.ReadContext();
    internal UiScope Capture() => new(session.CaptureOwnership(), Context, game.BindCurrentGame());
    internal bool Owns(UiScope scope) => scope.Game.IsCurrent && session.Owns(scope.Owner, scope.Context, false);
    internal bool CanStartArrival => operations.CanStartArrival;
    internal IReadOnlyList<ModWorldMapMarkerDto> FindTargets(Caravan caravan, bool forced) => ClientGiftTargets.Find(
        Context, capabilities(), markers(), caravan.Tile, RemoteWorldObjectUiUtility.ReadTileLayerId(caravan.Tile), forced);
    internal static ClientGiftTarget Target(ModWorldMapMarkerDto marker) => new(marker.OwnerUserId, marker.OwnerColonyId ?? "",
        marker.MapId ?? "", marker.WorldObjectId ?? "", marker.Tile, marker.TileLayerId, ClientWorldTargetPresentation.Label(marker));

    internal void OpenMenu(Caravan caravan, IReadOnlyList<ModWorldMapMarkerDto> targets, bool forced)
    {
        if (targets.Count == 0) { presentation.Status(presentation.Text("ClashOfRim.GiftDelivery.StatusNoTargets")); return; }
        var scope = Capture();
        var options = new List<FloatMenuOption>();
        foreach (var marker in targets)
        {
            var target = Target(marker);
            options.Add(new FloatMenuOption(presentation.Text("ClashOfRim.GiftDelivery.TargetOption", ("TARGET", target.Label)), () =>
            {
                if (Owns(scope)) Find.WindowStack.Add(new GiftDeliveryDialogWindow(this, caravan, target, !forced, forced, forced, scope));
            }));
        }
        Find.WindowStack.Add(new FloatMenu(options));
    }

    internal void SendCaravan(Caravan caravan, ClientGiftTarget target, IReadOnlyList<TradeOfferSelection> selections, bool forced, string? message) =>
        _ = operations.SendAsync(new ClientCaravanGiftSource(caravan, selections), target, forced, message);

    internal void SendTransportPods(string key, string userId, string colonyId, string snapshotId, string mapId,
        string worldObjectId, PlanetTile tile, string label, IReadOnlyList<ActiveTransporterInfo> pods, bool forced)
    {
        var target = Target(new ModWorldMapMarkerDto { OwnerUserId = userId, OwnerColonyId = colonyId,
            SnapshotId = snapshotId, MapId = mapId, WorldObjectId = worldObjectId, Tile = tile,
            TileLayerId = RemoteWorldObjectUiUtility.ReadTileLayerId(tile), Label = label });
        _ = operations.SendAsync(new ClientTransportPodGiftSource(key, tile, pods), target, forced, null);
    }

    internal sealed record UiScope(ClientSessionLifecycle.Ownership Owner, ClashOfRimClientNetworkContext Context, IClientSnapshotGameSession Game);
}
