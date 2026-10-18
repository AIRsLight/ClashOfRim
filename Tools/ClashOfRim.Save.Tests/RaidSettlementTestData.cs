using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;

// Shared real save inputs for independent module and durable commit tests.
internal sealed class RaidSettlementTestData
{
    private readonly string Root;
    internal DateTimeOffset Now => DateTimeOffset.UnixEpoch.AddHours(1);
    internal SaveSnapshotPackage Original { get; }
    internal SaveSnapshotPackage Evidence { get; }
    internal AuthoritativeEvent Raid { get; }
    internal RaidSettlementDeferredPayload Payload { get; }

    internal RaidSettlementTestData(string root)
    {
        Root = root;
        Original = Package("defender", "defender-colony", "before", true);
        Raid = AuthoritativeEventFactory.Create(ServerEventType.Raid, new EventParty("attacker", "attacker-colony"),
            new EventParty("defender", "defender-colony"), "test-raid", false,
            new RaidEventPayload("before", null, Now, null, null, new RaidAttackForceRecord("attack", [], [])), Now,
            new EventTargetContext("WorldObject_1", "0", 1, EventLandingMode.MapEdge));
        Evidence = Package("attacker", "attacker-colony", "evidence", false);
        Payload = new RaidSettlementDeferredPayload(Raid.EventId, "attacker", "attacker-colony", "defender", "defender-colony",
            "evidence", "before", "artifact", RaidSettlementOrigin.OnlineEvidence, "Applied");
    }

    private SaveSnapshotPackage Package(string owner, string colony, string id, bool things)
    {
        string path = Path.Combine(Root, id + ".rws");
        string world = things ? "" : "<world><worldObjects><worldObjects><li Class=\"AIRsLight.ClashOfRim.RemoteMaps.RemoteSessionMapParent\"><ID>7</ID>"
            + "<def>ClashOfRim_RemoteRaidBattleMapParent</def><clashOfRimRelatedEventId>" + Raid.EventId
            + "</clashOfRimRelatedEventId></li></worldObjects></worldObjects></world>";
        File.WriteAllText(path, "<savegame><meta><gameVersion>1.7-test</gameVersion></meta><game>" + world
            + "<maps><li><uniqueID>" + (things ? "0" : "7") + "</uniqueID><mapInfo><parent>WorldObject_7</parent></mapInfo><things>"
            + (things ? "<thing Class=\"ThingWithComps\"><id>steel</id><def>Steel</def><stackCount>10</stackCount></thing>" : "")
            + "</things></li></maps></game></savegame>");
        var package = SaveSnapshotPackageBuilder.FromFile(path, new SnapshotIdentity(owner, colony, id), Now);
        return package with { Envelope = package.Envelope with { NextLineageToken = id + "-token" } };
    }
}
