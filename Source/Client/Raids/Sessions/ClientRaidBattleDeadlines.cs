using AIRsLight.ClashOfRim.Modules.Raids;

namespace AIRsLight.ClashOfRim.Raids;

internal static class ClientRaidBattleDeadlines
{
    internal static void Apply(ActiveRaidBattleSession battle, ClientRaidServerDeadlines deadlines)
    {
        battle.StartedAtUtcTicks = deadlines.StartedAtUtcTicks;
        battle.DeadlineUtcTicks = deadlines.DeadlineUtcTicks;
        battle.FinalDeadlineUtcTicks = deadlines.FinalDeadlineUtcTicks;
    }
}
