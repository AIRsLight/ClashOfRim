using System.Text.Json;
using AIRsLight.ClashOfRim.Network;
using RimTest.Protocol;
using RimTest.Testing;

namespace ClashOfRim.GameTests;

internal sealed record ColonyMoveBaseline(string Client, string ColonyId, string SnapshotId, string LoanId, int Silver, int Steel, JsonElement DlcState);

internal sealed class ColonyMoveScenarioFixture(TestRun run, ScenarioPolling polling, ColonyScenarioFixture colony,
    EconomicScenarioFixture economy, WorldConfigurationRegistry world)
{
    internal async Task<ColonyMoveBaseline> Prepare(string client)
    {
        var dlc = await run.CallAsync(client, "clash.fixture.dlcPawn", new { acknowledgeFixture = true });
        var bank = await economy.RefreshBank(client);
        await run.InvokeClientAsync(client, "StartCreateBankLoan", new { principalSilver = bank.Int("minLoanSilver"), durationDays = bank.Int("minDurationDays") });
        await polling.Idle(client); await polling.Step(client, 600); bank = await economy.RefreshBank(client);
        string loan = bank.GetProperty("activeLoan").String("loanId");
        int silver = await colony.Stock(client, "Silver"), steel = await colony.Stock(client, "Steel");
        var state = await run.CallAsync(client, "clash.state");
        return new(client, state.String("colonyId"), state.String("currentSnapshotId"), loan, silver, steel, dlc.GetProperty("state"));
    }
    internal async Task AwaitCommit(ColonyMoveBaseline before)
    {
        await polling.Wait(before.Client, "clash.state", null, s => s.String("currentSnapshotId") != before.SnapshotId && !s.Bool("localAtomicMutationPending"), 180);
        await polling.Idle(before.Client);
        await economy.AssertSnapshotAdvanced(before.Client, before.SnapshotId);
    }
    internal async Task VerifyDestination(ColonyMoveBaseline before, int tile, JsonElement expectedPawns, int expectedSteel)
    {
        string client = before.Client;
        var homes = (await run.CallAsync(client, "clash.inventory")).GetProperty("maps");
        Check(homes.GetArrayLength() == 1 && homes[0].Int("tileId") == tile, "Move did not leave exactly one home at destination.");
        Check(await colony.Stock(client, "Silver") == before.Silver && await colony.Stock(client, "Steel") == expectedSteel, "Move lost or duplicated cargo.");
        var actual = homes[0].GetProperty("colonists").EnumerateArray().Select(p => p.String("id")).Order(StringComparer.Ordinal);
        Check(actual.SequenceEqual(expectedPawns.EnumerateArray().Select(p => p.GetString()!).Order(StringComparer.Ordinal)), "Move changed pawn identities.");
        Check((await run.CallAsync(client, "clash.state")).String("colonyId") == before.ColonyId, "Move changed colony account.");
        var sites = world.Current!.PlayerColonySites.Where(s => s.UserId == "rimtest-" + client && s.ColonyId == before.ColonyId).ToArray();
        Check(sites.Length == 1 && sites[0].Tile == tile, "Server retained another colony generation or destination.");
        Check((await economy.RefreshBank(client)).GetProperty("activeLoan").String("loanId") == before.LoanId, "Move lost the bank loan.");
        Equal(before.DlcState, await run.CallAsync(client, "clash.dlc.pawns"), "Move changed DLC pawn state");
    }
    internal async Task ReloadAndRepay(ColonyMoveBaseline before, int tile, JsonElement expectedPawns, int expectedSteel, string saveName)
    {
        await colony.Reload(before.Client, saveName);
        await VerifyDestination(before, tile, expectedPawns, expectedSteel);
        await run.InvokeClientAsync(before.Client, "StartRepayBankLoan"); await polling.Idle(before.Client);
        Check((await economy.RefreshBank(before.Client)).GetProperty("activeLoan").ValueKind == JsonValueKind.Null, "Moved colony could not repay loan.");
    }
}
