using System.Text.Json;
using RimTest.Protocol;

namespace ClashOfRim.GameTests;

internal sealed partial class GameSuite
{
    async Task GravshipMove(string client, bool existingSite)
    {
        var dlc = await Call(client, "clash.fixture.dlcPawn", new { acknowledgeFixture = true });
        var bank = await economy.RefreshBank(client);
        await Invoke(client, "StartCreateBankLoan", new { principalSilver = bank.Int("minLoanSilver"), durationDays = bank.Int("minDurationDays") });
        await Idle(client); await Step(client, 600); bank = await economy.RefreshBank(client);
        string loan = bank.GetProperty("activeLoan").String("loanId");
        int cash = await colonyFixture.Stock(client, "Silver"), steel = await colonyFixture.Stock(client, "Steel");
        var state = await Call(client, "clash.state");
        var fixture = await Call(client, "clash.fixture.gravship", new { site = existingSite, acknowledgeFixture = true });
        await Step(client, 30);
        await Call(client, "clash.gravship.pilot", new { pawn = fixture.GetProperty("pawn").String("handle"), console = fixture.GetProperty("console").String("handle") });
        await Call(client, "time.run", new { tps = 1200 });
        await Wait(client, "clash.gravship", null, s => s.Bool("ritualDialog"), 120); await Call(client, "time.cancel");
        await Call(client, "clash.gravship.begin"); await Confirm(client);
        await Call(client, "time.run", new { tps = 1200 });
        await Wait(client, "clash.gravship", null, s => s.Bool("choosingTile"), 120); await Call(client, "time.cancel");
        // Release RimTest's paused clock before the engine takes control of the cutscene.
        await Call(client, "time.set", new { speed = "Normal" });
        await Call(client, "clash.gravship.destination", new { tile = fixture.Int("tile") }); await Confirm(client);
        // The game's cutscene owns the clock and resumes world travel after takeoff.
        var arrival = await Wait(client, "clash.gravship", null, s => s.Bool("landing"), 180);
        if (arrival.GetProperty("markers").GetArrayLength() == 0) await Call(client, "clash.gravship.placeLanding");
        await Wait(client, "clash.gravship", null, s => s.GetProperty("markers").GetArrayLength() == 1);
        await Call(client, "clash.gravship.land");
        await Wait(client, "clash.state", null, s => s.String("currentSnapshotId") != state.String("currentSnapshotId") && !s.Bool("localAtomicMutationPending"), 180);
        await Idle(client); await Call(client, "time.set", new { speed = "Paused" });
        var home = (await Call(client, "clash.inventory")).GetProperty("maps");
        Check(home.GetArrayLength() == 1 && home[0].Int("tileId") == fixture.Int("tile"), "Flight did not establish exactly one destination home.");
        Equal(fixture.GetProperty("pawns"), JsonSerializer.SerializeToElement(home[0].GetProperty("colonists").EnumerateArray().Select(p => p.String("id")).Order().ToArray()), "Flight lost/duplicated colonists");
        Check(await colonyFixture.Stock(client, "Silver") == cash && await colonyFixture.Stock(client, "Steel") == steel, "Flight lost/duplicated loaded cargo.");
        var engine = (await Call(client, "clash.gravship")).GetProperty("engines");
        Check(engine.GetArrayLength() == 1 && engine[0].String("id") == fixture.String("engine") && engine[0].GetProperty("fuel").GetSingle() < 100, "Flight failed to transport original engine or consume fuel.");
        Check(ServerState.WorldConfiguration.Current!.PlayerColonySites.Single(s => s.UserId == "rimtest-" + client).Tile == fixture.Int("tile"), "Flight did not update server anchor.");
        Check((await economy.RefreshBank(client)).GetProperty("activeLoan").String("loanId") == loan, "Flight lost unpaid loan.");
        Equal(dlc.GetProperty("state"), await Call(client, "clash.dlc.pawns"), "Flight changed DLC pawn state");
        await colonyFixture.Reload(client, "rimtest-gravship");
        Check((await Call(client, "clash.inventory")).GetProperty("maps")[0].Int("tileId") == fixture.Int("tile"), "Reload undid flight.");
        Check(await colonyFixture.Stock(client, "Silver") == cash && await colonyFixture.Stock(client, "Steel") == steel, "Reload changed flight cargo.");
        Equal(dlc.GetProperty("state"), await Call(client, "clash.dlc.pawns"), "Reload changed flown DLC pawn state");
        await Invoke(client, "StartRepayBankLoan"); await Idle(client);
        Check((await economy.RefreshBank(client)).GetProperty("activeLoan").ValueKind == JsonValueKind.Null, "Landed colony cannot repay loan.");
    }
}
