namespace ClashOfRim.GameTests;

internal sealed class PawnTransferScenario(CaravanScenario caravan, SupportScenario support, RemoteMapScenario remoteMaps,
    DiplomacyScenario diplomacy, RaidScenario raids)
{
    internal ScenarioSequence CreateSequence(bool withRaid)
    {
        string? fixturePawn = null;
        var actions = new Dictionary<string, Func<Task>>
        {
            ["alliance-request-accept"] = diplomacy.Alliance,
            ["pawn-state-fixture"] = async () => fixturePawn = await caravan.PreparePawnState(),
            ["caravan-load-travel"] = () => caravan.Travel(fixturePawn),
            ["support-state-early-return"] = () => support.Run("early", true),
            ["support-state-rejected-return"] = () => support.Run("reject", true),
            ["support-state-expired-return"] = () => support.Run("expiry", true)
        };
        if (withRaid)
        {
            actions.Add("remote-map-isolation-repeat", async () => { for (int i = 0; i < 3; i++) await remoteMaps.ObserveAlly(true); });
            actions.Add("raid-loss-reload-settlement", () => raids.RunAndReconnect(true));
        }
        else actions.Add("support-death-expiry", () => support.Run("death", true));
        return (withRaid ? ScenarioCatalog.Transfers : ScenarioCatalog.SupportReturns).Bind(actions);
    }
}
