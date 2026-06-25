namespace AIRsLight.ClashOfRim.Save;

public sealed class ObjectIdentityMap
{
    private ObjectIdentityMap(IReadOnlyList<ObjectIdentityMapping> mappings)
    {
        Mappings = mappings;
        ByGlobalKey = mappings
            .GroupBy(mapping => mapping.GlobalKey)
            .ToDictionary(group => group.Key, group => group.First());
    }

    public IReadOnlyList<ObjectIdentityMapping> Mappings { get; }

    public IReadOnlyDictionary<string, ObjectIdentityMapping> ByGlobalKey { get; }

    public static ObjectIdentityMap FromIndex(
        SaveSnapshotIndex index,
        SnapshotIdentity identity,
        ObjectTracePurpose purpose)
    {
        ArgumentNullException.ThrowIfNull(index);

        return FromThings(index.Things, identity, purpose);
    }

    public static ObjectIdentityMap FromThings(
        IEnumerable<ThingSummary> things,
        SnapshotIdentity identity,
        ObjectTracePurpose purpose)
    {
        ArgumentNullException.ThrowIfNull(things);
        ArgumentNullException.ThrowIfNull(identity);

        var mappings = things
            .Select(thing => new ObjectIdentityMapping(
                GlobalObjectId.ForThing(identity, thing.MapUniqueId, thing.LocalId),
                new LocalThingReference(
                    thing.LocalId,
                    thing.MapUniqueId,
                    thing.Def,
                    thing.Position,
                    thing.Faction,
                    thing.IsPawn),
                purpose))
            .ToList();

        return new ObjectIdentityMap(mappings);
    }
}
