using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Protocol;
using AIRsLight.ClashOfRim.Raids;

internal static class RaidSettlementPayloadTests
{
    public static void Run()
    {
        foreach (string json in new[] { "", "null", "{}", "{\"Settlement\":null}", "{\"Message\":\"Settlement\"}",
            "{\"AttackForce\":{\"Settlement\":{}}}", "{\"Settlement\":" })
            Require(!RaidSettlementPayloadReader.HasSettlement(new() { EventType = ServerEventType.Raid, PayloadSummary = json }), json);
        foreach (string json in new[] { "{\"Settlement\":{\"Losses\":[]}}", "{\"Settle\\u006dent\":{\"Losses\":[]}}" })
        {
            Require(RaidSettlementPayloadReader.HasSettlement(new() { EventType = ServerEventType.Raid, PayloadSummary = json }), json);
            Require(!RaidSettlementPayloadReader.HasSettlement(new() { EventType = ServerEventType.ItemDelivery, PayloadSummary = json }), "non-raid");
        }
        var payload = RaidSettlementPayloadReader.Read("{\"Settlement\":{\"Losses\":[{\"Def\":\"Steel\",\"MapUniqueId\":\"Map_7\",\"LossCount\":3,\"WholeThingMissing\":true}]}}");
        var loss = payload!.Settlement!.Losses.Single();
        Require(loss.Def == "Steel" && loss.MapUniqueId == "Map_7" && loss.LossCount == 3 && loss.WholeThingMissing, "display fields");
    }
    private static void Require(bool condition, string scenario) { if (!condition) throw new Exception("Raid settlement: " + scenario); }
}
