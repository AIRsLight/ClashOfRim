using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Protocol;

internal static class GameWorldTestFixture
{
    internal static WorldConfigurationRegistry ReadyRegistry()
    {
        var registry = new WorldConfigurationRegistry();
        registry.Submit("user", new WorldConfigurationDto("world", "user", "colony", DateTimeOffset.UnixEpoch,
            "seed", "0.3", "Normal", "Normal", "Normal", "Normal", "0", ["OutlanderCivil"], [], [], [], [], [], "Cassandra", "Peaceful"));
        var payload = WorldSubstratePackageCodec.Encode(new WorldSubstratePackage(1, "<grid><layers><keys><li>0</li></keys><values><li/></values></layers></grid>",
            "<features/>", "<landmarks/>", WorldTileGeometryBinaryCodec.Encode(new WorldTileGeometryDto([new(0, "Surface", 1, [new(0, 1, 0, 0)])]))!));
        if (!registry.StoreWorldSubstrate("user", "colony", "world", payload).Accepted) throw new InvalidOperationException("Cannot seed authoritative test world.");
        return registry;
    }
}
