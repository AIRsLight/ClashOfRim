using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Network.Modules.Raids;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Save;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class RaidEvidenceTestClient
{
    private static ConfirmEventApplicationMetadataRequest Request(string raidId, SaveSnapshotPackage package,
        string owner = "userA", string colony = "colonyA", string result = "Applied", string? token = null)
        => new("request", raidId, null, owner, colony, "before", ProtocolDtoMapper.ToMetadataDto(package), result, token);

    public static string Binding(string raidId, SaveSnapshotPackage package)
        => RaidEvidenceRequestBinding.Hash(Request(raidId, package), package.Payload);

    public static ConfirmEventApplicationResponse Invoke(ClashOfRimNetworkState state, string raidId, SaveSnapshotPackage original,
        string attackerToken, string defenderToken, string identity = "attacker", SaveSnapshotPackage? package = null,
        string result = "Applied", Action<JsonObject>? mutate = null)
    {
        package ??= original;
        bool defender = identity is "defender" or "delegated";
        string owner = defender ? "userB" : "userA", colony = defender ? "colonyB" : "colonyA";
        var metadataPackage = identity is "defender" or "delegated" or "metadata"
            ? package with { Envelope = original.Envelope with { Identity = new SnapshotIdentity("userB", "colonyB", "evidence") } }
            : package;
        var request = Request(raidId, metadataPackage, owner, colony, result,
            defender && identity != "delegated" ? defenderToken : attackerToken);
        object body = request;
        if (mutate is not null) { var node = JsonNode.Parse(JsonSerializer.Serialize(request))!.AsObject(); mutate(node); body = node; }
        return SnapshotEndpointTestClient.Invoke<ConfirmEventApplicationResponse>("ConfirmEventApplication", state,
            _ => body, package.Payload,
            new ProtocolIdentity(identity == "header" ? "userB" : identity == "delegated" ? "userA" : owner,
                identity == "header" ? "colonyB" : identity == "delegated" ? "colonyA" : colony, null),
            identity == "header" || defender && identity != "delegated" ? defenderToken : attackerToken).GetAwaiter().GetResult();
    }
}
