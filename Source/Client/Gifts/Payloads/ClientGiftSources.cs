using System.Collections.Generic;
using System.Linq;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Gifts;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility;
using AIRsLight.ClashOfRim.Trades;
using AIRsLight.ClashOfRim.WorldObjects;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace AIRsLight.ClashOfRim.Gifts;

internal sealed class ClientCaravanGiftSource : IClientGiftSource
{
    private readonly Caravan caravan;
    private readonly IReadOnlyList<TradeOfferSelection> selections;
    internal ClientCaravanGiftSource(Caravan caravan, IReadOnlyList<TradeOfferSelection> selections)
    { this.caravan = caravan; this.selections = selections.Select(s => new TradeOfferSelection(s.Thing, s.Count)).ToArray(); }
    public ClientGiftCargo Describe() => new(ClientGiftSourceKind.Caravan, caravan.GetUniqueLoadID(), caravan.Tile,
        RemoteWorldObjectUiUtility.ReadTileLayerId(caravan.Tile), selections.Count != 0 && Find.WorldObjects.AllWorldObjects.Contains(caravan));
    public ClientGiftCapture Capture(ClashOfRimClientNetworkContext context, bool forced)
    {
        try
        {
            return new(selections.Select(s => TradeCaravanFulfillmentUtility.BuildThingReference(s.Thing, caravan,
                context.UserId, context.ColonyId, context.CurrentSnapshotId!, s.Count,
                forced ? ThingReferenceSurfaces.ForcedDelivery : ThingReferenceSurfaces.Gift)).ToArray());
        }
        catch (ThingTransferRejectedException ex) { return new(System.Array.Empty<ModThingReferenceDto>(), ThingTransferPipeline.RejectionMessage(ex.RejectionCode)); }
    }
    public ClientGiftRemoval Remove() => new(TradeCaravanFulfillmentUtility.RemoveSelectedThings(caravan, selections, out string message), message);
}

internal sealed class ClientTransportPodGiftSource : IClientGiftSource
{
    private readonly string key;
    private readonly PlanetTile tile;
    private readonly ActiveTransporterInfo[] cargo;
    internal ClientTransportPodGiftSource(string key, PlanetTile tile, IReadOnlyList<ActiveTransporterInfo> transporters)
    { this.key = key; this.tile = tile; cargo = transporters.ToArray(); }
    public ClientGiftCargo Describe() => new(ClientGiftSourceKind.TransportPods, key, tile,
        RemoteWorldObjectUiUtility.ReadTileLayerId(tile), cargo.Length != 0);
    public ClientGiftCapture Capture(ClashOfRimClientNetworkContext context, bool forced) => new(
        GiftTransporterPayloadUtility.BuildReferences(cargo, context.UserId, context.ColonyId, context.CurrentSnapshotId!, key, forced));
    public ClientGiftRemoval Remove() => throw new System.InvalidOperationException("The engine owns transporter arrival removal.");
}
