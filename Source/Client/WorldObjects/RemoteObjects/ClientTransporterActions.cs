using System;
using System.Collections.Generic;
using System.Linq;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Gifts;
using AIRsLight.ClashOfRim.Modules.Gifts;
using AIRsLight.ClashOfRim.Modules.Raids;
using AIRsLight.ClashOfRim.Raids;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace AIRsLight.ClashOfRim.WorldObjects;

// The engine constructs/deserializes arrival actions itself. Bind that entry
// point to the composed gift/raid modules without looking up the entire Mod.
internal sealed class ClientTransporterActions
{
    internal static ClientTransporterActions? Current { get; private set; }
    internal static void Attach(ClientTransporterActions? actions) => Current = actions;
    private readonly ClientGiftActions gifts;
    private readonly ClientRaidCommands raids;
    private readonly Func<ClientGiftCapabilities> capabilities;

    internal ClientTransporterActions(ClientGiftActions gifts, ClientRaidCommands raids, Func<ClientGiftCapabilities> capabilities)
    { this.gifts = gifts; this.raids = raids; this.capabilities = capabilities; }

    internal ClientGiftCapabilities Capabilities => capabilities();
    internal bool CanStartGiftArrival => gifts.CanStartArrival;

    internal FloatMenuAcceptanceReport GiftAcceptance(ModWorldMapMarkerDto target, IEnumerable<IThingHolder> pods, bool forced, bool menu = false)
    {
        if (ClientGiftTargets.TransporterRejection(target, Capabilities, forced, menu) is { } rejection) return Reject(rejection);
        return GiftTransporterPayloadUtility.CanSend(pods, forced ? ThingReferenceSurfaces.ForcedDelivery : ThingReferenceSurfaces.Gift);
    }

    internal FloatMenuAcceptanceReport RaidAcceptance(ModWorldMapMarkerDto target, IEnumerable<IThingHolder> pods)
    {
        if (ClientRaidTargets.Rejection(target, Capabilities.PvpEnabled) is { } rejection) return Reject(rejection);
        IReadOnlyList<IThingHolder> cargo = pods as IReadOnlyList<IThingHolder> ?? pods.ToArray();
        return cargo.Count > 0 && TransportersArrivalActionUtility.AnyNonDownedColonist(cargo)
            ? FloatMenuAcceptanceReport.WasAccepted : Reject("ClashOfRim.Raid.TransportPodRejectNoColonist");
    }

    internal void SendGift(string key, ModWorldMapMarkerDto target, PlanetTile tile, IReadOnlyList<ActiveTransporterInfo> pods, bool forced)
        => gifts.SendTransportPods(key, target.OwnerUserId, target.OwnerColonyId ?? "", target.SnapshotId ?? "",
            target.MapId ?? "", target.WorldObjectId ?? "", tile, target.Label ?? "", pods, forced);

    internal void StartRaid(string key, ModWorldMapMarkerDto target, List<ActiveTransporterInfo> pods)
        => _ = raids.StartTransporters(new ClientRaidCommandSource(new TransporterRaidAttackForceSource(
            pods, target.Tile, key, target.TileLayerId)), target);

    internal static FloatMenuAcceptanceReport Reject(string key) => FloatMenuAcceptanceReport.WithFailReason(ClashOfRimText.Key(key));
}
