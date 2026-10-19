using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using System.Text.Json;
using static RaidTestPreparation;

internal static class RaidCreationTestClient
{
    public static Task<EventCreationResponse> Invoke(ClashOfRimNetworkState state, SaveSnapshotPackage package, bool guarded,
        string? changed = null, ProtocolIdentity? principal = null, string key = "raid", string? token = null, ProtocolIdentity? actorOverride = null, Action<System.Text.Json.Nodes.JsonObject>? mutate = null)
    {
        var actor = actorOverride ?? (changed == "owner" ? new ProtocolIdentity("other", "other-colony", "before") : Attacker);
        var target = changed == "defender" ? new ProtocolIdentity("other", "other-colony", "defender") : Defender;
        var metadata = ProtocolDtoMapper.ToMetadataDto(changed == "snapshot" ? package with
            { Envelope = package.Envelope with { Identity = new SnapshotIdentity("userA", "colonyA", "changed") } } : package);
        var raid = new CreateRaidRequest(key, actor, target, true, false, 100000, null, null, "world",
            changed == "map" ? "other-map" : "0", changed == "baseline" ? "changed" : "defender",
            changed == "pawns" ? ["other-pawn"] : ["pawn"], changed == "items" ? [new ThingReferenceDto("item", "Silver", 1)] : [],
            changed == "tile" ? 999 : 1, changed == "opponent" ? "VanillaNpc" : "Player",
            changed == "guard" ? "changed-guard" : guarded ? "guard" : null);
        object body = new CreateRaidWithSnapshotRequest(raid, metadata);
        if (mutate is not null)
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(body))!.AsObject();
            mutate(node); body = node;
        }
        return SnapshotEndpointTestClient.Invoke<EventCreationResponse>("CreateRaidWithSnapshot", state,
            _ => body, package.Payload, principal ?? actor, token);
    }
}
