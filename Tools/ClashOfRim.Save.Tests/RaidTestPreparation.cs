using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;

internal static class RaidTestPreparation
{
    public static readonly ProtocolIdentity Attacker = new("userA", "colonyA", "before");
    public static readonly ProtocolIdentity Defender = new("userB", "colonyB", "defender");

    public static SaveSnapshotPackage Prepare(IColonySnapshotIndexStore snapshots, DiplomacyRelationRegistry relations,
        MercenaryGuardContractRegistry guards, Func<string, SaveSnapshotPackage> factory, bool guarded, ProtocolIdentity? actor = null)
    {
        actor ??= Attacker;
        var departure = SnapshotEndpointPreparation.Prepare(snapshots, factory, actor, "departed");
        var defender = factory("defender");
        defender = defender with { Envelope = defender.Envelope with { Identity = new SnapshotIdentity("userB", "colonyB", "defender") } };
        new SnapshotUploadReceiver(snapshots).Receive(new SnapshotUploadContext("userB", "colonyB", "defender"), defender, DateTimeOffset.UtcNow);
        SetHostile(relations, actor);
        if (guarded) SeedGuard(guards);
        return departure;
    }

    public static void SetHostile(DiplomacyRelationRegistry relations, ProtocolIdentity? actor = null)
    {
        actor ??= Attacker;
        relations.SetRelationKind(actor.UserId, actor.ColonyId!, "userB", "colonyB", DiplomacyRelationRegistry.RelationHostile,
            null, DateTimeOffset.UtcNow);
    }

    public static MercenaryGuardContractRecord SeedGuard(MercenaryGuardContractRegistry guards, string key = "guard") =>
        guards.ActivateWithSnapshot(key, "userB", "colonyB", key, "defender", "Apprentice", 600, 0.35f, 1000, DateTimeOffset.UnixEpoch)!;

    public static MercenaryGuardContractRegistry OpenGuards(string database) =>
        new(new SqliteDomainKeyedJsonRecordStore(database, SqliteDomainRegistrySchema.MercenaryGuards), null);
}
