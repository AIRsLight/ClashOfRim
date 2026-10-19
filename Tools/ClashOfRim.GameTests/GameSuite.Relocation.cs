using System.Text.Json;
using RimTest.Protocol;

namespace ClashOfRim.GameTests;

internal sealed partial class GameSuite
{
    async Task Relocate(string client, string kind)
    {
        var dlc = await Call(client, "clash.fixture.dlcPawn", new { acknowledgeFixture = true });
        var bank = await economy.RefreshBank(client);
        await Invoke(client, "StartCreateBankLoan", new { principalSilver = bank.Int("minLoanSilver"), durationDays = bank.Int("minDurationDays") });
        await Idle(client); await Step(client, 600); bank = await economy.RefreshBank(client);
        string loan = bank.GetProperty("activeLoan").String("loanId");
        int cash = await colonyFixture.Stock(client, "Silver");
        var before = await Call(client, "clash.state");
        var fixture = await Call(client, "clash.fixture.relocation", new { kind, acknowledgeFixture = true });
        int tile = fixture.Int("tile"); string target = fixture.GetProperty("target").String("handle");
        if (kind == "site")
        {
            var map = (await Call(client, "clash.relocation")).GetProperty("maps").EnumerateArray().Single(m => m.Int("tile") == tile);
            Check(map.String("parent") == "Site" && !map.Bool("home"), "Fixture must be a genuine unsettled quest Site.");
            var enemy = await Call(client, "clash.fixture.relocationEnemy", new { map = target, acknowledgeFixture = true });
            var threatened = (await Call(client, "clash.relocation")).GetProperty("maps").EnumerateArray().Single(m => m.Int("tile") == tile);
            Check(threatened.GetProperty("settle").Bool("disabled"), "Relocation bypassed the site's hostile threat check.");
            await Call(client, "clash.fixture.relocationEnemy", new { map = target, remove = enemy.String("id"), acknowledgeFixture = true });
            Check(!map.GetProperty("settle").Bool("disabled"), "Existing quest-map relocation is disabled: " + map.GetProperty("settle").GetRawText());
            await Call(client, "clash.relocation.settleMap", new { map = target });
        }
        else
        {
            await Call(client, "world.select", new { target });
            string label = (await Call(client, "clash.relocation")).String("label");
            var command = Items(await Call(client, "world.gizmos", new { target })).Single(c => c.String("label") == label);
            Check(!command.Bool("disabled"), "Caravan relocation disabled: " + command.GetRawText());
            await Call(client, "world.invokeGizmo", new { target, type = command.String("type"), label = command.String("label") });
        }
        await Call(client, "clash.relocation.confirm");
        await Wait(client, "clash.state", null, s => s.String("currentSnapshotId") != before.String("currentSnapshotId") && !s.Bool("localAtomicMutationPending"), 180);
        await Idle(client);
        var homes = (await Call(client, "clash.inventory")).GetProperty("maps");
        Check(homes.GetArrayLength() == 1 && homes[0].Int("tileId") == tile, "Relocation did not leave exactly one home at destination.");
        Check(await colonyFixture.Stock(client, "Silver") == cash && await colonyFixture.Stock(client, "Steel") == 10, "Relocation lost or duplicated packed cargo.");
        Equal(fixture.GetProperty("pawns"), JsonSerializer.SerializeToElement(homes[0].GetProperty("colonists").EnumerateArray().Select(p => p.String("id")).Order().ToArray()), "Relocation changed pawn identities");
        Check((await Call(client, "clash.state")).String("colonyId") == before.String("colonyId"), "Relocation changed colony account.");
        var site = ServerState.WorldConfiguration.Current!.PlayerColonySites.Single(s => s.UserId == "rimtest-" + client);
        Check(site.Tile == tile, "Server retained old colony location.");
        Check((await economy.RefreshBank(client)).GetProperty("activeLoan").String("loanId") == loan, "Moving lost the bank loan.");
        Equal(dlc.GetProperty("state"), await Call(client, "clash.dlc.pawns"), "Relocation changed DLC pawn state");
        await colonyFixture.Reload(client, "rimtest-relocation-" + kind);
        homes = (await Call(client, "clash.inventory")).GetProperty("maps");
        Check(homes.GetArrayLength() == 1 && homes[0].Int("tileId") == tile && await colonyFixture.Stock(client, "Silver") == cash, "Reload undid relocation/cash.");
        Equal(dlc.GetProperty("state"), await Call(client, "clash.dlc.pawns"), "Reload changed relocated DLC pawn state");
        await Invoke(client, "StartRepayBankLoan"); await Idle(client);
        Check((await economy.RefreshBank(client)).GetProperty("activeLoan").ValueKind == JsonValueKind.Null, "Relocated colony could not repay loan.");
    }
}
