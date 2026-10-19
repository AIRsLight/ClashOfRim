using System.Text.Json;
using System.Text.Json.Nodes;
using RimTest.Protocol;
using RimTest.Testing;

namespace ClashOfRim.GameTests;

internal sealed class ThirdPartyScenario(TestRun run, ScenarioPolling polling, ColonyScenarioFixture colony, TradeScenarioFixture trade, SupportScenario support, bool mvcfEquipmentVerbs)
{
    private JsonElement[] expected = [];
    private string[] handles = [];
    internal async Task PrepareStatue()
    {
        foreach (string c in new[] { "A", "B" })
        {
            var probe = await run.CallAsync(c, "clash.fixture.thirdPartyProbe", new { acknowledgeFixture = true });
            Check(probe.GetProperty("features").EnumerateArray().Any(f => f.GetString() == "ExtraEquipmentVerbs") == mvcfEquipmentVerbs,
                "MVCF startup feature activation differs from the requested control profile.");
        }
        await colony.StoreStartingResources("A"); await colony.StoreStartingResources("B");
        var fixture = await run.CallAsync("A", "clash.fixture.alienStatue", new { acknowledgeFixture = true });
        expected = Items(fixture).Select(i => i.GetProperty("data")).ToArray(); handles = Items(fixture).Select(H).ToArray();
        Check(expected.Length == 2 && expected.Any(d => d.String("race") == "Ratkin") && expected.Any(d => d.String("race") == "Human"), "Statue fixture must exercise both alien and ordinary human subjects.");
        Check(expected.Any(d => d.String("stateXml").Contains("AlienRace.HARStatueContainer")) && expected.Any(d => d.String("stateXml").Contains("FacialAnimation")), "Both HAR and Facial Animation must contribute native statue state.");
        Check(expected.All(d => d.GetProperty("faces").EnumerateArray().Any(f => !string.IsNullOrWhiteSpace(f.String("face")))), "Native statue display pawns must restore facial controller defs.");
        await colony.StoreResources("A", ["MinifiedThingRealtimeDraw"]);
    }
    internal async Task NativeReload()
    {
        await colony.Reload("A", "RimTestStatueNativeControl"); handles = await VerifyStatue("A");
        await run.RecordAsync(new { observation = "native-statue-control", note = "Compare native serialized appearance exactly; generated display-pawn hair color is separately retained in observations because native reload can randomize it." });
    }
    internal async Task CancelStatue()
    {
        await trade.Cancel(await trade.Create(handles)); await polling.Step("A", 600); await polling.Idle("A");
        handles = await VerifyStatue("A"); await colony.StoreResources("A", ["MinifiedThingRealtimeDraw"]);
    }
    internal async Task DeliverStatue()
    {
        var order = await trade.Create(handles); await trade.FulfillByDropPod(await trade.Refresh("B", order.EventId));
        await polling.Step("B", 600); await polling.Idle("B"); await VerifyStatue("B");
        Check(Items(await run.CallAsync("A", "clash.thirdParty.statues")).Length == 0, "Delivered statue retained by sender.");
        await colony.ReloadPaused("B", "RimTestAlienStatue"); await VerifyStatue("B");
    }
    private async Task<string[]> VerifyStatue(string client)
    {
        var items = Items(await run.CallAsync(client, "clash.thirdParty.statues")); Check(items.Length == expected.Length, "Statues lost or duplicated.");
        foreach (var data in expected)
        {
            var actual = items.Single(i => i.GetProperty("data").String("race") == data.String("race")).GetProperty("data");
            var e = JsonNode.Parse(data.GetRawText())!.AsObject(); var a = JsonNode.Parse(actual.GetRawText())!.AsObject();
            // CompStatue rebuilds a display-only pawn. Its generated hair can change
            // even in native save/load; the serialized hair and every mod field remain strict.
            e.Remove("hairColor"); a.Remove("hairColor");
            Check(JsonNode.DeepEquals(e, a), "Statue persisted appearance lost: " + data.String("race") + " expected=" + e + " actual=" + a);
        }
        return items.Select(H).ToArray();
    }
    internal async Task TransferPawn()
    {
        await support.Run("early", false, async id =>
        {
            var state = await run.CallAsync("B", "clash.thirdParty.pawn", new { thingId = id });
            Check(mvcfEquipmentVerbs
                ? state.Bool("managerPresent") && state.Int("verbCount") > 0 && state.Bool("ownerMatches") && state.Bool("verbOwnersMatch") && state.Bool("initializedByCompatibility")
                : !state.Bool("managerPresent") && !state.Bool("initializedByCompatibility"),
                "Transferred pawn did not receive a correctly owned MVCF manager from the compatibility restore hook: " + state);
        });
    }
}
