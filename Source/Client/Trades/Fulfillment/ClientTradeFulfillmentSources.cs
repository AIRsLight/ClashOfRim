using System;
using System.Linq;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Trades;
using AIRsLight.ClashOfRim.WorldObjects;
using RimWorld.Planet;
using Verse;

namespace AIRsLight.ClashOfRim.Trades;

internal sealed class ClientTradeCaravanSource : IClientTradeSource
{
    private readonly Caravan caravan;
    private readonly ClientTradeLocation? target;
    internal ClientTradeCaravanSource(Caravan caravan, ClientTradeLocation? target) { this.caravan = caravan; this.target = target; }
    public ClientTradeOperation Operation => ClientTradeOperation.SelfDelivery;
    public string Key => caravan.GetUniqueLoadID();
    public string? Validate(ClashOfRimClientNetworkContext context, ModTradeOrderSummaryDto order) => caravan.Spawned
        && ClientTradeTargets.CanSelfDeliver(order, target, caravan.Tile, RemoteWorldObjectUiUtility.ReadTileLayerId(caravan.Tile))
        ? null : ClashOfRimText.Key("ClashOfRim.Trade.StatusCaravanNotAtOwnerTile");
    public ClientTradeLocalResult Apply(ClashOfRimClientNetworkContext context, ModTradeOrderSummaryDto order, Action mutationStarting)
    {
        bool applied = TradeCaravanFulfillmentUtility.ApplyExchangeToCaravan(caravan, order.RequestedThings, order.OfferedThings,
            context.UserId, context.ColonyId, context.CurrentSnapshotId ?? "", out var delivered, out var message, mutationStarting);
        return new(applied, delivered, message);
    }
}

internal sealed class ClientTradeDropPodSource : IClientTradeSource
{
    private readonly Map? map;
    internal ClientTradeDropPodSource(Map? map) => this.map = map;
    public ClientTradeOperation Operation => ClientTradeOperation.ServerDropPod;
    public string Key => "";
    public string? Validate(ClashOfRimClientNetworkContext context, ModTradeOrderSummaryDto order)
    {
        if (map is null || !Find.Maps.Contains(map)) return ClashOfRimText.Key("ClashOfRim.Trade.StatusNoMapReserve");
        return order.ServerDropPodPostage is { Reachable: true, PostageSilver: >= 0 } ? null : ClashOfRimText.Key("ClashOfRim.Trade.StatusDropPodUnreachable");
    }
    public ClientTradeLocalResult Apply(ClashOfRimClientNetworkContext context, ModTradeOrderSummaryDto order, Action mutationStarting)
    {
        bool applied = TradeMapFulfillmentUtility.TryReserveDropPodDelivery(map!, order.RequestedThings, order.ServerDropPodPostage!.PostageSilver!.Value,
            context.UserId, context.ColonyId, context.CurrentSnapshotId ?? "", out var delivered, out var message, mutationStarting);
        return new(applied, delivered, message);
    }
}
